using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Wallpaper.Scripting;

namespace Wallpaper
{
    // 위젯 설정 값 하나의 정의. 스크립트 위젯(widget.json 의 params)과 내장 시계·달력이 같이 쓴다.
    //   type: bool | choice | color | number | text
    internal sealed class WidgetParameter
    {
        public string Name, Label, Description, Type, Default;
        public List<KeyValuePair<string, string>> Choices = new List<KeyValuePair<string, string>>();   // 보이는 이름 → 값
        public double Min = Double.NegativeInfinity, Max = Double.PositiveInfinity;

        public static WidgetParameter Parse(string name, JsonElement element)
        {
            var parameter = new WidgetParameter { Name = name, Label = Text(element, "label", name), Description = Text(element, "description", ""), Type = Text(element, "type", "text") };
            if (!new[] { "bool", "choice", "color", "number", "text" }.Contains(parameter.Type))
                throw new InvalidDataException(name + ": type 은 bool, choice, color, number, text 중 하나여야 합니다(" + parameter.Type + ").");
            JsonElement value;
            if (element.TryGetProperty("choices", out value))
            {
                if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) parameter.Choices.Add(new KeyValuePair<string, string>(item.GetString(), item.GetString()));
                else if (value.ValueKind == JsonValueKind.Object)
                    foreach (var item in value.EnumerateObject()) parameter.Choices.Add(new KeyValuePair<string, string>(item.Name, item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.GetRawText()));
            }
            if (parameter.Type == "choice" && parameter.Choices.Count == 0) throw new InvalidDataException(name + ": choice 는 choices 가 필요합니다.");
            if (element.TryGetProperty("min", out value) && value.ValueKind == JsonValueKind.Number) parameter.Min = value.GetDouble();
            if (element.TryGetProperty("max", out value) && value.ValueKind == JsonValueKind.Number) parameter.Max = value.GetDouble();
            if (element.TryGetProperty("default", out value))
                parameter.Default = value.ValueKind == JsonValueKind.String ? value.GetString()
                    : value.ValueKind == JsonValueKind.True ? "true" : value.ValueKind == JsonValueKind.False ? "false" : value.GetRawText();
            else parameter.Default = parameter.Type == "bool" ? "false" : parameter.Type == "color" ? "#FFFFFF" : parameter.Type == "number" ? "0"
                : parameter.Type == "choice" ? parameter.Choices[0].Key : "";
            return parameter;
        }

        public static WidgetParameter Make(string type, string name, string label, string defaultValue, string description, params string[] choices)
        {
            return new WidgetParameter
            {
                Type = type, Name = name, Label = label, Default = defaultValue, Description = description,
                Choices = choices.Select(c => new KeyValuePair<string, string>(c, c)).ToList()
            };
        }

