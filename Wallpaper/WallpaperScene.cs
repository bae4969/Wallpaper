using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    // 모니터 한 대의 움직이는 배경. Windows 11 24H2 이후의 바탕화면에서는 Progman 아래에
    // 불투명(255) 레이어 창을 아이콘 바로 뒤에 두고, 그 안의 자식 창에 D3D11 flip 스왑체인으로 그린다.
    // 레이어를 차례로 그린 뒤 위젯의 그림자와 내용을 합성한다.
    internal sealed class WallpaperScene : IDisposable
    {
        private const int Child = 0x40000000, ClipSiblings = 0x04000000, ClipChildren = 0x02000000;
        private const int ExLayered = 0x00080000, ExNoActivate = 0x08000000, ExToolWindow = 0x80, ExTransparent = 0x20;

        private readonly System.Drawing.Rectangle bounds;
        private readonly IntPtr progman;
        private readonly HostWindow holder, surface;
        private readonly int width, height, refreshRate;
        // 레이어는 layerWidth×layerHeight(렌더 해상도 배율 적용)로 그리고, 배율이 1보다 작으면 출력 크기로 확대한 뒤 위젯을 합성한다.
        private readonly int layerWidth, layerHeight;
        private long frameTicks;
        private volatile float quality = 2;
        private long frameCount;
        private readonly object gate = new object();
        private readonly Dictionary<object, WidgetFrame> frames = new Dictionary<object, WidgetFrame>();
        private readonly ManualResetEventSlim running = new ManualResetEventSlim(true);
        private readonly Thread thread;
        private volatile bool stopping;
        // 모든 화면이 같은 시계를 써서 메인·서브(이어지기·반사)가 같은 순간을 그린다.
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly SceneView view;

        private ID3D11Device device;
        private ID3D11DeviceContext context;
        private IDXGISwapChain1 swapChain;
        private ID3D11Texture2D backBuffer, sceneTexture, belowTexture, maskTexture, outputTexture;
        private ID3D11RenderTargetView sceneTarget, maskTarget, outputTarget;
        private ID3D11ShaderResourceView belowView, maskView, sceneView;
        private ID3D11Buffer constants;
        private ID3D11SamplerState sampler;
        private ID3D11BlendState opaque, premultiplied, additive, maximum;
        private ID3D11RasterizerState noCull;
        private ID3D11VertexShader fullscreenVertex, widgetVertex;
        private ID3D11PixelShader widgetShadow, widgetMask, widgetPixel, upscalePixel;
        private readonly List<Layer> layers = new List<Layer>();
        private readonly Dictionary<object, WidgetTextures> textures = new Dictionary<object, WidgetTextures>();
        private readonly float[] values = new float[44];

        // 초당 프레임 제한. 0 이거나 주사율 이상이면 수직동기에 맡긴다. 그리는 중에도 바꿀 수 있다.
        public int FrameLimit
        {
            set { Interlocked.Exchange(ref frameTicks, value > 0 && value < refreshRate ? Stopwatch.Frequency / value : 0); }
        }

        // 효과 품질 0(최저)~2(최고), 0.5 단위: 입자 수를 줄이고, 최저(0)에서는 post 레이어(유리 등)를 건너뛴다. 셰이더에는 Quality 로 전달된다.
        public double Quality
        {
            set { quality = (float)Math.Max(0, Math.Min(2, value)); }
        }

        // 옆 모니터까지 입자가 퍼질 때 메인 밀도를 유지하도록 늘리는 배율(가로 범위 ÷ 캔버스 폭)
        private float SpanFactor { get { return Math.Max(1f, view.SpanWidth / Math.Max(1f, view.CanvasWidth)); } }

        // 효과 품질별 입자 수 비율: 최고 100%·높음 80%·보통 60%·낮음 45%·최저 30%
        private float ParticleRatio { get { return quality >= 2 ? 1f : quality >= 1.5f ? 0.8f : quality >= 1 ? 0.6f : quality >= 0.5f ? 0.45f : 0.3f; } }

        // 지금까지 그린 프레임 수(측정용)
        public long FrameCount { get { return Interlocked.Read(ref frameCount); } }

        public bool Paused
        {
            get { return !running.IsSet; }
            set { if (value) running.Reset(); else running.Set(); }
        }

        public bool IsAlive
        {
            get { return !stopping && Native.IsWindow(progman) && Native.IsWindow(Outer) && Native.GetParent(Outer) == progman && thread.IsAlive; }
        }

        private IntPtr Outer { get { return holder != null ? holder.Handle : surface.Handle; } }

        // sub 면 장면의 서브 레이어를 그린다. view 는 캔버스(메인 화면)에서 이 화면이 놓인 자리(null 이면 이 화면 자체가 캔버스).
        public WallpaperScene(Forms.Screen screen, SceneDefinition scene, bool sub, SceneView view, int frameLimit, double renderScale, double effectQuality)
        {
            bounds = screen.Bounds;
            width = bounds.Width;
            height = bounds.Height;
            this.view = view ?? SceneView.Whole(width, height);
            layerWidth = Math.Max(1, (int)Math.Round(width * renderScale));
            layerHeight = Math.Max(1, (int)Math.Round(height * renderScale));
            refreshRate = Native.RefreshRate(screen.DeviceName);
            FrameLimit = frameLimit;
            Quality = effectQuality;
            progman = Native.FindWindow("Progman", null);
            IntPtr result;
            Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 2, 1000, out result);
            if ((Native.GetWindowLong(progman, -20) & 0x00200000) == 0)
                throw new NotSupportedException("움직이는 배경은 Windows 11 24H2 이후의 바탕화면 구조에서만 지원합니다.");
            var origin = new Native.Point { X = bounds.Left, Y = bounds.Top };
            Native.ScreenToClient(progman, ref origin);
            try
            {
                holder = new HostWindow(progman, origin.X, origin.Y, width, height,
                    Child | ClipSiblings | ClipChildren, ExLayered | ExNoActivate | ExToolWindow | ExTransparent);
                if ((Native.GetWindowLong(holder.Handle, -20) & ExLayered) == 0 || !Native.SetLayeredWindowAttributes(holder.Handle, 0, 255, 2))
                    throw new InvalidOperationException("바탕화면에 레이어 창을 만들지 못했습니다.");
                surface = new HostWindow(holder.Handle, 0, 0, width, height, Child | ClipSiblings, ExNoActivate | ExToolWindow | ExTransparent);
                CreateDevice();
                BuildLayers(scene, sub);
                RenderFrame();
                KeepUnderIcons();
                Native.ShowWindow(surface.Handle, 8);
                Native.ShowWindow(holder.Handle, 8);
            }
            catch
            {
                ReleaseDevice();
                if (surface != null) surface.DestroyHandle();
                if (holder != null) holder.DestroyHandle();
                throw;
            }
            thread = new Thread(Run) { IsBackground = true, Name = "WallpaperScene " + screen.DeviceName };
            thread.Start();
        }

        // 바탕화면 창 없이 텍스처에만 그린다(--render, --validate).
        private WallpaperScene(int width, int height, SceneDefinition scene, bool sub = false, SceneView view = null)
        {
            this.width = layerWidth = width;
            this.height = layerHeight = height;
            this.view = view ?? SceneView.Whole(width, height);
            try
            {
                CreateDevice();
                BuildLayers(scene, sub);
            }
            catch
            {
                ReleaseDevice();
                throw;
            }
        }

        // 장면의 모든 레이어를 컴파일해 본다. 문제가 있으면 예외 메시지에 레이어 번호·종류·셰이더 오류를 담는다.
        public static void Validate(SceneDefinition scene)
        {
            new WallpaperScene(64, 64, scene).ReleaseDevice();
            if (scene.HasSub) new WallpaperScene(64, 64, scene, true).ReleaseDevice();
        }

        // 지금까지 흐른 장면 시간(초). 편집 창 미리보기가 같은 시점을 그릴 때 쓴다.
        public float Time { get { return (float)clock.Elapsed.TotalSeconds; } }

        // time 초 시점의 장면을 한 장 그린다(위젯 제외). 편집 창 배경 미리보기용.
        public static System.Windows.Media.Imaging.BitmapSource Still(SceneDefinition scene, int width, int height, float time, double effectQuality, bool sub = false, SceneView view = null)
        {
            var pixels = Render(scene, width, height, time, effectQuality, sub, view);
            var image = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }

        private static byte[] Render(SceneDefinition scene, int width, int height, float time, double effectQuality, bool sub, SceneView view)
        {
            var offscreen = new WallpaperScene(width, height, scene, sub, view);
            offscreen.Quality = effectQuality;
            try
            {
                offscreen.RenderFrame(time);
                return offscreen.ReadPixels();
            }
            finally { offscreen.ReleaseDevice(); }
        }

        // time 초 시점의 장면을 PNG로 저장한다(위젯 제외).
        public static void RenderToFile(SceneDefinition scene, int width, int height, float time, string path, double effectQuality = 2, bool sub = false, SceneView view = null)
        {
            var pixels = Render(scene, width, height, time, effectQuality, sub, view);
            SavePng(pixels, width, height, path);
        }

        // 화면 없이 그린 BGRA 픽셀을 PNG 로 저장한다.
        public static byte[] RenderPixels(SceneDefinition scene, int width, int height, float time, double effectQuality, bool sub, SceneView view)
        {
            return Render(scene, width, height, time, effectQuality, sub, view);
        }

        public static void SavePng(byte[] pixels, int width, int height, string path)
        {
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                bitmap.UnlockBits(data);
                var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!String.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private byte[] ReadPixels()
        {
            using (var staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            }))
            {
                context.CopyResource(staging, Output);
                var mapped = context.Map(staging, 0, MapMode.Read);
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    Marshal.Copy(mapped.DataPointer + (int)(y * mapped.RowPitch), pixels, y * width * 4, width * 4);
                context.Unmap(staging, 0);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
        }

        private void BuildLayers(SceneDefinition scene, bool sub)
        {
            var types = new Dictionary<string, LayerType>();
            var list = scene.LayersFor(sub);
            for (int i = 0; i < list.Length; i++)
            {
                var definition = list[i];
                JsonElement typeName;
                string name = definition.TryGetProperty("type", out typeName) && typeName.ValueKind == JsonValueKind.String ? typeName.GetString() : "";
                try
                {
                    LayerType type;
                    if (!types.TryGetValue(name, out type)) types[name] = type = LayerType.Load(name);
                    layers.Add(Layer.Create(this, type, definition));
                }
                catch (Exception error)
                {
                    throw new InvalidDataException(scene.Id + (sub ? " 서브" : "") + " 레이어 " + (i + 1) + " (" + name + "): " + error.Message, error);
                }
            }
        }

        // Explorer가 바탕화면 창 순서를 바꾸면 아이콘 바로 뒤, Explorer 배경 층보다 앞으로 되돌린다.
        public void KeepUnderIcons()
        {
            var icons = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons != IntPtr.Zero && Native.GetWindow(icons, 2) != Outer)
                Native.SetWindowPos(Outer, icons, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            bool passed = false;
            for (var child = Native.GetWindow(progman, 5); child != IntPtr.Zero; child = Native.GetWindow(child, 2))
            {
                if (child == Outer) { passed = true; continue; }
                if (!passed && Native.ClassOf(child) == "WorkerW")
                {
                    Native.SetWindowPos(child, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                    break;
                }
            }
        }

        // 위젯 프레임: 화면 좌표의 사각형, premultiplied BGRA 내용, 그림자 알파(바이트).
        public void SetWidget(object key, System.Drawing.Rectangle area, byte[] pixels, byte[] shadow, int order, bool visible)
        {
            lock (gate)
            {
                WidgetFrame previous;
                frames.TryGetValue(key, out previous);
                frames[key] = new WidgetFrame
                {
                    Area = new System.Drawing.Rectangle(area.Left - bounds.Left, area.Top - bounds.Top, area.Width, area.Height),
                    Pixels = pixels, Shadow = shadow, Order = order, Visible = visible,
                    Version = previous == null ? 1 : previous.Version + 1
                };
            }
        }

        public void RemoveWidget(object key)
        {
            lock (gate) frames.Remove(key);
        }

        private void Run()
        {
            var timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
            try
            {
                long next = Stopwatch.GetTimestamp();
                while (!stopping)
                {
                    running.Wait();
                    if (stopping) break;
                    long interval = Interlocked.Read(ref frameTicks);
                    if (interval > 0)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (now - next > interval) next = now;   // 멈췄다 다시 그리거나 밀렸으면 지금부터 센다
                        else if (now < next) Wait(timer, next - now);
                        next += interval;
                    }
                    RenderFrame();
                    Interlocked.Increment(ref frameCount);
                }
            }
            catch (Exception error)
            {
                Trace.WriteLine("WallpaperScene: " + error);
                stopping = true;
            }
            finally
            {
                if (timer != IntPtr.Zero) Native.CloseHandle(timer);
                ReleaseDevice();
            }
        }

        // 고해상도 대기 타이머(없으면 Sleep)로 다음 프레임까지 기다린다.
        private static void Wait(IntPtr timer, long ticks)
        {
            long due = -(long)(ticks * 10000000.0 / Stopwatch.Frequency);
            if (timer != IntPtr.Zero && Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                Native.WaitForSingleObject(timer, 0xFFFFFFFF);
            else Thread.Sleep((int)Math.Max(1, ticks * 1000 / Stopwatch.Frequency));
        }

        private void RenderFrame(float? at = null)
        {
            float time = at ?? (float)clock.Elapsed.TotalSeconds;
            context.OMSetRenderTargets(sceneTarget);
            context.RSSetViewport(0, 0, layerWidth, layerHeight);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.PSSetSampler(0, sampler);
            foreach (var layer in layers) layer.Draw(this, time);
            context.RSSetViewport(0, 0, width, height);
            if (outputTexture != null)
            {
                context.OMSetRenderTargets(outputTarget);
                context.OMSetBlendState(opaque);
                context.VSSetShader(fullscreenVertex);
                context.PSSetShader(upscalePixel);
                context.PSSetShaderResource(0, sceneView);
                Apply(time, 0);
                context.Draw(3, 0);
                context.PSSetShaderResource(0, null);
            }
            DrawWidgets(time);
            if (swapChain == null) return;
            context.CopyResource(backBuffer, Output);
            swapChain.Present(1, PresentFlags.None);
        }

        private void DrawWidgets(float time)
        {
            KeyValuePair<object, WidgetFrame>[] current;
            lock (gate) current = frames.Where(f => f.Value.Visible && f.Value.Pixels != null).OrderBy(f => f.Value.Order).ToArray();
            foreach (var stale in textures.Keys.Where(key => !current.Any(f => f.Key == key)).ToArray())
            {
                textures[stale].Dispose();
                textures.Remove(stale);
            }
            if (current.Length == 0) return;
            var views = current.Select(entry => Upload(entry.Key, entry.Value)).ToArray();

            // 겹친 그림자는 가장 큰 값을 공유한다(기존 합성과 같은 규칙).
            context.ClearRenderTargetView(maskTarget, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            context.OMSetRenderTargets(maskTarget);
            context.OMSetBlendState(maximum);
            context.VSSetShader(widgetVertex);
            context.PSSetShader(widgetShadow);
            for (int i = 0; i < current.Length; i++) DrawWidgetQuad(current[i].Value.Area, views[i].Shadow, time);

            context.OMSetRenderTargets(outputTarget ?? sceneTarget);
            context.OMSetBlendState(premultiplied);
            context.VSSetShader(fullscreenVertex);
            context.PSSetShader(widgetMask);
            context.PSSetShaderResource(0, maskView);
            Apply(time, 0);
            context.Draw(3, 0);
            context.PSSetShaderResource(0, null);

            context.VSSetShader(widgetVertex);
            context.PSSetShader(widgetPixel);
            for (int i = 0; i < current.Length; i++) DrawWidgetQuad(current[i].Value.Area, views[i].Content, time);
            context.PSSetShaderResource(0, null);
        }

        private void DrawWidgetQuad(System.Drawing.Rectangle area, ID3D11ShaderResourceView view, float time)
        {
            Array.Clear(values, 4, 32);
            values[4] = area.Left; values[5] = area.Top; values[6] = area.Width; values[7] = area.Height;
            Apply(time, 0, false);
            context.PSSetShaderResource(0, view);
            context.Draw(6, 0);
        }

        // 위젯마다 텍스처를 두고, 크기가 바뀌면 새로 만들고 내용이 바뀌면 다시 올린다.
        private WidgetTextures Upload(object key, WidgetFrame frame)
        {
            WidgetTextures entry;
            if (textures.TryGetValue(key, out entry) && (entry.Width != frame.Area.Width || entry.Height != frame.Area.Height))
            {
                entry.Dispose();
                entry = null;
            }
            if (entry == null) textures[key] = entry = new WidgetTextures(device, frame.Area.Width, frame.Area.Height);
            if (entry.Version != frame.Version)
            {
                context.UpdateSubresource(frame.Pixels, entry.ContentTexture, 0, (uint)(frame.Area.Width * 4), 0);
                entry.Version = frame.Version;
            }
            if (entry.ShadowSource != frame.Shadow)
            {
                context.UpdateSubresource(frame.Shadow, entry.ShadowTexture, 0, (uint)frame.Area.Width, 0);
                entry.ShadowSource = frame.Shadow;
            }
            return entry;
        }

        private ID3D11Texture2D Output { get { return outputTexture ?? sceneTexture; } }

        private void Apply(float time, float seed, bool clear = true, bool layer = false)
        {
            if (clear) Array.Clear(values, 4, 32);
            // 레이어는 캔버스 좌표로 그린다(렌더 해상도 배율 적용). 위젯·확대 단계는 화면 그대로.
            float sx = layer ? layerWidth / (float)width : 1, sy = layer ? layerHeight / (float)height : 1;
            values[0] = layer ? view.CanvasWidth * sx : width; values[1] = layer ? view.CanvasHeight * sy : height;
            values[2] = time; values[3] = seed;
            values[36] = quality;
            values[37] = layer && view.Mirror ? 1 : 0;
            values[38] = layer ? layerWidth : width; values[39] = layer ? layerHeight : height;
            values[40] = layer ? view.OriginX * sx : 0; values[41] = layer ? view.OriginY * sy : 0;
            values[42] = layer ? view.SpanLeft * sx : 0; values[43] = layer ? view.SpanWidth * sx : width;
            context.UpdateSubresource(values, constants);
            context.VSSetConstantBuffer(0, constants);
            context.PSSetConstantBuffer(0, constants);
        }

        private void CreateDevice()
        {
            // GPU 장치를 만들 수 없으면(GPU 없는 PC·원격 데스크톱·CI 러너) WARP 소프트웨어 렌더러로 그린다.
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                out device, out context).Failure)
                D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                    out device, out context).CheckError();
            if (surface != null)
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            using (var factory = adapter.GetParent<IDXGIFactory2>())
                swapChain = factory.CreateSwapChainForHwnd(device, surface.Handle, new SwapChainDescription1
                {
                    Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                    BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore, Scaling = Scaling.Stretch
                });
            if (swapChain != null) backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            sceneTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)layerWidth, Height = (uint)layerHeight, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            sceneTarget = device.CreateRenderTargetView(sceneTexture);
            sceneView = device.CreateShaderResourceView(sceneTexture);
            if (layerWidth != width || layerHeight != height)
            {
                outputTexture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
                });
                outputTarget = device.CreateRenderTargetView(outputTexture);
            }
            belowTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)layerWidth, Height = (uint)layerHeight, MipLevels = 0, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.GenerateMips
            });
            belowView = device.CreateShaderResourceView(belowTexture);
            maskTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            maskTarget = device.CreateRenderTargetView(maskTexture);
            maskView = device.CreateShaderResourceView(maskTexture);
            constants = device.CreateBuffer(new BufferDescription((uint)(values.Length * 4), BindFlags.ConstantBuffer));
            sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            opaque = device.CreateBlendState(BlendDescription.Opaque);
            premultiplied = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
            additive = device.CreateBlendState(new BlendDescription(Blend.One, Blend.One));
            var max = new BlendDescription(Blend.One, Blend.One);
            max.RenderTarget[0].BlendOperation = BlendOperation.Max;
            max.RenderTarget[0].BlendOperationAlpha = BlendOperation.Max;
            maximum = device.CreateBlendState(max);
            // 입자 VS가 사각형을 회전·반전해도(예: streak) 뒷면으로 잘리지 않게 면 제거를 끈다.
            noCull = device.CreateRasterizerState(RasterizerDescription.CullNone);
            context.RSSetState(noCull);
            // 위젯 합성 셰이더는 사용자 폴더가 아닌 실행파일 내장본(common.hlsli + WidgetShaders.hlsl)으로 만든다.
            var internalSource = SceneLibrary.EmbeddedText("layers/common.hlsli") + "\n" + InternalShaders();
            fullscreenVertex = device.CreateVertexShader(Compile(internalSource, "FullscreenVS", "vs_5_0", "WidgetShaders.hlsl"));
            widgetVertex = device.CreateVertexShader(Compile(internalSource, "WidgetVS", "vs_5_0", "WidgetShaders.hlsl"));
            widgetShadow = device.CreatePixelShader(Compile(internalSource, "WidgetShadowPS", "ps_5_0", "WidgetShaders.hlsl"));
            widgetMask = device.CreatePixelShader(Compile(internalSource, "WidgetMaskPS", "ps_5_0", "WidgetShaders.hlsl"));
            widgetPixel = device.CreatePixelShader(Compile(internalSource, "WidgetPS", "ps_5_0", "WidgetShaders.hlsl"));
            upscalePixel = device.CreatePixelShader(Compile(internalSource, "UpscalePS", "ps_5_0", "WidgetShaders.hlsl"));
        }

        private static readonly Dictionary<string, byte[]> compiled = new Dictionary<string, byte[]>();

        private static string InternalShaders()
        {
            using (var stream = typeof(WallpaperScene).Assembly.GetManifestResourceStream("Wallpaper.WidgetShaders.hlsl"))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        // 같은 소스·진입점은 한 번만 컴파일한다. 컴파일러에 넘기는 길이가 글자 수 기준이라 한글 등은 공백으로 바꾼다.
        private static byte[] Compile(string source, string entry, string profile, string name)
        {
            var ascii = new string(source.Select(c => c < 128 ? c : ' ').ToArray());
            var key = entry + "|" + profile + "|" + ascii;
            lock (compiled)
            {
                byte[] code;
                if (compiled.TryGetValue(key, out code)) return code;
                try { code = Compiler.Compile(ascii, entry, name, profile, ShaderFlags.OptimizationLevel3).ToArray(); }
                catch (Exception error) { throw new InvalidDataException(error.Message.Trim(), error); }
                compiled[key] = code;
                return code;
            }
        }

        private void ReleaseDevice()
        {
            foreach (var entry in textures.Values) entry.Dispose();
            textures.Clear();
            foreach (var layer in layers) layer.Dispose();
            layers.Clear();
            var resources = new IDisposable[] { upscalePixel, outputTarget, outputTexture, sceneView, widgetPixel, widgetMask, widgetShadow, widgetVertex, fullscreenVertex,
                noCull, maximum, additive, premultiplied, opaque, sampler, constants, maskView, maskTarget, maskTexture, belowView, belowTexture,
                sceneTarget, sceneTexture, backBuffer, swapChain };
            foreach (var resource in resources) if (resource != null) resource.Dispose();
            if (context != null) { context.ClearState(); context.Flush(); context.Dispose(); }
            if (device != null) device.Dispose();
            widgetPixel = null; widgetMask = null; widgetShadow = null; widgetVertex = null; fullscreenVertex = null;
            noCull = null; maximum = additive = premultiplied = opaque = null; sampler = null; constants = null; maskView = null; maskTarget = null;
            maskTexture = null; belowView = null; belowTexture = null; sceneTarget = null; sceneTexture = null; backBuffer = null;
            swapChain = null; context = null; device = null;
            upscalePixel = null; outputTarget = null; outputTexture = null; sceneView = null;
        }

        public void Dispose()
        {
            if (thread == null) return;
            stopping = true;
            running.Set();
            if (!thread.Join(3000)) Trace.WriteLine("WallpaperScene: 렌더 스레드가 종료되지 않았습니다.");
            surface.DestroyHandle();
            holder.DestroyHandle();
        }

        // 모니터의 대부분을 다른 창이 덮고 있으면 그리지 않아도 된다.
        public static bool IsCovered(System.Drawing.Rectangle monitor)
        {
            bool covered = false;
            uint self = (uint)Process.GetCurrentProcess().Id;
            Native.EnumWindows(delegate (IntPtr window, IntPtr parameter)
            {
                if (!Native.IsWindowVisible(window) || Native.IsIconic(window)) return true;
                uint process;
                Native.GetWindowThreadProcessId(window, out process);
                if (process == self) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                var name = Native.ClassOf(window);
                if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return true;
                if ((Native.GetWindowLong(window, -20) & ExToolWindow) != 0) return true;
                Native.Rect rect;
                if (Native.DwmGetWindowAttribute(window, 9, out rect, 16) != 0) Native.GetWindowRect(window, out rect);
                var area = System.Drawing.Rectangle.Intersect(monitor, System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                if ((long)area.Width * area.Height >= (long)monitor.Width * monitor.Height * 9 / 10) { covered = true; return false; }
                return true;
            }, IntPtr.Zero);
            return covered;
        }

        private sealed class WidgetFrame
        {
            public System.Drawing.Rectangle Area;
            public byte[] Pixels, Shadow;
            public int Order, Version;
            public bool Visible;
        }

        private sealed class WidgetTextures : IDisposable
        {
            public readonly int Width, Height;
            public readonly ID3D11Texture2D ContentTexture, ShadowTexture;
            public readonly ID3D11ShaderResourceView Content, Shadow;
            public int Version;
            public byte[] ShadowSource;

            public WidgetTextures(ID3D11Device device, int width, int height)
            {
                Width = width; Height = height;
                ContentTexture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
                });
                ShadowTexture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.R8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
                });
                Content = device.CreateShaderResourceView(ContentTexture);
                Shadow = device.CreateShaderResourceView(ShadowTexture);
            }

            public void Dispose() { Content.Dispose(); Shadow.Dispose(); ContentTexture.Dispose(); ShadowTexture.Dispose(); }
        }

        // 장면 레이어 하나: 레이어 종류(layers\<이름>.hlsl·.json)에 장면 값을 채워 그린다.
        private sealed class Layer : IDisposable
        {
            private ID3D11PixelShader pixel;
            private ID3D11VertexShader vertex;
            private ID3D11BlendState blend;
            private string kind;
            private uint count = 1;
            private float seed;
            private readonly float[] parameters = new float[32];

            public static Layer Create(WallpaperScene scene, LayerType type, JsonElement definition)
            {
                var layer = new Layer { kind = type.Kind };
                JsonElement value;
                if (definition.TryGetProperty("seed", out value) && value.ValueKind == JsonValueKind.Number) layer.seed = (float)value.GetDouble();
                string mode = definition.TryGetProperty("blend", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : type.Blend;
                if (mode == "opaque") layer.blend = scene.opaque;
                else if (mode == "alpha") layer.blend = scene.premultiplied;
                else if (mode == "add") layer.blend = scene.additive;
                else throw new InvalidDataException("blend 는 opaque, alpha, add 중 하나여야 합니다: " + mode);
                // 오타를 바로 알 수 있게 정의에 없는 값은 거절한다.
                var known = new HashSet<string>(type.Parameters.Select(p => p.Name)) { "type", "seed", "blend", "comment" };
                foreach (var property in definition.EnumerateObject())
                    if (!known.Contains(property.Name))
                        throw new InvalidDataException("알 수 없는 값 \"" + property.Name + "\" (가능: " + String.Join(", ", type.Parameters.Select(p => p.Name)) + ")");
                foreach (var parameter in type.Parameters)
                {
                    var numbers = parameter.Read(definition);
                    if (parameter.Instances) { layer.count = (uint)Math.Max(1, numbers.Length > 0 ? numbers[0] : 1); continue; }
                    for (int i = 0; i < parameter.Components.Length && i < numbers.Length; i++)
                        layer.parameters[parameter.Slot * 4 + parameter.Components[i]] = numbers[i];
                }
                try
                {
                    layer.vertex = scene.device.CreateVertexShader(Compile(type.Source, type.Kind == "particles" ? "VS" : "FullscreenVS", "vs_5_0", type.Name + ".hlsl"));
                    layer.pixel = scene.device.CreatePixelShader(Compile(type.Source, "PS", "ps_5_0", type.Name + ".hlsl"));
                }
                catch
                {
                    layer.Dispose();
                    throw;
                }
                return layer;
            }

            public void Draw(WallpaperScene scene, float time)
            {
                var context = scene.context;
                bool below = kind == "post";
                if (below && scene.quality <= 0) return;
                if (below)
                {
                    // 아래 레이어 결과를 복사해 흐림 단계(mip)를 만든 뒤 그 위에 다시 그린다.
                    context.OMSetRenderTargets((ID3D11RenderTargetView)null);
                    context.CopySubresourceRegion(scene.belowTexture, 0, 0, 0, 0, scene.sceneTexture, 0);
                    context.GenerateMips(scene.belowView);
                    context.OMSetRenderTargets(scene.sceneTarget);
                    context.PSSetShaderResource(0, scene.belowView);
                }
                Array.Copy(parameters, 0, scene.values, 4, 32);
                scene.Apply(time, seed, false, true);
                context.OMSetBlendState(blend);
                context.VSSetShader(vertex);
                context.PSSetShader(pixel);
                if (kind == "particles")
                    context.DrawInstanced(6, (uint)Math.Max(1, Math.Round(count * scene.ParticleRatio * scene.SpanFactor)), 0, 0);
                else context.Draw(3, 0);
                if (below) context.PSSetShaderResource(0, null);
            }

            public void Dispose()
            {
                if (pixel != null) pixel.Dispose();
                if (vertex != null) vertex.Dispose();
            }
        }

        private sealed class HostWindow : Forms.NativeWindow
        {
            public HostWindow(IntPtr parent, int x, int y, int w, int h, int style, int exStyle)
            {
                CreateHandle(new Forms.CreateParams
                {
                    Caption = "Wallpaper.Wallpaper", Parent = parent, X = x, Y = y, Width = w, Height = h, Style = style, ExStyle = exStyle
                });
            }

            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // 클릭은 바탕화면으로 통과
                if (message.Msg == 0x0014) { message.Result = new IntPtr(1); return; } // 배경을 지우지 않는다
                base.WndProc(ref message);
            }
        }

        private static class Native
        {
            internal delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
            [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct DevMode
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
                public short SpecVersion, DriverVersion, Size, DriverExtra;
                public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
                public short Color, Duplex, YResolution, TTOption, Collate;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
                public short LogPixels;
                public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
                public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
            }

            internal static int RefreshRate(string device)
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
                return EnumDisplaySettings(device, -1, ref mode) && mode.DisplayFrequency > 1 ? mode.DisplayFrequency : 60;
            }

            internal static string ClassOf(IntPtr window)
            {
                var name = new System.Text.StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                return name.ToString();
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
            [DllImport("kernel32.dll")] internal static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr argument, bool resume);
            [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
            [DllImport("user32.dll")] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
            [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr window, int index);
            [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr window, ref Point point);
            [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
            [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
            [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
            [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);
            [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
            [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
            [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
            [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        }
    }
}
