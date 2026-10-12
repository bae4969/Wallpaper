using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Wallpaper
{
    internal static class Theme
    {
        public static readonly Brush White = Brush("#FFF0F1F4");
        public static readonly Brush Muted = Brush("#FF9EA4B3");
        public static readonly Brush Sunday = Brush("#FFF09898");
        public static readonly Brush Saturday = Brush("#FF9DB9E8");
        public const double FadeMargin = 112, FadeOpacity = 96.0 / 255;
        private static readonly int[] DitherMatrix = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        public static double Dither(int x, int y) { return (DitherMatrix[((y & 3) << 2) + (x & 3)] + 0.5) / 16.0; }

        public static double FadeAlpha(double width, double height, double x, double y, double margin)
        {
            double horizontal = Math.Min(1, Math.Max(0, Math.Min(x, width - x) / margin));
            double vertical = Math.Min(1, Math.Max(0, Math.Min(y, height - y) / margin));
            return FadeOpacity * horizontal * horizontal * (3 - 2 * horizontal) * vertical * vertical * (3 - 2 * vertical);
        }

        public static Brush Fade(double width, double height)
        {
            int w = (int)Math.Ceiling(width), h = (int)Math.Ceiling(height);
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    pixels[(y * w + x) * 4 + 3] = (byte)Math.Floor(FadeAlpha(width, height, x + 0.5, y + 0.5, FadeMargin) * 255 + Dither(x, y));
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            bitmap.Freeze();
            var cached = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            cached.Freeze();
            return cached;
        }

        public static Brush Brush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string text, double size, Brush color)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                FontSize = size,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
    }

    public sealed class FlipClock : Grid
    {
        private readonly StackPanel numbers;
        private FlipDigit[] digits;
        private string displayed;
        private bool hour24 = true, seconds = true, flip = true;
        private Brush color = Theme.White;

        public FlipClock()
        {
            Height = 408;
            numbers = new StackPanel
            {
                Orientation = Orientation.Horizontal, Height = 152,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Children.Add(numbers);
            Build();
        }

        // 숫자가 차지하는 폭(초 표시 692, 숨김 450). 이동·크기 조절 박스가 이 폭을 감싼다.
        public double ContentWidth { get { return numbers.Width; } }

        // 위젯 설정: 24시간제, 초 표시, 넘김 애니메이션, 숫자 색
        public void Configure(bool use24Hour, bool showSeconds, bool animate, Brush digitColor)
        {
            bool rebuild = showSeconds != seconds || !Equals(digitColor, color);
            hour24 = use24Hour;
            seconds = showSeconds;
            flip = animate;
            color = digitColor;
            if (rebuild) Build();
            displayed = null;
            UpdateTime(DateTime.Now, false);
        }

        private void Build()
        {
            numbers.Children.Clear();
            digits = new FlipDigit[seconds ? 6 : 4];
            for (int i = 0; i < digits.Length; i++)
            {
                if (i == 2 || i == 4)
                {
                    var colon = Theme.Text(":", 64, color);
                    colon.Width = 34;
                    colon.Margin = new Thickness(0, -12, 0, 0);
                    numbers.Children.Add(colon);
                }
                digits[i] = new FlipDigit(color);
                digits[i].Margin = new Thickness(5, 8, 5, 8);
                numbers.Children.Add(digits[i]);
            }
            numbers.Width = digits.Length * 104 + (digits.Length / 2 - 1) * 34;
            Width = numbers.Width + 256;
            Background = Theme.Fade(Width, Height);
            displayed = null;
        }

        public bool UpdateTime(DateTime time, bool animate)
        {
            var text = time.ToString(hour24 ? "HHmmss" : "hhmmss", CultureInfo.InvariantCulture).Substring(0, digits.Length);
            if (text == displayed) return false;
            for (int i = 0; i < digits.Length; i++) digits[i].SetDigit(text[i].ToString(), animate && flip);
            displayed = text;
            return true;
        }

        public bool UpdateAnimation()
        {
            long timestamp = Stopwatch.GetTimestamp();
            bool active = false;
            foreach (var digit in digits) active |= digit.UpdateAnimation(timestamp);
            return active;
        }
    }

    internal sealed class FlipDigit : Grid
    {
        private const double CardWidth = 94;
        private const double CardHeight = 136;
        private const double HalfHeight = CardHeight / 2;
        private readonly HalfCard top;
        private readonly HalfCard bottom;
        private readonly HalfCard fallingTop;
        private readonly HalfCard openingBottom;
        private readonly ScaleTransform topScale = new ScaleTransform(1, 1);
        private readonly ScaleTransform bottomScale = new ScaleTransform(1, 0);
        private string current;
        private long animationStarted;
        private bool animating;

        public FlipDigit(Brush color)
        {
            Width = CardWidth;
            Height = CardHeight;
            top = new HalfCard(true, color);
            bottom = new HalfCard(false, color);
            fallingTop = new HalfCard(true, color);
            openingBottom = new HalfCard(false, color);
            Children.Add(top);
            Children.Add(bottom);
            Children.Add(fallingTop);
            Children.Add(openingBottom);

            fallingTop.RenderTransform = topScale;
            fallingTop.RenderTransformOrigin = new Point(0.5, 1);
            openingBottom.RenderTransform = bottomScale;
            openingBottom.RenderTransformOrigin = new Point(0.5, 0);
            fallingTop.Visibility = Visibility.Hidden;
            openingBottom.Visibility = Visibility.Hidden;
            top.Visibility = Visibility.Visible;
            bottom.Visibility = Visibility.Visible;

        }

        public void SetDigit(string next, bool animate)
        {
            if (current == next) return;
            animating = false;
            top.Visibility = Visibility.Visible;
            bottom.Visibility = Visibility.Visible;
            fallingTop.Visibility = Visibility.Hidden;
            openingBottom.Visibility = Visibility.Hidden;

            if (current == null || !animate)
            {
                top.SetText(next);
                bottom.SetText(next);
                current = next;
                return;
            }

            top.SetText(next);
            bottom.SetText(current);
            top.Visibility = Visibility.Hidden;
            fallingTop.SetText(current);
            openingBottom.SetText(next);
            fallingTop.Visibility = Visibility.Visible;
            openingBottom.Visibility = Visibility.Visible;
            topScale.ScaleY = 1;
            bottomScale.ScaleY = 0;

            animationStarted = Stopwatch.GetTimestamp();
            animating = true;
            current = next;
        }

        public bool UpdateAnimation(long timestamp)
        {
            if (!animating) return false;
            double elapsed = (timestamp - animationStarted) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < 140)
            {
                double progress = elapsed / 140;
                topScale.ScaleY = 1 - progress * progress;
            }
            else if (elapsed < 300)
            {
                top.Visibility = Visibility.Visible;
                bottom.Visibility = Visibility.Hidden;
                fallingTop.Visibility = Visibility.Hidden;
                double progress = (elapsed - 140) / 160;
                bottomScale.ScaleY = 1 - (1 - progress) * (1 - progress);
            }
            else
            {
                bottom.SetText(current);
                top.Visibility = Visibility.Visible;
                bottom.Visibility = Visibility.Visible;
                fallingTop.Visibility = Visibility.Hidden;
                openingBottom.Visibility = Visibility.Hidden;
                topScale.ScaleY = 1;
                bottomScale.ScaleY = 0;
                animating = false;
            }
            return animating;
        }

        private sealed class HalfCard : Border
        {
            private readonly TextBlock label;

            public HalfCard(bool upper, Brush color)
            {
                Width = CardWidth;
                Height = HalfHeight;
                VerticalAlignment = upper ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                ClipToBounds = true;

                var canvas = new Canvas { ClipToBounds = true };
                var fullCard = new Grid { Width = CardWidth, Height = CardHeight };
                label = Theme.Text("0", 108, color);
                label.FontFamily = new FontFamily("Bahnschrift, Segoe UI");
                label.FontWeight = FontWeights.SemiBold;
                label.RenderTransform = new TranslateTransform(0, DigitOffset);
                TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
                fullCard.Children.Add(label);
                Canvas.SetTop(fullCard, upper ? 0 : -HalfHeight);
                canvas.Children.Add(fullCard);
                Child = canvas;
            }

            public void SetText(string value) { label.Text = value; }

            // 숫자 모양(잉크)의 세로 중심이 카드 가운데(가로줄)에 오도록 줄 상자 기준 가운데 정렬을 보정한다.
            private static readonly double DigitOffset = MeasureDigitOffset();

            private static double MeasureDigitOffset()
            {
                var typeface = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                var text = new FormattedText("0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 108, Brushes.White, 1.0);
                var ink = text.BuildGeometry(new Point(0, 0)).Bounds;
                double lineTop = (CardHeight - text.Height) / 2;
                return Math.Round(HalfHeight - (lineTop + ink.Top + ink.Height / 2));
            }
        }
    }

    public static class CalendarMath
    {
        public static DateTime[] Dates(DateTime date, bool mondayFirst = false)
        {
            var first = new DateTime(date.Year, date.Month, 1);
            var start = first.AddDays(-(mondayFirst ? ((int)first.DayOfWeek + 6) % 7 : (int)first.DayOfWeek));
            var dates = new DateTime[42];
            for (int i = 0; i < dates.Length; i++) dates[i] = start.AddDays(i);
            return dates;
        }
    }

    public sealed class CalendarView : Border
    {
        private readonly TextBlock title;
        private readonly Grid week;
        private readonly Grid days;
        private DateTime shown = DateTime.Today;
        private bool mondayFirst, otherMonth = true;
        private Brush textColor = Theme.White, todayColor = Theme.White;

        public CalendarView()
        {
            // Keep the 112px fade outside the calendar content, with a little extra breathing room.
            Width = 534;
            Height = 574;
            Padding = new Thickness(128);
            Background = Theme.Fade(Width, Height);

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            layout.RowDefinitions.Add(new RowDefinition());
            title = Theme.Text("", 24, Theme.White);
            title.FontWeight = FontWeights.SemiBold;
            title.HorizontalAlignment = HorizontalAlignment.Left;
            title.VerticalAlignment = VerticalAlignment.Top;
            layout.Children.Add(title);

            week = new Grid();
            for (int column = 0; column < 7; column++) week.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(week, 1);
            layout.Children.Add(week);

            days = new Grid();
            for (int i = 0; i < 7; i++) days.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 6; i++) days.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(days, 2);
            layout.Children.Add(days);
            Child = layout;
            BuildWeek();
        }

        // 위젯 설정: 주 시작 요일, 지난·다음 달 날짜 표시, 글자 색, 오늘 강조 색
        public void Configure(bool startMonday, bool showOtherMonth, Brush text, Brush today)
        {
            mondayFirst = startMonday;
            otherMonth = showOtherMonth;
            textColor = text;
            todayColor = today;
            title.Foreground = textColor;
            BuildWeek();
            UpdateDate(shown);
        }

        private void BuildWeek()
        {
            week.Children.Clear();
            var names = new[] { "일", "월", "화", "수", "목", "금", "토" };
            for (int column = 0; column < 7; column++)
            {
                int day = mondayFirst ? (column + 1) % 7 : column;
                var color = day == 0 ? Theme.Sunday : day == 6 ? Theme.Saturday : Theme.Muted;
                var name = Theme.Text(names[day], 12, color);
                Grid.SetColumn(name, column);
                week.Children.Add(name);
            }
        }

        public void UpdateDate(DateTime today)
        {
            shown = today;
            title.Text = today.ToString("yyyy년 M월", CultureInfo.InvariantCulture);
            days.Children.Clear();
            var dates = CalendarMath.Dates(today, mondayFirst);
            // 강조 색이 밝으면 오늘 숫자를 어둡게, 어두우면 밝게
            var highlight = todayColor as SolidColorBrush;
            bool bright = highlight == null || highlight.Color.R * 0.299 + highlight.Color.G * 0.587 + highlight.Color.B * 0.114 > 140;
            for (int i = 0; i < dates.Length; i++)
            {
                var date = dates[i];
                var inMonth = date.Month == today.Month;
                var isToday = date.Date == today.Date;
                if (!inMonth && !otherMonth) continue;
                var color = !inMonth ? Theme.Brush("#FF555C6B") : date.DayOfWeek == DayOfWeek.Sunday ? Theme.Sunday
                    : date.DayOfWeek == DayOfWeek.Saturday ? Theme.Saturday : textColor;
                var text = Theme.Text(date.Day.ToString(CultureInfo.InvariantCulture), 16,
                    isToday ? (bright ? Theme.Brush("#FF20232B") : Theme.White) : color);
                text.FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal;
                var cell = new Border
                {
                    Width = 34, Height = 34,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    CornerRadius = new CornerRadius(17),
                    Background = isToday ? todayColor : Brushes.Transparent,
                    Child = text
                };
                Grid.SetRow(cell, i / 7);
                Grid.SetColumn(cell, i % 7);
                days.Children.Add(cell);
            }
        }
    }
}
