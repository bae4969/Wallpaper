using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wallpaper
{
    // 설정 창이 앱에 요청하는 일(디스플레이·배경·위젯·장면·성능). device 는 Windows 장치 이름.
    internal interface ISettingsHost
    {
        IEnumerable<SettingsWindow.DisplayEntry> Displays();
        IList<SceneDefinition> Scenes();
        string Background(string device);
        void ChangeBackground(string device, string background);
        void OpenLayoutEditor(string device);
        IEnumerable<SettingsWindow.WidgetEntry> Widgets(string device);
        IList<WidgetScript> ScriptTypes();
        void EditWidgetOptions(string device, string id);
        void EditHardware(string device, string id);   // id 가 null 이면 새 하드웨어 상태 위젯
        void AddScript(string device, string type);
        void RemoveWidget(string device, string id);
        IList<string> SceneUsers(string scene);   // 그 장면을 배경으로 쓰는 디스플레이 이름
        void DeleteScene(string scene);           // 쓰던 화면은 Windows 배경화면으로 돌린다
        void ApplyPerformance();
    }

    // 앱 설정 창: 디스플레이 / 위젯 / 장면 / 성능 탭. 바꾸면 바로 적용·저장한다.
    // 위젯 켜고 끄기와 위치·크기는 레이아웃 편집 창에서 한다.
    internal sealed class SettingsWindow : Window
    {
        public sealed class DisplayEntry
        {
            public string Device, Name, Background;
            public bool Primary;
            public int Width, Height, Widgets;
        }

        public sealed class WidgetEntry
        {
            public string Id, Name, Kind;   // Kind: builtin | hardware | script
            public bool Enabled;
        }

        // 장면 미리보기 그림(파일 수정 시각이 같으면 다시 그리지 않는다)
        private static readonly Dictionary<string, KeyValuePair<DateTime, BitmapSource>> thumbnails = new Dictionary<string, KeyValuePair<DateTime, BitmapSource>>();
        private static readonly object renderGate = new object();
        private const int ThumbWidth = 160, ThumbHeight = 90;

        private readonly UserPreferences preferences;
        private readonly ISettingsHost host;
        private List<DisplayEntry> displays = new List<DisplayEntry>();
        private string device;   // 디스플레이·위젯 탭이 함께 쓰는 선택
        private readonly List<ComboBox> pickers = new List<ComboBox>();
        private readonly TextBlock displayInfo = new TextBlock();
        private readonly ComboBox backgroundChoice = new ComboBox { Width = 220 };
        private IList<SceneDefinition> scenes = new List<SceneDefinition>();
        private readonly StackPanel widgetRows = new StackPanel();
        private readonly StackPanel sceneRows = new StackPanel();
        private readonly TextBlock status;
        private bool loading;
        private int sceneVersion;

        public SettingsWindow(UserPreferences settings, ISettingsHost settingsHost)
        {
            preferences = settings;
            host = settingsHost;
            ModernTheme.Apply(this);
            Title = "Wallpaper — 설정";
            Width = 600;
            Height = 680;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var layout = new Grid { Margin = new Thickness(24, 20, 24, 18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock { Text = "설정", Style = ModernTheme.Style("Bae.Text.Title") });

            var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
            AutomationProperties.SetAutomationId(tabs, "SettingsTabs");
            tabs.Items.Add(Tab("디스플레이", "Display", DisplayTab()));
            tabs.Items.Add(Tab("위젯", "Widgets", WidgetTab()));
            tabs.Items.Add(Tab("장면", "Scenes", SceneTab()));
            tabs.Items.Add(Tab("성능", "Performance", PerformanceTab()));
            Grid.SetRow(tabs, 1);
            layout.Children.Add(tabs);

            status = new TextBlock { Style = ModernTheme.Style("Bae.Text.Hint"), FontSize = 12, Margin = new Thickness(2, 10, 0, 10) };
            Grid.SetRow(status, 2);
            layout.Children.Add(status);
            var footer = new StackPanel();
            footer.Children.Add(new Border { Style = ModernTheme.Style("Bae.DividerLine"), Margin = new Thickness(0, 0, 0, 14) });
            var close = new Button { Content = "닫기", HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
            AutomationProperties.SetAutomationId(close, "CloseSettings");
            close.Click += delegate { Close(); };
            footer.Children.Add(close);
            Grid.SetRow(footer, 3);
            layout.Children.Add(footer);
            Content = layout;
            RefreshDisplays();
        }

        private static TabItem Tab(string header, string id, FrameworkElement content)
        {
            var tab = new TabItem
            {
                Header = header,
                Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            AutomationProperties.SetAutomationId(tab, "Tab_" + id);
            return tab;
        }

        // ---- 디스플레이 탭: 디스플레이 고르기 · 레이아웃 편집 · 배경 ----
        private FrameworkElement DisplayTab()
        {
            var panel = new StackPanel();
            panel.Children.Add(Label("디스플레이", 0));
            displayInfo.Style = ModernTheme.Style("Bae.Text.Subtitle");
            displayInfo.Margin = new Thickness(2, 8, 0, 0);
            var section = new StackPanel();
            section.Children.Add(PickerRow("DisplaySelector", "EditDisplay"));
            section.Children.Add(displayInfo);
            panel.Children.Add(Card(section, new Thickness(16, 14, 14, 14)));

            panel.Children.Add(Label("배경", 18));
            AutomationProperties.SetAutomationId(backgroundChoice, "BackgroundSelector");
            AutomationProperties.SetName(backgroundChoice, "배경");
            backgroundChoice.SelectionChanged += delegate
            {
                if (loading || device == null || backgroundChoice.SelectedIndex < 0) return;
                var chosen = backgroundChoice.SelectedIndex == 0 ? "" : backgroundChoice.SelectedIndex <= scenes.Count ? scenes[backgroundChoice.SelectedIndex - 1].Id : null;
                if (chosen == null) return;
                Run(() => host.ChangeBackground(device, chosen));
            };
            panel.Children.Add(Card(OptionRow("배경", "움직이는 배경 장면 또는 Windows 배경화면입니다. 장면은 장면 탭에서 만들고 관리합니다.", backgroundChoice)));
            return panel;
        }

        // 디스플레이 고르기 + 레이아웃 편집(위치·크기·켜고 끄기). 디스플레이·위젯 탭이 같은 선택을 쓴다.
        private Grid PickerRow(string pickerId, string editId)
        {
            var picker = new ComboBox();
            AutomationProperties.SetAutomationId(picker, pickerId);
            AutomationProperties.SetName(picker, "디스플레이");
            picker.SelectionChanged += delegate
            {
                if (loading || picker.SelectedIndex < 0 || picker.SelectedIndex >= displays.Count) return;
                device = displays[picker.SelectedIndex].Device;
                ShowSelected();
            };
            pickers.Add(picker);
            var edit = new Button { Content = "레이아웃 편집", Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(edit, editId);
            edit.Click += delegate { if (device != null) host.OpenLayoutEditor(device); };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(picker);
            Grid.SetColumn(edit, 1);
            row.Children.Add(edit);
            return row;
        }

        // ---- 위젯 탭: 고른 디스플레이의 위젯 목록·추가·제거·설정 ----
        private FrameworkElement WidgetTab()
        {
            var panel = new StackPanel();
            panel.Children.Add(Label("디스플레이", 0));
            panel.Children.Add(Card(PickerRow("WidgetDisplaySelector", "WidgetEditLayout"), new Thickness(16, 14, 14, 14)));
            var header = new Grid { Margin = new Thickness(0, 18, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Label("위젯 · 켜고 끄기는 레이아웃 편집의 위젯 ▾", 0);
            title.Margin = new Thickness(2, 0, 0, 0);
            title.VerticalAlignment = VerticalAlignment.Bottom;
            header.Children.Add(title);
            var add = new Button { Content = "위젯 추가 ▾" };
            AutomationProperties.SetAutomationId(add, "AddWidget");
            add.Click += delegate { ShowAddMenu(add); };
            Grid.SetColumn(add, 1);
            header.Children.Add(add);
            panel.Children.Add(header);
            AutomationProperties.SetAutomationId(widgetRows, "WidgetRows");
            panel.Children.Add(Card(widgetRows));
            return panel;
        }

        private void RefreshWidgets()
        {
            widgetRows.Children.Clear();
            if (device == null) return;
            foreach (var widget in host.Widgets(device))
            {
                if (widgetRows.Children.Count > 0) widgetRows.Children.Add(Divider());
                var id = widget.Id;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (widget.Kind == "hardware")
                    buttons.Children.Add(SmallButton("설정", "WidgetOptions_" + id, () => host.EditHardware(device, id)));
                else
                    buttons.Children.Add(SmallButton("설정", "WidgetOptions_" + id, () => host.EditWidgetOptions(device, id)));
                if (widget.Kind != "builtin")
                    buttons.Children.Add(SmallButton("제거", "RemoveWidget_" + id, () => host.RemoveWidget(device, id)));
                var kind = widget.Kind == "builtin" ? "기본 위젯" : widget.Kind == "hardware" ? "하드웨어 상태" : "스크립트 위젯";
                widgetRows.Children.Add(Row(widget.Name, kind + (widget.Enabled ? "" : " · 꺼짐"), widget.Enabled, buttons));
            }
        }

        // 위젯 추가 메뉴: 하드웨어 상태(내 PC·SSH) + widgets 폴더의 스크립트 위젯
        private void ShowAddMenu(Button anchor)
        {
            var menu = Menu(anchor);
            var hardware = new MenuItem { Header = "하드웨어 상태 (내 PC · SSH)", ToolTip = "CPU·RAM·GPU·네트워크. 다른 PC는 SSH로 연결합니다." };
            AutomationProperties.SetAutomationId(hardware, "AddHardwareWidget");
            hardware.Click += delegate { if (device != null) Run(() => host.EditHardware(device, null)); };
            menu.Items.Add(hardware);
            var types = host.ScriptTypes();
            if (types.Count > 0) menu.Items.Add(new Separator());
            foreach (var type in types)
            {
                var item = new MenuItem { Header = type.Name, ToolTip = type.Description.Length > 0 ? type.Description : null };
                AutomationProperties.SetAutomationId(item, "AddScript_" + type.Id);
                var id = type.Id;
                item.Click += delegate { if (device != null) Run(() => host.AddScript(device, id)); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // ---- 장면 탭: 장면 목록·새로 만들기·복제·이름 바꾸기·파일 열기·삭제 ----
        private FrameworkElement SceneTab()
        {
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Label("장면", 0);
            title.Margin = new Thickness(2, 0, 0, 0);
            title.VerticalAlignment = VerticalAlignment.Bottom;
            header.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var folder = new Button { Content = "폴더 열기" };
            AutomationProperties.SetAutomationId(folder, "OpenSceneFolder");
            folder.ToolTip = "장면(scenes)·레이어(layers)·스크립트 위젯(widgets) 폴더. 파일을 저장하면 바로 반영됩니다. 작성법은 AUTHORING.md 또는 Wallpaper.exe --guide.";
            folder.Click += delegate
            {
                SceneLibrary.EnsureDefaults();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + SceneLibrary.Root + "\"") { UseShellExecute = true });
            };
            var create = new Button { Content = "새 장면", Margin = new Thickness(8, 0, 0, 0), Style = ModernTheme.Style("AccentButton") };
            AutomationProperties.SetAutomationId(create, "NewScene");
            create.Click += delegate { Run(() => OpenFile(SceneLibrary.NewScene())); };
            actions.Children.Add(folder);
            actions.Children.Add(create);
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            panel.Children.Add(header);
            AutomationProperties.SetAutomationId(sceneRows, "SceneRows");
            panel.Children.Add(Card(sceneRows));
            return panel;
        }

        private void RefreshScenes()
        {
            sceneRows.Children.Clear();
            int version = ++sceneVersion;
            var files = SceneLibrary.SceneFiles();
            if (files.Count == 0)
                sceneRows.Children.Add(new TextBlock { Text = "장면이 없습니다. 새 장면으로 만들어 보세요.", Style = ModernTheme.Style("Bae.Text.Subtitle"), Margin = new Thickness(0, 12, 0, 12) });
            foreach (var file in files)
            {
                if (sceneRows.Children.Count > 0) sceneRows.Children.Add(Divider());
                var id = file.Id;
                var users = host.SceneUsers(id);
                string detail = file.Definition == null ? "오류: " + file.Error
                    : file.Definition.Layers.Length + "개 레이어" + (users.Count > 0 ? " · 사용 중: " + String.Join(", ", users) : "");
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (file.Definition != null)
                {
                    buttons.Children.Add(SmallButton("이름", "RenameScene_" + id, () => Rename(file.Definition)));
                    buttons.Children.Add(SmallButton("복제", "DuplicateScene_" + id, () => SceneLibrary.DuplicateScene(id)));
                }
                buttons.Children.Add(SmallButton("편집", "EditScene_" + id, () => OpenFile(id)));
                buttons.Children.Add(SmallButton("삭제", "DeleteScene_" + id, () => Delete(id, file.Definition != null ? file.Definition.Name : id, users)));
                var thumb = new Border
                {
                    Width = ThumbWidth * 0.6, Height = ThumbHeight * 0.6, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 12, 0),
                    Background = ModernTheme.Brush("Bae.SurfaceAlt"), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center
                };
                if (file.Definition != null) LoadThumbnail(file.Definition, thumb, version);
                var row = Row(file.Definition != null ? file.Definition.Name : id + ".json", detail, file.Definition != null, buttons);
                row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
                foreach (UIElement child in row.Children) Grid.SetColumn(child, Grid.GetColumn(child) + 1);
                row.Children.Add(thumb);
                if (file.Definition == null) ((TextBlock)((StackPanel)row.Children[0]).Children[1]).Foreground = ModernTheme.Brush("Bae.Danger");
                sceneRows.Children.Add(row);
            }
        }

        // 미리보기는 뒤에서 하나씩 그린다(창이 멈추지 않게). 목록이 다시 그려졌으면 버린다.
        private void LoadThumbnail(SceneDefinition scene, Border target, int version)
        {
            DateTime written;
            try { written = System.IO.File.GetLastWriteTimeUtc(SceneLibrary.ScenePath(scene.Id)); }
            catch (Exception) { return; }
            KeyValuePair<DateTime, BitmapSource> cached;
            lock (thumbnails)
                if (thumbnails.TryGetValue(scene.Id, out cached) && cached.Key == written) { target.Child = Thumbnail(cached.Value); return; }
            int quality = preferences.EffectQuality;
            Task.Run(() =>
            {
                // 그리기는 한 번에 하나씩(renderGate). 목록 잠금(thumbnails)은 짧게만 잡아 창이 기다리지 않게 한다.
                lock (renderGate)
                {
                    KeyValuePair<DateTime, BitmapSource> done;
                    lock (thumbnails)
                        if (thumbnails.TryGetValue(scene.Id, out done) && done.Key == written) return done.Value;
                    var image = WallpaperScene.Still(scene, ThumbWidth, ThumbHeight, 6f, quality);
                    lock (thumbnails) thumbnails[scene.Id] = new KeyValuePair<DateTime, BitmapSource>(written, image);
                    return image;
                }
            }).ContinueWith(task =>
            {
                if (task.IsFaulted || version != sceneVersion) return;
                target.Child = Thumbnail(task.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static Image Thumbnail(BitmapSource image) { return new Image { Source = image, Stretch = Stretch.UniformToFill }; }

        private void Rename(SceneDefinition scene)
        {
            var box = new TextBox { Text = scene.Name, Margin = new Thickness(0, 0, 0, 0), MinWidth = 300 };
            AutomationProperties.SetAutomationId(box, "SceneName");
            if (!Ask("장면 이름 바꾸기", box, "바꾸기")) throw new OperationCanceledException();
            var name = box.Text.Trim();
            if (name.Length == 0 || name == scene.Name) throw new OperationCanceledException();
            SceneLibrary.RenameScene(scene.Id, name);
        }

        private void Delete(string id, string name, IList<string> users)
        {
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
                Text = "'" + name + "' 장면 파일을 지웁니다." + (users.Count > 0 ? "\n" + String.Join(", ", users) + "은(는) Windows 배경화면으로 바뀝니다." : "") };
            if (!Ask("장면 삭제", message, "삭제")) throw new OperationCanceledException();
            host.DeleteScene(id);
        }

        // 장면 파일을 기본 편집기로 연다(연결된 프로그램이 없으면 메모장).
        private static void OpenFile(string id)
        {
            var path = SceneLibrary.ScenePath(id);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
        }

        // 테마를 맞춘 작은 확인 창
        private bool Ask(string title, FrameworkElement body, string confirm)
        {
            var dialog = new Window { Owner = this, Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            ModernTheme.Apply(dialog);
            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
            panel.Children.Add(body);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = new Button { Content = "취소", IsCancel = true };
            var ok = new Button { Content = confirm, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = ModernTheme.Style("AccentButton") };
            AutomationProperties.SetAutomationId(ok, "ConfirmDialog");
            ok.Click += delegate { dialog.DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.Loaded += delegate { var box = body as TextBox; if (box != null) { box.Focus(); box.SelectAll(); } };
            return dialog.ShowDialog() == true;
        }

        // ---- 성능 탭 ----
        private FrameworkElement PerformanceTab()
        {
            var panel = new StackPanel();
            panel.Children.Add(Label("성능 · 바꾸면 바로 적용됩니다", 0));
            var options = new StackPanel();
            options.Children.Add(OptionRow("프레임 제한", "높을수록 부드럽고 GPU를 더 씁니다. 디스플레이 주사율은 화면 갱신마다 그립니다.",
                Choice("FrameLimit", new[] { "30 FPS", "60 FPS", "120 FPS", "디스플레이 주사율" },
                    Array.IndexOf(UserPreferences.FrameLimits, preferences.FrameLimit),
                    index => preferences.FrameLimit = UserPreferences.FrameLimits[index])));
            options.Children.Add(Divider());
            options.Children.Add(OptionRow("렌더 해상도", "배경을 낮은 해상도로 그린 뒤 확대합니다. 위젯은 항상 원래 해상도로 선명합니다.",
                Choice("RenderScale", new[] { "100% (선명)", "75%", "50% (절약)" },
                    Array.IndexOf(UserPreferences.RenderScales, preferences.RenderScale),
                    index => preferences.RenderScale = UserPreferences.RenderScales[index])));
            options.Children.Add(Divider());
            options.Children.Add(OptionRow("효과 품질", "낮출수록 입자 수와 잡음 계산을 줄입니다. 낮음에서는 유리 같은 후처리 레이어를 건너뜁니다.",
                Choice("EffectQuality", new[] { "높음", "보통", "낮음" }, 2 - preferences.EffectQuality,
                    index => preferences.EffectQuality = 2 - index)));
            panel.Children.Add(Card(options));

            var pauses = new StackPanel();
            pauses.Children.Add(Toggle("PauseWhenCovered", "창이 화면을 덮으면 멈춤", "최대화·전체 화면 창 뒤에서는 그리지 않습니다.",
                preferences.PauseWhenCovered, value => preferences.PauseWhenCovered = value));
            pauses.Children.Add(Divider());
            pauses.Children.Add(Toggle("PauseOnBattery", "배터리로 작동할 때 멈춤", "노트북이 전원에 연결되지 않으면 그리지 않습니다.",
                preferences.PauseOnBattery, value => preferences.PauseOnBattery = value));
            var pauseCard = Card(pauses);
            pauseCard.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(pauseCard);
            return panel;
        }

        // ---- 목록 새로 고침 ----

        // 디스플레이·장면 목록을 다시 읽는다(편집 창을 닫거나 화면 구성·장면 파일이 바뀌면 호출). 고른 화면은 유지한다.
        public void RefreshDisplays()
        {
            displays = host.Displays().ToList();
            if (device == null || !displays.Any(d => d.Device == device)) device = displays.Count > 0 ? displays[0].Device : null;
            loading = true;
            foreach (var picker in pickers)
            {
                picker.Items.Clear();
                foreach (var display in displays) picker.Items.Add(display.Name + (display.Primary ? " (주)" : ""));
            }
            loading = false;
            ShowSelected();
            RefreshScenes();
        }

        private void ShowSelected()
        {
            loading = true;
            int index = displays.FindIndex(d => d.Device == device);
            foreach (var picker in pickers) picker.SelectedIndex = index;
            var display = index >= 0 ? displays[index] : null;
            displayInfo.Text = display != null
                ? display.Width + " × " + display.Height + " · 배경: " + display.Background + " · 위젯 " + display.Widgets + "개"
                : "연결된 디스플레이가 없습니다.";
            scenes = host.Scenes();
            backgroundChoice.Items.Clear();
            backgroundChoice.Items.Add("Windows 배경화면");
            foreach (var scene in scenes) backgroundChoice.Items.Add(scene.Name);
            var current = device != null ? host.Background(device) ?? "" : "";
            int chosen = scenes.ToList().FindIndex(scene => scene.Id == current);
            if (chosen < 0 && current.Length > 0) backgroundChoice.Items.Add(current + " (파일 없음)");
            backgroundChoice.SelectedIndex = chosen >= 0 ? chosen + 1 : current.Length > 0 ? backgroundChoice.Items.Count - 1 : 0;
            RefreshWidgets();
            loading = false;
        }

        // 앱에 요청하고 결과를 보인 뒤 목록을 새로 고친다. 확인 창에서 취소하면 아무것도 하지 않는다.
        private void Run(Action action)
        {
            try
            {
                action();
                ShowStatus("적용했습니다.", "Bae.Success");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                ShowStatus("적용하지 못했습니다. " + error.Message, "Bae.Danger");
            }
            if (IsVisible) RefreshDisplays();
        }

        private void ShowStatus(string text, string brush)
        {
            status.Text = text;
            status.Foreground = ModernTheme.Brush(brush);
        }

        // ---- 공용 모양 ----

        private ContextMenu Menu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            ModernTheme.Apply(menu);
            menu.Style = ModernTheme.Style(typeof(ContextMenu));
            return menu;
        }

        // 목록 한 줄: 제목 + 설명 | 버튼들
        private static Grid Row(string title, string detail, bool active, FrameworkElement buttons)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ModernTheme.Brush(active ? "Bae.TextPrimary" : "Bae.TextDim")
            });
            text.Children.Add(new TextBlock
            {
                Text = detail, Style = ModernTheme.Style("Bae.Text.Subtitle"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            return row;
        }

        private Button SmallButton(string text, string id, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), MinWidth = 0, MinHeight = 28, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetAutomationId(button, id);
            button.Click += delegate { Run(action); };
            return button;
        }

        private static TextBlock Label(string text, double top)
        {
            return new TextBlock { Text = text, Style = ModernTheme.Style("Bae.Text.Label"), Margin = new Thickness(2, top, 0, 8) };
        }

        private static Border Card(FrameworkElement child, Thickness? padding = null)
        {
            return new Border { Style = ModernTheme.Style("Bae.Card"), Padding = padding ?? new Thickness(16, 6, 14, 6), Child = child };
        }

        private static Grid OptionRow(string title, string hint, FrameworkElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = Heading(title, hint);
            text.Margin = new Thickness(0, 0, 16, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private ComboBox Choice(string id, string[] items, int selected, Action<int> select)
        {
            var box = new ComboBox { Width = 170 };
            AutomationProperties.SetAutomationId(box, id);
            foreach (var item in items) box.Items.Add(item);
            box.SelectedIndex = Math.Max(0, selected);
            box.SelectionChanged += delegate
            {
                select(box.SelectedIndex);
                Run(host.ApplyPerformance);
            };
            return box;
        }

        private FrameworkElement Toggle(string id, string title, string hint, bool value, Action<bool> set)
        {
            var check = new CheckBox { Content = Heading(title, hint), IsChecked = value, Margin = new Thickness(0, 10, 0, 10) };
            AutomationProperties.SetAutomationId(check, id);
            AutomationProperties.SetName(check, title);
            check.Checked += delegate { set(true); Run(host.ApplyPerformance); };
            check.Unchecked += delegate { set(false); Run(host.ApplyPerformance); };
            return check;
        }

        // 제목 + ⓘ: 설명은 ⓘ에 마우스를 올리면 툴팁으로 보인다.
        private static StackPanel Heading(string title, string hint)
        {
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var info = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2),
                BorderBrush = ModernTheme.Brush("Bae.TextDim"), Background = Brushes.Transparent,
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Help,
                Child = new TextBlock
                {
                    Text = "i", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = ModernTheme.Brush("Bae.TextDim"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0)
                },
                ToolTip = new ToolTip
                {
                    Style = ModernTheme.Style(typeof(ToolTip)),
                    Content = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 }
                }
            };
            ToolTipService.SetInitialShowDelay(info, 150);
            ToolTipService.SetShowDuration(info, 30000);
            AutomationProperties.SetName(info, hint);
            info.MouseEnter += delegate { info.BorderBrush = ModernTheme.Brush("Bae.Accent"); ((TextBlock)info.Child).Foreground = ModernTheme.Brush("Bae.Accent"); };
            info.MouseLeave += delegate { info.BorderBrush = ModernTheme.Brush("Bae.TextDim"); ((TextBlock)info.Child).Foreground = ModernTheme.Brush("Bae.TextDim"); };
            text.Children.Add(info);
            return text;
        }

        private static Border Divider() { return new Border { Style = ModernTheme.Style("Bae.DividerLine") }; }
    }
}
