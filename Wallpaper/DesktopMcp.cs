using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wallpaper
{
    internal sealed class McpDisplay
    {
        public string Id { get; set; }
        public string Device { get; set; }
        public bool Main { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool SceneReady { get; set; }
        public string[] VisibleWidgets { get; set; } = Array.Empty<string>();
        public string[] FailedWidgets { get; set; } = Array.Empty<string>();
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Drawing.Rectangle Bounds => new System.Drawing.Rectangle(X, Y, Width, Height);
    }

    internal sealed class DesktopMcpContext
    {
        public readonly WidgetMcpContext Widgets;
        public readonly Func<McpDisplay[]> Displays;
        public readonly Func<bool> IsEditing;
        public readonly Action RefreshWidgets, RefreshScenes;
        public readonly string ScenesFolder;

        public DesktopMcpContext(WidgetMcpContext widgets, Func<McpDisplay[]> displays, Func<bool> isEditing,
            Action refreshWidgets, Action refreshScenes, string scenesFolder = null)
        {
            Widgets = widgets; Displays = displays; IsEditing = isEditing;
            RefreshWidgets = refreshWidgets; RefreshScenes = refreshScenes;
            ScenesFolder = scenesFolder ?? SceneLibrary.ScenesFolder;
        }

        public void RequireIdle()
        {
            if (IsEditing()) throw new McpException("바탕화면이나 설정을 편집 중입니다. 편집을 마친 뒤 다시 시도해 주세요.");
        }

        // 화면에 반영하기 전에 원자적 저장을 완료한다. 저장 실패 시 메모리만 되돌리면 된다.
        public void Commit(Action change, Action undo)
        {
            change();
            try { Widgets.Save(); }
            catch (Exception error)
            {
                undo();
                SceneLibrary.LogError("MCP 설정 저장 실패: " + error.Message);
                throw new McpException("설정을 저장하지 못했습니다. 기존 설정을 유지합니다.");
            }
        }

        // 저장 이후 화면 생성이 실패한 경우, 저장 결과와 화면 오류를 함께 알린다.
        public string Refresh(Action action)
        {
            try { action(); return null; }
            catch (Exception error)
            {
                SceneLibrary.LogError("MCP 화면 반영 실패: " + error);
                return error.Message;
            }
        }

        public static string Revision(object state) => Hash(JsonSerializer.Serialize(state));
        public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        public static void Match(string actual, string expected)
        {
            if (!String.Equals(actual, expected, StringComparison.Ordinal))
                throw new McpException("읽은 뒤 내용이 바뀌었습니다. 다시 조회한 revision으로 요청해 주세요.");
        }

        public static void Identifier(string id)
        {
            if (String.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !Char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new McpException("ID는 영문·숫자·하이픈·밑줄로 1~100자까지 입력해 주세요.");
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(id.ToUpperInvariant()))
                throw new McpException("이 ID는 Windows 예약 이름입니다.");
        }

        public object Status() => new { sceneId = Widgets.Preferences.Scene, monitors = Displays() };
    }

    internal sealed class DesktopMcpTools : IWidgetMcpFeature
    {
        private readonly DesktopMcpContext desktop;
        private WidgetMcpContext Context => desktop.Widgets;
        public DesktopMcpTools(DesktopMcpContext desktop) { this.desktop = desktop; }
        public bool Supports(WidgetSettings widget) => true;
        public string[] Tools => new[] { "widget_read", "widget_update", "widget_remove", "widget_place" };
        public void Register(IMcpServerBuilder builder) { builder.WithTools(this); }

        private static readonly WidgetParameter[] HardwareParameters =
        {
            WidgetParameter.Make("text", "name", "이름", "내 PC", ""),
            new WidgetParameter { Type = "number", Name = "intervalSeconds", Label = "갱신 간격(초)", Default = "2", Min = 1, Max = 60 },
            WidgetParameter.Make("bool", "showCpu", "CPU 표시", "true", ""),
            WidgetParameter.Make("bool", "showMemory", "RAM 표시", "true", ""),
            WidgetParameter.Make("bool", "showGpu", "GPU 표시", "true", ""),
            WidgetParameter.Make("bool", "showVram", "VRAM 표시", "true", ""),
            WidgetParameter.Make("bool", "showNetwork", "네트워크 표시", "true", "")
        };

        private static WidgetParameter[] Parameters(string kind, string type)
        {
            var own = kind == "script" ? WidgetScript.Load(type).Parameters.ToArray()
                : kind == "hardware" ? HardwareParameters : BuiltInWidgetOptions.For(kind);
            return (own ?? Array.Empty<WidgetParameter>()).Concat(BuiltInWidgetOptions.Common).ToArray();
        }

        private static object Parameter(WidgetParameter p) => new
        {
            name = p.Name, type = p.Type, label = p.Label, description = p.Description, defaultValue = p.Default,
            min = Double.IsFinite(p.Min) ? (double?)p.Min : null, max = Double.IsFinite(p.Max) ? (double?)p.Max : null,
            choices = p.Choices.Select(c => new { label = c.Key, value = c.Value }).ToArray(), multiline = p.Multiline
        };

        private static object State(WidgetSettings widget) => new
        {
            widgetId = widget.Id, kind = widget.Kind, type = widget.Type,
            options = Options(widget),
            monitors = widget.Monitors.OrderBy(m => m.Monitor, StringComparer.Ordinal).Select(m => new
            {
                id = m.Monitor, device = m.Device, enabled = m.Enabled,
                placement = m.Placement == null ? null : new
                {
                    x = m.Placement.X, y = m.Placement.Y, scale = m.Placement.Scale,
                    width = m.Placement.Width, height = m.Placement.Height, contentAnchored = m.Placement.ContentAnchored
                }
            }).ToArray()
        };

        private static Dictionary<string, string> Options(WidgetSettings widget)
        {
            var values = new Dictionary<string, string>(widget.Options);
            var h = widget.Hardware;
            if (h != null)
            {
                values["name"] = h.Name; values["intervalSeconds"] = h.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
                values["showCpu"] = h.ShowCpu.ToString(); values["showMemory"] = h.ShowMemory.ToString();
                values["showGpu"] = h.ShowGpu.ToString(); values["showVram"] = h.ShowVram.ToString(); values["showNetwork"] = h.ShowNetwork.ToString();
            }
            return values.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        }

        private static void SetOptions(WidgetSettings widget, Dictionary<string, string> values)
        {
            foreach (var pair in values)
            {
                var h = widget.Hardware;
                if (h == null) { widget.Options[pair.Key] = pair.Value; continue; }
                switch (pair.Key)
                {
                    case "name": h.Name = pair.Value; break;
                    case "intervalSeconds": h.IntervalSeconds = Int32.Parse(pair.Value, CultureInfo.InvariantCulture); break;
                    case "showCpu": h.ShowCpu = Boolean.Parse(pair.Value); break;
                    case "showMemory": h.ShowMemory = Boolean.Parse(pair.Value); break;
                    case "showGpu": h.ShowGpu = Boolean.Parse(pair.Value); break;
                    case "showVram": h.ShowVram = Boolean.Parse(pair.Value); break;
                    case "showNetwork": h.ShowNetwork = Boolean.Parse(pair.Value); break;
                    default: h.Options[pair.Key] = pair.Value; break;
                }
            }
        }

        // 인증 값은 반환하지 않지만 외부 설정 변경도 충돌 검사에는 반영한다.
        private static string Revision(WidgetSettings widget) => DesktopMcpContext.Revision(new
        {
            state = State(widget), hardware = widget.Hardware == null ? null :
                JsonSerializer.Serialize(widget.Hardware, new JsonSerializerOptions { IncludeFields = true })
        });
        private static object ReadState(WidgetSettings widget, string runtimeError = null)
        {
            object[] parameters;
            string parameterError = null;
            try { parameters = Parameters(widget.Kind, widget.Type).Select(Parameter).ToArray(); }
            catch (Exception error) { parameters = Array.Empty<object>(); parameterError = error.Message; }
            return new { state = State(widget), revision = Revision(widget), parameters, parameterError, runtimeError };
        }

        private WidgetSettings Editable(string id, string revision)
        {
            desktop.RequireIdle();
            var widget = Context.Require(id, Supports);
            if (Context.IsEditing(id)) throw new McpException("이 위젯을 편집 중입니다. 편집을 마친 뒤 다시 시도해 주세요.");
            DesktopMcpContext.Match(Revision(widget), revision);
            return widget;
        }

        private static void ValidateOptions(WidgetSettings widget, Dictionary<string, string> options)
        {
            if (options == null || options.Count > 100) throw new McpException("options 객체를 입력해 주세요(최대 100개).");
            var definitions = Parameters(widget.Kind, widget.Type).ToDictionary(p => p.Name);
            foreach (var pair in options)
            {
                if (!definitions.TryGetValue(pair.Key, out var p)) throw new McpException("알 수 없는 설정: " + pair.Key);
                var value = pair.Value;
                if (value == null || value.Length > 65536) throw new McpException("설정 값은 최대 65536자 문자열입니다.");
                try { System.Xml.XmlConvert.VerifyXmlChars(value); }
                catch (System.Xml.XmlException) { throw new McpException("저장할 수 없는 문자가 있습니다: " + pair.Key); }
                bool valid = true;
                switch (p.Type)
                {
                    case "number": valid = Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Double.IsFinite(n) && n >= p.Min && n <= p.Max; break;
                    case "bool": valid = Boolean.TryParse(value, out _); break;
                    case "choice": valid = p.Choices.Any(c => c.Key == value); break;
                    case "color": valid = WidgetParameter.Brush(value) != null; break;
                }
                if (!valid) throw new McpException("설정 형식이나 범위를 확인해 주세요: " + pair.Key);
                if (widget.Hardware != null && pair.Key == "intervalSeconds" && !Int32.TryParse(value, out _))
                    throw new McpException("갱신 간격은 1~60의 정수입니다.");
            }
            // XAML 생성까지 검사해 표시 불가능한 스크립트를 추가하지 않는다.
            if (widget.Kind == "script")
            {
                var values = new Dictionary<string, string>(widget.Options);
                foreach (var pair in options) values[pair.Key] = pair.Value;
                var script = WidgetScript.Load(widget.Type);
                var data = new Scripting.WidgetData(); data.SetParams(script.Parameters, values); script.Build(data);
            }
        }

        [McpServerTool(Name = "desktop_read", ReadOnly = true, OpenWorld = false)]
        [Description("현재 배경 장면과 연결된 모니터의 ID·픽셀 영역·메인 여부, 실제 생성된 배경·위젯 상태를 조회합니다. SceneReady는 배경 창 생성 여부이며 화면 캡처 검증은 아닙니다.")]
        public Task<CallToolResult> Desktop(CancellationToken cancellationToken) =>
            Context.OnUi(() => WidgetMcpContext.Result(desktop.Status()), cancellationToken);

        [McpServerTool(Name = "widget_types", ReadOnly = true, OpenWorld = false)]
        [Description("추가할 수 있는 위젯 종류와 설정 항목·기본값·범위를 조회합니다. script는 기존 설치된 위젯만, hardware는 로컬 PC로 생성합니다. choice 설정은 label을 전달합니다.")]
        public Task<CallToolResult> Types(CancellationToken cancellationToken) => Context.OnUi(() =>
        {
            var errors = new List<string>();
            var types = new[] { "clock", "calendar", "hardware" }.Select(k => new { kind = k, type = "", name = k, parameters = Parameters(k, "").Select(Parameter).ToArray() })
                .Concat(WidgetScript.All(errors).Select(s => new { kind = "script", type = s.Id, name = s.Name, parameters = Parameters("script", s.Id).Select(Parameter).ToArray() })).ToArray();
            return WidgetMcpContext.Result(new { types, errors });
        }, cancellationToken);

        [McpServerTool(Name = "widget_read", ReadOnly = true, OpenWorld = false)]
        [Description("위젯 설정·모니터별 배치·revision과 설정 항목을 읽습니다. 메모 본문은 options에 포함되며 SSH 인증 정보는 반환하지 않습니다.")]
        public Task<CallToolResult> Read(string widgetId, CancellationToken cancellationToken) =>
            Context.OnUi(() => WidgetMcpContext.Result(ReadState(Context.Require(widgetId, Supports))), cancellationToken);

        [McpServerTool(Name = "widget_create", Destructive = false, OpenWorld = false)]
        [Description("공용 목록에 위젯을 생성·저장합니다. kind는 clock/calendar/hardware/script. script의 type은 widget_types의 ID, hardware는 로컬 PC입니다. 화면 표시는 반환된 ID로 widget_place를 호출합니다. runtimeError가 있으면 저장 후 화면 갱신이 실패한 것입니다.")]
        public Task<CallToolResult> Create(string kind, string type = "", Dictionary<string, string> options = null, CancellationToken cancellationToken = default) => Context.OnUi(() =>
        {
            desktop.RequireIdle();
            if (!WidgetSettings.Kinds.Contains(kind)) throw new McpException("지원하지 않는 위젯 종류입니다.");
            if (kind == "script") DesktopMcpContext.Identifier(type);
            else if (!String.IsNullOrEmpty(type)) throw new McpException("type은 script에만 지정합니다.");
            var widget = WidgetSettings.Create(kind); widget.Type = type ?? "";
            if (kind == "hardware") widget.Hardware = new HardwareWidgetSettings();
            ValidateOptions(widget, options ?? new Dictionary<string, string>());
            SetOptions(widget, options ?? new Dictionary<string, string>());
            desktop.Commit(() => Context.Preferences.Widgets.Add(widget), () => Context.Preferences.Widgets.Remove(widget));
            string error = desktop.Refresh(desktop.RefreshWidgets);
            return WidgetMcpContext.Result(ReadState(widget, error));
        }, cancellationToken);

        [McpServerTool(Name = "widget_update", Destructive = true, OpenWorld = false)]
        [Description("widget_read의 revision으로 지정한 설정 값만 수정·저장·화면 반영합니다. 다른 설정은 유지하며, SSH 연결 설정은 지원하지 않습니다. 편집 중이거나 상태가 바뀌었으면 거부합니다. runtimeError가 있으면 저장 후 화면 갱신이 실패한 것입니다.")]
        public Task<CallToolResult> Update(string widgetId, string expectedRevision, Dictionary<string, string> options, CancellationToken cancellationToken) => Context.OnUi(() =>
        {
            var widget = Editable(widgetId, expectedRevision); ValidateOptions(widget, options);
            var previous = new Dictionary<string, string>(widget.Options);
            var hardware = widget.Hardware;
            desktop.Commit(() =>
                {
                    if (hardware != null) { widget.Hardware = hardware.Copy(); widget.Hardware.Options = new Dictionary<string, string>(previous); }
                    SetOptions(widget, options);
                },
                () =>
                {
                    if (hardware != null) widget.Hardware = hardware;
                    else { widget.Options.Clear(); foreach (var pair in previous) widget.Options[pair.Key] = pair.Value; }
                });
            string error = desktop.Refresh(() => { Context.Apply(widget); desktop.RefreshWidgets(); });
            return WidgetMcpContext.Result(ReadState(widget, error));
        }, cancellationToken);

        [McpServerTool(Name = "widget_remove", Destructive = true, OpenWorld = false)]
        [Description("widget_read의 revision으로 위젯을 공용 목록과 모든 모니터에서 삭제합니다. 메모 본문도 삭제됩니다. 위젯 종류 파일은 유지합니다. runtimeError와 desktop 상태로 화면 반영 결과를 확인합니다.")]
        public Task<CallToolResult> Remove(string widgetId, string expectedRevision, CancellationToken cancellationToken) => Context.OnUi(() =>
        {
            var widget = Editable(widgetId, expectedRevision); int index = Context.Preferences.Widgets.IndexOf(widget);
            desktop.Commit(() => Context.Preferences.Widgets.RemoveAt(index), () => Context.Preferences.Widgets.Insert(index, widget));
            string error = desktop.Refresh(desktop.RefreshWidgets);
            return WidgetMcpContext.Result(new { removed = widgetId, desktop = desktop.Status(), runtimeError = error });
        }, cancellationToken);

        [McpServerTool(Name = "widget_place", Destructive = true, OpenWorld = false)]
        [Description("widget_read의 revision으로 연결된 모니터의 표시 여부·위치를 저장·반영합니다. x/y는 내용 좌상단의 모니터 내 비율(0~1), scale은 글자 포함 배율(0.05~10). width/height는 메모 내용 크기(DIP), 둘 다 0이면 기본 크기. 숨겨도 위치는 보존합니다. runtimeError와 desktop 상태로 화면 반영 결과를 확인합니다.")]
        public Task<CallToolResult> Place(string widgetId, string expectedRevision, string monitorId, bool enabled,
            double? x = null, double? y = null, double? scale = null, double? width = null, double? height = null,
            CancellationToken cancellationToken = default) => Context.OnUi(() =>
        {
            var widget = Editable(widgetId, expectedRevision);
            var monitor = desktop.Displays().FirstOrDefault(m => m.Id == monitorId);
            if (monitor == null) throw new McpException("연결된 모니터 ID를 desktop_read로 확인해 주세요.");
            if (x.HasValue != y.HasValue || width.HasValue != height.HasValue ||
                new[] { x, y }.Any(n => n.HasValue && (!Double.IsFinite(n.Value) || n < 0 || n > 1)) ||
                (scale.HasValue && (!Double.IsFinite(scale.Value) || scale < 0.05 || scale > 10)) ||
                new[] { width, height }.Any(n => n.HasValue && (!Double.IsFinite(n.Value) || n < 0 || n > 16384)) ||
                (width.HasValue && ((width == 0) != (height == 0))))
                throw new McpException("위치·크기 범위를 확인하고 x/y, width/height는 각각 함께 지정해 주세요.");
            if (width.HasValue && !(widget.Kind == "script" && widget.Type == "memo")) throw new McpException("폭·높이 지정은 메모만 지원합니다.");
            var old = widget.Monitors.FirstOrDefault(m => m.Monitor == monitorId || m.Monitor == monitor.Device);
            var p = old?.Placement;
            if (p != null && !p.ContentAnchored && !x.HasValue && (scale.HasValue || width.HasValue))
                throw new McpException("기존 배치의 크기를 바꾸려면 새 내용 좌표 x/y도 지정해 주세요.");
            var next = new MonitorPlacement { Monitor = monitor.Id, Device = monitor.Device, Enabled = enabled, Placement = p };
            if (x.HasValue || scale.HasValue || width.HasValue)
            {
                if (p == null && !x.HasValue) throw new McpException("첫 크기 지정에는 내용 좌표 x/y도 필요합니다.");
                next.Placement = new WidgetPlacement { X = x ?? p.X, Y = y ?? p.Y, Scale = scale ?? p?.Scale ?? 1,
                    Width = width ?? p?.Width ?? 0, Height = height ?? p?.Height ?? 0, ContentAnchored = x.HasValue || p.ContentAnchored };
            }
            int index = old == null ? widget.Monitors.Count : widget.Monitors.IndexOf(old);
            desktop.Commit(() => { if (old == null) widget.Monitors.Add(next); else widget.Monitors[index] = next; },
                () => { if (old == null) widget.Monitors.Remove(next); else widget.Monitors[index] = old; });
            string error = desktop.Refresh(desktop.RefreshWidgets);
            return WidgetMcpContext.Result(new { widget = ReadState(widget, error), desktop = desktop.Status(), runtimeError = error });
        }, cancellationToken);
    }
}
