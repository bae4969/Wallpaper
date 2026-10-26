using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wallpaper
{
    // 합성된 메모 위에 편집할 동안만 실제 입력 창을 놓는다. 저장 값은 기존 메모의 text 옵션이다.
    internal sealed class MemoEditorWindow : Window
    {
        private readonly DispatcherTimer saveTimer;
        private readonly Func<bool> save;
        private readonly TextBlock status;
        private bool pending;
        private bool closing;

        public MemoEditorWindow(System.Drawing.Rectangle bounds, string text, double fontSize, Brush color,
            TextAlignment alignment, Action<string> changed, Func<bool> saveChanges)
        {
            save = saveChanges;
            ModernTheme.Apply(this);
            Title = "메모 — 바탕화면 편집";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(160, bounds.Width);
            Height = Math.Max(100, bounds.Height);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var input = new TextBox
            {
                Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Segoe UI Variable Display, Malgun Gothic"),
                FontWeight = FontWeights.SemiBold, Foreground = color, TextAlignment = alignment,
                Padding = new Thickness(6), BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetAutomationId(input, "MemoText");
            AutomationProperties.SetName(input, "메모 내용");
            status = new TextBlock
            {
                Text = "자동 저장 · 바깥 클릭 또는 Esc로 완료", FontSize = 11,
                Foreground = ModernTheme.Brush("Bae.TextDim"), Margin = new Thickness(8, 4, 8, 5)
            };
            Grid.SetRow(status, 1);
            layout.Children.Add(input);
            layout.Children.Add(status);
            Content = new Border { BorderBrush = ModernTheme.Brush("Bae.Accent"), BorderThickness = new Thickness(1), Child = layout };
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += delegate { Flush(); };
            input.TextChanged += delegate
            {
                changed(input.Text);
                pending = true;
                status.Text = "저장 중…";
                saveTimer.Stop();
                saveTimer.Start();
            };
            Loaded += delegate
            {
                var source = PresentationSource.FromVisual(this);
                double dpi = source.CompositionTarget.TransformToDevice.M11;
                input.FontSize = fontSize / dpi;
                Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, bounds.Left, bounds.Top,
                    Math.Max((int)Math.Ceiling(160 * dpi), bounds.Width), Math.Max((int)Math.Ceiling(100 * dpi), bounds.Height), 0x0004);
                Activate();
                input.Focus();
                input.CaretIndex = input.Text.Length;
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { e.Handled = true; if (!closing) Close(); }
                else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Flush(); }
            };
            Deactivated += delegate { if (!closing) Close(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                // 닫는 중 포커스를 잃어 Deactivated가 와도 Close를 다시 호출하지 않는다.
                closing = true;
                if (!Flush()) { e.Cancel = true; closing = false; Activate(); }
            };
            Closed += delegate { saveTimer.Stop(); };
        }

        private bool Flush()
        {
            saveTimer.Stop();
            if (!pending) return true;
            if (!save()) { status.Text = "저장 실패 · Ctrl+S로 다시 시도"; return false; }
            pending = false;
            status.Text = "저장됨 · 바깥 클릭 또는 Esc로 완료";
            return true;
        }
    }
}
