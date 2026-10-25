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
        // 연결된 모니터마다 배경과 그 화면에 놓인 위젯 창. 키는 장치 이름. 위젯 목록 자체는 모든 화면 공용(preferences.Widgets).
        private readonly Dictionary<string, MonitorRuntime> monitors = new Dictionary<string, MonitorRuntime>();
        // 하드웨어 수집은 위젯마다 하나(그 위젯을 놓은 모든 화면이 함께 쓴다)와 마지막 값
        private readonly Dictionary<string, HardwareFeed> feeds = new Dictionary<string, HardwareFeed>();
        private readonly Dictionary<string, HardwareSnapshot> snapshots = new Dictionary<string, HardwareSnapshot>();
        // 마지막으로 맞춘 화면 구성(Monitors.Describe)
        private string topology;
        private bool misplacedLogged;
        // 화면을 맞추다 설정이 바뀌었으면(예전 장치 이름 → 모니터 키, 새 위젯 자리) 저장한다.
        private bool dirty;
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
        private DesktopMemoInput memoInput;
        private MemoEditorWindow memoEditor;
        private string memoEditingId;

        public ClockApplication()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            preferences = UserPreferences.Load();
            bool firstRun = !preferences.FromFile;
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

            if (firstRun)
            {
                // 첫 실행: 주 화면에 시계·달력을 놓는다.
                var primary = Monitors.All().FirstOrDefault(s => s.Primary);
                if (primary != null)
                    foreach (var kind in new[] { "clock", "calendar" })
                    {
                        var widget = WidgetSettings.Create(kind);
                        widget.Monitors.Add(new MonitorPlacement { Monitor = Monitors.Key(primary.DeviceName), Device = primary.DeviceName });
                        preferences.Widgets.Add(widget);
                    }
            }
            SyncMonitors();
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

        private IEnumerable<WidgetRuntime> Runtimes
        {
            get { return monitors.Values.SelectMany(m => m.Items.Values); }
        }

        // 메인 모니터: 설정에서 고른 화면이 연결돼 있으면 그것, 아니면 Windows 주 모니터. 나머지는 서브다.
        // 예전 설정은 장치 이름으로 저장했으므로 그것도 받는다.
        private Forms.Screen MainScreen(Forms.Screen[] screens)
        {
            var chosen = preferences.MainDisplay;
            return screens.FirstOrDefault(s => chosen.Length > 0 && (s.DeviceName == chosen || Monitors.Key(s.DeviceName) == chosen))
                ?? screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault();
        }

        // 화면이 그릴 배경(세트 id · 메인 · 모든 화면의 배치). 달라지면 장면을 다시 만든다.
        private string SceneKey(Forms.Screen screen, Forms.Screen main, Forms.Screen[] screens)
        {
            if (String.IsNullOrEmpty(preferences.Scene)) return "";
            return preferences.Scene + "|" + screen.DeviceName + "|" + main.DeviceName + "|" + String.Join(";", screens.Select(s => s.Bounds.ToString()));
        }

        // 연결된 화면마다 배경을 만들고, 끊긴 화면·배경이 바뀐 화면은 없앤다. 위젯은 공용 목록에서 그 화면에 놓인 것만 만든다.
        private void SyncMonitors()
        {
            var screens = Monitors.All();
            var main = MainScreen(screens);
            var described = Monitors.Describe(screens);
            if (described != topology)
            {
                SceneLibrary.LogError("화면 구성: " + described);
                topology = described;
                misplacedLogged = false;
            }
            if (main != null && preferences.MainDisplay == main.DeviceName)
            {
                preferences.MainDisplay = Monitors.Key(main.DeviceName);
                dirty = true;
            }
            foreach (var monitor in monitors.Values.ToArray())
            {
                var screen = screens.FirstOrDefault(s => s.DeviceName == monitor.Screen.DeviceName);
                if (screen == null || Monitors.Key(screen.DeviceName) != monitor.Key ||
                    monitor.SceneKey != SceneKey(screen, main, screens) ||
                    (monitor.Scene != null && (screen.Bounds != monitor.Screen.Bounds || monitor.RenderScale != preferences.RenderScale)))
                {
                    DestroyMonitor(monitor);
                    continue;
                }
                monitor.Screen = screen;
                RefreshBackdrop(monitor);
            }
            foreach (var screen in screens)
                if (!monitors.ContainsKey(screen.DeviceName)) CreateMonitor(screen, main, screens);
            SyncWidgets();
        }

        private void CreateMonitor(Forms.Screen screen, Forms.Screen main, Forms.Screen[] screens)
        {
            var monitor = new MonitorRuntime { Screen = screen, Key = Monitors.Key(screen.DeviceName), Backdrop = WallpaperBackdrop.Load(screen), SceneKey = SceneKey(screen, main, screens) };
            monitors.Add(screen.DeviceName, monitor);
            if (monitor.SceneKey.Length == 0) return;
            // 움직이는 배경을 만들 수 없으면 Windows 배경화면으로 표시한다. 옆 모니터는 메인 풍경을 이어서, 아래 등 나머지는
            // 세트의 서브 구성을 메인과의 위치에 맞춰 그린다.
            try
            {
                var definition = SceneLibrary.Scene(preferences.Scene);
                if (definition == null) throw new FileNotFoundException("장면 파일이 없습니다: scenes\\" + preferences.Scene + ".json");
                bool sub; string role;
                monitor.View = SceneView.Arrange(definition, main.Bounds, screen.Bounds, screens.Select(s => s.Bounds).ToList(), out sub, out role);
                monitor.IsSub = sub;
                monitor.Role = role;
                monitor.Scene = new WallpaperScene(screen, definition, monitor.IsSub, monitor.View, preferences.FrameLimit, preferences.RenderScale, preferences.EffectQuality);
                monitor.SceneDefinition = definition;
                monitor.RenderScale = preferences.RenderScale;
                monitor.Scene.Paused = userPaused;
            }
            catch (Exception error) { ReportSceneError(DisplayName(screen.DeviceName), error); }
        }

        // 화면을 없앤다. 하드웨어 수집은 그대로 두고, 뒤따르는 SyncMonitors → SyncWidgets 가 정리한다.
        private void DestroyMonitor(MonitorRuntime monitor)
        {
            if (editing == monitor && editorWindow != null) editorWindow.Close();
            foreach (var id in monitor.Items.Keys.ToArray()) DestroyWidget(monitor, id);
            monitor.Backdrop.Dispose();
            if (monitor.Scene != null) monitor.Scene.Dispose();
            monitors.Remove(monitor.Screen.DeviceName);
        }

        // ---- 위젯: 공용 목록(preferences.Widgets) ↔ 화면마다 만든 창 ----

        // 위젯이 이 화면에 놓인 자리. 예전 설정에서 옮긴 자리(장치 이름)는 처음 맞을 때 모니터 키로 바꾼다.
        private MonitorPlacement Spot(WidgetSettings widget, MonitorRuntime monitor)
        {
            var spot = widget.Monitors.FirstOrDefault(p => p.Monitor == monitor.Key)
                ?? widget.Monitors.FirstOrDefault(p => p.Monitor == monitor.Screen.DeviceName);
            if (spot != null && (spot.Monitor != monitor.Key || spot.Device != monitor.Screen.DeviceName))
            {
                spot.Monitor = monitor.Key;
                spot.Device = monitor.Screen.DeviceName;
                dirty = true;
            }
            return spot;
        }

        // 화면마다 켜진 자리가 있는 위젯은 만들고, 목록에서 빠졌거나 꺼진 위젯은 없앤다. 하드웨어 수집도 맞춘다.
        private void SyncWidgets()
        {
            foreach (var monitor in monitors.Values)
            {
                foreach (var id in monitor.Items.Keys.ToArray())
                {
                    var widget = preferences.Widget(id);
                    var spot = widget == null ? null : Spot(widget, monitor);
                    if (spot == null || !spot.Enabled) DestroyWidget(monitor, id);
                }
                foreach (var widget in preferences.Widgets.ToArray())
                {
                    if (monitor.Items.ContainsKey(widget.Id) || monitor.Failed.Contains(widget.Id)) continue;
                    var spot = Spot(widget, monitor);
                    if (spot != null && spot.Enabled) CreateWidget(monitor, widget, spot);
                }
            }
            UpdateFeeds();
            UpdateMemoInput();
            if (dirty)
            {
                dirty = false;
                SavePreferences();
            }
        }

        private void CreateWidget(MonitorRuntime monitor, WidgetSettings widget, MonitorPlacement spot)
        {
            var runtime = new WidgetRuntime { Settings = widget, Spot = spot };
            var now = DateTime.Now;
            FrameworkElement view;
            if (widget.Kind == "clock")
            {
                runtime.Clock = new FlipClock();
                BuiltInWidgetOptions.Apply(runtime.Clock, widget.Options);
                runtime.Clock.UpdateTime(now, false);
                view = runtime.Clock;
            }
            else if (widget.Kind == "calendar")
            {
                runtime.Calendar = new CalendarView();
                runtime.Calendar.UpdateDate(now);
                BuiltInWidgetOptions.Apply(runtime.Calendar, widget.Options);
                view = runtime.Calendar;
            }
            else if (widget.Kind == "hardware")
            {
                runtime.Hardware = new HardwareView(widget.Hardware);
                HardwareSnapshot last;
                if (snapshots.TryGetValue(widget.Id, out last)) runtime.Hardware.Update(last);
                view = runtime.Hardware;
            }
            else
            {
                try
                {
                    runtime.Script = WidgetScript.Load(widget.Type);
                    runtime.Data = new Scripting.WidgetData();
                    runtime.Data.SetParams(runtime.Script.Parameters, widget.Options);
                    runtime.Data.Update(now, LocalMetrics.Latest);
                    view = runtime.Script.Build(runtime.Data);
                }
                catch (Exception error)
                {
                    monitor.Failed.Add(widget.Id);
                    ReportError("Wallpaper 위젯 오류", DisplayName(monitor.Screen.DeviceName) + " · " + widget.Type, error);
                    return;
                }
                if (runtime.Script.Metrics) { LocalMetrics.Acquire(); runtime.UsesMetrics = true; }
            }
            runtime.Widget = new DesktopWidget("Wallpaper." + widget.Id, view, monitor.Scene, IsMemo(runtime));
            monitor.Items.Add(widget.Id, runtime);
            monitor.Widgets.Add(widget.Id, runtime.Widget);
            runtime.Widget.Show();
            runtime.Widget.SetVisible(true);
            BuiltInWidgetOptions.ApplyShadow(runtime.Widget, widget.Options);
            // 하드웨어·스크립트 위젯은 처음 놓일 때 다른 위젯과 겹치지 않는 자리를 찾는다(시계는 가운데, 달력은 좌하단이 기본).
            if (spot.Placement == null && runtime.Clock == null && runtime.Calendar == null)
            {
                spot.Placement = NewPlacement(monitor, runtime.Widget);
                dirty = true;
            }
            PlaceWidget(monitor, runtime);
            if (editing == monitor) AttachPreview(widget.Id);
        }

        private void DestroyWidget(MonitorRuntime monitor, string id)
        {
            if (memoEditingId == id && memoEditor != null) memoEditor.Close();
            var runtime = monitor.Items[id];
            monitor.Items.Remove(id);
            if (runtime.UsesMetrics) LocalMetrics.Release();
            if (editing == monitor)
            {
                if (editorWindow != null) editorWindow.RemoveWidget(id);
                DetachPreview(id);
            }
            monitor.Widgets.Remove(id);
            runtime.Widget.Dispose();
        }

        // 하드웨어 수집은 위젯마다 하나: 어느 화면에든 놓여 있으면 돌리고, 아무 데도 없으면 멈춘다.
        private void UpdateFeeds()
        {
            var shown = new HashSet<string>(Runtimes.Where(r => r.Hardware != null).Select(r => r.Settings.Id));
            foreach (var id in feeds.Keys.ToArray())
                if (!shown.Contains(id)) StopFeed(id);
            foreach (var id in shown)
            {
                if (feeds.ContainsKey(id)) continue;
                var feed = new HardwareFeed(preferences.Widget(id).Hardware);
                feeds.Add(id, feed);
                var key = id;
                feed.Updated += snapshot =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke(new Action(() => ShowSnapshot(key, feed, snapshot)));
                };
                feed.Start();
            }
        }

        private void StopFeed(string id)
        {
            HardwareFeed feed;
            if (!feeds.TryGetValue(id, out feed)) return;
            feeds.Remove(id);
            snapshots.Remove(id);
            feed.Dispose();
        }

        private void ShowSnapshot(string id, HardwareFeed feed, HardwareSnapshot snapshot)
        {
            HardwareFeed current;
            if (shuttingDown || !feeds.TryGetValue(id, out current) || current != feed) return;
            snapshots[id] = snapshot;
            foreach (var monitor in monitors.Values)
            {
                WidgetRuntime runtime;
                if (!monitor.Items.TryGetValue(id, out runtime) || runtime.Hardware == null) continue;
                bool resized = runtime.Hardware.Update(snapshot);
                if (resized) PlaceWidget(monitor, runtime);
                else runtime.Widget.Render();
                if (resized && editorWindow != null && editing == monitor) editorWindow.SyncWidget(id);
            }
        }

        // 목록에 보일 이름. 같은 이름이 여럿이면 뒤에 번호를 붙인다(시계 2).
        private Dictionary<string, string> WidgetNames()
        {
            var types = new Dictionary<string, string>();
            foreach (var type in WidgetScript.All()) types[type.Id] = type.Name;
            var names = new Dictionary<string, string>();
            var counts = new Dictionary<string, int>();
            foreach (var widget in preferences.Widgets)
            {
                string type, name = widget.Kind == "clock" ? "시계" : widget.Kind == "calendar" ? "달력"
                    : widget.Kind == "hardware" ? widget.Hardware.Name
                    : types.TryGetValue(widget.Type, out type) ? type : widget.Type + " (오류)";
                int count;
                counts.TryGetValue(name, out count);
                counts[name] = ++count;
                names[widget.Id] = count > 1 ? name + " " + count : name;
            }
            return names;
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
        string ISettingsHost.Scene() { return preferences.Scene; }
        void ISettingsHost.ChangeScene(string scene) { ChangeScene(scene); }

        void ISettingsHost.ChangeMainDisplay(string device)
        {
            MonitorRuntime monitor;
            if (!monitors.TryGetValue(device, out monitor) || preferences.MainDisplay == monitor.Key) return;
            preferences.MainDisplay = monitor.Key;
            preferences.Save();
            SyncMonitors();
        }

        void ISettingsHost.OpenLayoutEditor(string device)
        {
            Dispatcher.BeginInvoke(new Action(() => OpenEditor(device)));
        }

        // 공용 위젯 목록
        IEnumerable<SettingsWindow.WidgetEntry> ISettingsHost.Widgets()
        {
            var names = WidgetNames();
            return preferences.Widgets.Select(widget => new SettingsWindow.WidgetEntry { Id = widget.Id, Name = names[widget.Id], Kind = widget.Kind }).ToList();
        }

        void ISettingsHost.EditWidget(string id)
        {
            var widget = preferences.Widget(id);
            if (widget == null) return;
            if (widget.Kind == "hardware") EditHardware(id);
            else EditOptions(widget);
        }

        // 새 위젯은 목록에만 넣는다(화면에 놓기는 레이아웃 편집의 위젯 ▾). 하드웨어 상태는 먼저 연결 설정 창을 연다.
        void ISettingsHost.AddWidget(string kind, string type)
        {
            if (kind == "hardware") { EditHardware(null); return; }
            if (kind == "script")
            {
                try { WidgetScript.Load(type); }
                catch (Exception error)
                {
                    MessageBox.Show("위젯을 읽지 못했습니다.\n" + error.Message, "Wallpaper");
                    return;
                }
            }
            var widget = WidgetSettings.Create(kind);
            widget.Type = type ?? "";
            AddToList(widget);
        }

        void ISettingsHost.RemoveWidget(string id)
        {
            var widget = preferences.Widget(id);
            if (widget == null) return;
            preferences.Widgets.Remove(widget);
            SavePreferences();
            SyncWidgets();
        }

        IList<string> ISettingsHost.SceneUsers(string scene)
        {
            return preferences.Scene == scene ? OrderedMonitors.Select(m => DisplayName(m.Screen.DeviceName)).ToList() : new List<string>();
        }

        // 장면 삭제: 그 장면을 쓰던 화면은 Windows 배경화면으로 돌리고 저장한 뒤 파일을 지운다.
        void ISettingsHost.DeleteScene(string scene)
        {
            if (preferences.Scene == scene) preferences.Scene = "";
            SavePreferences();
            SyncMonitors();
            SceneLibrary.DeleteScene(scene);
        }

        private void AddToList(WidgetSettings widget)
        {
            preferences.Widgets.Add(widget);
            SavePreferences();
        }

        // 레이아웃 편집의 위젯 ▾: 위젯을 이 화면에 놓거나 뺀다. 뺀 자리는 지우지 않아 다시 놓으면 마지막 자리로 돌아간다.
        private void ShowWidget(MonitorRuntime monitor, string id, bool shown)
        {
            var widget = preferences.Widget(id);
            if (widget == null) return;
            var spot = Spot(widget, monitor);
            if (spot == null)
            {
                if (!shown) return;
                spot = new MonitorPlacement { Monitor = monitor.Key, Device = monitor.Screen.DeviceName };
                widget.Monitors.Add(spot);
            }
            if (spot.Enabled == shown && monitor.Items.ContainsKey(id) == shown) return;
            spot.Enabled = shown;
            foreach (var each in monitors.Values) each.Failed.Remove(id);
            SavePreferences();
            SyncWidgets();
        }

        // 위젯 대화 상자의 주인: 편집 창이 열려 있으면 그것, 아니면 설정 창
        private Window DialogOwner { get { return (Window)editorWindow ?? settingsWindow; } }

        // 설정 창의 디스플레이 목록: 이름·해상도·배경·놓인 위젯 수
        private IEnumerable<SettingsWindow.DisplayEntry> DisplayEntries()
        {
            var main = MainScreen(Monitors.All());
            foreach (var monitor in OrderedMonitors)
            {
                string background = "Windows 배경화면";
                if (monitor.SceneKey.Length > 0)
                {
                    var scene = monitor.SceneDefinition;
                    background = (scene == null ? preferences.Scene + " (오류 — Windows 배경화면으로 표시 중)"
                        : monitor.Scene == null ? scene.Name + " (오류 — Windows 배경화면으로 표시 중)"
                        : !scene.HasSub ? (monitor.Role == "main" ? "메인" : "같은 장면을 따로")
                        : monitor.Role == "main" ? "메인 구성"
                        : monitor.Role == "left" ? "옆 모니터(왼쪽): 메인 풍경이 이어짐"
                        : monitor.Role == "right" ? "옆 모니터(오른쪽): 메인 풍경이 이어짐"
                        : SubModeName(scene));
                }
                yield return new SettingsWindow.DisplayEntry
                {
                    Device = monitor.Screen.DeviceName, Name = DisplayName(monitor.Screen.DeviceName), Primary = monitor.Screen.Primary,
                    Main = main != null && monitor.Screen.DeviceName == main.DeviceName, Role = monitor.Role,
                    X = monitor.Screen.Bounds.X, Y = monitor.Screen.Bounds.Y, Width = monitor.Screen.Bounds.Width, Height = monitor.Screen.Bounds.Height, Background = background,
                    Widgets = monitor.Widgets.Count
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

        private static string SubModeName(SceneDefinition scene)
        {
            return scene.SubMode == "mirror" ? "서브: 메인을 비추는 수면" : scene.SubMode == "continue" ? "서브: 메인 아래로 이어짐" : "서브: 같은 장면을 따로";
        }

        // 설정 창에서 배경 세트를 바꾸면 저장하고 모든 화면을 다시 만든다.
        private void ChangeScene(string scene)
        {
            if (preferences.Scene == (scene ?? "")) return;
            var previous = preferences.Scene;
            preferences.Scene = scene ?? "";
            try { preferences.Save(); }
            catch (Exception error)
            {
                preferences.Scene = previous;
                MessageBox.Show("배경을 저장하지 못했습니다.\n" + error.Message, "Wallpaper");
                return;
            }
            SyncMonitors();
        }

        // Wallpapers 폴더가 바뀌면 움직이는 배경·스크립트 위젯을 쓰는 화면을 다시 만든다(장면 작성 중 바로 확인).
        private void ReloadScenes()
        {
            if (shuttingDown) return;
            Func<MonitorRuntime, bool> scripted = m => m.SceneKey.Length > 0 || m.Failed.Count > 0 || m.Items.Values.Any(r => r.Script != null);
            var reopen = editing != null && scripted(editing) ? editing.Screen.DeviceName : null;
            foreach (var monitor in monitors.Values.Where(scripted).ToArray()) DestroyMonitor(monitor);
            SyncMonitors();
            if (settingsWindow != null) settingsWindow.RefreshDisplays();
            if (reopen != null && editorWindow == null) OpenEditor(reopen);
        }

        // 화면 하나의 레이아웃 편집. 위젯 ▾ 메뉴에는 공용 목록 전체가 나오고, 체크하면 그 위젯을 이 화면에 놓는다.
        private void OpenEditor(string device)
        {
            if (memoEditor != null) memoEditor.Close();
            MonitorRuntime monitor;
            if (!monitors.TryGetValue(device, out monitor)) return;
            if (editorWindow != null)
            {
                if (editing == monitor) { editorWindow.Activate(); return; }
                editorWindow.Close();
            }
            var window = new LayoutEditorWindow(monitor.Screen, EditorBackground(monitor), monitor.Widgets, monitor.Widgets.ContainsKey,
                () => WidgetNames().ToArray(),
                (id, enabled) => ShowWidget(monitor, id, enabled), (id, placement) => ApplyLayout(monitor, id, placement));
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
                    return WallpaperScene.Still(monitor.SceneDefinition, bounds.Width, bounds.Height, monitor.Scene.Time, preferences.EffectQuality, monitor.IsSub, monitor.View);
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

        // 하드웨어 상태 설정 창. id 가 null 이면 새 위젯(목록에만 넣는다). 바꾸면 수집과 모든 화면의 위젯을 새로 만든다.
        private void EditHardware(string id)
        {
            var widget = id == null ? null : preferences.Widget(id);
            var dialog = new HardwareSettingsWindow(widget != null ? widget.Hardware : new HardwareWidgetSettings()) { Owner = DialogOwner };
            if (dialog.ShowDialog() != true) return;
            if (widget == null)
            {
                widget = WidgetSettings.Create("hardware");
                widget.Hardware = dialog.Result;
                AddToList(widget);
                return;
            }
            var previous = widget.Hardware;
            widget.Hardware = dialog.Result;
            try { preferences.Save(); }
            catch (Exception error)
            {
                widget.Hardware = previous;
                MessageBox.Show("위젯 설정을 저장하지 못했습니다.\n" + error.Message, "하드웨어 위젯"); return;
            }
            StopFeed(id);
            foreach (var monitor in monitors.Values.Where(m => m.Items.ContainsKey(id))) DestroyWidget(monitor, id);
            SyncWidgets();
            if (editorWindow != null) editorWindow.RefreshWidget(id);
        }

        // 기존 위젯과 겹치지 않는 첫 자리
        private WidgetPlacement NewPlacement(MonitorRuntime monitor, DesktopWidget widget)
        {
            var screen = monitor.Screen.Bounds;
            double dpi = widget.DpiScale;
            int width = (int)Math.Round(widget.BaseContentPixelWidth), height = (int)Math.Round(widget.BaseContentPixelHeight);
            int gap = (int)Math.Round(16 * dpi), step = Math.Max(1, (int)Math.Round(32 * dpi));
            var occupied = monitor.Widgets.Values.Where(w => w != widget).Select(w =>
            {
                var bounds = w.ContentBounds; bounds.Inflate(gap, gap); return bounds;
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

        // 위젯 설정 창(시계·달력·스크립트 위젯). 설정 값은 모든 화면이 함께 쓴다. 바꾸면 바로 반영하고 닫을 때 저장한다.
        private void EditOptions(WidgetSettings widget)
        {
            // 위젯 고유 값 + 공통(배경 진하기·그라데이션 범위)
            var own = BuiltInWidgetOptions.For(widget.Kind);
            if (own == null && widget.Kind == "script")
            {
                try { own = WidgetScript.Load(widget.Type).Parameters.ToArray(); }
                catch (Exception error)
                {
                    MessageBox.Show("위젯을 읽지 못했습니다.\n" + error.Message, "Wallpaper");
                    return;
                }
            }
            var parameters = (own ?? new WidgetParameter[0]).Concat(BuiltInWidgetOptions.Common).ToArray();
            var window = new WidgetOptionsWindow(WidgetNames()[widget.Id], parameters, widget.Options, () => ApplyOptions(widget)) { Owner = DialogOwner };
            window.ShowDialog();
            SavePreferences();
        }

        private void ApplyOptions(WidgetSettings widget)
        {
            foreach (var monitor in monitors.Values)
            {
                WidgetRuntime runtime;
                if (!monitor.Items.TryGetValue(widget.Id, out runtime)) continue;
                if (runtime.Clock != null) BuiltInWidgetOptions.Apply(runtime.Clock, widget.Options);
                else if (runtime.Calendar != null) BuiltInWidgetOptions.Apply(runtime.Calendar, widget.Options);
                else if (runtime.Script != null)
                {
                    runtime.Data.SetParams(runtime.Script.Parameters, widget.Options);
                    runtime.Widget.Render();
                }
                if (runtime.Clock != null || runtime.Calendar != null) PlaceWidget(monitor, runtime);
                BuiltInWidgetOptions.ApplyShadow(runtime.Widget, widget.Options);
                if (editorWindow != null && editing == monitor) editorWindow.RefreshWidget(widget.Id);
            }
        }

        // 메모가 하나라도 놓였을 때만 더블클릭을 관찰한다. 입력 이벤트는 바탕화면으로 그대로 통과한다.
        private void UpdateMemoInput()
        {
            bool needed = monitors.Values.Any(m => m.Items.Values.Any(IsMemo));
            if (!needed && memoInput != null) { memoInput.Dispose(); memoInput = null; }
            if (!needed || memoInput != null) return;
            try { memoInput = new DesktopMemoInput(Dispatcher, OpenMemoAt); }
            catch (System.ComponentModel.Win32Exception error) { ReportError("메모 편집 오류", "바탕화면 입력을 연결하지 못했습니다.", error); }
        }

        private static bool IsMemo(WidgetRuntime runtime)
        {
            return runtime.Settings.Kind == "script" && runtime.Settings.Type == "memo" && runtime.Script != null;
        }

        private async void OpenMemoAt(Native.Point point)
        {
            if (shuttingDown || editorWindow != null || memoEditor != null) return;
            var runtime = monitors.Values.Where(m => m.Screen.Bounds.Contains(point.X, point.Y))
                .SelectMany(m => m.Items.Values).Where(IsMemo)
                .OrderByDescending(r => r.Widget.LayerIndex)
                .FirstOrDefault(r => r.Widget.ContentBounds.Contains(point.X, point.Y));
            if (runtime == null) return;
            var target = DesktopMemoInput.WindowAt(point);
            var foreground = DesktopMemoInput.Foreground;
            if (!await System.Threading.Tasks.Task.Run(() => DesktopMemoInput.IsEmptyDesktop(point))) return;
            // UIA 판정 중 화면·창이 바뀌었으면 늦게 편집 창을 열어 포커스를 빼앗지 않는다.
            if (shuttingDown || editorWindow != null || memoEditor != null || DesktopMemoInput.WindowAt(point) != target ||
                DesktopMemoInput.Foreground != foreground ||
                !monitors.Values.Any(m => m.Items.Values.Contains(runtime))) return;
            var widget = runtime.Settings;
            var parameters = runtime.Script.Parameters.ToDictionary(p => p.Name);
            WidgetParameter text, size, color, align;
            if (!parameters.TryGetValue("text", out text) || !parameters.TryGetValue("size", out size) ||
                !parameters.TryGetValue("color", out color) || !parameters.TryGetValue("align", out align)) return;
            System.Windows.TextAlignment alignment;
            if (!Enum.TryParse(Convert.ToString(align.Value(widget.Options)), out alignment)) alignment = System.Windows.TextAlignment.Left;
            double zoom = runtime.Widget.DpiScale * runtime.Widget.DisplayScale;
            var window = new MemoEditorWindow(runtime.Widget.ContentBounds, Convert.ToString(text.Value(widget.Options)),
                Convert.ToDouble(size.Value(widget.Options)) * zoom, color.Value(widget.Options) as System.Windows.Media.Brush, alignment,
                value => { widget.Options["text"] = value; ApplyOptions(widget); }, () =>
                {
                    try { preferences.Save(); return true; }
                    catch (Exception error) { SceneLibrary.LogError("메모 저장 실패: " + error.Message); return false; }
                });
            memoEditor = window;
            memoEditingId = widget.Id;
            window.Closed += delegate { if (memoEditor == window) { memoEditor = null; memoEditingId = null; } };
            window.Show();
        }

        // 위젯마다 정한 간격(interval 초)의 시각 경계에서 다시 그린다(초 단위면 초가 바뀔 때).
        private void RenderScripts(DateTime now)
        {
            foreach (var runtime in Runtimes)
            {
                if (runtime.Script == null || now < runtime.Next) continue;
                long interval = Math.Max(1, (long)(runtime.Script.Interval * TimeSpan.TicksPerSecond));
                runtime.Next = new DateTime((now.Ticks / interval + 1) * interval);
                runtime.Data.Update(now, LocalMetrics.Latest);
                runtime.Widget.Render();
            }
        }

        // 화면 하나에 놓인 위젯 창. 설정(Settings)·자리(Spot)는 공용 목록의 것을 그대로 가리킨다.
        private sealed class WidgetRuntime
        {
            public WidgetSettings Settings;
            public MonitorPlacement Spot;
            public DesktopWidget Widget;
            public FlipClock Clock;
            public CalendarView Calendar;
            public HardwareView Hardware;
            public WidgetScript Script;
            public Scripting.WidgetData Data;
            public bool UsesMetrics;
            public DateTime Next;
        }

        private sealed class MonitorRuntime
        {
            public Forms.Screen Screen;
            public string Key = "";   // 모니터 키(Monitors.Key)
            public WallpaperBackdrop Backdrop;
            public WallpaperScene Scene;
            public SceneDefinition SceneDefinition;
            public string SceneKey = "";
            public bool IsSub;
            public string Role = "";
            public SceneView View;
            public double RenderScale = 1;
            public readonly Dictionary<string, WidgetRuntime> Items = new Dictionary<string, WidgetRuntime>();
            public readonly Dictionary<string, DesktopWidget> Widgets = new Dictionary<string, DesktopWidget>();   // 편집 창용(id → 창)
            public readonly HashSet<string> Failed = new HashSet<string>();   // 만들지 못한 스크립트 위젯(장면 폴더가 바뀌면 다시 시도)
        }

        private void ApplyLayout(MonitorRuntime monitor, string id, WidgetPlacement placement)
        {
            WidgetRuntime runtime;
            if (!monitor.Items.TryGetValue(id, out runtime)) return;
            runtime.Spot.Placement = placement;
            PlaceWidget(monitor, runtime);
        }

        private void PlaceWidgets(MonitorRuntime monitor)
        {
            foreach (var runtime in monitor.Items.Values) PlaceWidget(monitor, runtime);
        }

        private void PlaceWidget(MonitorRuntime monitor, WidgetRuntime runtime)
        {
            var widget = runtime.Widget;
            var placement = runtime.Spot.Placement;
            var kind = runtime.Settings.Kind;
            var screen = monitor.Screen;
            var area = screen.WorkingArea;
            var bounds = screen.Bounds;
            var margin = (int)Math.Round(36 * widget.DpiScale);
            double scale = placement == null ? 1 : placement.Scale;
            if (widget.FreeResize)
            {
                var size = WidgetPlacement.MemoSize(placement, new System.Windows.Size(runtime.Script.Width, runtime.Script.Height),
                    new System.Windows.Size(bounds.Width, bounds.Height), widget.DpiScale, out scale);
                widget.ResizeContent(size.Width, size.Height);
            }
            else scale = Math.Min(scale, Math.Min(bounds.Width / widget.BaseContentPixelWidth, bounds.Height / widget.BaseContentPixelHeight));
            int width = (int)Math.Round(widget.BasePixelWidth * scale);
            int height = (int)Math.Round(widget.BasePixelHeight * scale);
            var content = widget.ContentArea;
            int insetX = (int)Math.Round(content.Left * widget.DpiScale * scale);
            int insetY = (int)Math.Round(content.Top * widget.DpiScale * scale);
            int contentWidth = (int)Math.Round(content.Right * widget.DpiScale * scale) - insetX;
            int contentHeight = (int)Math.Round(content.Bottom * widget.DpiScale * scale) - insetY;
            int x = placement != null ? bounds.Left + (int)Math.Round(placement.X * bounds.Width)
                : kind == "clock" ? bounds.Left + (bounds.Width - width) / 2
                : area.Left + margin;
            int y = placement != null ? bounds.Top + (int)Math.Round(placement.Y * bounds.Height)
                : kind == "clock" ? bounds.Top + (bounds.Height - height) / 2
                : kind == "calendar" ? area.Bottom - margin - height : area.Top + margin;
            if (placement != null && placement.ContentAnchored) { x -= insetX; y -= insetY; }
            x = Math.Max(bounds.Left - insetX, Math.Min(bounds.Right - contentWidth - insetX, x));
            y = Math.Max(bounds.Top - insetY, Math.Min(bounds.Bottom - contentHeight - insetY, y));
            widget.Place(x, y, monitor.Backdrop, scale);
        }

        private void Tick(object sender, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var runtime in Runtimes)
                if (runtime.Clock != null && runtime.Clock.UpdateTime(now, true)) StartAnimation();
            if (now.Date != calendarDate)
            {
                calendarDate = now.Date;
                foreach (var runtime in Runtimes.Where(r => r.Calendar != null))
                {
                    runtime.Calendar.UpdateDate(now);
                    runtime.Widget.Render();
                }
            }
            RenderScripts(now);
            if (hostCheck % 15 == 0) UpdatePause();
            if (++hostCheck < 60) return;
            hostCheck = 0;
            foreach (var monitor in monitors.Values) if (monitor.Scene != null && monitor.Scene.IsAlive) monitor.Scene.KeepUnderIcons();
            CheckDesktop();
        }

        // 2초마다 바탕화면 상태를 확인한다.
        // ① Explorer 가 다시 시작되면 기존 자식 창도 사라지므로 새 창을 만든다.
        // ② 화면 구성이 바뀌었는데 알림을 놓쳤으면(원격 데스크톱·잠금 해제 등 세션 전환 중) 다시 맞춘다.
        // ③ 화면 구성 변경 직후 바탕화면 창이 늦게 옮겨져 위젯·배경 창이 어긋났으면 다시 놓는다.
        private void CheckDesktop()
        {
            if (monitors.Values.Any(monitor => monitor.Widgets.Values.Any(widget => !widget.IsDesktopAttached) ||
                (monitor.Scene != null && !monitor.Scene.IsAlive)))
            {
                foreach (var monitor in monitors.Values.ToArray()) DestroyMonitor(monitor);
                SyncMonitors();
                return;
            }
            if (Monitors.Describe(Monitors.All()) != topology)
            {
                DisplayChanged(this, EventArgs.Empty);
                return;
            }
            var misplaced = monitors.Values.Where(m => (m.Scene != null && m.Scene.IsMisplaced) || m.Widgets.Values.Any(w => w.IsMisplaced)).ToArray();
            if (misplaced.Length == 0) return;
            if (!misplacedLogged)
            {
                SceneLibrary.LogError("위젯·배경 창이 화면에서 어긋나 다시 놓습니다: " + String.Join(", ", misplaced.Select(m => DisplayName(m.Screen.DeviceName))));
                misplacedLogged = true;
            }
            bool rebuild = false;
            foreach (var monitor in misplaced)
                if (monitor.Scene != null && monitor.Scene.IsMisplaced) { DestroyMonitor(monitor); rebuild = true; }
                else PlaceWidgets(monitor);
            if (rebuild) SyncMonitors();
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
            foreach (var runtime in Runtimes)
            {
                if (runtime.Clock == null) continue;
                active |= runtime.Clock.UpdateAnimation();
                runtime.Widget.Render();
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
            if (memoInput != null) memoInput.Dispose();
            if (memoEditor != null) memoEditor.Close();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            if (timer != null) timer.Stop();
            if (animationTimer != null) animationTimer.Stop();
            if (editorWindow != null) editorWindow.Close();
            if (sceneWatcher != null) sceneWatcher.Dispose();
            if (settingsWindow != null) settingsWindow.Close();
            foreach (var feed in feeds.Values) feed.Dispose();
            foreach (var monitor in monitors.Values)
            {
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
