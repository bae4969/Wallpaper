using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    internal sealed class DesktopWidget : IDisposable
    {
        private readonly FrameworkElement view;
        private readonly Window renderer;
        private readonly Viewbox viewport;
        private static readonly List<DesktopWidget> scene = new List<DesktopWidget>();
        public static event Action SceneBackgroundChanged;
        private readonly LayerWindow surface;
        private readonly IntPtr host;
        private IntPtr memoryDc;
        private IntPtr bitmap;
        private IntPtr previousBitmap;
        private IntPtr pixels;
        private byte[] pixelBuffer;
        private byte[] backdropPixels;
        private byte[] foregroundPixels, basePixels;
        private float[] shadowAlpha;
        private byte[] shadowBytes;
        // 위젯 뒤 그라데이션: 진하기(0~1)와 내용 가장자리에서 사라질 때까지의 거리(DIP)
        private double shadowOpacity = Theme.FadeOpacity, shadowRange = 110;
        // 움직이는 배경 모니터의 위젯은 자기 창에 그리지 않고 장면에 내용과 그림자를 넘긴다.
        private readonly WallpaperScene wallpaper;
        private bool backgroundDirty = true, disposed;
        private RenderTargetBitmap renderBitmap;
        private int surfaceWidth;
        private int surfaceHeight;
        private double scale = 1;
        private bool visible = true;
        public System.Drawing.Rectangle Bounds { get; private set; }
        // 위젯이 놓인 모니터 영역. 그림자·합성은 같은 모니터의 위젯끼리만 한다.
        private System.Drawing.Rectangle monitor;
        public int LayerIndex { get { return scene.IndexOf(this); } }
        public event Action<BitmapSource> FrameRendered;

        public DesktopWidget(string name, FrameworkElement content, WallpaperScene wallpaperScene = null)
        {
            view = content;
            wallpaper = wallpaperScene;
            ClearBackground();
            host = Native.FindDesktopHost();
            if (host == IntPtr.Zero) throw new InvalidOperationException("Windows 배경화면 계층을 찾지 못했습니다.");
            surface = new LayerWindow(name, host);
            viewport = new Viewbox { Child = view, Width = view.Width, Height = view.Height, Stretch = Stretch.Uniform };
            // WPF는 화면 밖에서 위젯 내용을 렌더링하고 배경에는 합성 비트맵을 표시한다.
            // WPF 투명 창을 Explorer의 자식으로 직접 붙이면 일부 Windows에서 보이지 않는다.
            renderer = new Window
            {
                Title = "FlipCalendar.Render",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000,
                Width = view.Width, Height = view.Height,
                Content = viewport
            };
            renderer.SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(renderer).Handle;
                Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x80 | 0x08000000);
            };
            scene.Add(this);
        }

        public double DpiScale
        {
            get
            {
                var source = PresentationSource.FromVisual(renderer);
                return source == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            }
        }

        public double BasePixelWidth { get { return view.Width * DpiScale; } }
        public double BasePixelHeight { get { return view.Height * DpiScale; } }
        public Rect ContentArea
        {
            get
            {
                var border = view as Border;
                if (border != null)
                    return new Rect(border.Padding.Left, border.Padding.Top,
                        view.Width - border.Padding.Left - border.Padding.Right,
                        view.Height - border.Padding.Top - border.Padding.Bottom);
                var clock = view as FlipClock;
                double width = clock != null ? clock.ContentWidth : 692;
                return new Rect((view.Width - width) / 2, (view.Height - 152) / 2, width, 152);
            }
        }
        public double BaseContentPixelWidth { get { return ContentArea.Width * DpiScale; } }
        public double BaseContentPixelHeight { get { return ContentArea.Height * DpiScale; } }
        public System.Drawing.Rectangle ContentPixels
        {
            get
            {
                var area = ContentArea;
                double zoom = DpiScale * scale;
                return System.Drawing.Rectangle.FromLTRB((int)Math.Round(area.Left * zoom), (int)Math.Round(area.Top * zoom),
                    (int)Math.Round(area.Right * zoom), (int)Math.Round(area.Bottom * zoom));
            }
        }
        public System.Drawing.Rectangle ContentBounds
        {
            get
            {
                var area = ContentPixels;
                area.Offset(Bounds.Left, Bounds.Top);
                return area;
            }
        }
        public int PixelWidth { get { return (int)Math.Round(BasePixelWidth * scale); } }
        public int PixelHeight { get { return (int)Math.Round(BasePixelHeight * scale); } }
        public bool IsDesktopAttached
        {
            get { return Native.IsWindow(surface.Handle) && Native.IsWindow(host) && Native.GetParent(surface.Handle) == host; }
        }

        // 화면 구성이 바뀐 직후 바탕화면 창이 늦게 옮겨지면 자식 창인 위젯이 엉뚱한 곳에 남는다. 실제 창 위치가 놓은 자리와 다른지 본다.
        public bool IsMisplaced
        {
            get
            {
                Native.Rect rect;
                return !Bounds.IsEmpty && Native.GetWindowRect(surface.Handle, out rect) && (rect.Left != Bounds.Left || rect.Top != Bounds.Top);
            }
        }

        public void Show() { renderer.Show(); }

        public void SetVisible(bool enabled)
        {
            bool changed = visible != enabled;
            visible = enabled;
            Native.SetWindowPos(surface.Handle, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0004 | 0x0010 | (enabled && wallpaper == null ? 0x0040u : 0x0080u));
            Publish();
            if (changed) RefreshScene(Bounds, true);
        }

        public void Place(int x, int y, WallpaperBackdrop backdrop, double widgetScale = 1)
        {
            var previousBounds = Bounds;
            scale = widgetScale;
            viewport.Width = view.Width * scale;
            viewport.Height = view.Height * scale;
            renderer.Width = viewport.Width;
            renderer.Height = viewport.Height;
            var point = new Native.Point { X = x, Y = y };
            Native.ScreenToClient(host, ref point);
            var visibleArea = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(x, y, PixelWidth, PixelHeight), backdrop.Bounds);
            surface.ClipTo(visibleArea.IsEmpty ? System.Drawing.Rectangle.Empty :
                new System.Drawing.Rectangle(visibleArea.Left - x, visibleArea.Top - y, visibleArea.Width, visibleArea.Height));
            if (!Native.SetWindowPos(surface.Handle, IntPtr.Zero, point.X, point.Y, PixelWidth, PixelHeight,
                0x0010 | (visible && wallpaper == null ? 0x0040u : 0x0080u)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            PrepareBitmap();
            Bounds = new System.Drawing.Rectangle(x, y, PixelWidth, PixelHeight);
            monitor = backdrop.Bounds;
            backdropPixels = wallpaper == null ? backdrop.Pixels(new System.Drawing.Rectangle(x, y, PixelWidth, PixelHeight)) : null;
            ReadForeground();
            Publish();
            RefreshScene(System.Drawing.Rectangle.Union(previousBounds, Bounds), true);
        }

        public void Render()
        {
            if (!IsDesktopAttached) return;
            ReadForeground();
            if (wallpaper != null) { Publish(); return; }
            RefreshScene(Bounds, false);
        }

        private void Publish()
        {
            if (wallpaper == null || foregroundPixels == null) return;
            wallpaper.SetWidget(this, Bounds, (byte[])foregroundPixels.Clone(), shadowBytes, LayerIndex, visible);
            if (visible && FrameRendered != null) FrameRendered(ForegroundSnapshot());
        }

        private void ClearBackground()
        {
            var panel = view as Panel;
            var border = view as Border;
            if (panel != null) panel.Background = null;
            if (border != null) border.Background = null;
        }

        private void ReadForeground()
        {
            ClearBackground();
            PrepareBitmap();
            viewport.Measure(new Size(viewport.Width, viewport.Height));
            viewport.Arrange(new Rect(0, 0, viewport.Width, viewport.Height));
            viewport.UpdateLayout();
            renderBitmap.Clear();
            renderBitmap.Render(viewport);
            renderBitmap.CopyPixels(foregroundPixels, surfaceWidth * 4, 0);
        }

        private static void RefreshScene(System.Drawing.Rectangle dirty, bool backgrounds)
        {
            foreach (var widget in scene)
            {
                if (!widget.visible || widget.backdropPixels == null || !widget.Bounds.IntersectsWith(dirty)) continue;
                if (backgrounds) widget.backgroundDirty = true;
                widget.PaintScene();
            }
            if (backgrounds && SceneBackgroundChanged != null) SceneBackgroundChanged();
        }

        private void PaintScene()
        {
            if (!IsDesktopAttached) return;
            if (backgroundDirty) BakeSceneBackground();
            Buffer.BlockCopy(basePixels, 0, pixelBuffer, 0, pixelBuffer.Length);
            foreach (var other in scene)
            {
                if (!other.visible || other.foregroundPixels == null || other.monitor != monitor) continue;
                var intersection = System.Drawing.Rectangle.Intersect(Bounds, other.Bounds);
                for (int y = intersection.Top; y < intersection.Bottom; y++)
                {
                    int source = ((y - other.Bounds.Top) * other.surfaceWidth + intersection.Left - other.Bounds.Left) * 4;
                    int target = ((y - Bounds.Top) * surfaceWidth + intersection.Left - Bounds.Left) * 4;
                    for (int x = intersection.Left; x < intersection.Right; x++, source += 4, target += 4)
                    {
                        int alpha = other.foregroundPixels[source + 3];
                        if (alpha == 0) continue;
                        // Transparent scene pixels use a colour key on the native desktop window.
                        if (pixelBuffer[target + 3] == 0)
                            for (int color = 0; color < 3; color++) pixelBuffer[target + color] = backdropPixels[target + color];
                        for (int color = 0; color < 3; color++)
                            pixelBuffer[target + color] = (byte)Math.Min(255, other.foregroundPixels[source + color] +
                                (pixelBuffer[target + color] * (255 - alpha) + 127) / 255);
                        pixelBuffer[target + 3] = 255;
                    }
                }
            }
            Marshal.Copy(pixelBuffer, 0, pixels, pixelBuffer.Length);
            surface.PaintBitmap(memoryDc, surfaceWidth, surfaceHeight);
            if (FrameRendered != null) FrameRendered(ForegroundSnapshot());
        }

        private void BakeSceneBackground()
        {
            var mask = new float[surfaceWidth * surfaceHeight];
            foreach (var other in scene)
            {
                if (!other.visible || other.shadowAlpha == null || other.monitor != monitor) continue;
                var intersection = System.Drawing.Rectangle.Intersect(Bounds, other.Bounds);
                for (int y = intersection.Top; y < intersection.Bottom; y++)
                {
                    int source = (y - other.Bounds.Top) * other.surfaceWidth + intersection.Left - other.Bounds.Left;
                    int target = (y - Bounds.Top) * surfaceWidth + intersection.Left - Bounds.Left;
                    for (int x = intersection.Left; x < intersection.Right; x++, source++, target++)
                        mask[target] = Math.Max(mask[target], other.shadowAlpha[source]);
                }
            }
            for (int y = 0; y < surfaceHeight; y++)
                for (int x = 0; x < surfaceWidth; x++)
                {
                    int offset = (y * surfaceWidth + x) * 4;
                    double alpha = mask[offset / 4];
                    double noise = Theme.Dither(Bounds.Left + x, Bounds.Top + y);
                    if (Math.Floor(alpha * 255 + noise) == 0)
                    {
                        basePixels[offset] = basePixels[offset + 2] = 255;
                        basePixels[offset + 1] = basePixels[offset + 3] = 0;
                        continue;
                    }
                    for (int color = 0; color < 3; color++)
                        basePixels[offset + color] = (byte)Math.Floor(backdropPixels[offset + color] * (1 - alpha) + noise);
                    basePixels[offset + 3] = 255;
                }
            backgroundDirty = false;
        }

        public BitmapSource ForegroundSnapshot()
        {
            var snapshot = renderBitmap.Clone();
            snapshot.Freeze();
            return snapshot;
        }

        public BitmapSource Snapshot()
        {
            var combined = (byte[])foregroundPixels.Clone();
            for (int i = 0; i < combined.Length; i += 4)
                combined[i + 3] = (byte)Math.Min(255, combined[i + 3] + Math.Round(shadowAlpha[i / 4] * (255 - combined[i + 3])));
            var snapshot = BitmapSource.Create(surfaceWidth, surfaceHeight, 96 * DpiScale, 96 * DpiScale, PixelFormats.Pbgra32,
                null, combined, surfaceWidth * 4);
            snapshot.Freeze(); return snapshot;
        }

        public static BitmapSource SceneFade(System.Drawing.Rectangle bounds, double dpi)
        {
            var mask = new float[bounds.Width * bounds.Height];
            foreach (var widget in scene)
            {
                if (!widget.visible || widget.shadowAlpha == null || widget.monitor != bounds) continue;
                var intersection = System.Drawing.Rectangle.Intersect(bounds, widget.Bounds);
                for (int y = intersection.Top; y < intersection.Bottom; y++)
                {
                    int source = (y - widget.Bounds.Top) * widget.surfaceWidth + intersection.Left - widget.Bounds.Left;
                    int target = (y - bounds.Top) * bounds.Width + intersection.Left - bounds.Left;
                    for (int x = intersection.Left; x < intersection.Right; x++, source++, target++)
                        mask[target] = Math.Max(mask[target], widget.shadowAlpha[source]);
                }
            }
            var pixels = new byte[mask.Length * 4];
            for (int y = 0; y < bounds.Height; y++)
                for (int x = 0; x < bounds.Width; x++)
                    pixels[(y * bounds.Width + x) * 4 + 3] = (byte)Math.Floor(mask[y * bounds.Width + x] * 255 + Theme.Dither(bounds.Left + x, bounds.Top + y));
            var image = BitmapSource.Create(bounds.Width, bounds.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, pixels, bounds.Width * 4);
            image.Freeze(); return image;
        }

        private void PrepareBitmap()
        {
            if (surfaceWidth == PixelWidth && surfaceHeight == PixelHeight && bitmap != IntPtr.Zero) return;
            ReleaseBitmap();
            surfaceWidth = PixelWidth;
            surfaceHeight = PixelHeight;
            memoryDc = Native.CreateCompatibleDC(IntPtr.Zero);
            var info = new Native.BitmapInfo();
            info.HeaderSize = 40;
            info.Width = surfaceWidth;
            info.Height = -surfaceHeight;
            info.Planes = 1;
            info.BitCount = 32;
            bitmap = Native.CreateDIBSection(memoryDc, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = Native.SelectObject(memoryDc, bitmap);
            pixelBuffer = new byte[surfaceWidth * surfaceHeight * 4];
            foregroundPixels = new byte[pixelBuffer.Length];
            basePixels = new byte[pixelBuffer.Length];
            shadowAlpha = new float[surfaceWidth * surfaceHeight];
            BuildShadow();
            backgroundDirty = true;
            renderBitmap = new RenderTargetBitmap(surfaceWidth, surfaceHeight,
                96 * DpiScale, 96 * DpiScale, PixelFormats.Pbgra32);
        }

        // 위젯 설정: 배경 진하기(0~1)·그라데이션 범위(DIP). 바로 다시 그린다.
        public void SetShadow(double opacity, double range)
        {
            shadowOpacity = Math.Max(0, Math.Min(1, opacity));
            shadowRange = Math.Max(0, range);
            if (shadowAlpha == null) return;
            BuildShadow();
            Publish();
            RefreshScene(Bounds, true);
        }

        // 내용 사각형 안은 진하기 그대로, 바깥은 거리 d 가 range 에 이를 때까지 부드럽게(smoothstep) 사라진다(모서리는 둥글게).
        private void BuildShadow()
        {
            var content = ContentPixels;
            double range = Math.Max(0.5, shadowRange * DpiScale * scale);
            for (int y = 0; y < surfaceHeight; y++)
            {
                double py = y + 0.5, dy = Math.Max(0, Math.Max(content.Top - py, py - content.Bottom));
                for (int x = 0; x < surfaceWidth; x++)
                {
                    double px = x + 0.5, dx = Math.Max(0, Math.Max(content.Left - px, px - content.Right));
                    double t = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / range);
                    shadowAlpha[y * surfaceWidth + x] = (float)(shadowOpacity * t * t * (3 - 2 * t));
                }
            }
            shadowBytes = shadowAlpha.Select(a => (byte)Math.Round(Math.Min(1, Math.Max(0, a)) * 255)).ToArray();
            backgroundDirty = true;
        }

        private void ReleaseBitmap()
        {
            if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero) Native.SelectObject(memoryDc, previousBitmap);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
            previousBitmap = bitmap = memoryDc = pixels = IntPtr.Zero;
            renderBitmap = null;
            foregroundPixels = basePixels = null;
            shadowAlpha = null;
            shadowBytes = null;
        }

        public void Close() { Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            scene.Remove(this);
            if (wallpaper != null) wallpaper.RemoveWidget(this);
            surface.DestroyHandle();
            renderer.Close();
            ReleaseBitmap();
            RefreshScene(Bounds, true);
        }

        private sealed class LayerWindow : Forms.NativeWindow
        {
            private IntPtr drawingDc;
            private int width, height;
            public LayerWindow(string name, IntPtr parent)
            {
                CreateHandle(new Forms.CreateParams
                {
                    Caption = name,
                    ClassName = "STATIC",
                    Parent = parent,
                    Style = 0x40000000,
                    ExStyle = 0x00080000 | 0x20 | 0x80 | 0x08000000,
                    Width = 1, Height = 1
                });
                if (!Native.SetLayeredWindowAttributes(Handle, 0x00FF00FF, 255, 3))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            public void PaintBitmap(IntPtr dc, int bitmapWidth, int bitmapHeight)
            {
                drawingDc = dc;
                width = bitmapWidth;
                height = bitmapHeight;
                Native.InvalidateRect(Handle, IntPtr.Zero, false);
                Native.UpdateWindow(Handle);
            }

            public void ClipTo(System.Drawing.Rectangle bounds)
            {
                var region = Native.CreateRectRgn(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                if (region == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                // Windows owns the region after a successful SetWindowRgn call.
                if (Native.SetWindowRgn(Handle, region, false) != 0) return;
                Native.DeleteObject(region);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; }
                if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; }
                if (message.Msg == 0x000F)
                {
                    Native.Paint paint;
                    var dc = Native.BeginPaint(Handle, out paint);
                    if (drawingDc != IntPtr.Zero) Native.BitBlt(dc, 0, 0, width, height, drawingDc, 0, 0, 0x00CC0020);
                    Native.EndPaint(Handle, ref paint);
                    message.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref message);
            }
        }
    }

    internal static class Native
    {
        internal delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
        private static readonly Dictionary<IntPtr, int> hostStyles = new Dictionary<IntPtr, int>();

        internal static IntPtr FindDesktopHost()
        {
            var progman = FindWindow("Progman", null);
            IntPtr result;
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 2, 1000, out result);
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                var shellView = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView == IntPtr.Zero) return true;
                var worker = FindWindowEx(window, IntPtr.Zero, "WorkerW", null);
                if (worker != IntPtr.Zero)
                {
                    found = worker;
                    var style = GetWindowLong(worker, -16);
                    if ((style & 0x02000000) == 0)
                    {
                        hostStyles[worker] = style;
                        SetWindowLong(worker, -16, style | 0x02000000);
                    }
                }
                else found = FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
                return false;
            }, IntPtr.Zero);
            return found;
        }

        internal static void RestoreDesktopStyles()
        {
            foreach (var saved in hostStyles)
                if (IsWindow(saved.Key) && FindWindowEx(saved.Key, IntPtr.Zero, null, null) == IntPtr.Zero)
                    SetWindowLong(saved.Key, -16, GetWindowLong(saved.Key, -16) & ~0x02000000);
            hostStyles.Clear();
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo
        {
            public uint HeaderSize;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, ImageSize;
            public int XPixelsPerMeter, YPixelsPerMeter;
            public uint ColorsUsed, ColorsImportant, Color;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Paint
        {
            public IntPtr Dc;
            public int Erase, Left, Top, Right, Bottom, Restore, Update;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);
        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr bitmap);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")]
        internal static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
        [DllImport("user32.dll")]
        internal static extern bool UpdateWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern IntPtr BeginPaint(IntPtr window, out Paint paint);
        [DllImport("user32.dll")]
        internal static extern bool EndPaint(IntPtr window, ref Paint paint);
        [DllImport("gdi32.dll")]
        internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);
    }
}
