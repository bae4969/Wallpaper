using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using PixelFormats = System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;

namespace Wallpaper
{
    internal sealed class WallpaperBackdrop : IDisposable
    {
        private readonly Bitmap image;
        private readonly Rectangle bounds;
        private readonly Color color;
        public Rectangle Bounds { get { return bounds; } }

        private WallpaperBackdrop(Bitmap image, Rectangle bounds, Color color)
        {
            this.image = image;
            this.bounds = bounds;
            this.color = color;
        }

        public static WallpaperBackdrop Load(Forms.Screen screen)
        {
            var bounds = screen.Bounds;
            var desktop = (IDesktopWallpaper)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));
            string filename = null;
            uint background;
            int position;
            var span = Rectangle.Empty;
            try
            {
                uint count;
                desktop.GetMonitorDevicePathCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    string monitor;
                    Rect rect;
                    desktop.GetMonitorDevicePathAt(i, out monitor);
                    // Windows는 분리된 모니터도 목록에 남기므로 연결된 화면만 사용한다.
                    if (desktop.GetMonitorRECT(monitor, out rect) != 0) continue;
                    var area = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    span = span.IsEmpty ? area : Rectangle.Union(span, area);
                    if (area == bounds) desktop.GetWallpaper(monitor, out filename);
                }
                if (filename == null) desktop.GetWallpaper(null, out filename);
                desktop.GetBackgroundColor(out background);
                desktop.GetPosition(out position);
            }
            finally { Marshal.FinalReleaseComObject(desktop); }

            var color = Color.FromArgb((int)(background & 255), (int)((background >> 8) & 255), (int)((background >> 16) & 255));
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(color);
                if (!String.IsNullOrEmpty(filename) && File.Exists(filename))
                {
                    using (var wallpaper = Image.FromFile(filename))
                    {
                        if (position == 1)
                        {
                            using (var texture = new TextureBrush(wallpaper, WrapMode.Tile))
                                graphics.FillRectangle(texture, 0, 0, bounds.Width, bounds.Height);
                        }
                        else
                        {
                            var area = position == 5 ? span : bounds;
                            float width = wallpaper.Width, height = wallpaper.Height;
                            if (position == 2) { width = area.Width; height = area.Height; }
                            else if (position != 0)
                            {
                                float scale = position == 3
                                    ? Math.Min(area.Width / width, area.Height / height)
                                    : Math.Max(area.Width / width, area.Height / height);
                                width *= scale;
                                height *= scale;
                            }
                            float x = area.Left - bounds.Left + (area.Width - width) / 2;
                            float y = area.Top - bounds.Top + (area.Height - height) / 2;
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(wallpaper, x, y, width, height);
                        }
                    }
                }
            }
            return new WallpaperBackdrop(bitmap, bounds, color);
        }

        public byte[] Pixels(Rectangle region)
        {
            using (var crop = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(crop))
                {
                    graphics.Clear(color);
                    graphics.DrawImageUnscaled(image, bounds.Left - region.Left, bounds.Top - region.Top);
                }
                var data = crop.LockBits(new Rectangle(0, 0, crop.Width, crop.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var pixels = new byte[region.Width * region.Height * 4];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    return pixels;
                }
                finally { crop.UnlockBits(data); }
            }
        }

        public void Dispose() { image.Dispose(); }

        public BitmapSource Snapshot()
        {
            var pixels = Pixels(bounds);
            var source = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96,
                PixelFormats.Bgra32, null, pixels, bounds.Width * 4);
            source.Freeze();
            return source;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        // Windows SDK의 IDesktopWallpaper 순서대로 선언한다. 조회 메서드만 호출한다.
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
            void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
            void GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out Rect rect);
            void SetBackgroundColor(uint color);
            void GetBackgroundColor(out uint color);
            void SetPosition(int position);
            void GetPosition(out int position);
        }
    }
}
