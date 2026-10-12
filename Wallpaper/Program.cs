using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    internal static class Program
    {
        // 예전 이름(DesktopClock)의 데이터 폴더(설정·장면·위젯)가 있으면 새 이름으로 옮긴다.
        private static void MoveOldData()
        {
            var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string previous = Path.Combine(data, "DesktopClock"), current = Path.Combine(data, "Wallpaper");
            if (!Directory.Exists(previous) || Directory.Exists(current)) return;
            try { Directory.Move(previous, current); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            MoveOldData();
            // 인자가 있으면 트레이 앱 대신 장면 작성 도구로 동작한다(--help 참고).
            if (args.Length > 0) return WallpaperCommands.Run(args);
            bool first;
            using (var mutex = new Mutex(true, "Local\\Wallpaper.FlipCalendar", out first))
            {
                if (!first) return 0;
                try
                {
                    var app = new ClockApplication();
                    app.Run();
                }
                catch (Exception error)
                {
                    MessageBox.Show("Wallpaper를 실행하지 못했습니다.\n\n" + error.Message,
                        "Wallpaper", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return 0;
        }
    }

    internal sealed class ClockApplication : Application, ISettingsHost
    {
        // 연결된 모니터마다 위젯 묶음 하나. 키는 장치 이름.
        private readonly Dictionary<string, MonitorRuntime> monitors = new Dictionary<string, MonitorRuntime>();
        private readonly Dictionary<string, Action<System.Windows.Media.Imaging.BitmapSource>> previewHandlers = new Dictionary<string, Action<System.Windows.Media.Imaging.BitmapSource>>();
        private TaskbarIcon tray;
        private ContextMenu menu;
        private MenuItem pauseItem;
        // 트레이에서 일시정지하면 모든 움직이는 배경을 멈춘다(이번 실행에만).
        private bool userPaused;
        private IDisposable sceneWatcher;
        private DispatcherTimer timer;
        private DispatcherTimer animationTimer;
        private DateTime calendarDate;
        private int hostCheck;
        private bool shuttingDown;
        private bool animationRendering;
        private long nextFrameTimestamp;
        private static readonly long FrameTicks = Stopwatch.Frequency / 60;
        private UserPreferences preferences;
        private LayoutEditorWindow editorWindow;
        private SettingsWindow settingsWindow;
        private MonitorRuntime editing;

        public ClockApplication()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            preferences = UserPreferences.Load();
            bool firstRun = preferences.Displays.Count == 0;
            try { SceneLibrary.EnsureDefaults(); }
            catch (Exception error) { SceneLibrary.LogError("기본 장면을 풀지 못했습니다: " + error.Message); }
            calendarDate = DateTime.Now.Date;

            // 트레이 메뉴: Wallpaper / 일시정지·재개 / 설정 / 종료. 디스플레이 편집은 설정 창에서 한다. 설정 창과 같은 다크 테마.
            menu = new ContextMenu();
            ModernTheme.Apply(menu);
            menu.Style = ModernTheme.Style(typeof(ContextMenu));
            menu.Items.Add(new MenuItem { Header = "Wallpaper", IsEnabled = false });
            menu.Items.Add(new Separator());
            pauseItem = MenuEntry("일시정지", TogglePause);
            menu.Items.Add(pauseItem);
            menu.Items.Add(MenuEntry("설정", OpenSettings));
            menu.Items.Add(MenuEntry("종료", Shutdown));
            tray = new TaskbarIcon
            {
                // Id 를 고정하지 않는다: Windows 는 트레이 GUID 를 실행파일 경로에 묶어서, 고정 GUID 로는 exe 를 옮기거나
                // 이름을 바꾸면 등록이 거부돼 아이콘이 사라진다. 기본값(경로에서 만든 GUID)이면 옮겨도 다시 등록된다.
                Icon = MakeTrayIcon(), ToolTipText = "Wallpaper", ContextMenu = menu,
                MenuActivation = PopupActivationMode.LeftOrRightClick
            };
            // 효율 모드(EcoQoS)를 켜면 시계 애니메이션·움직이는 배경이 느려지므로 끈다.
            CreateTrayIcon(1);

            SyncMonitors(firstRun);
            if (firstRun)
            {
                SavePreferences();
                tray.ShowNotification("Wallpaper", "트레이 아이콘 → 설정에서 디스플레이를 골라 위젯과 배경을 편집할 수 있습니다.");
            }
            sceneWatcher = SceneLibrary.Watch(() => Dispatcher.BeginInvoke(new Action(ReloadScenes)));

            // Poll more often than 60Hz so Windows timer rounding does not skip frame deadlines.
            animationTimer = new DispatcherTimer(DispatcherPriority.Render);
            animationTimer.Interval = TimeSpan.FromMilliseconds(8);
            animationTimer.Tick += RenderAnimation;
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(33);
            timer.Tick += Tick;
            timer.Start();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
        }

        // 트레이 등록(Shell_NotifyIcon). 로그인·탐색기 재시작 직후에는 거부될 수 있어 3초 뒤 한 번 더 시도하고 실패를 기록한다.
        private void CreateTrayIcon(int attempt)
        {
            try { tray.ForceCreate(false); }
            catch (Exception error)
            {
                SceneLibrary.LogError("트레이 아이콘 등록 실패(" + attempt + "회차): " + error.Message);
                if (attempt >= 2) return;
                var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                retry.Tick += delegate { retry.Stop(); CreateTrayIcon(attempt + 1); };
                retry.Start();
            }
        }

        internal static string DisplayName(string device)
        {
            return "디스플레이 " + new string(device.Reverse().TakeWhile(Char.IsDigit).Reverse().ToArray());
        }

        private void SavePreferences()
        {
            try { preferences.Save(); }
            catch (Exception error) { MessageBox.Show("설정을 저장하지 못했습니다.\n" + error.Message, "Wallpaper"); }
        }

        // 장면 오류는 errors.log 에 남기고 트레이 알림으로 알린다. 그 화면은 Windows 배경화면으로 표시한다.
        private void ReportSceneError(string where, Exception error)
        {
            ReportError("Wallpaper 장면 오류", where, error);
        }

        private void ReportError(string title, string where, Exception error)
        {
            var message = where + ": " + error.Message;
            SceneLibrary.LogError(message);
            if (tray != null)
                tray.ShowNotification(title, message.Length > 240 ? message.Substring(0, 240) + "…" : message, NotificationIcon.Warning);
        }

        private IEnumerable<MonitorRuntime> OrderedMonitors
        {
            get { return monitors.Values.OrderByDescending(m => m.Screen.Primary).ThenBy(m => m.Screen.DeviceName); }
        }

        // 연결된 화면마다 위젯 묶음을 만들고, 끊긴 화면·배경이 바뀐 화면은 없앤다.
        private void SyncMonitors(bool firstRun = false)
        {
            var screens = Forms.Screen.AllScreens;
            foreach (var monitor in monitors.Values.ToArray())
            {
                var screen = screens.FirstOrDefault(s => s.DeviceName == monitor.Screen.DeviceName);
                if (screen == null || preferences.Display(screen.DeviceName) != monitor.Layout ||
                    monitor.Background != monitor.Layout.Background ||
                    (monitor.Scene != null && (screen.Bounds != monitor.Screen.Bounds || monitor.RenderScale != preferences.RenderScale)))
                {
                    DestroyMonitor(monitor);
                    continue;
                }
                monitor.Screen = screen;
                RefreshBackdrop(monitor);
            }
            foreach (var screen in screens)
            {
                if (monitors.ContainsKey(screen.DeviceName)) continue;
                var layout = preferences.Display(screen.DeviceName);
                if (layout == null)
                {
                    // 처음 보는 화면은 위젯 없이 시작한다. 첫 실행이면 주 화면에만 시계·달력을 켠다.
                    bool start = firstRun && screen.Primary;
                    layout = new DisplayLayout { Device = screen.DeviceName, ClockEnabled = start, CalendarEnabled = start };
                    preferences.Displays.Add(layout);
                }
                CreateMonitor(screen, layout);
            }
        }

        private void CreateMonitor(Forms.Screen screen, DisplayLayout layout)
        {
            var monitor = new MonitorRuntime { Screen = screen, Layout = layout, Backdrop = WallpaperBackdrop.Load(screen), Background = layout.Background };
            monitors.Add(screen.DeviceName, monitor);
            if (!String.IsNullOrEmpty(layout.Background))
            {
                // 움직이는 배경을 만들 수 없으면 Windows 배경화면으로 표시한다.
                try
                {
                    var definition = SceneLibrary.Scene(layout.Background);
                    if (definition == null) throw new FileNotFoundException("장면 파일이 없습니다: scenes\\" + layout.Background + ".json");
                    monitor.Scene = new WallpaperScene(screen, definition, preferences.FrameLimit, preferences.RenderScale, preferences.EffectQuality);
                    monitor.SceneDefinition = definition;
                    monitor.RenderScale = preferences.RenderScale;
                    monitor.Scene.Paused = userPaused;
                }
                catch (Exception error) { ReportSceneError(DisplayName(screen.DeviceName), error); }
            }
            monitor.ClockView = new FlipClock();
            monitor.CalendarView = new CalendarView();
            monitor.Clock = new DesktopWidget("Wallpaper.Clock", monitor.ClockView, monitor.Scene);
            monitor.Calendar = new DesktopWidget("Wallpaper.Calendar", monitor.CalendarView, monitor.Scene);
            monitor.Widgets.Add("clock", monitor.Clock); monitor.Widgets.Add("calendar", monitor.Calendar);
            foreach (var entry in monitor.Widgets)
            {
                entry.Value.Show();
                entry.Value.SetVisible(layout.IsEnabled(entry.Key));
            }
            var now = DateTime.Now;
            BuiltInWidgetOptions.Apply(monitor.ClockView, layout.ClockOptions);
            BuiltInWidgetOptions.ApplyShadow(monitor.Clock, layout.ClockOptions);
            BuiltInWidgetOptions.ApplyShadow(monitor.Calendar, layout.CalendarOptions);
            monitor.ClockView.UpdateTime(now, false);
            monitor.CalendarView.UpdateDate(now);
            BuiltInWidgetOptions.Apply(monitor.CalendarView, layout.CalendarOptions);
            PlaceWidgets(monitor);
            foreach (var settings in layout.Hardware) CreateHardware(monitor, settings);
            foreach (var settings in layout.Scripts) CreateScript(monitor, settings);
        }

        private void DestroyMonitor(MonitorRuntime monitor)
        {
            if (editing == monitor && editorWindow != null) editorWindow.Close();
            foreach (var id in monitor.Hardware.Keys.ToArray()) DestroyHardware(monitor, id);
            foreach (var id in monitor.Scripts.Keys.ToArray()) DestroyScript(monitor, id);
            foreach (var widget in monitor.Widgets.Values) widget.Dispose();
            monitor.Widgets.Clear();
            monitor.Backdrop.Dispose();
            if (monitor.Scene != null) monitor.Scene.Dispose();
            monitors.Remove(monitor.Screen.DeviceName);
        }

        private MenuItem MenuEntry(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += delegate { Dispatcher.BeginInvoke(action); };
            return item;
        }

        private void TogglePause()
        {
            userPaused = !userPaused;
            pauseItem.Header = userPaused ? "재개" : "일시정지";
            tray.ToolTipText = userPaused ? "Wallpaper (일시정지됨)" : "Wallpaper";
            UpdatePause();
        }

        // 일시정지·배터리·가려짐 중 하나라도 해당하면 그 화면의 배경을 멈춘다.
        private void UpdatePause()
        {
            bool battery = preferences.PauseOnBattery && Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline;
            foreach (var monitor in monitors.Values)
                if (monitor.Scene != null)
                    monitor.Scene.Paused = userPaused || battery || (preferences.PauseWhenCovered && WallpaperScene.IsCovered(monitor.Screen.Bounds));
        }

        private void OpenSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow(preferences, this);
                settingsWindow.Closed += delegate { settingsWindow = null; };
                settingsWindow.Show();
            }
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
        }

        // ---- 설정 창이 요청하는 일(ISettingsHost) ----

        IEnumerable<SettingsWindow.DisplayEntry> ISettingsHost.Displays() { return DisplayEntries(); }
        IList<SceneDefinition> ISettingsHost.Scenes() { return SceneLibrary.Scenes(); }
        IList<WidgetScript> ISettingsHost.ScriptTypes() { return WidgetScript.All(); }
        void ISettingsHost.ApplyPerformance() { ApplyPerformance(); }
        void ISettingsHost.ChangeBackground(string device, string background) { ChangeBackground(device, background); }

        string ISettingsHost.Background(string device)
        {
            var layout = preferences.Display(device);
            return layout == null ? "" : layout.Background;
        }

        void ISettingsHost.OpenLayoutEditor(string device)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenEditor(device)));
        }

        IEnumerable<SettingsWindow.WidgetEntry> ISettingsHost.Widgets(string device)
        {
            MonitorRuntime monitor;
            return monitors.TryGetValue(device, out monitor) ? WidgetEntries(monitor) : Enumerable.Empty<SettingsWindow.WidgetEntry>();
        }

        // 화면의 위젯 목록(설정 창 목록·편집 창 켜고 끄기 메뉴): 시계·달력 → 하드웨어 → 스크립트 순
        private IEnumerable<SettingsWindow.WidgetEntry> WidgetEntries(MonitorRuntime monitor)
        {
            var layout = monitor.Layout;
            yield return new SettingsWindow.WidgetEntry { Id = "clock", Name = "시계", Kind = "builtin", Enabled = layout.ClockEnabled };
            yield return new SettingsWindow.WidgetEntry { Id = "calendar", Name = "달력", Kind = "builtin", Enabled = layout.CalendarEnabled };
            foreach (var hardware in layout.Hardware.ToArray())
                yield return new SettingsWindow.WidgetEntry { Id = hardware.Id, Name = hardware.Name, Kind = "hardware", Enabled = hardware.Enabled };
            foreach (var script in layout.Scripts.ToArray())
                yield return new SettingsWindow.WidgetEntry { Id = script.Id, Name = ScriptName(monitor, script), Kind = "script", Enabled = script.Enabled };
        }

        void ISettingsHost.EditWidgetOptions(string device, string id)
        {
            MonitorRuntime monitor;
            if (monitors.TryGetValue(device, out monitor)) EditOptions(monitor, id);
        }

        void ISettingsHost.EditHardware(string device, string id)
        {
            MonitorRuntime monitor;
            if (monitors.TryGetValue(device, out monitor)) EditHardware(monitor, id);
        }

        void ISettingsHost.AddScript(string device, string type)
        {
            MonitorRuntime monitor;
            if (monitors.TryGetValue(device, out monitor)) AddScript(monitor, type);
        }

        void ISettingsHost.RemoveWidget(string device, string id)
        {
            MonitorRuntime monitor;
            if (!monitors.TryGetValue(device, out monitor)) return;
            if (monitor.Layout.Hardware.Any(w => w.Id == id)) RemoveHardware(monitor, id);
            else RemoveScript(monitor, id);
        }

        IList<string> ISettingsHost.SceneUsers(string scene)
        {
            return preferences.Displays.Where(d => d.Background == scene).Select(d => DisplayName(d.Device)).OrderBy(n => n).ToList();
        }

        // 장면 삭제: 그 장면을 쓰던 화면은 Windows 배경화면으로 돌리고 저장한 뒤 파일을 지운다.
        void ISettingsHost.DeleteScene(string scene)
        {
            foreach (var layout in preferences.Displays.Where(d => d.Background == scene)) layout.Background = "";
            SavePreferences();
            SyncMonitors();
            SceneLibrary.DeleteScene(scene);
        }

        // 위젯 대화 상자의 주인: 편집 창이 열려 있으면 그것, 아니면 설정 창
        private Window DialogOwner { get { return (Window)editorWindow ?? settingsWindow; } }

        // 설정 창의 디스플레이 목록: 이름·해상도·배경·켜진 위젯 수
        private IEnumerable<SettingsWindow.DisplayEntry> DisplayEntries()
        {
            foreach (var monitor in OrderedMonitors)
            {
                string background = "Windows 배경화면";
                if (!String.IsNullOrEmpty(monitor.Background))
                {
                    SceneDefinition scene = null;
                    try { scene = SceneLibrary.Scene(monitor.Background); }
                    catch (Exception) { }
                    background = (scene != null ? scene.Name : monitor.Background) + (monitor.Scene == null ? " (오류 — Windows 배경화면으로 표시 중)" : "");
                }
                yield return new SettingsWindow.DisplayEntry
                {
                    Device = monitor.Screen.DeviceName, Name = DisplayName(monitor.Screen.DeviceName), Primary = monitor.Screen.Primary,
                    Width = monitor.Screen.Bounds.Width, Height = monitor.Screen.Bounds.Height, Background = background,
                    Widgets = monitor.Widgets.Keys.Count(monitor.Layout.IsEnabled)
                };
            }
        }

        // 성능 설정 저장·적용. 프레임 제한·효과 품질은 그리는 중에 바꾸고, 렌더 해상도가 바뀌면 장면을 다시 만든다.
        private void ApplyPerformance()
        {
            preferences.Save();
            foreach (var monitor in monitors.Values.Where(m => m.Scene != null))
            {
                monitor.Scene.FrameLimit = preferences.FrameLimit;
                monitor.Scene.Quality = preferences.EffectQuality;
            }
            if (monitors.Values.Any(m => m.Scene != null && m.RenderScale != preferences.RenderScale)) SyncMonitors();
        }

        // 설정 창에서 배경을 바꾸면 저장하고 그 화면을 다시 만든다.
        private void ChangeBackground(string device, string background)
        {
            var layout = preferences.Display(device);
            if (layout == null || layout.Background == (background ?? "")) return;
            var previous = layout.Background;
            layout.Background = background ?? "";
            try { preferences.Save(); }
            catch (Exception error)
            {
                layout.Background = previous;
                MessageBox.Show("배경을 저장하지 못했습니다.\n" + error.Message, "Wallpaper");
                return;
            }
            SyncMonitors();
        }

        // Wallpapers 폴더가 바뀌면 움직이는 배경을 쓰는 화면을 다시 만든다(장면 작성 중 바로 확인).
        private void ReloadScenes()
        {
            if (shuttingDown) return;
            Func<MonitorRuntime, bool> scripted = m => !String.IsNullOrEmpty(m.Background) || m.Layout.Scripts.Count > 0;
            var reopen = editing != null && scripted(editing) ? editing.Screen.DeviceName : null;
            foreach (var monitor in monitors.Values.Where(scripted).ToArray()) DestroyMonitor(monitor);
            SyncMonitors();
            if (settingsWindow != null) settingsWindow.RefreshDisplays();
            if (reopen != null && editorWindow == null) OpenEditor(reopen);
        }

        private void OpenEditor(string device)
        {
            MonitorRuntime monitor;
            if (!monitors.TryGetValue(device, out monitor)) return;
            if (editorWindow != null)
            {
                if (editing == monitor) { editorWindow.Activate(); return; }
                editorWindow.Close();
            }
            var window = new LayoutEditorWindow(monitor.Screen, EditorBackground(monitor), monitor.Widgets, monitor.Layout.IsEnabled,
                () => WidgetEntries(monitor).Select(entry => new KeyValuePair<string, string>(entry.Id, entry.Name)).ToArray(),
                (id, enabled) => SetWidgetEnabled(monitor, id, enabled), (id, placement) => ApplyLayout(monitor, id, placement));
            window.Title = "Wallpaper · " + DisplayName(device) + " 편집";
            editorWindow = window;
            editing = monitor;
            foreach (var id in monitor.Widgets.Keys) AttachPreview(id);
            window.Closed += delegate
            {
                foreach (var id in previewHandlers.Keys.ToArray()) DetachPreview(id);
                if (editorWindow == window) { editorWindow = null; editing = null; }
                SavePreferences();
                // 편집 창을 여는 동안 숨겼던 설정 창을 다시 보인다.
                if (settingsWindow != null && editorWindow == null)
                {
                    settingsWindow.Show();
                    settingsWindow.RefreshDisplays();
                    settingsWindow.Activate();
                }
            };
            if (settingsWindow != null) settingsWindow.Hide();
            window.Show();
            window.Activate();
        }

        // 편집 창 배경: 움직이는 배경이면 지금 시점의 장면 한 프레임, 아니면(또는 그리지 못하면) Windows 배경화면
        private System.Windows.Media.ImageSource EditorBackground(MonitorRuntime monitor)
        {
            if (monitor.Scene != null && monitor.SceneDefinition != null)
            {
                try
                {
                    var bounds = monitor.Screen.Bounds;
                    return WallpaperScene.Still(monitor.SceneDefinition, bounds.Width, bounds.Height, monitor.Scene.Time, preferences.EffectQuality);
                }
                catch (Exception error) { SceneLibrary.LogError("편집 창 배경 미리보기 실패: " + error.Message); }
            }
            return monitor.Backdrop.Snapshot();
        }

        private void AttachPreview(string id)
        {
            if (editorWindow == null || previewHandlers.ContainsKey(id)) return;
            var window = editorWindow;
            Action<System.Windows.Media.Imaging.BitmapSource> handler = image =>
            {
                if (editorWindow == window) window.UpdateWidget(id, image);
            };
            previewHandlers.Add(id, handler);
            editing.Widgets[id].FrameRendered += handler;
        }

        private void DetachPreview(string id)
        {
            Action<System.Windows.Media.Imaging.BitmapSource> handler;
            DesktopWidget widget;
            if (!previewHandlers.TryGetValue(id, out handler)) return;
            if (editing != null && editing.Widgets.TryGetValue(id, out widget)) widget.FrameRendered -= handler;
            previewHandlers.Remove(id);
        }

        private void EditHardware(MonitorRuntime monitor, string id)
        {
            var layout = monitor.Layout;
            var previous = id == null ? null : layout.Hardware.First(w => w.Id == id);
            var options = previous ?? new HardwareWidgetSettings
            {
                Placement = NewHardwarePlacement(monitor)
            };
            var dialog = new HardwareSettingsWindow(options) { Owner = DialogOwner };
            if (dialog.ShowDialog() != true) return;
            int index = previous == null ? layout.Hardware.Count : layout.Hardware.IndexOf(previous);
            if (previous == null) layout.Hardware.Add(dialog.Result);
            else layout.Hardware[index] = dialog.Result;
            try { preferences.Save(); }
            catch (Exception error)
            {
                if (previous == null) layout.Hardware.Remove(dialog.Result); else layout.Hardware[index] = previous;
                MessageBox.Show("위젯 설정을 저장하지 못했습니다.\n" + error.Message, "하드웨어 위젯"); return;
            }
            if (previous != null) DestroyHardware(monitor, id);
            CreateHardware(monitor, dialog.Result);
            if (editorWindow != null && editing == monitor) editorWindow.RefreshWidget(dialog.Result.Id);
        }

        private WidgetPlacement NewHardwarePlacement(MonitorRuntime monitor)
        {
            return NewPlacement(monitor, 224, 312);
        }

        // 기존 위젯과 겹치지 않는 첫 자리(내용 크기 width×height, DIP 단위)
        private WidgetPlacement NewPlacement(MonitorRuntime monitor, double contentWidth, double contentHeight)
        {
            var screen = monitor.Screen.Bounds;
            double dpi = monitor.Clock.DpiScale;
            int width = (int)Math.Round(contentWidth * dpi), height = (int)Math.Round(contentHeight * dpi);
            int gap = (int)Math.Round(16 * dpi), step = Math.Max(1, (int)Math.Round(32 * dpi));
            var occupied = monitor.Widgets.Where(w => monitor.Layout.IsEnabled(w.Key)).Select(w =>
            {
                var bounds = w.Value.ContentBounds; bounds.Inflate(gap, gap); return bounds;
            }).ToArray();
            for (int y = (int)Math.Round(90 * dpi); y + height <= screen.Height - gap; y += step)
                for (int x = (int)Math.Round(24 * dpi); x + width <= screen.Width - gap; x += step)
                {
                    var candidate = new Rectangle(screen.Left + x, screen.Top + y, width, height);
                    if (occupied.Any(bounds => bounds.IntersectsWith(candidate))) continue;
                    return new WidgetPlacement { X = x / (double)screen.Width, Y = y / (double)screen.Height, Scale = 1, ContentAnchored = true };
                }
            return new WidgetPlacement { X = 0.08, Y = 0.12, Scale = 1, ContentAnchored = true };
        }

        private void RemoveHardware(MonitorRuntime monitor, string id)
        {
            var layout = monitor.Layout;
            var settings = layout.Hardware.First(w => w.Id == id);
            int index = layout.Hardware.IndexOf(settings);
            layout.Hardware.Remove(settings);
            try { preferences.Save(); }
            catch (Exception error)
            {
                layout.Hardware.Insert(index, settings);
                MessageBox.Show("위젯 제거를 저장하지 못했습니다.\n" + error.Message, "하드웨어 위젯"); return;
            }
            DestroyHardware(monitor, id);
        }

        private void DestroyHardware(MonitorRuntime monitor, string id)
        {
            var runtime = monitor.Hardware[id];
            monitor.Hardware.Remove(id);
            if (runtime.Feed != null) runtime.Feed.Dispose();
            if (editing == monitor)
            {
                if (editorWindow != null) editorWindow.RemoveWidget(id);
                DetachPreview(id);
            }
            monitor.Widgets.Remove(id);
            runtime.Widget.Dispose();
        }

        private void CreateHardware(MonitorRuntime monitor, HardwareWidgetSettings settings)
        {
            var runtime = new HardwareRuntime { Settings = settings, View = new HardwareView(settings) };
            runtime.Widget = new DesktopWidget(settings.Id == "gpu" ? "Wallpaper.GPU" : "Wallpaper." + settings.Id, runtime.View, monitor.Scene);
            monitor.Hardware.Add(settings.Id, runtime); monitor.Widgets.Add(settings.Id, runtime.Widget);
            runtime.Widget.Show(); runtime.Widget.SetVisible(settings.Enabled);
            BuiltInWidgetOptions.ApplyShadow(runtime.Widget, settings.Options);
            PlaceWidget(monitor, runtime.Widget, settings.Placement, settings.Id);
            if (editing == monitor) AttachPreview(settings.Id);
            if (!settings.Enabled) return;
            runtime.Feed = new HardwareFeed(settings);
            runtime.Feed.Updated += snapshot =>
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    HardwareRuntime current;
                    if (shuttingDown || !monitor.Hardware.TryGetValue(settings.Id, out current) || current != runtime) return;
                    bool resized = runtime.View.Update(snapshot);
                    if (resized) PlaceWidget(monitor, runtime.Widget, runtime.Settings.Placement, settings.Id);
                    else runtime.Widget.Render();
                    if (resized && editorWindow != null && editing == monitor) editorWindow.SyncWidget(settings.Id);
                }));
            };
            runtime.Feed.Start();
        }

        // ---- 스크립트 위젯 (Wallpapers\widgets) ----

        private string ScriptName(MonitorRuntime monitor, ScriptWidgetSettings settings)
        {
            ScriptRuntime runtime;
            return monitor.Scripts.TryGetValue(settings.Id, out runtime) ? runtime.Definition.Name : settings.Type + " (오류)";
        }

        private void CreateScript(MonitorRuntime monitor, ScriptWidgetSettings settings)
        {
            var data = new Scripting.WidgetData();
            WidgetScript definition;
            System.Windows.Controls.Border view;
            try
            {
                definition = WidgetScript.Load(settings.Type);
                data.SetParams(definition.Parameters, settings.Options);
                data.Update(DateTime.Now, LocalMetrics.Latest);
                view = definition.Build(data);
            }
            catch (Exception error)
            {
                ReportError("Wallpaper 위젯 오류", DisplayName(monitor.Screen.DeviceName) + " · " + settings.Type, error);
                return;
            }
            var runtime = new ScriptRuntime { Settings = settings, Definition = definition, Data = data, Widget = new DesktopWidget("Wallpaper." + settings.Id, view, monitor.Scene) };
            if (definition.Metrics) { LocalMetrics.Acquire(); runtime.UsesMetrics = true; }
            monitor.Scripts.Add(settings.Id, runtime);
            monitor.Widgets.Add(settings.Id, runtime.Widget);
            runtime.Widget.Show();
            runtime.Widget.SetVisible(settings.Enabled);
            BuiltInWidgetOptions.ApplyShadow(runtime.Widget, settings.Options);
            PlaceWidget(monitor, runtime.Widget, settings.Placement, settings.Id);
            if (editing == monitor) AttachPreview(settings.Id);
        }

        private void DestroyScript(MonitorRuntime monitor, string id)
        {
            var runtime = monitor.Scripts[id];
            monitor.Scripts.Remove(id);
            if (runtime.UsesMetrics) LocalMetrics.Release();
            if (editing == monitor)
            {
                if (editorWindow != null) editorWindow.RemoveWidget(id);
                DetachPreview(id);
            }
            monitor.Widgets.Remove(id);
            runtime.Widget.Dispose();
        }

        private void AddScript(MonitorRuntime monitor, string type)
        {
            WidgetScript definition;
            try { definition = WidgetScript.Load(type); }
            catch (Exception error)
            {
                MessageBox.Show("위젯을 읽지 못했습니다.\n" + error.Message, "Wallpaper");
                return;
            }
            var settings = new ScriptWidgetSettings { Type = type, Placement = NewPlacement(monitor, definition.Width, definition.Height) };
            monitor.Layout.Scripts.Add(settings);
            SavePreferences();
            CreateScript(monitor, settings);
            if (editorWindow != null && editing == monitor) editorWindow.RefreshWidget(settings.Id);
        }

        private void RemoveScript(MonitorRuntime monitor, string id)
        {
            monitor.Layout.Scripts.RemoveAll(script => script.Id == id);
            SavePreferences();
            if (monitor.Scripts.ContainsKey(id)) DestroyScript(monitor, id);
        }

        // 위젯 설정 창(시계·달력·스크립트 위젯). 바꾸면 바로 반영하고 닫을 때 저장한다.
        private void EditOptions(MonitorRuntime monitor, string id)
        {
            ScriptRuntime runtime;
            monitor.Scripts.TryGetValue(id, out runtime);
            var values = monitor.Layout.Options(id);
            if (values == null || !monitor.Widgets.ContainsKey(id)) return;
            // 위젯 고유 값 + 공통(배경 진하기·그라데이션 범위). 하드웨어 위젯은 공통 값만(연결은 자기 설정 창).
            var own = BuiltInWidgetOptions.For(id) ?? (runtime != null ? runtime.Definition.Parameters.ToArray() : new WidgetParameter[0]);
            var parameters = own.Concat(BuiltInWidgetOptions.Common).ToArray();
            var hardware = monitor.Layout.Hardware.FirstOrDefault(w => w.Id == id);
            string title = id == "clock" ? "시계" : id == "calendar" ? "달력" : runtime != null ? runtime.Definition.Name : hardware != null ? hardware.Name : id;
            var window = new WidgetOptionsWindow(title, parameters, values, () => ApplyOptions(monitor, id)) { Owner = DialogOwner };
            window.ShowDialog();
            SavePreferences();
        }

        private void ApplyOptions(MonitorRuntime monitor, string id)
        {
            var layout = monitor.Layout;
            if (id == "clock")
            {
                BuiltInWidgetOptions.Apply(monitor.ClockView, layout.ClockOptions);
                PlaceWidget(monitor, monitor.Clock, layout.Clock, id);
            }
            else if (id == "calendar")
            {
                BuiltInWidgetOptions.Apply(monitor.CalendarView, layout.CalendarOptions);
                PlaceWidget(monitor, monitor.Calendar, layout.Calendar, id);
            }
            else
            {
                ScriptRuntime runtime;
                if (monitor.Scripts.TryGetValue(id, out runtime))
                {
                    runtime.Data.SetParams(runtime.Definition.Parameters, runtime.Settings.Options);
                    runtime.Widget.Render();
                }
            }
            BuiltInWidgetOptions.ApplyShadow(monitor.Widgets[id], layout.Options(id));
            if (editorWindow != null && editing == monitor && layout.IsEnabled(id)) editorWindow.RefreshWidget(id);
        }

        // 위젯마다 정한 간격(interval 초)의 시각 경계에서 다시 그린다(초 단위면 초가 바뀔 때).
        private void RenderScripts(DateTime now)
        {
            foreach (var monitor in monitors.Values)
                foreach (var runtime in monitor.Scripts.Values)
                {
                    if (!runtime.Settings.Enabled || now < runtime.Next) continue;
                    long interval = Math.Max(1, (long)(runtime.Definition.Interval * TimeSpan.TicksPerSecond));
                    runtime.Next = new DateTime((now.Ticks / interval + 1) * interval);
                    runtime.Data.Update(now, LocalMetrics.Latest);
                    runtime.Widget.Render();
                }
        }

        private sealed class ScriptRuntime
        {
            public ScriptWidgetSettings Settings;
            public WidgetScript Definition;
            public Scripting.WidgetData Data;
            public DesktopWidget Widget;
            public bool UsesMetrics;
            public DateTime Next;
        }

        private sealed class HardwareRuntime
        {
            public HardwareWidgetSettings Settings;
            public HardwareView View;
            public DesktopWidget Widget;
            public HardwareFeed Feed;
        }

        private sealed class MonitorRuntime
        {
            public Forms.Screen Screen;
            public DisplayLayout Layout;
            public WallpaperBackdrop Backdrop;
            public WallpaperScene Scene;
            public SceneDefinition SceneDefinition;
            public string Background;
            public double RenderScale = 1;
            public FlipClock ClockView;
            public CalendarView CalendarView;
            public DesktopWidget Clock;
            public DesktopWidget Calendar;
            public readonly Dictionary<string, DesktopWidget> Widgets = new Dictionary<string, DesktopWidget>();
            public readonly Dictionary<string, HardwareRuntime> Hardware = new Dictionary<string, HardwareRuntime>();
            public readonly Dictionary<string, ScriptRuntime> Scripts = new Dictionary<string, ScriptRuntime>();
        }

        private void ApplyLayout(MonitorRuntime monitor, string widget, WidgetPlacement placement)
        {
            monitor.Layout.SetPlacement(widget, placement);
            PlaceWidget(monitor, monitor.Widgets[widget], placement, widget);
        }

        // 편집 창의 위젯 켜고 끄기. 바로 저장한다.
        private void SetWidgetEnabled(MonitorRuntime monitor, string id, bool enabled)
        {
            if (!monitor.Widgets.ContainsKey(id) || monitor.Layout.IsEnabled(id) == enabled) return;
            monitor.Layout.SetEnabled(id, enabled);
            SavePreferences();
            var hardware = monitor.Layout.Hardware.FirstOrDefault(w => w.Id == id);
            if (hardware != null)
            {
                // 하드웨어 위젯은 꺼져 있는 동안 수집을 멈추므로 새로 만든다(켜면 수집 시작).
                DestroyHardware(monitor, id);
                CreateHardware(monitor, hardware);
                return;
            }
            monitor.Widgets[id].SetVisible(enabled);
            if (id == "clock")
            {
                monitor.ClockView.UpdateTime(DateTime.Now, false);
                if (enabled && monitor.ClockView.UpdateAnimation()) StartAnimation();
            }
            if (enabled) PlaceWidget(monitor, monitor.Widgets[id], monitor.Layout.Placement(id), id);
        }

        private void PlaceWidgets(MonitorRuntime monitor)
        {
            foreach (var entry in monitor.Widgets) PlaceWidget(monitor, entry.Value, monitor.Layout.Placement(entry.Key), entry.Key);
        }

        private void PlaceWidget(MonitorRuntime monitor, DesktopWidget widget, WidgetPlacement placement, string id)
        {
            var screen = monitor.Screen;
            var area = screen.WorkingArea;
            var bounds = screen.Bounds;
            var dpi = monitor.Clock.DpiScale;
            var margin = (int)Math.Round(36 * dpi);
            double scale = placement == null ? 1 : placement.Scale;
            scale = Math.Min(scale, Math.Min(bounds.Width / widget.BaseContentPixelWidth, bounds.Height / widget.BaseContentPixelHeight));
            int width = (int)Math.Round(widget.BasePixelWidth * scale);
            int height = (int)Math.Round(widget.BasePixelHeight * scale);
            var content = widget.ContentArea;
            int insetX = (int)Math.Round(content.Left * widget.DpiScale * scale);
            int insetY = (int)Math.Round(content.Top * widget.DpiScale * scale);
            int contentWidth = (int)Math.Round(content.Right * widget.DpiScale * scale) - insetX;
            int contentHeight = (int)Math.Round(content.Bottom * widget.DpiScale * scale) - insetY;
            int x = placement != null ? bounds.Left + (int)Math.Round(placement.X * bounds.Width)
                : id == "clock" ? bounds.Left + (bounds.Width - width) / 2
                : area.Left + margin;
            int y = placement != null ? bounds.Top + (int)Math.Round(placement.Y * bounds.Height)
                : id == "clock" ? bounds.Top + (bounds.Height - height) / 2
                : id == "calendar" ? area.Bottom - margin - height : area.Top + margin;
            if (placement != null && placement.ContentAnchored) { x -= insetX; y -= insetY; }
            x = Math.Max(bounds.Left - insetX, Math.Min(bounds.Right - contentWidth - insetX, x));
            y = Math.Max(bounds.Top - insetY, Math.Min(bounds.Bottom - contentHeight - insetY, y));
            widget.Place(x, y, monitor.Backdrop, scale);
        }

        private void Tick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var monitor in monitors.Values)
                if (monitor.ClockView.UpdateTime(now, monitor.Layout.ClockEnabled) && monitor.Layout.ClockEnabled) StartAnimation();
            if (now.Date != calendarDate)
            {
                calendarDate = now.Date;
                foreach (var monitor in monitors.Values)
                {
                    monitor.CalendarView.UpdateDate(now);
                    if (monitor.Layout.CalendarEnabled) monitor.Calendar.Render();
                }
            }
            RenderScripts(now);
            if (hostCheck % 15 == 0) UpdatePause();
            if (++hostCheck < 60) return;
            hostCheck = 0;
            foreach (var monitor in monitors.Values) if (monitor.Scene != null && monitor.Scene.IsAlive) monitor.Scene.KeepUnderIcons();
            if (monitors.Values.Any(monitor => monitor.Widgets.Values.Any(widget => !widget.IsDesktopAttached) ||
                (monitor.Scene != null && !monitor.Scene.IsAlive)))
            {
                // Explorer가 다시 시작되면 기존 자식 창도 사라지므로 새 창을 만든다.
                foreach (var monitor in monitors.Values.ToArray()) DestroyMonitor(monitor);
                SyncMonitors();
            }
        }

        private void StartAnimation()
        {
            if (animationRendering) return;
            nextFrameTimestamp = Stopwatch.GetTimestamp();
            animationTimer.Start();
            animationRendering = true;
        }

        private void RenderAnimation(object sender, EventArgs e)
        {
            long timestamp = Stopwatch.GetTimestamp();
            if (timestamp < nextFrameTimestamp) return;
            nextFrameTimestamp += ((timestamp - nextFrameTimestamp) / FrameTicks + 1) * FrameTicks;
            bool active = false;
            foreach (var monitor in monitors.Values)
            {
                if (!monitor.Layout.ClockEnabled) continue;
                active |= monitor.ClockView.UpdateAnimation();
                monitor.Clock.Render();
            }
            if (active) return;
            animationTimer.Stop();
            animationRendering = false;
        }

        private void DisplayChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!shuttingDown)
                {
                    if (editorWindow != null) editorWindow.Close();
                    SyncMonitors();
                    if (settingsWindow != null) settingsWindow.RefreshDisplays();
                }
            }));
        }

        private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.Desktop && e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.General) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!shuttingDown) foreach (var monitor in monitors.Values) RefreshBackdrop(monitor);
            }));
        }

        private void RefreshBackdrop(MonitorRuntime monitor)
        {
            var previous = monitor.Backdrop;
            monitor.Backdrop = WallpaperBackdrop.Load(monitor.Screen);
            PlaceWidgets(monitor);
            previous.Dispose();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            shuttingDown = true;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            if (timer != null) timer.Stop();
            if (animationTimer != null) animationTimer.Stop();
            if (editorWindow != null) editorWindow.Close();
            if (sceneWatcher != null) sceneWatcher.Dispose();
            if (settingsWindow != null) settingsWindow.Close();
            foreach (var monitor in monitors.Values)
            {
                foreach (var runtime in monitor.Hardware.Values) if (runtime.Feed != null) runtime.Feed.Dispose();
                foreach (var widget in monitor.Widgets.Values) widget.Dispose();
                monitor.Backdrop.Dispose();
                if (monitor.Scene != null) monitor.Scene.Dispose();
            }
            Native.RestoreDesktopStyles();
            if (tray != null) tray.Dispose();
            base.OnExit(e);
        }

        private static Icon MakeTrayIcon()
        {
            using (var resource = typeof(Program).Assembly.GetManifestResourceStream("Wallpaper.app.ico"))
                return new Icon(resource, Forms.SystemInformation.SmallIconSize);
        }
    }
}
