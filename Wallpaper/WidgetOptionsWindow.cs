using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wallpaper
{
    // 위젯 설정 창: 값 정의(WidgetParameter)로 입력 칸을 만들고, 바꾸면 바로 values 에 넣고 changed 를 부른다.
    internal sealed class WidgetOptionsWindow : Window
    {
        private readonly IList<WidgetParameter> parameters;
        private readonly Dictionary<string, string> values;
        private readonly Action changed;
        private readonly StackPanel rows = new StackPanel();
        private bool loading;

        public WidgetOptionsWindow(string title, IList<WidgetParameter> parameterList, Dictionary<string, string> optionValues, Action onChanged)
        {
            parameters = parameterList;
            values = optionValues;
            changed = onChanged;
            ModernTheme.Apply(this);
            Title = title + " — 위젯 설정";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            var layout = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
            layout.Children.Add(new TextBlock { Text = title, Style = ModernTheme.Style("Bae.Text.Title") });
            layout.Children.Add(new Border { Style = ModernTheme.Style("Bae.Card"), Padding = new Thickness(16, 6, 14, 6), Margin = new Thickness(0, 16, 0, 0), Child = rows });
            layout.Children.Add(new Border { Style = ModernTheme.Style("Bae.DividerLine"), Margin = new Thickness(0, 18, 0, 14) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var reset = new Button { Content = "기본값", Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(reset, "ResetOptions");
            reset.Click += delegate { values.Clear(); Build(); changed(); };
            var close = new Button { Content = "닫기", IsCancel = true, IsDefault = true };
            AutomationProperties.SetAutomationId(close, "CloseOptions");
            close.Click += delegate { Close(); };
            buttons.Children.Add(reset);
            buttons.Children.Add(close);
            layout.Children.Add(buttons);
            Content = layout;
            Build();
        }

        private void Build()
        {
            loading = true;
            rows.Children.Clear();
            foreach (var parameter in parameters)
            {
                if (rows.Children.Count > 0) rows.Children.Add(new Border { Style = ModernTheme.Style("Bae.DividerLine") });
                rows.Children.Add(Row(parameter));
            }
            if (parameters.Count == 0) rows.Children.Add(new TextBlock { Text = "설정할 항목이 없습니다.", Style = ModernTheme.Style("Bae.Text.Subtitle"), Margin = new Thickness(0, 10, 0, 10) });
            loading = false;
        }

        private string Current(WidgetParameter parameter)
        {
            string value;
            return values.TryGetValue(parameter.Name, out value) ? value : parameter.Default;
        }

        private void Set(WidgetParameter parameter, string value)
        {
            if (loading) return;
            values[parameter.Name] = value;
            changed();
        }

        private FrameworkElement Row(WidgetParameter parameter)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = parameter.Label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (!String.IsNullOrEmpty(parameter.Description))
                label.Children.Add(new TextBlock
                {
                    Text = "ⓘ", Margin = new Thickness(6, 0, 0, 0), Foreground = ModernTheme.Brush("Bae.TextDim"), VerticalAlignment = VerticalAlignment.Center,
                    Cursor = System.Windows.Input.Cursors.Help,
                    ToolTip = new ToolTip { Style = ModernTheme.Style(typeof(ToolTip)), Content = new TextBlock { Text = parameter.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 } }
                });
            FrameworkElement control = Control(parameter);
            AutomationProperties.SetAutomationId(control, "Option_" + parameter.Name);
            AutomationProperties.SetName(control, parameter.Label);
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(label);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private FrameworkElement Control(WidgetParameter parameter)
        {
            var current = Current(parameter);
            switch (parameter.Type)
            {
                case "bool":
                    var check = new CheckBox { IsChecked = String.Equals(current, "true", StringComparison.OrdinalIgnoreCase) };
                    check.Checked += delegate { Set(parameter, "true"); };
                    check.Unchecked += delegate { Set(parameter, "false"); };
                    return check;
                case "choice":
                    var combo = new ComboBox { Width = 170 };
                    foreach (var choice in parameter.Choices) combo.Items.Add(choice.Key);
                    combo.SelectedIndex = Math.Max(0, parameter.Choices.FindIndex(c => c.Key == current));
                    combo.SelectionChanged += delegate { if (combo.SelectedIndex >= 0) Set(parameter, parameter.Choices[combo.SelectedIndex].Key); };
                    return combo;
                case "color":
                    var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = ModernTheme.Brush("Bae.BorderHover"),
                        Background = WidgetParameter.Brush(current) ?? Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0) };
                    var colorBox = new TextBox { Width = 110, Text = current };
                    colorBox.TextChanged += delegate
                    {
                        var brush = WidgetParameter.Brush(colorBox.Text.Trim());
                        colorBox.BorderBrush = brush == null ? ModernTheme.Brush("Bae.Danger") : ModernTheme.Brush("Bae.Border");
                        if (brush == null) return;
                        swatch.Background = brush;
                        Set(parameter, colorBox.Text.Trim());
                    };
                    var colorPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    colorPanel.Children.Add(swatch);
                    colorPanel.Children.Add(colorBox);
                    return colorPanel;
                case "number":
                    var numberBox = new TextBox { Width = 110, Text = current };
                    numberBox.TextChanged += delegate
                    {
                        double number;
                        bool valid = Double.TryParse(numberBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                        numberBox.BorderBrush = valid ? ModernTheme.Brush("Bae.Border") : ModernTheme.Brush("Bae.Danger");
                        if (valid) Set(parameter, Math.Max(parameter.Min, Math.Min(parameter.Max, number)).ToString(CultureInfo.InvariantCulture));
                    };
                    return numberBox;
                default:
                    var textBox = new TextBox { Width = 200, Text = current };
                    textBox.TextChanged += delegate { Set(parameter, textBox.Text); };
                    return textBox;
            }
        }
    }
}
