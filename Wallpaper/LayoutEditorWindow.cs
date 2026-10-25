using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace Wallpaper
{
    internal sealed class LayoutEditorWindow : Window
    {
        private readonly Canvas canvas;
        private readonly Dictionary<string, Frame> frames = new Dictionary<string, Frame>();
        private readonly Action<string, WidgetPlacement> changed;
        private readonly Dictionary<string, DesktopWidget> widgets;
        private readonly Func<string, bool> isEnabled;
        private readonly Func<IEnumerable<KeyValuePair<string, string>>> widgetNames;
        private readonly Action<string, bool> toggle;
        private readonly System.Drawing.Rectangle screenBounds;
        private readonly Line verticalGuide;
        private readonly Line horizontalGuide;
        private readonly Image sharedFade = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill };
        private Frame dragging;
        private string operation;
        private Point pointerStart;
        private Rect dragStart;
        private double dpi = 1;

        // 위치·크기 조절과 위젯 켜고 끄기를 하는 편집 화면. 위젯 추가·제거·설정과 배경은 설정 창에서 한다.
        // background 는 그 화면의 배경 미리보기(움직이는 배경이면 장면의 한 프레임, 아니면 Windows 배경화면).
        // listWidgets 는 모든 모니터 공용 위젯 목록(id, 이름), onToggle 은 그 위젯을 이 화면에 놓거나 빼기.
        public LayoutEditorWindow(Forms.Screen screen, ImageSource background,
            Dictionary<string, DesktopWidget> desktopWidgets, Func<string, bool> enabled,
            Func<IEnumerable<KeyValuePair<string, string>>> listWidgets, Action<string, bool> onToggle, Action<string, WidgetPlacement> onChanged)
        {
            widgetNames = listWidgets;
            toggle = onToggle;
            changed = onChanged;
            widgets = desktopWidgets;
            isEnabled = enabled;
            screenBounds = screen.Bounds;
            Title = "바탕화면 레이아웃 편집";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = screen.Bounds.Left;
            Top = screen.Bounds.Top;
            Width = screen.Bounds.Width;
            Height = screen.Bounds.Height;
            Topmost = true;
            var layout = new Grid();
            layout.Children.Add(new Image { Source = background, Stretch = Stretch.Fill });
            canvas = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };
            canvas.Children.Add(sharedFade);
            layout.Children.Add(canvas);
            verticalGuide = Guide();
            horizontalGuide = Guide();
            canvas.Children.Add(verticalGuide);
            canvas.Children.Add(horizontalGuide);
            foreach (var entry in widgets)
                if (isEnabled(entry.Key)) AddFrame(entry.Key, entry.Value);

            // 안내 막대는 설정 창과 같은 다크 테마(Styles/ModernControls.xaml)
            var toolbar = new Border
            {
                Background = Theme.Brush("#F2202020"), BorderBrush = ModernTheme.Brush("Bae.Border"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 10, 10, 10), Margin = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top
            };
            ModernTheme.Apply(toolbar);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(new TextBlock
            {
                Text = "레이아웃 편집  ·  드래그로 이동 / 모서리로 크기 조절",
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center
            });
            var widgetMenu = new Button { Content = "위젯 ▾", Margin = new Thickness(18, 0, 0, 0) };
            AutomationProperties.SetAutomationId(widgetMenu, "ToggleWidgets");
            widgetMenu.Click += delegate { ShowWidgetMenu(widgetMenu); };
            controls.Children.Add(widgetMenu);
            var done = new Button
            {
                Content = "완료", Margin = new Thickness(8, 0, 0, 0), Style = ModernTheme.Style("AccentButton")
            };
            AutomationProperties.SetAutomationId(done, "FinishLayout");
            done.Click += delegate { Close(); };
            controls.Children.Add(done);
            toolbar.Child = controls;
            layout.Children.Add(toolbar);
            Content = layout;

            Loaded += delegate
            {
                var source = PresentationSource.FromVisual(this);
                dpi = source.CompositionTarget.TransformToDevice.M11;
                Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1),
                    screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0x0040);
                foreach (var entry in frames)
                    PositionFrame(entry.Value, widgets[entry.Key].ContentBounds, screen.Bounds);
                UpdateFade();
                DesktopWidget.SceneBackgroundChanged += UpdateFade;
            };
            Closed += delegate { DesktopWidget.SceneBackgroundChanged -= UpdateFade; };
            MouseMove += Move;
            MouseLeftButtonUp += delegate { EndDrag(); };
            LostMouseCapture += delegate
            {
                if (Mouse.Captured == this) return;
                dragging = null; HideGuides();
            };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        public void UpdateWidget(string id, BitmapSource image)
        {
            Frame frame;
            if (frames.TryGetValue(id, out frame))
            {
                frame.Image.Source = image;
                UpdatePreview(frame);
            }
        }

        public void SyncWidget(string id)
        {
            Frame frame;
            if (!frames.TryGetValue(id, out frame)) return;
            PositionFrame(frame, widgets[id].ContentBounds, screenBounds);
            if (dragging == frame) { dragStart = frame.Bounds; pointerStart = Mouse.GetPosition(canvas); }
        }

        public void RemoveWidget(string id)
        {
            Frame frame;
            if (!frames.TryGetValue(id, out frame)) return;
            if (dragging == frame) EndDrag();
            canvas.Children.Remove(frame.Root);
            frames.Remove(id);
            HideGuides();
        }

        // 위젯 켜고 끄기 메뉴. 여러 개를 연달아 바꿀 수 있게 눌러도 닫히지 않는다.
        private void ShowWidgetMenu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            ModernTheme.Apply(menu);
            menu.Style = ModernTheme.Style(typeof(ContextMenu));
            foreach (var entry in widgetNames())
            {
                var id = entry.Key;
                var item = new MenuItem { Header = entry.Value, IsCheckable = true, IsChecked = isEnabled(id), StaysOpenOnClick = true };
                AutomationProperties.SetAutomationId(item, "ToggleWidget_" + id);
                item.Click += delegate
                {
                    toggle(id, item.IsChecked);
                    item.IsChecked = isEnabled(id);
                    RefreshWidget(id);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // 위젯이 새로 켜졌거나 크기·모양이 바뀌었을 때 편집 상자를 다시 만든다.
        public void RefreshWidget(string id)
        {
            RemoveWidget(id);
            DesktopWidget widget;
            if (id == null || !isEnabled(id) || !widgets.TryGetValue(id, out widget)) return;
            AddFrame(id, widget);
            SyncWidget(id);
        }

        private void AddFrame(string name, DesktopWidget widget)
        {
            var root = new Grid { Cursor = Cursors.SizeAll };
            Panel.SetZIndex(root, widget.LayerIndex + 1);
            var image = new Image { Source = widget.ForegroundSnapshot(), Stretch = Stretch.Fill, IsHitTestVisible = false };
            var preview = new Canvas { IsHitTestVisible = false };
            preview.Children.Add(image);
            root.Children.Add(preview);
            root.Children.Add(new Border { BorderBrush = ModernTheme.Brush("Bae.Accent"), BorderThickness = new Thickness(1), Background = Brushes.Transparent });
            var frame = new Frame { Name = name, Root = root, Image = image, Widget = widget, BaseWidth = widget.BaseContentPixelWidth };
            frames[name] = frame;
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { BeginDrag(frame, "move", e); };
            var handles = widget.FreeResize ? new[] { "lt", "rt", "lb", "rb", "l", "r", "t", "b" } : new[] { "lt", "rt", "lb", "rb" };
            foreach (var corner in handles)
            {
                var handle = new Border
                {
                    Width = 12, Height = 12, Background = Brushes.White,
                    BorderBrush = ModernTheme.Brush("Bae.AccentPress"), BorderThickness = new Thickness(1),
                    HorizontalAlignment = corner.Contains('l') ? HorizontalAlignment.Left : corner.Contains('r') ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                    VerticalAlignment = corner.Contains('t') ? VerticalAlignment.Top : corner.Contains('b') ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                    Cursor = corner == "l" || corner == "r" ? Cursors.SizeWE : corner == "t" || corner == "b" ? Cursors.SizeNS
                        : corner == "lt" || corner == "rb" ? Cursors.SizeNWSE : Cursors.SizeNESW
                };
                var captured = corner;
                handle.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { BeginDrag(frame, captured, e); };
                root.Children.Add(handle);
            }
            canvas.Children.Add(root);
        }

        private void PositionFrame(Frame frame, System.Drawing.Rectangle bounds, System.Drawing.Rectangle screen)
        {
            frame.Root.Width = bounds.Width / dpi;
            frame.Root.Height = bounds.Height / dpi;
            Canvas.SetLeft(frame.Root, (bounds.Left - screen.Left) / dpi);
            Canvas.SetTop(frame.Root, (bounds.Top - screen.Top) / dpi);
            UpdatePreview(frame);
        }

        private void UpdateFade()
        {
            foreach (var frame in frames.Values) Panel.SetZIndex(frame.Root, frame.Widget.LayerIndex + 1);
            sharedFade.Width = screenBounds.Width / dpi; sharedFade.Height = screenBounds.Height / dpi;
            sharedFade.Source = DesktopWidget.SceneFade(screenBounds, dpi);
        }

        private void UpdatePreview(Frame frame)
        {
            var inset = frame.Widget.ContentPixels;
            frame.Image.Width = frame.Widget.Bounds.Width / dpi;
            frame.Image.Height = frame.Widget.Bounds.Height / dpi;
            Canvas.SetLeft(frame.Image, -inset.Left / dpi);
            Canvas.SetTop(frame.Image, -inset.Top / dpi);
        }

        private void BeginDrag(Frame frame, string action, MouseButtonEventArgs e)
        {
            dragging = frame;
            operation = action;
            pointerStart = Mouse.GetPosition(canvas);
            dragStart = frame.Bounds;
            Mouse.Capture(this);
            e.Handled = true;
        }

        private void Move(object sender, MouseEventArgs e)
        {
            if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pointer = Mouse.GetPosition(canvas);
            double dx = pointer.X - pointerStart.X, dy = pointer.Y - pointerStart.Y;
            Rect bounds;
            if (operation == "move")
            {
                bounds = new Rect(Math.Max(0, Math.Min(canvas.ActualWidth - dragStart.Width, dragStart.Left + dx)),
                    Math.Max(0, Math.Min(canvas.ActualHeight - dragStart.Height, dragStart.Top + dy)), dragStart.Width, dragStart.Height);
                double guideX, guideY;
                bounds = LayoutSnap.Move(bounds, new Size(canvas.ActualWidth, canvas.ActualHeight),
                    frames.Values.Where(f => f != dragging).Select(f => f.Bounds), out guideX, out guideY);
                ShowGuides(guideX, guideY);
            }
            else if (dragging.Widget.FreeResize)
            {
                double guideX, guideY;
                bounds = LayoutSnap.ResizeFree(dragStart, new Vector(dx, dy), new Size(canvas.ActualWidth, canvas.ActualHeight),
                    frames.Values.Where(f => f != dragging).Select(f => f.Bounds), operation, new Size(160, 100), out guideX, out guideY);
                ShowGuides(guideX, guideY);
            }
            else
            {
                bool left = operation[0] == 'l', top = operation[1] == 't';
                double ratio = dragStart.Width / dragStart.Height;
                double width = Math.Abs(dx) / dragStart.Width >= Math.Abs(dy) / dragStart.Height
                    ? dragStart.Width + (left ? -dx : dx) : (dragStart.Height + (top ? -dy : dy)) * ratio;
                double limit = Math.Min(left ? dragStart.Right : canvas.ActualWidth - dragStart.Left,
                    (top ? dragStart.Bottom : canvas.ActualHeight - dragStart.Top) * ratio);
                width = Math.Max(Math.Min(dragging.BaseWidth / dpi * 0.25, limit), Math.Min(width, limit));
                double height = width / ratio;
                bounds = new Rect(left ? dragStart.Right - width : dragStart.Left,
                    top ? dragStart.Bottom - height : dragStart.Top, width, height);
                double guideX, guideY;
                bounds = LayoutSnap.Resize(bounds, new Size(canvas.ActualWidth, canvas.ActualHeight),
                    frames.Values.Where(f => f != dragging).Select(f => f.Bounds), left, top,
                    Math.Min(dragging.BaseWidth / dpi * 0.25, limit), limit, out guideX, out guideY);
                ShowGuides(guideX, guideY);
            }
            Canvas.SetLeft(dragging.Root, bounds.Left);
            Canvas.SetTop(dragging.Root, bounds.Top);
            dragging.Root.Width = bounds.Width;
            dragging.Root.Height = bounds.Height;
            changed(dragging.Name, new WidgetPlacement
            {
                X = bounds.Left / canvas.ActualWidth,
                Y = bounds.Top / canvas.ActualHeight,
                Scale = dragging.Widget.FreeResize ? dragging.Widget.DisplayScale : bounds.Width * dpi / dragging.BaseWidth,
                Width = dragging.Widget.FreeResize ? bounds.Width / (dpi * dragging.Widget.DisplayScale) : 0,
                Height = dragging.Widget.FreeResize ? bounds.Height / (dpi * dragging.Widget.DisplayScale) : 0,
                ContentAnchored = true
            });
        }

        private void EndDrag()
        {
            if (dragging != null) SyncWidget(dragging.Name);
            dragging = null;
            Mouse.Capture(null);
            HideGuides();
        }

        private static Line Guide()
        {
            return new Line { Stroke = Theme.Brush("#FFC4A9FF"), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new[] { 5.0, 4.0 }), IsHitTestVisible = false, Visibility = Visibility.Hidden };
        }

        private void ShowGuides(double x, double y)
        {
            verticalGuide.Visibility = Double.IsNaN(x) ? Visibility.Hidden : Visibility.Visible;
            if (!Double.IsNaN(x))
            {
                verticalGuide.X1 = verticalGuide.X2 = x;
                verticalGuide.Y1 = 0; verticalGuide.Y2 = canvas.ActualHeight;
            }
            horizontalGuide.Visibility = Double.IsNaN(y) ? Visibility.Hidden : Visibility.Visible;
            if (!Double.IsNaN(y))
            {
                horizontalGuide.Y1 = horizontalGuide.Y2 = y;
                horizontalGuide.X1 = 0; horizontalGuide.X2 = canvas.ActualWidth;
            }
            Panel.SetZIndex(verticalGuide, Int32.MaxValue);
            Panel.SetZIndex(horizontalGuide, Int32.MaxValue);
        }

        private void HideGuides() { verticalGuide.Visibility = horizontalGuide.Visibility = Visibility.Hidden; }

        private sealed class Frame
        {
            public DesktopWidget Widget;
            public string Name;
            public Grid Root;
            public Image Image;
            public double BaseWidth;
            public Rect Bounds { get { return new Rect(Canvas.GetLeft(Root), Canvas.GetTop(Root), Root.Width, Root.Height); } }
        }
    }

    internal static class LayoutSnap
    {
        public static Rect Move(Rect bounds, Size area, IEnumerable<Rect> others, out double guideX, out double guideY)
        {
            List<double> x, y;
            Targets(area, others, out x, out y);
            double dx = Delta(new[] { bounds.Left + bounds.Width / 2, bounds.Left, bounds.Right }, x, out guideX);
            double dy = Delta(new[] { bounds.Top + bounds.Height / 2, bounds.Top, bounds.Bottom }, y, out guideY);
            return new Rect(bounds.Left + dx, bounds.Top + dy, bounds.Width, bounds.Height);
        }

        public static Rect Resize(Rect bounds, Size area, IEnumerable<Rect> others, bool left, bool top,
            double minimumWidth, double maximumWidth, out double guideX, out double guideY)
        {
            List<double> x, y;
            Targets(area, others, out x, out y);
            double ratio = bounds.Width / bounds.Height;
            double anchorX = left ? bounds.Right : bounds.Left;
            double anchorY = top ? bounds.Bottom : bounds.Top;
            double directionX = left ? -1 : 1, directionY = top ? -1 : 1;
            double width = bounds.Width, best = Double.PositiveInfinity;
            // A uniform resize has one degree of freedom. Snap the nearest moving edge
            // or center while keeping the opposite corner and aspect ratio fixed.
            foreach (double fraction in new[] { 1.0, 0.5 })
            {
                foreach (double target in x)
                    ResizeCandidate(anchorX + directionX * bounds.Width * fraction, target,
                        (target - anchorX) / (directionX * fraction), bounds.Width,
                        minimumWidth, maximumWidth, ref width, ref best);
                foreach (double target in y)
                    ResizeCandidate(anchorY + directionY * bounds.Height * fraction, target,
                        (target - anchorY) * ratio / (directionY * fraction), bounds.Width,
                        minimumWidth, maximumWidth, ref width, ref best);
            }
            double height = width / ratio;
            guideX = Aligned(new[] { anchorX + directionX * width, anchorX + directionX * width / 2 }, x);
            guideY = Aligned(new[] { anchorY + directionY * height, anchorY + directionY * height / 2 }, y);
            return new Rect(left ? anchorX - width : anchorX, top ? anchorY - height : anchorY, width, height);
        }

        // 메모 전용: 변을 당기면 한 축만, 모서리를 당기면 두 축을 독립적으로 바꾼다.
        public static Rect ResizeFree(Rect original, Vector delta, Size area, IEnumerable<Rect> others, string handle,
            Size minimum, out double guideX, out double guideY)
        {
            List<double> x, y;
            Targets(area, others, out x, out y);
            bool left = handle.Contains('l'), right = handle.Contains('r'), top = handle.Contains('t'), bottom = handle.Contains('b');
            double width = original.Width, height = original.Height;
            guideX = guideY = Double.NaN;
            if (left || right)
            {
                double maximum = left ? original.Right : area.Width - original.Left;
                width = FreeLength(left ? original.Right : original.Left, left ? -1 : 1, original.Width + (left ? -delta.X : delta.X),
                    Math.Min(minimum.Width, maximum), maximum, x, out guideX);
            }
            if (top || bottom)
            {
                double maximum = top ? original.Bottom : area.Height - original.Top;
                height = FreeLength(top ? original.Bottom : original.Top, top ? -1 : 1, original.Height + (top ? -delta.Y : delta.Y),
                    Math.Min(minimum.Height, maximum), maximum, y, out guideY);
            }
            return new Rect(left ? original.Right - width : original.Left, top ? original.Bottom - height : original.Top, width, height);
        }

        private static double FreeLength(double anchor, double direction, double requested, double minimum, double maximum,
            List<double> targets, out double guide)
        {
            double current = Math.Max(minimum, Math.Min(requested, maximum)), length = current, best = Double.PositiveInfinity;
            foreach (double fraction in new[] { 1.0, 0.5 })
                foreach (double target in targets)
                    ResizeCandidate(anchor + direction * current * fraction, target, (target - anchor) / (direction * fraction),
                        current, minimum, maximum, ref length, ref best);
            guide = Aligned(new[] { anchor + direction * length, anchor + direction * length / 2 }, targets);
            return length;
        }

        private static void ResizeCandidate(double source, double target, double candidate, double current,
            double minimum, double maximum, ref double width, ref double best)
        {
            double difference = Math.Abs(candidate - current);
            if (Math.Abs(target - source) >= 9 || candidate < minimum || candidate > maximum || difference >= best) return;
            width = candidate;
            best = difference;
        }

        private static double Aligned(double[] sources, List<double> targets)
        {
            foreach (double source in sources)
                foreach (double target in targets)
                    if (Math.Abs(target - source) < 0.01) return target;
            return Double.NaN;
        }

        private static void Targets(Size area, IEnumerable<Rect> others, out List<double> x, out List<double> y)
        {
            x = new List<double> { area.Width / 2, 0, area.Width };
            y = new List<double> { area.Height / 2, 0, area.Height };
            foreach (var other in others)
            {
                x.AddRange(new[] { other.Left + other.Width / 2, other.Left, other.Right });
                y.AddRange(new[] { other.Top + other.Height / 2, other.Top, other.Bottom });
            }
        }

        private static double Delta(double[] sources, List<double> targets, out double guide)
        {
            double best = 9, delta = 0;
            guide = Double.NaN;
            foreach (var source in sources)
                foreach (var target in targets)
                    if (Math.Abs(target - source) < best)
                    {
                        best = Math.Abs(target - source);
                        delta = target - source;
                        guide = target;
                    }
            return delta;
        }
    }
}
