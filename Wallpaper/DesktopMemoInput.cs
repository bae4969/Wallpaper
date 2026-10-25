using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    // 위젯의 클릭 통과는 유지한다. 훅에서는 좌표만 기록하고 실제 판정·편집은 메시지를 넘긴 뒤 한다.
    internal sealed class DesktopMemoInput : IDisposable
    {
        private delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        private readonly MouseProc callback;
        private readonly Dispatcher dispatcher;
        private readonly Action<Native.Point> doubleClick;
        private IntPtr hook;
        private Native.Point previous;
        private uint previousTime;
        private bool firstClick, pending;

        public DesktopMemoInput(Dispatcher ui, Action<Native.Point> onDoubleClick)
        {
            dispatcher = ui;
            doubleClick = onDoubleClick;
            callback = MouseMessage;
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private IntPtr MouseMessage(int code, IntPtr message, IntPtr pointer)
        {
            if (code >= 0 && (message.ToInt64() == 0x0201 || message.ToInt64() == 0x0202))
            {
                var data = Marshal.PtrToStructure<MouseData>(pointer);
                var size = Forms.SystemInformation.DoubleClickSize;
                bool nearby = Math.Abs((long)data.Point.X - previous.X) <= size.Width / 2 &&
                    Math.Abs((long)data.Point.Y - previous.Y) <= size.Height / 2;
                if (message.ToInt64() == 0x0201)
                {
                    pending = firstClick && unchecked(data.Time - previousTime) <= GetDoubleClickTime() && nearby;
                    firstClick = !pending;
                    previous = data.Point;
                    previousTime = data.Time;
                }
                else if (pending)
                {
                    pending = false;
                    if (nearby)
                    {
                        var point = data.Point;
                        dispatcher.BeginInvoke(new Action(() => { if (hook != IntPtr.Zero) doubleClick(point); }));
                    }
                }
                else if (!nearby) firstClick = false;
            }
            return CallNextHookEx(hook, code, message, pointer);
        }

        // UIA는 느려질 수 있어 훅·UI 스레드 밖에서 호출한다. 아이콘 위의 클릭은 메모 편집으로 바꾸지 않는다.
        internal static bool IsEmptyDesktop(Native.Point point)
        {
            var window = WindowFromPoint(point);
            var root = GetAncestor(window, 2);
            string rootClass = ClassName(root), windowClass = ClassName(window);
            if (rootClass != "Progman" && rootClass != "WorkerW") return false;
            if (windowClass == "Progman" || windowClass == "WorkerW" || windowClass == "SHELLDLL_DefView") return true;
            if (windowClass != "SysListView32" || ClassName(Native.GetParent(window)) != "SHELLDLL_DefView") return false;
            try
            {
                var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                return element != null && element.Current.ControlType == ControlType.List &&
                    element.Current.NativeWindowHandle == unchecked((int)window.ToInt64());
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
        }

        internal static IntPtr WindowAt(Native.Point point) { return WindowFromPoint(point); }
        internal static IntPtr Foreground { get { return GetForegroundWindow(); } }

        private static string ClassName(IntPtr window)
        {
            var text = new StringBuilder(128);
            GetClassName(window, text, text.Capacity);
            return text.ToString();
        }

        public void Dispose()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            public Native.Point Point;
            public uint Data, Flags, Time;
            public UIntPtr Extra;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int kind, MouseProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Native.Point point);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    }
}
