using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wallpaper
{
    // 앱의 저장·화면 갱신 경로를 공유한다. 위젯 상태 접근은 모두 UI 스레드에서 수행한다.
    internal sealed class WidgetMcpContext
    {
        public readonly UserPreferences Preferences;
        public readonly Dispatcher Dispatcher;
        public readonly Action Save;
        public readonly Action<WidgetSettings> Apply;
        public readonly Func<string, bool> IsEditing;
        public readonly Func<Dictionary<string, string>> Names;
        public readonly Func<string, HardwareSnapshot> Snapshot;

        public WidgetMcpContext(UserPreferences preferences, Dispatcher dispatcher, Action save,
            Action<WidgetSettings> apply, Func<string, bool> isEditing,
            Func<Dictionary<string, string>> names, Func<string, HardwareSnapshot> snapshot)
        {
            Preferences = preferences; Dispatcher = dispatcher; Save = save; Apply = apply;
            IsEditing = isEditing; Names = names; Snapshot = snapshot;
        }

        public async Task<T> OnUi<T>(Func<T> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Dispatcher.HasShutdownStarted) throw new McpException("앱이 종료 중입니다.");
            return await Dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        }

        public WidgetSettings Require(string id, Func<WidgetSettings, bool> supports)
        {
            var widget = Preferences.Widget(id);
            if (widget == null) throw new McpException("위젯을 찾을 수 없습니다. widgets_list로 ID를 확인해 주세요.");
            if (!supports(widget)) throw new McpException("이 위젯이 지원하지 않는 기능입니다.");
            return widget;
        }

        public static CallToolResult Result(object value)
        {
            var json = JsonSerializer.SerializeToElement(value);
            return new CallToolResult
            {
                StructuredContent = json,
                Content = new List<ContentBlock> { new TextContentBlock { Text = json.GetRawText() } }
            };
        }
    }

    // 새 위젯 기능은 지원 대상·도구 이름·도구 등록을 구현하고 레지스트리에 추가한다.
    internal interface IWidgetMcpFeature
    {
        bool Supports(WidgetSettings widget);
        string[] Tools { get; }
        void Register(IMcpServerBuilder builder);
    }

    internal sealed class WidgetMcpRegistry
    {
        private readonly WidgetMcpContext context;
        private readonly DesktopMcpContext desktop;
        public readonly List<IWidgetMcpFeature> Features = new List<IWidgetMcpFeature>();

        public WidgetMcpRegistry(WidgetMcpContext context, DesktopMcpContext desktop = null)
        {
            this.context = context;
            this.desktop = desktop;
            Features.Add(new MemoMcpFeature(context));
            Features.Add(new PcMcpFeature(context));
            if (desktop != null) Features.Add(new DesktopMcpTools(desktop));
        }

        public void Register(IMcpServerBuilder builder)
        {
            builder.WithTools(this);
            foreach (var feature in Features) feature.Register(builder);
            if (desktop != null) builder.WithTools(new SceneMcpTools(desktop));
        }

        [McpServerTool(Name = "widgets_list", ReadOnly = true, OpenWorld = false)]
        [Description("전체 위젯의 ID, 이름, 종류, 모니터별 배치와 지원 MCP 도구를 조회합니다. 메모 본문과 SSH 인증 정보는 포함하지 않습니다.")]
        public async Task<CallToolResult> List(CancellationToken cancellationToken)
        {
            return await context.OnUi(() =>
            {
                var names = context.Names();
                return WidgetMcpContext.Result(new
                {
                    widgets = context.Preferences.Widgets.Select(w => new
                    {
                        id = w.Id, name = names.GetValueOrDefault(w.Id, w.Id), kind = w.Kind, type = w.Type,
                        tools = Features.Where(f => f.Supports(w)).SelectMany(f => f.Tools).Distinct().ToArray(),
                        monitors = w.Monitors.Select(m => new
                        {
                            id = m.Monitor, device = m.Device, enabled = m.Enabled,
                            placement = m.Placement == null ? null : new
                            {
                                x = m.Placement.X, y = m.Placement.Y, scale = m.Placement.Scale,
                                width = m.Placement.Width, height = m.Placement.Height, contentAnchored = m.Placement.ContentAnchored
                            }
                        }).ToArray()
                    }).ToArray()
                });
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class MemoMcpFeature : IWidgetMcpFeature
    {
        private readonly WidgetMcpContext context;
        public MemoMcpFeature(WidgetMcpContext context) { this.context = context; }
        public bool Supports(WidgetSettings widget) { return widget.Kind == "script" && widget.Type == "memo"; }
        public string[] Tools { get { return new[] { "memo_read", "memo_write" }; } }
        public void Register(IMcpServerBuilder builder) { builder.WithTools(this); }

        private static string Text(WidgetSettings widget)
        {
            string text;
            if (widget.Options.TryGetValue("text", out text)) return text;
            return WidgetScript.Load(widget.Type).Parameters.First(p => p.Name == "text").Default;
        }

        private static string Revision(string text) { return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); }
        private static object State(WidgetSettings widget)
        {
            var text = Text(widget);
            return new { widgetId = widget.Id, text, revision = Revision(text) };
        }

        [McpServerTool(Name = "memo_read", ReadOnly = true, OpenWorld = false)]
        [Description("메모 내용과 revision을 읽습니다. 화면에 표시하지 않는 메모도 조회할 수 있습니다.")]
        public async Task<CallToolResult> Read([Description("widgets_list의 메모 위젯 ID")] string widgetId, CancellationToken cancellationToken)
        {
            return await context.OnUi(() => WidgetMcpContext.Result(State(context.Require(widgetId, Supports))), cancellationToken).ConfigureAwait(false);
        }

        [McpServerTool(Name = "memo_write", Destructive = true, OpenWorld = false)]
        [Description("기존 메모 본문을 교체하고 저장·화면 반영합니다. 먼저 memo_read의 revision을 받아야 합니다. 편집 중이거나 내용이 바뀌었으면 거부합니다.")]
        public async Task<CallToolResult> Write(string widgetId, [Description("새 본문. 빈 문자열이면 내용을 비웁니다. 최대 65536자.")] string text,
            [Description("memo_read로 받은 revision. 이후 내용이 바뀌었으면 다시 읽어야 합니다.")] string expectedRevision, CancellationToken cancellationToken)
        {
            if (text == null || text.Length > 65536 || !ValidXml(text))
                throw new McpException("메모는 XML에 저장할 수 있는 문자로 최대 65536자까지 입력해 주세요.");
            return await context.OnUi(() =>
            {
                var widget = context.Require(widgetId, Supports);
                if (context.IsEditing(widgetId)) throw new McpException("사용자가 이 메모를 편집 중입니다. 편집을 마친 뒤 다시 시도해 주세요.");
                if (!String.Equals(Revision(Text(widget)), expectedRevision, StringComparison.Ordinal))
                    throw new McpException("메모 내용이 바뀌었습니다. memo_read로 다시 읽은 후 수정해 주세요.");
                string previous;
                bool hadValue = widget.Options.TryGetValue("text", out previous);
                widget.Options["text"] = text;
                try { context.Apply(widget); context.Save(); }
                catch (Exception error)
                {
                    if (hadValue) widget.Options["text"] = previous;
                    else widget.Options.Remove("text");
                    try { context.Apply(widget); }
                    catch (Exception restoreError) { SceneLibrary.LogError("MCP 메모 화면 복구 실패: " + restoreError.Message); }
                    SceneLibrary.LogError("MCP 메모 변경 실패: " + error.Message);
                    throw new McpException("메모 변경을 저장하지 못했습니다. 기존 내용을 유지합니다.");
                }
                return WidgetMcpContext.Result(State(widget));
            }, cancellationToken).ConfigureAwait(false);
        }

        private static bool ValidXml(string text)
        {
            try { System.Xml.XmlConvert.VerifyXmlChars(text); return true; }
            catch (System.Xml.XmlException) { return false; }
        }
    }

    internal sealed class PcMcpFeature : IWidgetMcpFeature
    {
        private readonly WidgetMcpContext context;
        // 요청마다 SSH 프로세스를 쌓지 않도록 임시 수집은 한 번에 하나만 수행한다.
        private readonly SemaphoreSlim sampling = new SemaphoreSlim(1, 1);
        public PcMcpFeature(WidgetMcpContext context) { this.context = context; }
        public bool Supports(WidgetSettings widget)
        {
            return widget.Kind == "hardware" || (widget.Kind == "script" && widget.Type == "system-meters");
        }
        public string[] Tools { get { return new[] { "pc_read" }; } }
        public void Register(IMcpServerBuilder builder) { builder.WithTools(this); }

        [McpServerTool(Name = "pc_read", ReadOnly = true, OpenWorld = true)]
        [Description("PC의 CPU·RAM·GPU·네트워크 상태를 조회합니다. widgetId를 생략하면 로컬 PC, 지정하면 하드웨어 위젯의 PC(SSH 포함) 또는 시스템 상태 스크립트 위젯의 로컬 PC를 조회합니다. 숨겨진 위젯은 요청 중에만 수집합니다. 수치가 null이면 측정 불가이며, 추이 이력은 제공하지 않습니다.")]
        public async Task<CallToolResult> Read([Description("하드웨어 또는 시스템 상태 위젯 ID. 생략하면 내 PC.")] string widgetId = null, CancellationToken cancellationToken = default)
        {
            var target = await context.OnUi(() =>
            {
                var widget = String.IsNullOrEmpty(widgetId) ? null : context.Require(widgetId, Supports);
                var settings = widget != null && widget.Hardware != null ? widget.Hardware.Copy() : new HardwareWidgetSettings();
                var cached = widget == null ? null : widget.Kind == "hardware" ? context.Snapshot(widgetId) : LocalMetrics.Latest;
                return (settings, cached);
            }, cancellationToken).ConfigureAwait(false);
            var snapshot = target.cached;
            bool cachedValue = snapshot != null && DateTimeOffset.UtcNow - snapshot.CapturedAt <= TimeSpan.FromSeconds(target.settings.IntervalSeconds + 1);
            if (!cachedValue)
            {
                if (!await sampling.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    throw new McpException("다른 PC 상태를 측정 중입니다. 잠시 후 다시 시도해 주세요.");
                try { snapshot = await Sample(target.settings, cancellationToken).ConfigureAwait(false); }
                finally { sampling.Release(); }
            }
            var system = snapshot.System;
            return WidgetMcpContext.Result(new
            {
                widgetId, name = target.settings.Name, remote = target.settings.Remote,
                sampledAt = snapshot.CapturedAt, cached = cachedValue, error = snapshot.Error,
                cpuPercent = system.Cpu, memoryPercent = system.MemoryPercent,
                memoryUsedBytes = system.MemoryUsedBytes, memoryTotalBytes = system.MemoryTotalBytes,
                downloadBytesPerSecond = system.DownloadBytes, uploadBytesPerSecond = system.UploadBytes,
                gpuError = snapshot.Gpu.Error,
                gpus = snapshot.Gpu.Devices.Select(g => new
                {
                    index = g.Index, name = g.Name, usagePercent = g.Utilization,
                    memoryUsedMiB = g.MemoryUsed, memoryTotalMiB = g.MemoryTotal,
                    temperatureCelsius = g.Temperature, powerWatts = g.Power
                }).ToArray()
            });
        }

        private static async Task<HardwareSnapshot> Sample(HardwareWidgetSettings settings, CancellationToken cancellationToken)
        {
            var result = new TaskCompletionSource<HardwareSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            // CPU·네트워크 속도의 차분 계산을 위해 두 번 측정한 뒤 멈춘다.
            settings.IntervalSeconds = 1;
            using (var feed = new HardwareFeed(settings))
            {
                int samples = 0;
                feed.Updated += snapshot =>
                {
                    if (snapshot.Error != null || ++samples >= 2) result.TrySetResult(snapshot);
                };
                feed.Start();
                try { return await result.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { throw new McpException("PC 상태 측정 시간이 초과되었습니다."); }
            }
        }
    }
}
