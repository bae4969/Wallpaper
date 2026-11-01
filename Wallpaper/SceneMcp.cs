using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wallpaper
{
    internal sealed class SceneMcpTools
    {
        private readonly DesktopMcpContext desktop;
        private readonly string folder;
        // 컴파일·미리보기를 직렬화해 GPU 작업이 요청마다 쌓이지 않게 한다.
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private WidgetMcpContext Context => desktop.Widgets;

        public SceneMcpTools(DesktopMcpContext desktop)
        {
            this.desktop = desktop; folder = desktop.ScenesFolder;
        }

        private string PathFor(string id)
        {
            DesktopMcpContext.Identifier(id);
            return Path.Combine(folder, id + ".json");
        }

        private string Text(string id)
        {
            string path = PathFor(id);
            if (!File.Exists(path)) throw new McpException("장면이 없습니다. scenes_list로 ID를 확인해 주세요.");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private SceneDefinition Definition(string id, string text)
        {
            if (text == null || text.Length > 262144) throw new McpException("장면 JSON은 최대 262144자입니다.");
            var scene = SceneDefinition.Parse(text, id);
            if (scene.Layers.Length == 0 || scene.Layers.Length > 64 || scene.SubLayers?.Length > 64)
                throw new McpException("메인 레이어는 1~64개, 서브 레이어는 최대 64개입니다.");
            // 레이어 종류는 설치된 목록에서만 고른다. 파일 경로를 type으로 받지 않는다.
            var known = new HashSet<string>(Directory.GetFiles(SceneLibrary.LayersFolder, "*.json").Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
            foreach (var layer in scene.Layers.Concat(scene.SubLayers ?? Array.Empty<JsonElement>()))
            {
                if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String || !known.Contains(type.GetString()))
                    throw new McpException("알 수 없는 레이어 종류입니다. layers_list의 ID를 사용해 주세요.");
            }
            using (var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!new[] { "name", "description", "layers", "sub" }.Contains(property.Name)) throw new McpException("알 수 없는 장면 항목: " + property.Name);
                if (document.RootElement.TryGetProperty("sub", out var sub))
                    foreach (var property in sub.EnumerateObject())
                    {
                        if (!new[] { "mode", "layers", "add" }.Contains(property.Name)) throw new McpException("알 수 없는 sub 항목: " + property.Name);
                        if ((property.Name == "layers" || property.Name == "add") && property.Value.ValueKind != JsonValueKind.Array)
                            throw new McpException("sub.layers와 sub.add는 배열이어야 합니다.");
                    }
            }
            return scene;
        }

        [McpServerTool(Name = "scenes_list", ReadOnly = true, OpenWorld = false)]
        [Description("저장된 배경 장면의 ID·이름·설명과 읽기 오류를 조회합니다. JSON 내용은 scene_read로 조회합니다.")]
        public async Task<CallToolResult> List(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var scenes = new List<object>();
                if (Directory.Exists(folder))
                    foreach (var path in Directory.GetFiles(folder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        var id = Path.GetFileNameWithoutExtension(path);
                        try { var s = SceneDefinition.Load(path); scenes.Add(new { id, name = s.Name, description = s.Description, error = (string)null }); }
                        catch (Exception error) { scenes.Add(new { id, name = id, description = "", error = error.Message }); }
                    }
                return WidgetMcpContext.Result(new { scenes });
            }
            finally { gate.Release(); }
        }

        [McpServerTool(Name = "layers_list", ReadOnly = true, OpenWorld = false)]
        [Description("배경 제작에 쓸 수 있는 설치된 레이어 ID·용도·파라미터 기본값·선택값을 조회합니다. 장면은 {name,description,layers:[{type,...}],sub:{mode:mirror|continue|screen,layers?:[],add?:[]}} 형식입니다. 색은 #RRGGBB 또는 #RRGGBBAA, 위치·크기는 AUTHORING.md 기준입니다.")]
        public CallToolResult Layers()
        {
            var errors = new List<string>();
            var layers = SceneLibrary.LayerTypes(errors).Select(t => new
            {
                id = t.Name, description = t.Description, kind = t.Kind, blend = t.Blend,
                parameters = t.Parameters.Select(p => new { name = p.Name, description = p.Description,
                    defaultValue = p.Default.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : p.Default, values = p.Values }).ToArray()
            }).ToArray();
            return WidgetMcpContext.Result(new { layers, errors });
        }

        [McpServerTool(Name = "scene_read", ReadOnly = true, OpenWorld = false)]
        [Description("장면의 원본 JSON과 revision을 읽습니다. 수정·적용 요청에는 이 revision이 필요합니다. 문법이 잘못된 기존 파일도 읽어 수정할 수 있습니다.")]
        public async Task<CallToolResult> Read(string sceneId, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { var text = Text(sceneId); return WidgetMcpContext.Result(new { sceneId, json = text, revision = DesktopMcpContext.Hash(text) }); }
            finally { gate.Release(); }
        }

        [McpServerTool(Name = "scene_write", Destructive = true, OpenWorld = false)]
        [Description("장면 JSON을 컴파일 검사한 뒤 원자적으로 저장합니다. 새 ID는 expectedRevision에 빈 문자열, 기존 ID는 scene_read의 revision을 전달합니다. 잘못된 장면·충돌은 저장하지 않습니다. 적용 중인 장면을 수정하면 파일 감시로 화면에도 반영됩니다. 다른 장면은 scene_apply로 적용합니다.")]
        public async Task<CallToolResult> Write(string sceneId, string json, string expectedRevision, CancellationToken cancellationToken)
        {
            string path = PathFor(sceneId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Context.OnUi(() => { desktop.RequireIdle(); return true; }, cancellationToken).ConfigureAwait(false);
                var scene = Definition(sceneId, json);
                await Task.Run(() => WallpaperScene.Validate(scene), cancellationToken).ConfigureAwait(false);
                return await Context.OnUi(() =>
                {
                    desktop.RequireIdle();
                    DesktopMcpContext.Match(File.Exists(path) ? DesktopMcpContext.Hash(File.ReadAllText(path, Encoding.UTF8)) : "", expectedRevision);
                    Directory.CreateDirectory(folder);
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(temporary, json, new UTF8Encoding(false));
                        // 새 ID는 덮어쓰지 않고 기존 ID만 교체한다.
                        File.Move(temporary, path, expectedRevision != "");
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return WidgetMcpContext.Result(new { sceneId, revision = DesktopMcpContext.Hash(json), validated = true,
                        active = Context.Preferences.Scene == sceneId });
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not McpException && error is not OperationCanceledException)
            {
                throw new McpException("장면을 저장하지 못했습니다: " + error.Message);
            }
            finally { gate.Release(); }
        }

        [McpServerTool(Name = "scene_preview", ReadOnly = true, OpenWorld = false)]
        [Description("저장된 장면을 지정 모니터의 실제 메인·서브·옆 화면 배치로 렌더해 PNG 이미지로 반환합니다(위젯 제외). 적용 상태는 바꾸지 않습니다. monitorId 생략 시 메인. width는 64~1600px, 긴 변은 최대 1600px. time은 장면 시작 후 초입니다.")]
        public async Task<CallToolResult> Preview(string sceneId, string monitorId = null, double time = 3, int width = 960, CancellationToken cancellationToken = default)
        {
            if (!Double.IsFinite(time) || time < 0 || time > 10000000 || width < 64 || width > 1600) throw new McpException("time(0~10000000초)과 width(64~1600)를 확인해 주세요.");
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var text = Text(sceneId); var scene = Definition(sceneId, text);
                var state = await Context.OnUi(() => (displays: desktop.Displays(), quality: Context.Preferences.EffectQuality), cancellationToken).ConfigureAwait(false);
                var main = state.displays.FirstOrDefault(m => m.Main);
                var screen = monitorId == null ? main : state.displays.FirstOrDefault(m => m.Id == monitorId);
                if (main == null || screen == null) throw new McpException("연결된 모니터 ID를 desktop_read로 확인해 주세요.");
                var view = SceneView.Arrange(scene, main.Bounds, screen.Bounds, state.displays.Select(m => m.Bounds).ToList(), out bool sub, out string role);
                double scale = Math.Min(width / (double)screen.Width, 1600.0 / screen.Height);
                int w = Math.Max(1, (int)Math.Round(screen.Width * scale)), h = Math.Max(1, (int)Math.Round(screen.Height * scale));
                byte[] png = await Task.Run(() =>
                {
                    var image = WallpaperScene.Still(scene, w, h, (float)time, state.quality, sub, view.Scaled(scale));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var buffer = new MemoryStream()) { encoder.Save(buffer); return buffer.ToArray(); }
                }, cancellationToken).ConfigureAwait(false);
                var result = WidgetMcpContext.Result(new { sceneId, revision = DesktopMcpContext.Hash(text), monitorId = screen.Id, role, width = w, height = h, time });
                result.Content.Add(ImageContentBlock.FromBytes(png, "image/png"));
                return result;
            }
            catch (Exception error) when (error is not McpException && error is not OperationCanceledException)
            {
                throw new McpException("장면을 미리 보지 못했습니다: " + error.Message);
            }
            finally { gate.Release(); }
        }

        [McpServerTool(Name = "scene_apply", Destructive = true, OpenWorld = false)]
        [Description("scene_read/scene_write의 revision과 desktop_read의 현재 sceneId로 배경 세트를 모든 화면에 적용합니다. sceneId와 expectedRevision이 빈 문자열이면 Windows 배경으로 돌아갑니다. 저장 후 화면 반영 오류(runtimeError)와 monitors의 SceneReady를 확인해 주세요.")]
        public async Task<CallToolResult> Apply(string sceneId, string expectedRevision, string expectedCurrentScene, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (sceneId != "")
                {
                    var text = Text(sceneId); DesktopMcpContext.Match(DesktopMcpContext.Hash(text), expectedRevision);
                    var scene = Definition(sceneId, text);
                    await Task.Run(() => WallpaperScene.Validate(scene), cancellationToken).ConfigureAwait(false);
                }
                else DesktopMcpContext.Match("", expectedRevision);
                return await Context.OnUi(() =>
                {
                    desktop.RequireIdle(); DesktopMcpContext.Match(Context.Preferences.Scene, expectedCurrentScene);
                    if (sceneId != "") DesktopMcpContext.Match(DesktopMcpContext.Hash(Text(sceneId)), expectedRevision);
                    var previous = Context.Preferences.Scene;
                    desktop.Commit(() => Context.Preferences.Scene = sceneId, () => Context.Preferences.Scene = previous);
                    string error = desktop.Refresh(desktop.RefreshScenes);
                    return WidgetMcpContext.Result(new { sceneId = Context.Preferences.Scene, monitors = desktop.Displays(), runtimeError = error });
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not McpException && error is not OperationCanceledException)
            {
                throw new McpException("장면을 적용하지 못했습니다: " + error.Message);
            }
            finally { gate.Release(); }
        }
    }
}