        private static string Text(JsonElement element, string name, string fallback)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallback;
        }

        // 저장된 문자열(없으면 기본값)을 형식에 맞는 값으로 바꾼다: bool, double, Brush(색), 선택지 값, 문자열
        public object Value(IDictionary<string, string> values)
        {
            string raw;
            if (values == null || !values.TryGetValue(Name, out raw) || raw == null) raw = Default;
            switch (Type)
            {
                case "bool": return String.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
                case "number":
                    double number;
                    if (!Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                        !Double.TryParse(Default, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) number = 0;
                    return Math.Max(Min, Math.Min(Max, number));
                case "color": return Brush(raw) ?? Brush(Default) ?? Brushes.White;
                case "choice":
                    var choice = Choices.FirstOrDefault(c => c.Key == raw);
                    if (choice.Key == null) choice = Choices.FirstOrDefault(c => c.Key == Default);
                    return choice.Key != null ? choice.Value : Choices[0].Value;
                default: return raw ?? "";
            }
        }

        public static SolidColorBrush Brush(string text)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
                brush.Freeze();
                return brush;
            }
            catch (FormatException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (NullReferenceException) { return null; }
        }
    }

    // 내장 위젯(시계·달력)의 설정 정의
    internal static class BuiltInWidgetOptions
    {
        public static readonly WidgetParameter[] Clock =
        {
            WidgetParameter.Make("bool", "hour24", "24시간제", "true", "끄면 12시간제(01~12)로 표시합니다."),
            WidgetParameter.Make("bool", "seconds", "초 표시", "true", "끄면 시·분만 표시하고 위젯 폭이 줄어듭니다."),
            WidgetParameter.Make("bool", "flip", "넘김 애니메이션", "true", "숫자가 바뀔 때 카드가 넘어가는 애니메이션입니다."),
            WidgetParameter.Make("color", "color", "숫자 색", "#F0F1F4", "")
        };

        public static readonly WidgetParameter[] Calendar =
        {
            WidgetParameter.Make("choice", "weekStart", "주 시작 요일", "일요일", "", "일요일", "월요일"),
            WidgetParameter.Make("bool", "otherMonth", "지난·다음 달 날짜", "true", "이번 달 앞뒤 날짜를 흐리게 표시합니다."),
            WidgetParameter.Make("color", "textColor", "글자 색", "#F0F1F4", "제목과 평일 날짜 색입니다. 주말 색은 고정입니다."),
            WidgetParameter.Make("color", "todayColor", "오늘 강조 색", "#F0F1F4", "오늘 날짜 원의 색입니다.")
        };

        // 모든 위젯 공통: 뒤에 깔리는 어두운 그라데이션
        public static readonly WidgetParameter[] Common =
        {
            new WidgetParameter { Type = "number", Name = "shadowOpacity", Label = "배경 진하기", Default = "38", Min = 0, Max = 100,
                Description = "위젯 뒤 어두운 그라데이션의 진하기(%)입니다. 0이면 배경 없이 내용만 보입니다." },
            new WidgetParameter { Type = "number", Name = "shadowRange", Label = "그라데이션 범위", Default = "110", Min = 0, Max = 128,
                Description = "내용 가장자리에서 그라데이션이 사라질 때까지의 거리(px, 0~128)입니다." }
        };

        public static void ApplyShadow(DesktopWidget widget, IDictionary<string, string> values)
        {
            widget.SetShadow((double)Common[0].Value(values) / 100, (double)Common[1].Value(values));
        }

        public static WidgetParameter[] For(string id)
        {
            return id == "clock" ? Clock : id == "calendar" ? Calendar : null;
        }

        public static void Apply(FlipClock clock, IDictionary<string, string> values)
        {
            clock.Configure((bool)Clock[0].Value(values), (bool)Clock[1].Value(values), (bool)Clock[2].Value(values), (Brush)Clock[3].Value(values));
        }

        public static void Apply(CalendarView calendar, IDictionary<string, string> values)
        {
            calendar.Configure((string)Calendar[0].Value(values) == "월요일", (bool)Calendar[1].Value(values), (Brush)Calendar[2].Value(values), (Brush)Calendar[3].Value(values));
        }
    }

    // 스크립트 위젯: Wallpapers\widgets\<id>\widget.json + widget.xaml
    internal sealed class WidgetScript
    {
        public const string ScriptNamespace = "clr-namespace:Wallpaper.Scripting;assembly=Wallpaper";
        public string Id, Name, Description, Xaml;
        public double Width = 300, Height = 150, Interval = 1;
        public bool Metrics;
        public readonly List<WidgetParameter> Parameters = new List<WidgetParameter>();

        public static string Folder { get { return Path.Combine(SceneLibrary.Root, "widgets"); } }

        public static List<WidgetScript> All(List<string> errors = null)
        {
            var list = new List<WidgetScript>();
            if (!Directory.Exists(Folder)) return list;
            foreach (var directory in Directory.GetDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                try { list.Add(Load(Path.GetFileName(directory))); }
                catch (Exception error) { if (errors != null) errors.Add("widgets\\" + Path.GetFileName(directory) + ": " + error.Message); }
            }
            return list;
        }

        public static WidgetScript Load(string id)
        {
            var directory = Directory.Exists(id) ? id : Path.Combine(Folder, id);
            var json = Path.Combine(directory, "widget.json");
            var xaml = Path.Combine(directory, "widget.xaml");
            if (!File.Exists(json)) throw new FileNotFoundException("위젯 정의가 없습니다: " + json);
            if (!File.Exists(xaml)) throw new FileNotFoundException("위젯 XAML이 없습니다: " + xaml);
            var definition = new WidgetScript { Id = Path.GetFileName(directory.TrimEnd('\\', '/')), Xaml = File.ReadAllText(xaml, Encoding.UTF8) };
            using (var document = JsonDocument.Parse(File.ReadAllText(json, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var root = document.RootElement;
                JsonElement value;
                definition.Name = root.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : definition.Id;
                definition.Description = root.TryGetProperty("description", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
                if (root.TryGetProperty("width", out value) && value.ValueKind == JsonValueKind.Number) definition.Width = Math.Max(20, value.GetDouble());
                if (root.TryGetProperty("height", out value) && value.ValueKind == JsonValueKind.Number) definition.Height = Math.Max(20, value.GetDouble());
                if (root.TryGetProperty("interval", out value) && value.ValueKind == JsonValueKind.Number) definition.Interval = Math.Max(0.1, value.GetDouble());
                if (root.TryGetProperty("metrics", out value)) definition.Metrics = value.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("params", out value))
                    foreach (var property in value.EnumerateObject()) definition.Parameters.Add(WidgetParameter.Parse(property.Name, property.Value));
            }
            CheckXaml(definition.Xaml);
            return definition;
        }

        // XAML 은 코드를 실행하도록 꾸밀 수도 있어 위험한 요소를 거절한다(내 폴더의 파일만 읽지만, 받은 위젯 파일 대비).
        private static void CheckXaml(string text)
        {
            foreach (var word in new[] { "x:Class", "x:Code", "ObjectDataProvider", "XmlDataProvider", "WebBrowser", "<Frame", "siteoforigin" })
                if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("위젯 XAML에 쓸 수 없는 요소가 있습니다: " + word);
            foreach (Match match in Regex.Matches(text, "clr-namespace:[^\"']*"))
                if (match.Value != ScriptNamespace)
                    throw new InvalidDataException("위젯 XAML에서는 xmlns 로 \"" + ScriptNamespace + "\" 만 쓸 수 있습니다: " + match.Value);
            foreach (Match match in Regex.Matches(text, "(\\w[\\w:.]*)\\s*=\\s*[\"']\\s*https?://", RegexOptions.IgnoreCase))
                if (!match.Groups[1].Value.StartsWith("xmlns", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("위젯 XAML에서 인터넷 주소를 읽을 수 없습니다: " + match.Groups[1].Value);
        }

        // 화면 요소: 그림자 여백(128) 안에 XAML 을 width×height 로 둔다. DataContext 는 data.
        public Border Build(WidgetData data)
        {
            FrameworkElement content;
            try { content = (FrameworkElement)XamlReader.Parse(Xaml); }
            catch (XamlParseException error) { throw new InvalidDataException("widget.xaml " + error.LineNumber + "줄 " + error.LinePosition + "칸: " + error.Message, error); }
            catch (InvalidCastException) { throw new InvalidDataException("widget.xaml 의 최상위 요소는 화면 요소(Grid, Border, StackPanel 등)여야 합니다."); }
            content.Width = Width;
            content.Height = Height;
            const double margin = 128;
            return new Border { Width = Width + margin * 2, Height = Height + margin * 2, Padding = new Thickness(margin), Child = content, DataContext = data };
        }
    }

    // 로컬 PC의 하드웨어 측정(스크립트 위젯 공용). 필요한 위젯이 있을 때만 1초마다 측정한다(GPU는 2초마다).
    internal static class LocalMetrics
    {
        private static readonly object gate = new object();
        private static Thread thread;
        private static int users;
        public static volatile HardwareSnapshot Latest;

        public static void Acquire()
        {
            lock (gate)
            {
                users++;
                if (thread != null) return;
                thread = new Thread(Run) { IsBackground = true, Name = "LocalMetrics" };
                thread.Start();
            }
        }

        public static void Release()
        {
            lock (gate) users = Math.Max(0, users - 1);
        }

        private static void Run()
        {
            var monitor = new HardwareMonitor();
            GpuSnapshot gpu = null;
            for (int tick = 0; ; tick++)
            {
                bool active;
                lock (gate) active = users > 0;
                if (active)
                {
                    var snapshot = new HardwareSnapshot { System = monitor.ReadSystem() };
                    if (gpu == null || tick % 2 == 0) gpu = GpuMonitor.Read();
                    snapshot.Gpu = gpu;
                    Latest = snapshot;
                }
                Thread.Sleep(1000);
            }
        }
    }
}

namespace Wallpaper.Scripting
{
    // 위젯 XAML 의 DataContext. {Binding Now}, {Binding Params[color]}, {Binding Cpu} 처럼 쓴다.
    public sealed class WidgetData : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public DateTime Now { get; private set; }
        public IDictionary<string, object> Params { get; private set; }
        public IDictionary<string, Color> ParamColors { get; private set; }

        public double Cpu { get; private set; }
        public double CpuRatio { get { return Cpu / 100; } }
        public double Ram { get; private set; }
        public double RamRatio { get { return Ram / 100; } }
        public double RamUsedGB { get; private set; }
        public double RamTotalGB { get; private set; }
        public double Download { get; private set; }
        public double Upload { get; private set; }
        public string DownloadText { get { return Converters.FormatRate(Download); } }
        public string UploadText { get { return Converters.FormatRate(Upload); } }
        public IList<GpuInfo> Gpus { get; private set; }
        public GpuInfo Gpu { get { return Gpus.Count > 0 ? Gpus[0] : null; } }
        public bool HasGpu { get { return Gpus.Count > 0; } }

        public WidgetData()
        {
            Now = DateTime.Now;
            Params = new Dictionary<string, object>();
            ParamColors = new Dictionary<string, Color>();
            Gpus = new List<GpuInfo>();
        }

        internal void SetParams(IEnumerable<Wallpaper.WidgetParameter> parameters, IDictionary<string, string> values)
        {
            var typed = new Dictionary<string, object>();
            var colors = new Dictionary<string, Color>();
            foreach (var parameter in parameters)
            {
                var value = parameter.Value(values);
                typed[parameter.Name] = value;
                var brush = value as SolidColorBrush;
                if (brush != null) colors[parameter.Name] = brush.Color;
            }
            Params = typed;
            ParamColors = colors;
            Changed();
        }

        internal void Update(DateTime now, Wallpaper.HardwareSnapshot snapshot)
        {
            Now = now;
            if (snapshot != null)
            {
                var system = snapshot.System;
                Cpu = system.Cpu ?? 0;
                Ram = system.MemoryPercent ?? 0;
                RamUsedGB = (system.MemoryUsedBytes ?? 0) / 1073741824.0;
                RamTotalGB = (system.MemoryTotalBytes ?? 0) / 1073741824.0;
                Download = system.DownloadBytes ?? 0;
                Upload = system.UploadBytes ?? 0;
                Gpus = (snapshot.Gpu != null ? snapshot.Gpu.Devices : new Wallpaper.GpuSample[0]).Select(d => new GpuInfo
                {
                    Name = d.Name, Usage = d.Utilization ?? 0, MemoryUsedGB = (d.MemoryUsed ?? 0) / 1024, MemoryTotalGB = (d.MemoryTotal ?? 0) / 1024,
                    Temperature = d.Temperature ?? 0, Power = d.Power ?? 0
                }).ToList();
            }
            Changed();
        }

        private void Changed()
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(String.Empty));
        }
    }

    public sealed class GpuInfo
    {
        public string Name { get; set; }
        public double Usage { get; set; }
        public double UsageRatio { get { return Usage / 100; } }
        public double MemoryUsedGB { get; set; }
        public double MemoryTotalGB { get; set; }
        public double MemoryPercent { get { return MemoryTotalGB > 0 ? MemoryUsedGB / MemoryTotalGB * 100 : 0; } }
        public double MemoryRatio { get { return MemoryPercent / 100; } }
        public double Temperature { get; set; }
        public double Power { get; set; }
    }

    // XAML 에서 {x:Static w:Converters.Format} 처럼 쓰는 변환기
    public static class Converters
    {
        // MultiBinding: [값, 형식 문자열] → 값.ToString(형식). 예: Now + "HH:mm:ss"
        public static readonly IMultiValueConverter Format = new FormatConverter();
        // bool → Visible/Collapsed, 반대
        public static readonly IValueConverter Visible = new VisibleConverter(false);
        public static readonly IValueConverter Hidden = new VisibleConverter(true);
        // 바이트/초 → "12.3 MB/s"
        public static readonly IValueConverter Rate = new RateConverter();

        public static string FormatRate(double bytes)
        {
            string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        private sealed class FormatConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Length == 0 || values[0] == null || values[0] == DependencyProperty.UnsetValue) return "";
                var format = values.Length > 1 ? values[1] as string : parameter as string;
                if (values.Length > 1 && String.IsNullOrEmpty(format)) return "";   // 형식이 비면(예: "표시 안 함") 아무것도 표시하지 않는다
                var formattable = values[0] as IFormattable;
                return formattable != null && !String.IsNullOrEmpty(format) ? formattable.ToString(format, CultureInfo.CurrentCulture) : values[0].ToString();
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }

        private sealed class VisibleConverter : IValueConverter
        {
            private readonly bool invert;
            public VisibleConverter(bool invert) { this.invert = invert; }

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                bool on = value is bool ? (bool)value : value != null;
                return on != invert ? Visibility.Visible : Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }

        private sealed class RateConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return value is double ? FormatRate((double)value) : "";
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
        }
    }
}
