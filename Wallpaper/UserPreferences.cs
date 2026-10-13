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

        public readonly List<DisplayLayout> Displays = new List<DisplayLayout>();
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
        // 배경 세트(장면 id, 빈 값은 Windows 배경화면)와 메인 모니터(빈 값이면 Windows 주 모니터). 나머지 화면은 서브 구성을 쓴다.
        public string Scene = "";
        public string MainDisplay = "";
        // 2세트로 합친 예전 서브 전용 장면 → 세트 id
        private static readonly Dictionary<string, string> MergedScenes = new Dictionary<string, string>
        {
            { "aurora-lake", "aurora-sky" }, { "neon-river", "neon-skyline" }, { "sky-mirror-reflection", "sky-mirror" }
        };

        public DisplayLayout Display(string device)
        {
            return Displays.FirstOrDefault(d => d.Device == device);
        }

        public static UserPreferences Load()
        {
            return Load(FilePath);
        }

        public static UserPreferences Load(string path)
        {
            var settings = new UserPreferences();
            if (!File.Exists(path)) return settings;
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
                var legacyBackgrounds = new List<string>();
                foreach (var element in document.Elements("display"))
                {
                    string device = (string)element.Attribute("device");
                    if (String.IsNullOrEmpty(device) || settings.Display(device) != null) continue;
                    var layout = ReadLayout(element);
                    layout.Device = device;
                    // 예전 형식의 "이 화면 사용 안 함"은 위젯을 모두 끈 상태로 옮긴다.
                    if (!ReadEnabled(element, true))
                    {
                        layout.ClockEnabled = layout.CalendarEnabled = false;
                        foreach (var widget in layout.Hardware) widget.Enabled = false;
                    }
                    legacyBackgrounds.Add((string)element.Attribute("background") ?? "");
                    settings.Displays.Add(layout);
                }
                // 이전 형식: 화면 하나의 장치 이름과 배치를 최상위에 저장했다.
                string legacy = document.Element("display") != null && document.Element("display").Attribute("device") == null
                    ? ((string)document.Element("display")).Trim() : "";
                if (settings.Displays.Count == 0 && legacy.Length > 0)
                {
                    var layout = ReadLayout(document);
                    layout.Device = legacy;
                    settings.Displays.Add(layout);
                }
                // 예전 형식: 화면마다 배경을 따로 골랐다. 처음 고른 장면을 세트로 옮긴다.
                if (sceneElement == null)
                {
                    var first = legacyBackgrounds.FirstOrDefault(b => b.Length > 0) ?? "";
                    string merged;
                    settings.Scene = MergedScenes.TryGetValue(first, out merged) ? merged : first;
                }
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
                Displays.Select(layout =>
                new XElement("display", new XAttribute("device", layout.Device),
                    WithOptions(WritePlacement("clock", layout.Clock, layout.ClockEnabled), layout.ClockOptions),
                    WithOptions(WritePlacement("calendar", layout.Calendar, layout.CalendarEnabled), layout.CalendarOptions),
                    new XElement("scriptWidgets", layout.Scripts.Select(widget =>
                    {
                        var element = WritePlacement("widget", widget.Placement, widget.Enabled);
                        element.Add(new XAttribute("id", widget.Id), new XAttribute("type", widget.Type));
                        return WithOptions(element, widget.Options);
                    })),
                    new XElement("hardwareWidgets", layout.Hardware.Select(WriteHardware))))).Save(path);
        }

        private static DisplayLayout ReadLayout(XElement parent)
        {
            var layout = new DisplayLayout
            {
                Clock = ReadPlacement(parent.Element("clock")), Calendar = ReadPlacement(parent.Element("calendar")),
                ClockEnabled = ReadEnabled(parent.Element("clock"), true), CalendarEnabled = ReadEnabled(parent.Element("calendar"), true)
            };
            ReadOptions(parent.Element("clock"), layout.ClockOptions);
            ReadOptions(parent.Element("calendar"), layout.CalendarOptions);
            var scripts = parent.Element("scriptWidgets");
            if (scripts != null)
                foreach (var element in scripts.Elements("widget"))
                {
                    string id = (string)element.Attribute("id"), type = (string)element.Attribute("type");
                    if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(type) || layout.Scripts.Any(w => w.Id == id)) continue;
                    var widget = new ScriptWidgetSettings { Id = id, Type = type, Enabled = ReadEnabled(element, true), Placement = ReadPlacement(element) };
                    ReadOptions(element, widget.Options);
                    layout.Scripts.Add(widget);
                }
            var hardware = parent.Element("hardwareWidgets");
            if (hardware != null)
            {
                foreach (var element in hardware.Elements("widget"))
                {
                    string id = (string)element.Attribute("id");
                    if (String.IsNullOrWhiteSpace(id) || id == "clock" || id == "calendar" || layout.Hardware.Any(w => w.Id == id)) continue;
                    var widget = new HardwareWidgetSettings
                    {
                        Id = id, Name = (string)element.Attribute("name") ?? "내 PC",
                        Enabled = ReadEnabled(element, true), Remote = ReadBool(element, "remote", false),
                        Host = (string)element.Attribute("host") ?? "", User = (string)element.Attribute("user") ?? "",
                        KeyFile = (string)element.Attribute("key") ?? "", Port = ReadInt(element, "port", 0, 0, 65535),
                        JumpHost = (string)element.Attribute("jump") ?? "",
                        IntervalSeconds = ReadInt(element, "interval", 2, 1, 60),
                        ShowCpu = ReadBool(element, "cpu", true), ShowMemory = ReadBool(element, "memory", true),
                        ShowGpu = ReadBool(element, "gpu", true), ShowVram = ReadBool(element, "vram", true),
                        ShowNetwork = ReadBool(element, "network", true), Placement = ReadPlacement(element)
                    };
                    ReadOptions(element, widget.Options);
                    layout.Hardware.Add(widget);
                }
            }
            else if (parent.Element("gpu") != null)
                layout.Hardware.Add(new HardwareWidgetSettings
                {
                    Id = "gpu", Name = "하드웨어 상태", Enabled = ReadEnabled(parent.Element("gpu"), false),
                    Placement = ReadPlacement(parent.Element("gpu"))
                });
            return layout;
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

        private static XElement WriteHardware(HardwareWidgetSettings widget)
        {
            var element = WritePlacement("widget", widget.Placement, widget.Enabled);
            element.Add(new XAttribute("id", widget.Id), new XAttribute("name", widget.Name),
                new XAttribute("remote", widget.Remote), new XAttribute("host", widget.Host),
                new XAttribute("user", widget.User), new XAttribute("key", widget.KeyFile),
                new XAttribute("jump", widget.JumpHost),
                new XAttribute("port", widget.Port), new XAttribute("interval", widget.IntervalSeconds),
                new XAttribute("cpu", widget.ShowCpu), new XAttribute("memory", widget.ShowMemory),
                new XAttribute("gpu", widget.ShowGpu), new XAttribute("vram", widget.ShowVram), new XAttribute("network", widget.ShowNetwork));
            return WithOptions(element, widget.Options);
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

    // 모니터 한 대의 위젯 구성. 연결이 끊겨도 보관한다(배경은 UserPreferences.Scene 세트 하나).
    internal sealed class DisplayLayout
    {
        public string Device = "";
        public WidgetPlacement Clock;
        public WidgetPlacement Calendar;
        public bool ClockEnabled = true;
        public bool CalendarEnabled = true;
        public readonly List<HardwareWidgetSettings> Hardware = new List<HardwareWidgetSettings>();
        public readonly Dictionary<string, string> ClockOptions = new Dictionary<string, string>();
        public readonly Dictionary<string, string> CalendarOptions = new Dictionary<string, string>();
        public readonly List<ScriptWidgetSettings> Scripts = new List<ScriptWidgetSettings>();

        public bool IsEnabled(string id)
        {
            if (id == "clock") return ClockEnabled;
            if (id == "calendar") return CalendarEnabled;
            var script = Scripts.FirstOrDefault(w => w.Id == id);
            if (script != null) return script.Enabled;
            var widget = Hardware.FirstOrDefault(w => w.Id == id);
            return widget != null && widget.Enabled;
        }

        public void SetEnabled(string id, bool enabled)
        {
            if (id == "clock") ClockEnabled = enabled;
            else if (id == "calendar") CalendarEnabled = enabled;
            else if (Scripts.Any(w => w.Id == id)) Scripts.First(w => w.Id == id).Enabled = enabled;
            else Hardware.First(w => w.Id == id).Enabled = enabled;
        }

        public WidgetPlacement Placement(string id)
        {
            if (id == "clock") return Clock;
            if (id == "calendar") return Calendar;
            if (Scripts.Any(w => w.Id == id)) return Scripts.First(w => w.Id == id).Placement;
            return Hardware.First(w => w.Id == id).Placement;
        }

        public void SetPlacement(string id, WidgetPlacement placement)
        {
            if (id == "clock") Clock = placement;
            else if (id == "calendar") Calendar = placement;
            else if (Scripts.Any(w => w.Id == id)) Scripts.First(w => w.Id == id).Placement = placement;
            else Hardware.First(w => w.Id == id).Placement = placement;
        }

        // 위젯 설정 값(시계·달력·스크립트 위젯). 하드웨어 위젯은 자기 설정 창을 쓴다.
        public Dictionary<string, string> Options(string id)
        {
            if (id == "clock") return ClockOptions;
            if (id == "calendar") return CalendarOptions;
            var script = Scripts.FirstOrDefault(w => w.Id == id);
            if (script != null) return script.Options;
            var hardware = Hardware.FirstOrDefault(w => w.Id == id);
            return hardware != null ? hardware.Options : null;
        }
    }

    // 스크립트 위젯 인스턴스: 종류(widgets\<Type>) + 배치 + 설정 값
    internal sealed class ScriptWidgetSettings
    {
        public string Id = "script-" + Guid.NewGuid().ToString("N");
        public string Type = "";
        public bool Enabled = true;
        public WidgetPlacement Placement;
        public readonly Dictionary<string, string> Options = new Dictionary<string, string>();
    }

    internal sealed class WidgetPlacement
    {
        public double X, Y;
        public double Scale = 1;
        public bool ContentAnchored;
    }
}
