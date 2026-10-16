using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    // 연결된 모니터 목록과 모니터 키.
    // 장치 이름(\\.\DISPLAY1)은 원격 데스크톱·드라이버 재설정 뒤에 번호가 바뀔 수 있어, 위젯 자리는 모니터 장치 경로
    // (EDID 기반 \\?\DISPLAY#…, 같은 모니터·같은 포트면 그대로)를 키로 기억한다. 경로를 얻지 못하면 장치 이름을 쓴다.
    internal static class Monitors
    {
        // Forms.Screen.AllScreens 는 화면 변경 알림을 받아야 목록을 새로 읽는다(세션 전환 중에는 놓칠 수 있다).
        // 매번 Windows 에 직접 물어 만든다.
        public static Forms.Screen[] All()
        {
            var rectangles = new List<System.Drawing.Rectangle>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data)
            {
                rectangles.Add(System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                return true;
            }, IntPtr.Zero);
            var screens = rectangles.Select(Forms.Screen.FromRectangle).GroupBy(s => s.DeviceName).Select(g => g.First()).ToArray();
            return screens.Length > 0 ? screens : Forms.Screen.AllScreens;
        }

        public static string Key(string device)
        {
            var info = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
            string first = null;
            for (uint index = 0; EnumDisplayDevices(device, index, ref info, 1); index++)
            {
                if (!String.IsNullOrEmpty(info.DeviceID))
                {
                    if ((info.StateFlags & 1) != 0) return info.DeviceID;   // DISPLAY_DEVICE_ACTIVE
                    if (first == null) first = info.DeviceID;
                }
                info.cb = Marshal.SizeOf(typeof(DisplayDevice));
            }
            return first ?? device;
        }

        // 화면 구성 요약(장치·위치·크기·주 모니터·키). 달라지면 위젯과 배경을 다시 맞춘다.
        public static string Describe(IEnumerable<Forms.Screen> screens)
        {
            return String.Join("; ", screens.OrderBy(s => s.DeviceName).Select(s =>
                s.DeviceName + (s.Primary ? "*" : "") + " " + s.Bounds.X + "," + s.Bounds.Y + " " + s.Bounds.Width + "x" + s.Bounds.Height + " " + Key(s.DeviceName)));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    }
}
