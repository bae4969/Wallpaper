using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Wallpaper
{
    // Styles/ModernControls.xaml 다크 테마를 창·메뉴 단위로 붙인다.
    // 앱 전체에 병합하면 위젯의 TextBlock에도 기본 스타일이 걸리므로 필요한 곳에만 쓴다.
    internal static class ModernTheme
    {
        private static ResourceDictionary resources;

        public static ResourceDictionary Resources
        {
            get
            {
                return resources ?? (resources = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Wallpaper;component/Styles/ModernControls.xaml", UriKind.Absolute)
                });
            }
        }

        public static Brush Brush(string key) { return (Brush)Resources[key]; }

        public static Style Style(object key) { return (Style)Resources[key]; }

        public static void Apply(FrameworkElement element)
        {
            element.Resources.MergedDictionaries.Add(Resources);
        }

        // 제목 표시줄·테두리를 다크로 맞춘다(Windows 10 1809+ 다크 모드, Windows 11 22H2+ 캡션 색).
        public static void Apply(Window window)
        {
            Apply((FrameworkElement)window);
            window.Background = Brush("Bae.Bg");
            window.Foreground = Brush("Bae.TextPrimary");
            window.FontFamily = (FontFamily)Resources["Bae.Font.Ui"];
            window.FontSize = 13;
            window.UseLayoutRounding = true;
            window.SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(window).Handle;
                int dark = 1, caption = ColorRef("Bae.Bg"), border = ColorRef("Bae.Border");
                DwmSetWindowAttribute(handle, 20, ref dark, 4);
                DwmSetWindowAttribute(handle, 35, ref caption, 4);
                DwmSetWindowAttribute(handle, 34, ref border, 4);
            };
        }

        private static int ColorRef(string key)
        {
            var color = ((SolidColorBrush)Resources[key]).Color;
            return color.R | (color.G << 8) | (color.B << 16);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
