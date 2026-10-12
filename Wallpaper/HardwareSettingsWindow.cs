using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Wallpaper
{
    // 하드웨어 상태 위젯 설정: 연결 대상·갱신 간격·표시 항목 + 모양(배경 진하기·그라데이션 범위). 설정 창과 같은 다크 테마.
    // 켜고 끄기는 레이아웃 편집 창에서 한다.
    internal sealed class HardwareSettingsWindow : Window
    {
        private readonly HardwareWidgetSettings original;
        private readonly TextBox name, host, user, port, key, interval, jump, shadowOpacity, shadowRange;
        private readonly ComboBox source;
        private readonly CheckBox cpu, memory, gpu, vram, network;
        private readonly StackPanel remote;
        private readonly TextBlock status;
        private readonly Button test;
        private HardwareFeed testing;
        private bool closed;
        public HardwareWidgetSettings Result { get; private set; }

        public HardwareSettingsWindow(HardwareWidgetSettings options)
        {
            original = options.Copy();
            ModernTheme.Apply(this);
            Title = "하드웨어 상태 위젯 설정";
            Width = 500; Height = 680; MinHeight = 420;
            MaxHeight = SystemParameters.VirtualScreenHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize; Topmost = true;
            var layout = new Grid { Margin = new Thickness(24, 20, 24, 18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock { Text = "하드웨어 상태", Style = ModernTheme.Style("Bae.Text.Title"), Margin = new Thickness(0, 0, 0, 16) });
            var fields = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            name = Field(fields, "이름", options.Name, "HardwareName");
            source = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
            var localChoice = new ComboBoxItem { Content = "내 PC" };
            var remoteChoice = new ComboBoxItem { Content = "리눅스 PC · SSH" };
            AutomationProperties.SetAutomationId(localChoice, "HardwareSourceLocal");
            AutomationProperties.SetAutomationId(remoteChoice, "HardwareSourceRemote");
            source.Items.Add(localChoice); source.Items.Add(remoteChoice);
            AutomationProperties.SetAutomationId(source, "HardwareSource");
            fields.Children.Add(Label("연결 대상")); fields.Children.Add(source);
            remote = new StackPanel();
            host = Field(remote, "SSH 주소 또는 호스트 이름", options.Host, "HardwareHost");
            jump = Field(remote, "경유 SSH 호스트 (선택 · 예: nas 또는 사용자@주소:포트)", options.JumpHost, "HardwareJumpHost");
            user = Field(remote, "사용자 (선택)", options.User, "HardwareUser");
            port = Field(remote, "포트 (비워두면 SSH 설정 사용)", options.Port == 0 ? "" : options.Port.ToString(), "HardwarePort");
            key = Field(remote, "접속 키 파일 (선택 · 경유 시 NAS 접속용)", options.KeyFile, "HardwareKey");
            var browse = Button("찾아보기", "BrowseHardwareKey");
            browse.HorizontalAlignment = HorizontalAlignment.Right;
            browse.Margin = new Thickness(0, -6, 0, 14);
            browse.Click += delegate
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "SSH 키 파일 선택", CheckFileExists = true };
                if (picker.ShowDialog(this) == true) key.Text = picker.FileName;
            };
            remote.Children.Add(browse);
            fields.Children.Add(remote);
            source.SelectionChanged += delegate { remote.Visibility = source.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; };
            source.SelectedIndex = options.Remote ? 1 : 0;
            interval = Field(fields, "갱신 간격 (초, 1~60)", options.IntervalSeconds.ToString(), "HardwareInterval");
            fields.Children.Add(Label("표시 항목"));
            var items = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            cpu = Check(items, "CPU", options.ShowCpu, "ShowHardwareCpu");
            memory = Check(items, "RAM", options.ShowMemory, "ShowHardwareMemory");
            gpu = Check(items, "GPU", options.ShowGpu, "ShowHardwareGpu");
            vram = Check(items, "VRAM", options.ShowVram, "ShowHardwareVram");
            network = Check(items, "다운로드·업로드", options.ShowNetwork, "ShowHardwareNetwork");
            fields.Children.Add(items);
            var common = BuiltInWidgetOptions.Common;
            shadowOpacity = Field(fields, common[0].Label + " (0~100%)", Number(common[0], options.Options), "Option_" + common[0].Name);
            shadowRange = Field(fields, common[1].Label + " (0~128px)", Number(common[1], options.Options), "Option_" + common[1].Name);
            var scroller = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
            var bottom = new StackPanel();
            status = new TextBlock { Style = ModernTheme.Style("Bae.Text.Hint"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(status, "HardwareSettingsStatus");
            bottom.Children.Add(status);
            bottom.Children.Add(new Border { Style = ModernTheme.Style("Bae.DividerLine"), Margin = new Thickness(0, 14, 0, 14) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            test = Button("연결 확인", "TestHardwareConnection"); test.Click += Test;
            var cancel = Button("취소", "CancelHardwareSettings"); cancel.IsCancel = true;
            var save = Button("저장", "SaveHardwareSettings"); save.IsDefault = true;
            save.Style = ModernTheme.Style("AccentButton");
            save.Click += delegate
            {
                var settings = Read();
                if (settings == null) return;
                Result = settings; DialogResult = true;
            };
            buttons.Children.Add(test); buttons.Children.Add(cancel); buttons.Children.Add(save);
            bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
            Content = layout;
            Closed += delegate { closed = true; if (testing != null) testing.Dispose(); };
        }

        private static string Number(WidgetParameter parameter, IDictionary<string, string> values)
        {
            return ((double)parameter.Value(values)).ToString(CultureInfo.InvariantCulture);
        }

        private HardwareWidgetSettings Read()
        {
            int seconds, sshPort = 0;
            double opacity, range;
            if (!Int32.TryParse(interval.Text, out seconds)) { ShowStatus("갱신 간격을 숫자로 입력해 주세요."); return null; }
            if (source.SelectedIndex == 1 && port.Text.Trim().Length > 0 && (!Int32.TryParse(port.Text, out sshPort) || sshPort < 1))
            { ShowStatus("포트를 확인해 주세요."); return null; }
            if (!Double.TryParse(shadowOpacity.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out opacity) ||
                !Double.TryParse(shadowRange.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out range))
            { ShowStatus("배경 진하기·그라데이션 범위를 숫자로 입력해 주세요."); return null; }
            var settings = original.Copy();
            settings.Name = name.Text.Trim(); settings.Remote = source.SelectedIndex == 1;
            settings.Host = host.Text.Trim(); settings.User = user.Text.Trim(); settings.Port = sshPort;
            settings.JumpHost = jump.Text.Trim();
            settings.KeyFile = key.Text.Trim(); settings.IntervalSeconds = seconds;
            settings.ShowCpu = cpu.IsChecked == true; settings.ShowMemory = memory.IsChecked == true;
            settings.ShowGpu = gpu.IsChecked == true; settings.ShowVram = vram.IsChecked == true;
            settings.ShowNetwork = network.IsChecked == true;
            var common = BuiltInWidgetOptions.Common;
            settings.Options = new Dictionary<string, string>(original.Options);
            settings.Options[common[0].Name] = Math.Max(common[0].Min, Math.Min(common[0].Max, opacity)).ToString(CultureInfo.InvariantCulture);
            settings.Options[common[1].Name] = Math.Max(common[1].Min, Math.Min(common[1].Max, range)).ToString(CultureInfo.InvariantCulture);
            string error = HardwareFeed.Validate(settings);
            if (error != null) { ShowStatus(error); return null; }
            return settings;
        }

        private async void Test(object sender, RoutedEventArgs e)
        {
            var settings = Read();
            if (settings == null) return;
            test.IsEnabled = false; ShowStatus("연결 확인 중…");
            var result = new TaskCompletionSource<HardwareSnapshot>();
            var feed = new HardwareFeed(settings);
            testing = feed;
            feed.Updated += sample => result.TrySetResult(sample);
            feed.Start();
            try
            {
                var finished = await Task.WhenAny(result.Task, Task.Delay(15000));
                if (closed) return;
                string message = finished == result.Task ? result.Task.Result.Error : "응답 지연";
                if (message == "SSH 호스트 키 확인 필요")
                    message += " · 터미널에서 해당 PC에 SSH 접속해 호스트 키를 확인해 주세요.";
                ShowStatus(message ?? "연결됨");
            }
            finally
            {
                feed.Dispose();
                if (testing == feed) testing = null;
                if (!closed) test.IsEnabled = true;
            }
        }

        private void ShowStatus(string message) { status.Text = message; status.Visibility = Visibility.Visible; }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Style = ModernTheme.Style("Bae.Text.Label"), Margin = new Thickness(2, 0, 0, 6) };
        }

        private static TextBox Field(Panel parent, string label, string value, string id)
        {
            parent.Children.Add(Label(label));
            var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 14) };
            AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, label);
            parent.Children.Add(box); return box;
        }

        private static CheckBox Check(Panel parent, string label, bool value, string id)
        {
            var checkbox = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 16, 8) };
            AutomationProperties.SetAutomationId(checkbox, id); parent.Children.Add(checkbox); return checkbox;
        }

        private static Button Button(string label, string id)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(button, id); return button;
        }
    }
}
