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
        private readonly Canvas gapGuides = new Canvas { IsHitTestVisible = false };
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
            Panel.SetZIndex(gapGuides, Int32.MaxValue);
            canvas.Children.Add(gapGuides);
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
                Text = "레이아웃 편집  ·  드래그로 이동 · 간격/여백 맞춤 / 모서리로 크기 조절",
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
                List<LayoutGap> gaps;
                bounds = LayoutSnap.Move(bounds, new Size(canvas.ActualWidth, canvas.ActualHeight),
                    frames.Values.Where(f => f != dragging).Select(f => f.Bounds), out guideX, out guideY, out gaps);
                ShowGuides(guideX, guideY);
                ShowGaps(gaps);
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
            gapGuides.Children.Clear();
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

        private void ShowGaps(IEnumerable<LayoutGap> gaps)
        {
            var brush = Theme.Brush("#FFC4A9FF");
            foreach (var gap in gaps)
            {
                bool horizontal = gap.Start.Y == gap.End.Y;
                gapGuides.Children.Add(new Line { X1 = gap.Start.X, Y1 = gap.Start.Y, X2 = gap.End.X, Y2 = gap.End.Y,
                    Stroke = brush, StrokeThickness = 1 });
                foreach (var end in new[] { gap.Start, gap.End })
                    gapGuides.Children.Add(new Line
                    {
                        X1 = end.X - (horizontal ? 0 : 4), X2 = end.X + (horizontal ? 0 : 4),
                        Y1 = end.Y - (horizontal ? 4 : 0), Y2 = end.Y + (horizontal ? 4 : 0),
                        Stroke = brush, StrokeThickness = 1
                    });
                var label = new Border
                {
                    Background = Theme.Brush("#EE202020"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 2, 4, 2),
                    Child = new TextBlock { Text = (gap.Length * dpi).ToString("0") + " px", FontSize = 11, Foreground = brush }
                };
                label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                double x = (gap.Start.X + gap.End.X) / 2 + (horizontal ? -label.DesiredSize.Width / 2 : 7);
                double y = (gap.Start.Y + gap.End.Y) / 2 - (horizontal ? label.DesiredSize.Height + 5 : label.DesiredSize.Height / 2);
                Canvas.SetLeft(label, Math.Max(0, Math.Min(canvas.ActualWidth - label.DesiredSize.Width, x)));
                Canvas.SetTop(label, Math.Max(0, Math.Min(canvas.ActualHeight - label.DesiredSize.Height, y)));
                gapGuides.Children.Add(label);
            }
        }

        private void HideGuides()
        {
            verticalGuide.Visibility = horizontalGuide.Visibility = Visibility.Hidden;
            gapGuides.Children.Clear();
        }

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

    internal sealed class LayoutGap
    {
        public readonly Point Start, End;
        public double Length { get { return (End - Start).Length; } }
        public LayoutGap(Point start, Point end) { Start = start; End = end; }
    }

    internal static class LayoutSnap
    {
        public static Rect Move(Rect bounds, Size area, IEnumerable<Rect> otherBounds,
            out double guideX, out double guideY, out List<LayoutGap> gaps)
        {
            var others = otherBounds.ToList();
            List<double> x, y;
            Targets(area, others, out x, out y);
            double dx = Delta(new[] { bounds.Left + bounds.Width / 2, bounds.Left, bounds.Right }, x,
                -bounds.Left, area.Width - bounds.Right, out guideX);
            double dy = Delta(new[] { bounds.Top + bounds.Height / 2, bounds.Top, bounds.Bottom }, y,
                -bounds.Top, area.Height - bounds.Bottom, out guideY);
            double left = SnapGap(bounds.Left, dx, guideX, GapTargets(bounds, area, others, true));
            double top = SnapGap(bounds.Top, dy, guideY, GapTargets(bounds, area, others, false));
            var result = new Rect(left, top, bounds.Width, bounds.Height);
            // 다른 축의 스냅으로 행·열 관계가 달라졌으면 간격 스냅을 취소한다.
            if ((Math.Abs(left - bounds.Left - dx) > 0.01 && !HasGap(result, area, others, true)) ||
                (Math.Abs(top - bounds.Top - dy) > 0.01 && !HasGap(result, area, others, false)))
                result = new Rect(bounds.Left + dx, bounds.Top + dy, bounds.Width, bounds.Height);
            guideX = Aligned(new[] { result.Left + result.Width / 2, result.Left, result.Right }, x);
            guideY = Aligned(new[] { result.Top + result.Height / 2, result.Top, result.Bottom }, y);
            gaps = new List<LayoutGap>();
            foreach (bool horizontal in new[] { true, false })
            {
                var target = GapTargets(result, area, others, horizontal).FirstOrDefault(t => Math.Abs(t.Position - Start(result, horizontal)) < 0.01);
                if (target != null)
                    foreach (var gap in target.Guides)
                        if (!gaps.Any(g => g.Start == gap.Start && g.End == gap.End)) gaps.Add(gap);
            }
            return result;
        }

        private sealed class GapTarget
        {
            public double Position;
            public LayoutGap[] Guides;
        }

        private static double Start(Rect rect, bool horizontal) { return horizontal ? rect.Left : rect.Top; }
        private static double End(Rect rect, bool horizontal) { return horizontal ? rect.Right : rect.Bottom; }
        private static LayoutGap Gap(bool horizontal, double start, double end, double cross)
        {
            return horizontal ? new LayoutGap(new Point(start, cross), new Point(end, cross))
                : new LayoutGap(new Point(cross, start), new Point(cross, end));
        }

        private static bool Available(Rect rect, Size area, List<Rect> others)
        {
            return rect.Left >= 0 && rect.Top >= 0 && rect.Right <= area.Width && rect.Bottom <= area.Height &&
                !others.Any(other => Math.Min(rect.Right, other.Right) > Math.Max(rect.Left, other.Left) &&
                    Math.Min(rect.Bottom, other.Bottom) > Math.Max(rect.Top, other.Top));
        }

        private static List<GapTarget> GapTargets(Rect bounds, Size area, List<Rect> others, bool horizontal)
        {
            var targets = new List<GapTarget>();
            double length = End(bounds, horizontal) - Start(bounds, horizontal);
            double extent = horizontal ? area.Width : area.Height;
            void Add(double position, LayoutGap first, LayoutGap second)
            {
                var candidate = new Rect(horizontal ? position : bounds.Left, horizontal ? bounds.Top : position, bounds.Width, bounds.Height);
                if (Available(candidate, area, others)) targets.Add(new GapTarget { Position = position, Guides = new[] { first, second } });
            }
            // 같은 행·열의 이웃만 본다. 중간 위젯을 건너뛴 간격에는 붙이지 않는다.
            var row = others.Where(other => Math.Min(End(bounds, !horizontal), End(other, !horizontal)) >
                Math.Max(Start(bounds, !horizontal), Start(other, !horizontal))).OrderBy(other => Start(other, horizontal)).ToList();
            for (int i = 0; i + 1 < row.Count; i++)
            {
                var first = row[i]; var second = row[i + 1];
                double crossStart = Math.Max(Start(bounds, !horizontal), Math.Max(Start(first, !horizontal), Start(second, !horizontal)));
                double crossEnd = Math.Min(End(bounds, !horizontal), Math.Min(End(first, !horizontal), End(second, !horizontal)));
                double distance = Start(second, horizontal) - End(first, horizontal);
                if (crossEnd <= crossStart || distance <= 0) continue;
                double cross = (crossStart + crossEnd) / 2;
                double middleGap = (distance - length) / 2;
                if (middleGap > 0)
                {
                    double middle = End(first, horizontal) + middleGap;
                    Add(middle, Gap(horizontal, End(first, horizontal), middle, cross),
                        Gap(horizontal, middle + length, Start(second, horizontal), cross));
                }
                double before = Start(first, horizontal) - distance - length;
                Add(before, Gap(horizontal, before + length, Start(first, horizontal), cross),
                    Gap(horizontal, End(first, horizontal), Start(second, horizontal), cross));
                double after = End(second, horizontal) + distance;
                Add(after, Gap(horizontal, End(first, horizontal), Start(second, horizontal), cross),
                    Gap(horizontal, End(second, horizontal), after, cross));
            }
            // 각 위젯이 가까운 화면 가장자리에서 띄운 여백을 가로·세로 어느 가장자리에도 맞춘다.
            foreach (var other in others)
            {
                if (other.Left < 0 || other.Top < 0 || other.Right > area.Width || other.Bottom > area.Height) continue;
                var references = new[]
                {
                    other.Left <= area.Width - other.Right
                        ? Gap(true, 0, other.Left, other.Top + other.Height / 2)
                        : Gap(true, other.Right, area.Width, other.Top + other.Height / 2),
                    other.Top <= area.Height - other.Bottom
                        ? Gap(false, 0, other.Top, other.Left + other.Width / 2)
                        : Gap(false, other.Bottom, area.Height, other.Left + other.Width / 2)
                };
                foreach (var reference in references)
                {
                    double margin = reference.Length;
                    if (margin <= 0 || margin > (extent - length) / 2) continue;
                    double cross = (Start(bounds, !horizontal) + End(bounds, !horizontal)) / 2;
                    Add(margin, Gap(horizontal, 0, margin, cross), reference);
                    Add(extent - length - margin, Gap(horizontal, extent - margin, extent, cross), reference);
                }
            }
            return targets;
        }

        private static double SnapGap(double start, double alignmentDelta, double alignmentGuide, List<GapTarget> targets)
        {
            double best = Double.IsNaN(alignmentGuide) ? 9 : Math.Abs(alignmentDelta);
            double position = start + alignmentDelta;
            foreach (var target in targets)
                if (Math.Abs(target.Position - start) < best)
                {
                    best = Math.Abs(target.Position - start);
                    position = target.Position;
                }
            return position;
        }

        private static bool HasGap(Rect bounds, Size area, List<Rect> others, bool horizontal)
        {
            return GapTargets(bounds, area, others, horizontal).Any(t => Math.Abs(t.Position - Start(bounds, horizontal)) < 0.01);
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

        private static double Delta(double[] sources, List<double> targets, double minimum, double maximum, out double guide)
        {
            double best = 9, delta = 0;
            guide = Double.NaN;
            foreach (var source in sources)
                foreach (var target in targets)
                    if (target - source >= minimum && target - source <= maximum && Math.Abs(target - source) < best)
                    {
                        best = Math.Abs(target - source);
                        delta = target - source;
                        guide = target;
                    }
            return delta;
        }
    }
}
