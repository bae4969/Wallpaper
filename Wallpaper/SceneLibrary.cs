using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Wallpaper
{
    // 움직이는 배경의 스크립트 폴더(%LOCALAPPDATA%\Wallpaper\Wallpapers).
    //   layers\common.hlsli          모든 레이어 셰이더 앞에 붙는 공통 헤더
    //   layers\<종류>.hlsl + .json   레이어 종류 = 셰이더 + 값 정의(slot·기본값)
    //   scenes\<id>.json             레이어를 쌓은 장면(id = 파일 이름)
    // 기본 파일은 실행파일에 포함돼 있고, 폴더에 없는 파일만 채운다(고친 파일은 덮어쓰지 않는다).
    internal static class SceneLibrary
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallpaper", "Wallpapers");
        private const string ResourcePrefix = "Wallpaper.Wallpapers/";

        public static string LayersFolder { get { return Path.Combine(Root, "layers"); } }
        public static string ScenesFolder { get { return Path.Combine(Root, "scenes"); } }
        public static string ErrorLog { get { return Path.Combine(Root, "errors.log"); } }

        private static string ManifestPath { get { return Path.Combine(Root, ".defaults"); } }

        // 기본 파일을 채운다. 이미 있는 파일은 사용자가 고치지 않았을 때만(처음 풀 때 기록한 해시와 같을 때만) 새 기본본으로 바꾼다.
        public static void EnsureDefaults()
        {
            var assembly = typeof(SceneLibrary).Assembly;
            var recorded = ReadManifest();
            bool changed = false;
            var shipped = new HashSet<string>();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix)))
            {
                var relative = name.Substring(ResourcePrefix.Length).Replace('\\', '/');
                shipped.Add(relative);
                var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                byte[] embedded;
                using (var source = assembly.GetManifestResourceStream(name))
                using (var buffer = new MemoryStream()) { source.CopyTo(buffer); embedded = buffer.ToArray(); }
                var embeddedHash = Hash(embedded);
                string known;
                recorded.TryGetValue(relative, out known);
                if (known == Deleted && !File.Exists(path)) continue;   // 사용자가 지운 기본 장면은 다시 풀지 않는다
                if (File.Exists(path))
                {
                    var current = Hash(File.ReadAllBytes(path));
                    if (current == embeddedHash)
                    {
                        if (known != embeddedHash) { recorded[relative] = embeddedHash; changed = true; }
                        continue;
                    }
                    if (known == null || current != known) continue;   // 사용자가 고친 파일
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, embedded);
                recorded[relative] = embeddedHash;
                changed = true;
            }
            // 더 이상 실행파일에 없는 기본 파일(예: 2세트로 합친 서브 전용 장면)은 사용자가 고치지 않았으면 지운다.
            foreach (var relative in recorded.Keys.Where(r => !shipped.Contains(r)).ToList())
            {
                var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path) && Hash(File.ReadAllBytes(path)) != recorded[relative]) continue;
                if (File.Exists(path)) File.Delete(path);
                recorded.Remove(relative);
                changed = true;
            }
            if (changed) WriteManifest(recorded);
        }

        private const string Deleted = "deleted";

        private static void WriteManifest(Dictionary<string, string> recorded)
        {
            File.WriteAllLines(ManifestPath, recorded.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "\t" + e.Value), Encoding.UTF8);
        }

        private static Dictionary<string, string> ReadManifest()
        {
            var recorded = new Dictionary<string, string>();
            if (!File.Exists(ManifestPath)) return recorded;
            foreach (var line in File.ReadAllLines(ManifestPath, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2) recorded[parts[0]] = parts[1];
            }
            return recorded;
        }

        private static string Hash(byte[] content)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "");
        }

        public static string EmbeddedText(string relative)
        {
            var assembly = typeof(SceneLibrary).Assembly;
            // MSBuild 는 하위 폴더 구분자를 \ 로 넣으므로 / 로 맞춰 찾는다.
            var name = assembly.GetManifestResourceNames().First(n => n.Replace('\\', '/') == ResourcePrefix + relative);
            using (var stream = assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
        }

        // 장면 목록. 읽지 못한 장면은 건너뛰고 오류 목록에 남긴다.
        public static List<SceneDefinition> Scenes(List<string> errors = null)
        {
            var scenes = new List<SceneDefinition>();
            if (!Directory.Exists(ScenesFolder)) return scenes;
            foreach (var path in Directory.GetFiles(ScenesFolder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { scenes.Add(SceneDefinition.Load(path)); }
                catch (Exception error) { if (errors != null) errors.Add(Path.GetFileName(path) + ": " + error.Message); }
            }
            return scenes;
        }

        public static SceneDefinition Scene(string id)
        {
            if (String.IsNullOrEmpty(id)) return null;
            var path = Path.Combine(ScenesFolder, id + ".json");
            return File.Exists(path) ? SceneDefinition.Load(path) : null;
        }

        // ---- 장면 관리(설정 창 장면 탭) ----

        public static string ScenePath(string id) { return Path.Combine(ScenesFolder, id + ".json"); }

        // 장면 파일 목록. 읽지 못한 파일도 오류와 함께 넣는다.
        public static List<SceneFile> SceneFiles()
        {
            var files = new List<SceneFile>();
            if (!Directory.Exists(ScenesFolder)) return files;
            foreach (var path in Directory.GetFiles(ScenesFolder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var file = new SceneFile { Id = Path.GetFileNameWithoutExtension(path) };
                try { file.Definition = SceneDefinition.Load(path); }
                catch (Exception error) { file.Error = error.Message; }
                files.Add(file);
            }
            return files;
        }

        // 빈 틀(그라데이션 + 가장자리 어둡게)로 새 장면을 만들고 id 를 돌려준다.
        public static string NewScene()
        {
            Directory.CreateDirectory(ScenesFolder);
            var id = FreeId("new-scene");
            File.WriteAllText(ScenePath(id), "{\n" +
                "  // 새 장면(메인·서브 2세트). 레이어 종류와 값: AUTHORING.md 또는 Wallpaper.exe --list\n" +
                "  \"name\": \"새 장면\",\n" +
                "  \"description\": \"\",\n" +
                "  \"layers\": [\n" +
                "    {\"type\": \"gradient\", \"top\": \"#0E0C1C\", \"bottom\": \"#2A2046\", \"glow\": \"#C4B5FD\", \"glowStrength\": 0.2, \"glowCenter\": 0.75, \"glowWidth\": 0.25},\n" +
                "    {\"type\": \"vignette\", \"strength\": 0.45, \"radius\": 0.45, \"softness\": 0.7}\n" +
                "  ],\n" +
                "  // 서브 모니터: mirror(메인을 수면처럼 비춤) | continue(메인 아래로 이어짐) | screen(같은 장면을 따로)\n" +
                "  \"sub\": {\n" +
                "    \"mode\": \"mirror\",\n" +
                "    \"add\": [{\"type\": \"water\", \"reflectivity\": 0.75, \"ripple\": 8, \"wavelength\": 22}]\n" +
                "  }\n" +
                "}\n", new UTF8Encoding(false));
            return id;
        }

        // 장면 파일을 복사하고 이름 뒤에 " 복사본"을 붙인다.
        public static string DuplicateScene(string id)
        {
            var text = File.ReadAllText(ScenePath(id), Encoding.UTF8);
            var copy = FreeId(id + "-copy");
            var definition = SceneDefinition.Load(ScenePath(id));
            File.WriteAllText(ScenePath(copy), WithName(text, definition.Name + " 복사본"), new UTF8Encoding(false));
            return copy;
        }

        // 장면의 보이는 이름("name")만 바꾼다. 주석·나머지 내용은 그대로 둔다.
        public static void RenameScene(string id, string name)
        {
            var path = ScenePath(id);
            File.WriteAllText(path, WithName(File.ReadAllText(path, Encoding.UTF8), name), new UTF8Encoding(false));
        }

        // 장면 파일을 지운다. 기본 장면이면 다음 실행에 다시 풀리지 않게 기록한다.
        public static void DeleteScene(string id)
        {
            File.Delete(ScenePath(id));
            var recorded = ReadManifest();
            var relative = "scenes/" + id + ".json";
            if (!recorded.ContainsKey(relative)) return;
            recorded[relative] = Deleted;
            WriteManifest(recorded);
        }

        private static string FreeId(string start)
        {
            var id = start;
            for (int n = 2; File.Exists(ScenePath(id)); n++) id = start + "-" + n;
            return id;
        }

        private static string WithName(string text, string name)
        {
            var quoted = JsonSerializer.Serialize(name, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var pattern = new System.Text.RegularExpressions.Regex("\"name\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"");
            if (pattern.IsMatch(text)) return pattern.Replace(text, "\"name\": " + quoted.Replace("$", "$$"), 1);
            int open = text.IndexOf('{');
            return text.Substring(0, open + 1) + "\n  \"name\": " + quoted + "," + text.Substring(open + 1);
        }

        public static List<LayerType> LayerTypes(List<string> errors = null)
        {
            var types = new List<LayerType>();
            if (!Directory.Exists(LayersFolder)) return types;
            foreach (var path in Directory.GetFiles(LayersFolder, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { types.Add(LayerType.Load(Path.GetFileNameWithoutExtension(path))); }
                catch (Exception error) { if (errors != null) errors.Add(Path.GetFileName(path) + ": " + error.Message); }
            }
            return types;
        }

        public static void LogError(string message)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(ErrorLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // 폴더 안 파일이 바뀌면 잠시 기다렸다가 한 번 알린다(편집기가 여러 번 저장해도 한 번만).
        public static IDisposable Watch(Action changed)
        {
            Directory.CreateDirectory(Root);
            var watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            Timer timer = null;
            FileSystemEventHandler handler = (sender, e) =>
            {
                if (e.FullPath.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return;
                if (timer == null) timer = new Timer(delegate { changed(); }, null, Timeout.Infinite, Timeout.Infinite);
                timer.Change(400, Timeout.Infinite);
            };
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += (sender, e) => handler(sender, e);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
    }

    internal sealed class SceneFile
    {
        public string Id, Error;
        public SceneDefinition Definition;   // 읽지 못했으면 null
    }

    // 장면 = 메인·서브 2세트. "layers" 는 메인(또는 한 화면용) 레이어, "sub" 는 서브 모니터 구성이다.
    //   "sub": { "mode": "mirror" | "continue" | "screen", "layers": [...](없으면 메인 레이어), "add": [...](뒤에 덧붙임) }
    // "sub" 가 없는 예전 장면은 모든 화면에 같은 레이어를 각자 따로(screen) 그린다.
    internal sealed class SceneDefinition
    {
        public static readonly string[] SubModes = { "mirror", "continue", "screen" };
        public string Id, Name, Description;
        public JsonElement[] Layers;
        public JsonElement[] SubLayers;          // 없으면 null
        public string SubMode = "screen";

        public bool HasSub { get { return SubLayers != null; } }
        public JsonElement[] LayersFor(bool sub) { return sub && SubLayers != null ? SubLayers : Layers; }

        public static SceneDefinition Load(string path)
        {
            using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var root = document.RootElement;
                JsonElement layers, name, description;
                if (!root.TryGetProperty("layers", out layers) || layers.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("\"layers\" 배열이 없습니다.");
                var id = Path.GetFileNameWithoutExtension(path);
                var scene = new SceneDefinition
                {
                    Id = id,
                    Name = root.TryGetProperty("name", out name) && name.ValueKind == JsonValueKind.String ? name.GetString() : id,
                    Description = root.TryGetProperty("description", out description) && description.ValueKind == JsonValueKind.String ? description.GetString() : "",
                    Layers = layers.EnumerateArray().Select(layer => layer.Clone()).ToArray()
                };
                JsonElement sub;
                if (root.TryGetProperty("sub", out sub))
                {
                    if (sub.ValueKind != JsonValueKind.Object) throw new InvalidDataException("\"sub\" 는 { \"mode\", \"layers\", \"add\" } 객체여야 합니다.");
                    JsonElement mode, subLayers, add;
                    scene.SubMode = sub.TryGetProperty("mode", out mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : "continue";
                    if (!SubModes.Contains(scene.SubMode)) throw new InvalidDataException("\"sub\".\"mode\" 는 mirror, continue, screen 중 하나입니다: " + scene.SubMode);
                    var list = sub.TryGetProperty("layers", out subLayers) && subLayers.ValueKind == JsonValueKind.Array
                        ? subLayers.EnumerateArray().Select(layer => layer.Clone()).ToList() : scene.Layers.ToList();
                    if (sub.TryGetProperty("add", out add) && add.ValueKind == JsonValueKind.Array) list.AddRange(add.EnumerateArray().Select(layer => layer.Clone()));
                    scene.SubLayers = list.ToArray();
                }
                return scene;
            }
        }
    }

    // 캔버스(메인 화면)에서 한 화면이 놓인 자리(픽셀). 서브가 메인 아래로 이어지거나(continue) 메인을 수면처럼 비출(mirror) 때 쓴다.
    internal sealed class SceneView
    {
        public float OriginX, OriginY, CanvasWidth, CanvasHeight;
        public bool Mirror;
        public float SpanLeft, SpanWidth;   // 모든 화면을 합친 가로 범위(캔버스 픽셀). 입자가 옆 모니터까지 퍼진다

        public static SceneView Whole(int width, int height) { return new SceneView { CanvasWidth = width, CanvasHeight = height, SpanWidth = width }; }

        // 화면 하나가 그릴 구성과 자리. role: main | left | right(메인과 높이가 겹치는 옆 모니터 — 메인 레이어를 가로로 이어 그린다)
        // | sub(그 밖, 예: 아래 — 장면의 서브 구성). 2세트가 아닌 장면은 화면마다 따로 그린다.
        // main·screen·all 은 같은 좌표계(데스크톱 픽셀)의 사각형이고 all 은 연결된 모든 화면이다.
        public static SceneView Arrange(SceneDefinition scene, System.Drawing.Rectangle main, System.Drawing.Rectangle screen,
            IList<System.Drawing.Rectangle> all, out bool sub, out string role)
        {
            bool beside = screen.Top < main.Bottom && screen.Bottom > main.Top && (screen.Right <= main.Left || screen.Left >= main.Right);
            role = screen == main ? "main" : beside ? (screen.Left < main.Left ? "left" : "right") : "sub";
            sub = role == "sub" && scene.HasSub;
            if (!scene.HasSub || (sub && scene.SubMode == "screen")) return Whole(screen.Width, screen.Height);
            var view = role == "main" ? Whole(main.Width, main.Height)
                : sub ? For(scene.SubMode, main, screen)
                : new SceneView { OriginX = screen.Left - main.Left, OriginY = screen.Top - main.Top, CanvasWidth = main.Width, CanvasHeight = main.Height };
            int left = all.Min(r => r.Left), right = all.Max(r => r.Right);
            view.SpanLeft = left - main.Left;
            view.SpanWidth = right - left;
            return view;
        }

        // main·screen 은 같은 좌표계(데스크톱 픽셀)의 사각형. mode 가 continue·mirror 가 아니면 이 화면 자체가 캔버스다.
        public static SceneView For(string mode, System.Drawing.Rectangle main, System.Drawing.Rectangle screen)
        {
            if (mode != "continue" && mode != "mirror") return Whole(screen.Width, screen.Height);
            return new SceneView
            {
                OriginX = screen.Left - main.Left, OriginY = screen.Top - main.Top,
                CanvasWidth = main.Width, CanvasHeight = main.Height, Mirror = mode == "mirror", SpanWidth = main.Width
            };
        }

        // 크기를 scale 배 한 자리(미리보기용)
        public SceneView Scaled(double scale)
        {
            return new SceneView { OriginX = (float)(OriginX * scale), OriginY = (float)(OriginY * scale),
                CanvasWidth = (float)(CanvasWidth * scale), CanvasHeight = (float)(CanvasHeight * scale), Mirror = Mirror,
                SpanLeft = (float)(SpanLeft * scale), SpanWidth = (float)(SpanWidth * scale) };
        }
    }

    // 레이어 종류: layers\<이름>.json(값 정의) + layers\<이름>.hlsl(셰이더)
    internal sealed class LayerType
    {
        public string Name, Description, Kind, Blend, Source;
        public readonly List<LayerParameter> Parameters = new List<LayerParameter>();

        public static LayerType Load(string name)
        {
            var folder = SceneLibrary.LayersFolder;
            var json = Path.Combine(folder, name + ".json");
            var hlsl = Path.Combine(folder, name + ".hlsl");
            if (!File.Exists(json)) throw new FileNotFoundException("레이어 정의가 없습니다: layers\\" + name + ".json");
            if (!File.Exists(hlsl)) throw new FileNotFoundException("레이어 셰이더가 없습니다: layers\\" + name + ".hlsl");
            var type = new LayerType { Name = name };
            using (var document = JsonDocument.Parse(File.ReadAllText(json, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var root = document.RootElement;
                type.Description = Text(root, "description", "");
                type.Kind = Text(root, "kind", "fullscreen");
                if (type.Kind != "fullscreen" && type.Kind != "particles" && type.Kind != "post")
                    throw new InvalidDataException("kind 는 fullscreen, particles, post 중 하나여야 합니다: " + type.Kind);
                type.Blend = Text(root, "blend", "alpha");
                JsonElement parameters;
                if (root.TryGetProperty("params", out parameters))
                    foreach (var property in parameters.EnumerateObject())
                        type.Parameters.Add(LayerParameter.Parse(property.Name, property.Value));
            }
            var common = Path.Combine(folder, "common.hlsli");
            type.Source = (File.Exists(common) ? File.ReadAllText(common, Encoding.UTF8) : "") + "\n#line 1 \"" + name + ".hlsl\"\n" + File.ReadAllText(hlsl, Encoding.UTF8);
            return type;
        }

        private static string Text(JsonElement element, string name, string fallback)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallback;
        }
    }

    // 값 하나의 정의: slot 은 "P0.rgb", "P3.w", "P2" 처럼 상수 P[0..7]의 성분, 또는 "instances"(입자 수).
    internal sealed class LayerParameter
    {
        public string Name, Description;
        public int Slot = -1;          // P 번호
        public int[] Components;       // 0=x/r 1=y/g 2=z/b 3=w/a
        public bool Instances;
        public JsonElement Default;
        public Dictionary<string, float> Values;

        public static LayerParameter Parse(string name, JsonElement element)
        {
            var parameter = new LayerParameter { Name = name };
            JsonElement value;
            parameter.Description = element.TryGetProperty("description", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
            parameter.Default = element.TryGetProperty("default", out value) ? value.Clone() : default(JsonElement);
            if (element.TryGetProperty("values", out value) && value.ValueKind == JsonValueKind.Object)
                parameter.Values = value.EnumerateObject().ToDictionary(p => p.Name, p => (float)p.Value.GetDouble());
            string slot = element.TryGetProperty("slot", out value) && value.ValueKind == JsonValueKind.String ? value.GetString().Trim() : "";
            if (slot == "instances") { parameter.Instances = true; return parameter; }
            var parts = slot.Split('.');
            int index;
            if (parts[0].Length < 2 || (parts[0][0] != 'P' && parts[0][0] != 'p') || !Int32.TryParse(parts[0].Substring(1), out index) || index < 0 || index > 7)
                throw new InvalidDataException(name + ": slot 은 P0~P7 또는 instances 여야 합니다(" + slot + ").");
            parameter.Slot = index;
            string mask = parts.Length > 1 ? parts[1] : "xyzw";
            parameter.Components = mask.Select(c => "xyzw".IndexOf(c) >= 0 ? "xyzw".IndexOf(c) : "rgba".IndexOf(c)).ToArray();
            if (parameter.Components.Length == 0 || parameter.Components.Any(c => c < 0))
                throw new InvalidDataException(name + ": 성분은 xyzw 또는 rgba 여야 합니다(" + slot + ").");
            return parameter;
        }

        // 장면에 적힌 값(없으면 기본값)을 숫자 배열로 바꾼다. 색은 "#RRGGBB" 또는 "#RRGGBBAA".
        public float[] Read(JsonElement layer)
        {
            JsonElement value;
            if (!layer.TryGetProperty(Name, out value)) value = Default;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number: return new[] { (float)value.GetDouble() };
                case JsonValueKind.True: return new[] { 1f };
                case JsonValueKind.False: return new[] { 0f };
                case JsonValueKind.Array: return value.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
                case JsonValueKind.String:
                    var text = value.GetString();
                    float mapped;
                    if (Values != null && Values.TryGetValue(text, out mapped)) return new[] { mapped };
                    if (text.StartsWith("#") && (text.Length == 7 || text.Length == 9))
                        return Enumerable.Range(0, (text.Length - 1) / 2).Select(i => Int32.Parse(text.Substring(1 + i * 2, 2), NumberStyles.HexNumber) / 255f).ToArray();
                    throw new InvalidDataException(Name + ": 알 수 없는 값 \"" + text + "\"" + (Values != null ? " (가능: " + String.Join(", ", Values.Keys) + ")" : ""));
                default: return new float[0];
            }
        }
    }
}
