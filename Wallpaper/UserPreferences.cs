using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Wallpaper
{
    internal sealed class UserPreferences
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wallpaper", "preferences.xml");

        // 모든 모니터가 함께 쓰는 위젯 목록. 위젯마다 어느 모니터에 어디에 놓였는지를 가진다(같은 위젯을 여러 모니터에 놓을 수 있다).
        public readonly List<WidgetSettings> Widgets = new List<WidgetSettings>();
        // 설정 파일이 있었는지(없으면 첫 실행)
        public bool FromFile;
        // 움직이는 배경 성능: 프레임 제한(0 = 디스플레이 주사율)과 렌더 해상도 배율(위젯은 항상 원래 해상도)
        public static readonly int[] FrameLimits = { 15, 20, 24, 30, 40, 45, 50, 60, 72, 90, 100, 120, 144, 0 };
        public static readonly double[] RenderScales = { 1, 0.9, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.33, 0.25 };
        // 효과 품질: 높은 것부터 최고·높음·보통·낮음·최저. 예전 저장값 2·1·0(높음·보통·낮음)은 최고·보통·최저로 읽힌다.
        public static readonly double[] EffectQualities = { 2, 1.5, 1, 0.5, 0 };
        public int FrameLimit = 60;
        public double RenderScale = 1;
        public double EffectQuality = 2;
        public bool PauseWhenCovered = true;     // 창이 화면을 덮으면 멈춤
        public bool PauseOnBattery = true;       // 배터리로 작동할 때 멈춤
        // 배경 세트(장면 id, 빈 값은 Windows 배경화면)와 메인 모니터(모니터 키, 빈 값이면 Windows 주 모니터). 나머지 화면은 서브 구성을 쓴다.
        public string Scene = "";
        public string MainDisplay = "";
        // 2세트로 합친 예전 서브 전용 장면 → 세트 id
        private static readonly Dictionary<string, string> MergedScenes = new Dictionary<string, string>
        {
            { "aurora-lake", "aurora-sky" }, { "neon-river", "neon-skyline" }, { "sky-mirror-reflection", "sky-mirror" }
        };

        public WidgetSettings Widget(string id)
        {
            return Widgets.FirstOrDefault(w => w.Id == id);
        }

        public static UserPreferences Load()
        {
            return Load(FilePath);
        }

        public static UserPreferences Load(string path)
        {
            var settings = new UserPreferences();
            if (!File.Exists(path)) return settings;
            settings.FromFile = true;
            try
            {
                var document = XElement.Load(path);
                var performance = document.Element("performance");
                if (performance != null)
                {
                    int fps = ReadInt(performance, "fps", 60, 0, 1000);
                    double scale, quality;
                    settings.FrameLimit = FrameLimits.Contains(fps) ? fps : 60;
                    settings.RenderScale = Double.TryParse((string)performance.Attribute("scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
                        && RenderScales.Contains(scale) ? scale : 1;
                    settings.EffectQuality = Double.TryParse((string)performance.Attribute("quality"), NumberStyles.Float, CultureInfo.InvariantCulture, out quality)
                        && EffectQualities.Contains(quality) ? quality : 2;
                    settings.PauseWhenCovered = ReadBool(performance, "pauseCovered", true);
                    settings.PauseOnBattery = ReadBool(performance, "pauseBattery", true);
                }
                var sceneElement = document.Element("scene");
                if (sceneElement != null)
                {
                    settings.Scene = (string)sceneElement.Attribute("id") ?? "";
                    settings.MainDisplay = (string)sceneElement.Attribute("main") ?? "";
                }
                var widgets = document.Element("widgets");
                if (widgets != null)
                    foreach (var element in widgets.Elements("widget"))
                    {
                        var widget = ReadWidget(element);
                        if (widget != null && settings.Widget(widget.Id) == null) settings.Widgets.Add(widget);
                    }
                else
                    settings.ReadLegacy(document, sceneElement == null);
            }
            catch (XmlException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        public void Save()
        {
            Save(FilePath);
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            new XElement("settings",
                new XElement("performance", new XAttribute("fps", FrameLimit), new XAttribute("scale", RenderScale.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("quality", EffectQuality.ToString(CultureInfo.InvariantCulture)), new XAttribute("pauseCovered", PauseWhenCovered), new XAttribute("pauseBattery", PauseOnBattery)),
                new XElement("scene", new XAttribute("id", Scene), new XAttribute("main", MainDisplay)),
                new XElement("widgets", Widgets.Select(WriteWidget))).Save(path);
        }

        // <widget id kind [type] [하드웨어 속성…]><option/>…<monitor key device enabled x y scale anchor/>…</widget>
        private static WidgetSettings ReadWidget(XElement element)
        {
            string id = (string)element.Attribute("id"), kind = (string)element.Attribute("kind");
            if (String.IsNullOrWhiteSpace(id) || !WidgetSettings.Kinds.Contains(kind)) return null;
            var widget = new WidgetSettings { Id = id, Kind = kind, Type = (string)element.Attribute("type") ?? "" };
            if (kind == "script" && widget.Type.Length == 0) return null;
            if (kind == "hardware") widget.Hardware = ReadHardware(element);
            ReadOptions(element, widget.Options);
            foreach (var spot in element.Elements("monitor"))
            {
                string key = (string)spot.Attribute("key");
                if (String.IsNullOrEmpty(key) || widget.Monitors.Any(m => m.Monitor == key)) continue;
                widget.Monitors.Add(new MonitorPlacement
                {
                    Monitor = key, Device = (string)spot.Attribute("device") ?? "",
                    Enabled = ReadEnabled(spot, true), Placement = ReadPlacement(spot)
                });
            }
            return widget;
        }

        private static XElement WriteWidget(WidgetSettings widget)
        {
            var element = new XElement("widget", new XAttribute("id", widget.Id), new XAttribute("kind", widget.Kind));
            if (widget.Kind == "script") element.Add(new XAttribute("type", widget.Type));
            if (widget.Hardware != null) WriteHardware(element, widget.Hardware);
            WithOptions(element, widget.Options);
            foreach (var spot in widget.Monitors)
            {
                var monitor = new XElement("monitor", new XAttribute("key", spot.Monitor), new XAttribute("device", spot.Device));
                monitor.Add(WritePlacement("monitor", spot.Placement, spot.Enabled).Attributes());
                element.Add(monitor);
            }
            return element;
        }

        private static HardwareWidgetSettings ReadHardware(XElement element)
        {
            return new HardwareWidgetSettings
            {
                Name = (string)element.Attribute("name") ?? "내 PC", Remote = ReadBool(element, "remote", false),
                Host = (string)element.Attribute("host") ?? "", User = (string)element.Attribute("user") ?? "",
                KeyFile = (string)element.Attribute("key") ?? "", Port = ReadInt(element, "port", 0, 0, 65535),
                JumpHost = (string)element.Attribute("jump") ?? "",
                IntervalSeconds = ReadInt(element, "interval", 2, 1, 60),
                ShowCpu = ReadBool(element, "cpu", true), ShowMemory = ReadBool(element, "memory", true),
                ShowGpu = ReadBool(element, "gpu", true), ShowVram = ReadBool(element, "vram", true),
                ShowNetwork = ReadBool(element, "network", true)
            };
        }

        private static void WriteHardware(XElement element, HardwareWidgetSettings widget)
        {
            element.Add(new XAttribute("name", widget.Name),
                new XAttribute("remote", widget.Remote), new XAttribute("host", widget.Host),
                new XAttribute("user", widget.User), new XAttribute("key", widget.KeyFile),
                new XAttribute("jump", widget.JumpHost),
                new XAttribute("port", widget.Port), new XAttribute("interval", widget.IntervalSeconds),
                new XAttribute("cpu", widget.ShowCpu), new XAttribute("memory", widget.ShowMemory),
                new XAttribute("gpu", widget.ShowGpu), new XAttribute("vram", widget.ShowVram), new XAttribute("network", widget.ShowNetwork));
        }

        // ---- 예전 형식: 화면(<display device=…>)마다 위젯을 따로 저장했다 ----
        // 위젯은 공용 목록으로 옮기고 놓인 화면은 장치 이름으로 기억한다(처음 연결될 때 모니터 키로 바뀐다).
        // 시계·달력은 설정 값이 같으면 위젯 하나로 합치고, 꺼진 것은 같은 종류가 이미 있을 때만 그 위젯에 자리를 남긴다.
        private void ReadLegacy(XElement document, bool legacyScene)
        {
            var displays = new List<KeyValuePair<string, XElement>>();
            var legacyBackgrounds = new List<string>();
            foreach (var element in document.Elements("display"))
            {
                string device = (string)element.Attribute("device");
                if (String.IsNullOrEmpty(device) || displays.Any(d => d.Key == device)) continue;
                displays.Add(new KeyValuePair<string, XElement>(device, element));
                legacyBackgrounds.Add((string)element.Attribute("background") ?? "");
            }
            // 더 이전 형식: 화면 하나의 장치 이름과 배치를 최상위에 저장했다.
            string single = document.Element("display") != null && document.Element("display").Attribute("device") == null
                ? ((string)document.Element("display")).Trim() : "";
            if (displays.Count == 0 && single.Length > 0) displays.Add(new KeyValuePair<string, XElement>(single, document));
            // 예전 형식: 화면마다 배경을 따로 골랐다. 처음 고른 장면을 세트로 옮긴다.
            if (legacyScene)
            {
                var first = legacyBackgrounds.FirstOrDefault(b => b.Length > 0) ?? "";
                string merged;
                Scene = MergedScenes.TryGetValue(first, out merged) ? merged : first;
            }

            var builtIns = new List<Tuple<string, XElement, string, bool>>();   // 종류, 요소, 장치, 켜짐
            foreach (var display in displays)
            {
                // 예전 형식의 "이 화면 사용 안 함"은 위젯을 모두 끈 상태로 옮긴다.
                bool used = ReadEnabled(display.Value, true);
                foreach (var kind in new[] { "clock", "calendar" })
                    builtIns.Add(Tuple.Create(kind, display.Value.Element(kind), display.Key, used && ReadEnabled(display.Value.Element(kind), true)));
            }
            foreach (var entry in builtIns.OrderBy(e => e.Item4 ? 0 : 1))
            {
                var options = new Dictionary<string, string>();
                ReadOptions(entry.Item2, options);
                var widget = Widgets.FirstOrDefault(w => w.Kind == entry.Item1 && SameOptions(w.Options, options))
                    ?? (entry.Item4 ? null : Widgets.FirstOrDefault(w => w.Kind == entry.Item1));
                if (widget == null)
                {
                    if (!entry.Item4) continue;
                    widget = WidgetSettings.Create(entry.Item1);
                    foreach (var option in options) widget.Options[option.Key] = option.Value;
                    Widgets.Add(widget);
                }
                widget.Monitors.Add(new MonitorPlacement { Monitor = entry.Item3, Device = entry.Item3, Enabled = entry.Item4, Placement = ReadPlacement(entry.Item2) });
            }
            foreach (var display in displays)
            {
                bool used = ReadEnabled(display.Value, true);
                var scripts = display.Value.Element("scriptWidgets");
                if (scripts != null)
                    foreach (var element in scripts.Elements("widget"))
                    {
                        string id = (string)element.Attribute("id"), type = (string)element.Attribute("type");
                        if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(type)) continue;
                        var widget = WidgetSettings.Create("script");
                        if (Widget(id) == null) widget.Id = id;
                        widget.Type = type;
                        ReadOptions(element, widget.Options);
                        AddLegacy(widget, display.Key, used && ReadEnabled(element, true), ReadPlacement(element));
                    }
                var hardware = display.Value.Element("hardwareWidgets");
                var elements = hardware != null ? hardware.Elements("widget").ToList()
                    : display.Value.Element("gpu") != null ? new List<XElement> { display.Value.Element("gpu") } : new List<XElement>();
                foreach (var element in elements)
                {
                    string id = hardware != null ? (string)element.Attribute("id") : "gpu";
                    if (String.IsNullOrWhiteSpace(id) || id == "clock" || id == "calendar") continue;
                    var widget = WidgetSettings.Create("hardware");
                    if (Widget(id) == null) widget.Id = id;
                    widget.Hardware = ReadHardware(element);
                    if (hardware == null) widget.Hardware.Name = "하드웨어 상태";
                    ReadOptions(element, widget.Options);
                    AddLegacy(widget, display.Key, used && ReadEnabled(element, hardware != null), ReadPlacement(element));
                }
            }
        }

        private void AddLegacy(WidgetSettings widget, string device, bool enabled, WidgetPlacement placement)
        {
            widget.Monitors.Add(new MonitorPlacement { Monitor = device, Device = device, Enabled = enabled, Placement = placement });
            Widgets.Add(widget);
        }

        private static bool SameOptions(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            return a.Count == b.Count && a.All(pair => { string value; return b.TryGetValue(pair.Key, out value) && value == pair.Value; });
        }

        // 위젯 설정 값: <option name="..." value="..."/>
        private static void ReadOptions(XElement element, Dictionary<string, string> options)
        {
            if (element == null) return;
            foreach (var option in element.Elements("option"))
            {
                var name = (string)option.Attribute("name");
                if (!String.IsNullOrEmpty(name)) options[name] = (string)option.Attribute("value") ?? "";
            }
        }

        private static XElement WithOptions(XElement element, Dictionary<string, string> options)
        {
            foreach (var option in options) element.Add(new XElement("option", new XAttribute("name", option.Key), new XAttribute("value", option.Value)));
            return element;
        }

        private static bool ReadBool(XElement element, string attribute, bool fallback)
        {
            bool value;
            return Boolean.TryParse((string)element.Attribute(attribute), out value) ? value : fallback;
        }

        private static int ReadInt(XElement element, string attribute, int fallback, int minimum, int maximum)
        {
            int value;
            return Int32.TryParse((string)element.Attribute(attribute), out value) && value >= minimum && value <= maximum ? value : fallback;
        }

        private static bool ReadEnabled(XElement element, bool defaultValue)
        {
            bool enabled;
            return element != null && Boolean.TryParse((string)element.Attribute("enabled"), out enabled)
                ? enabled : defaultValue;
        }

        private static WidgetPlacement ReadPlacement(XElement element)
        {
            if (element == null) return null;
            double x, y, scale;
            if (!Double.TryParse((string)element.Attribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !Double.TryParse((string)element.Attribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !Double.TryParse((string)element.Attribute("scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out scale) ||
                Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(scale) ||
                Double.IsInfinity(x) || Double.IsInfinity(y) || Double.IsInfinity(scale) ||
                x < 0 || x > 1 || y < 0 || y > 1 || scale <= 0) return null;
            return new WidgetPlacement { X = x, Y = y, Scale = scale,
                ContentAnchored = (string)element.Attribute("anchor") == "content" };
        }

        private static XElement WritePlacement(string name, WidgetPlacement placement, bool enabled)
        {
            var element = new XElement(name, new XAttribute("enabled", enabled));
            if (placement != null)
            {
                element.Add(new XAttribute("x", placement.X), new XAttribute("y", placement.Y),
                    new XAttribute("scale", placement.Scale));
                if (placement.ContentAnchored) element.Add(new XAttribute("anchor", "content"));
            }
            return element;
        }
    }

    // 공용 목록의 위젯 하나: 종류·설정 값과 모니터별 자리. 시계·달력도 다른 위젯처럼 여러 개 둘 수 있다.
    internal sealed class WidgetSettings
    {
        public static readonly string[] Kinds = { "clock", "calendar", "hardware", "script" };
        public string Id = "";
        public string Kind = "";                 // clock | calendar | hardware | script
        public string Type = "";                 // script: widgets\<Type>
        public HardwareWidgetSettings Hardware;  // hardware: 연결·표시 항목·이름
        private readonly Dictionary<string, string> options = new Dictionary<string, string>();
        // 모니터마다 자리. 꺼도 지우지 않아 다시 켜면 마지막 자리로 돌아간다. 연결이 끊긴 모니터의 자리도 보관한다.
        public readonly List<MonitorPlacement> Monitors = new List<MonitorPlacement>();

        // 위젯 설정 값(모든 모니터 공통). 하드웨어 위젯은 자기 설정(Hardware.Options)을 쓴다.
        public Dictionary<string, string> Options { get { return Hardware != null ? Hardware.Options : options; } }

        public static WidgetSettings Create(string kind)
        {
            return new WidgetSettings { Id = kind + "-" + Guid.NewGuid().ToString("N"), Kind = kind };
        }
    }

    // 위젯이 모니터 하나에 놓인 자리. Monitor 는 모니터 키(예전 설정에서 옮긴 것은 장치 이름), Device 는 마지막으로 본 장치 이름(표시용).
    internal sealed class MonitorPlacement
    {
        public string Monitor = "";
        public string Device = "";
        public bool Enabled = true;
        public WidgetPlacement Placement;
    }

    internal sealed class WidgetPlacement
    {
        public double X, Y;
        public double Scale = 1;
        public bool ContentAnchored;
    }
}
