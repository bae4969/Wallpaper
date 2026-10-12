using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Wallpaper
{
    // 장면 작성 도구(명령줄). 사람이나 AI가 실행파일만 가지고 장면을 만들고 확인할 수 있게 한다.
    internal static class WallpaperCommands
    {
        private const string Usage = @"Wallpaper 움직이는 배경 장면 도구

사용법: Wallpaper.exe <명령> [옵션]

  --help                       이 도움말
  --guide                      장면 작성 안내서(AUTHORING.md) 전체 출력 — 처음이면 이것부터
  --folder                     장면 폴더 경로 출력(없으면 기본 파일을 채운다)
  --list                       장면 목록과 레이어 종류·값(slot·기본값·설명) 목록
  --validate [장면 id]         장면을 컴파일해 검사. 장면을 생략하면 전부. 실패하면 종료 코드 1
  --render <장면 id|json 경로> <out.png> [--time 초] [--size 1920x1080] [--quality high|medium|low]
                               바탕화면 없이 장면 한 프레임을 PNG로 저장(위젯 제외)
  --render-widget <위젯 id|폴더> <out.png> [--option 이름=값]...
                               스크립트 위젯 하나를 어두운 배경 위에 PNG로 저장(하드웨어 값은 실제 측정)

공통 옵션: --out <파일>  출력을 UTF-8 파일로 쓴다(콘솔 인코딩이 다른 환경용)
PowerShell 은 이 실행파일(창 프로그램)이 끝나기를 기다리지 않는다. 결과가 필요하면 끝에 | Out-Null 을 붙이거나
Start-Process -Wait 로 실행한다(cmd·bash·파이썬 subprocess 는 출력을 받을 때 기다린다).

작업 순서(AI 포함):
  1) --guide 로 형식을 읽는다.  2) --folder 의 scenes\<id>.json 을 만들거나 고친다.
  3) --validate <id> 로 오류를 고친다.  4) --render <id> out.png --time 3 으로 결과를 본다.
  5) 트레이 앱에서 디스플레이를 눌러 편집 창의 '배경'에서 고른다. 실행 중이면 파일을 저장할 때마다 바로 다시 그린다.
";

        public static int Run(string[] args)
        {
            var arguments = args.ToList();
            string outputPath = Option(arguments, "--out");
            AttachConsole(-1);
            TextWriter output;
            if (outputPath != null) output = new StreamWriter(outputPath, false, new UTF8Encoding(false));
            else if (Console.IsOutputRedirected) output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            else output = Console.Out;
            try
            {
                SceneLibrary.EnsureDefaults();
                string command = arguments.Count > 0 ? arguments[0].ToLowerInvariant() : "--help";
                switch (command)
                {
                    case "--guide": output.Write(Guide()); return 0;
                    case "--folder": output.WriteLine(SceneLibrary.Root); return 0;
                    case "--list": return List(output);
                    case "--validate": return Validate(output, arguments.Count > 1 ? arguments[1] : null);
                    case "--render": return Render(output, arguments);
                    case "--render-widget": return RenderWidget(output, arguments);
                    case "--help": case "-h": case "/?": output.Write(Usage); return 0;
                    default: output.WriteLine("알 수 없는 명령: " + arguments[0]); output.Write(Usage); return 2;
                }
            }
            catch (Exception error)
            {
                output.WriteLine("오류: " + error.Message);
                return 1;
            }
            finally { output.Flush(); if (output != Console.Out) output.Dispose(); }
        }

        private static string Guide()
        {
            var path = Path.Combine(SceneLibrary.Root, "AUTHORING.md");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : SceneLibrary.EmbeddedText("AUTHORING.md");
        }

        private static int List(TextWriter output)
        {
            var errors = new List<string>();
            output.WriteLine("장면 폴더: " + SceneLibrary.Root);
            output.WriteLine();
            output.WriteLine("[장면] scenes\\<id>.json");
            foreach (var scene in SceneLibrary.Scenes(errors))
                output.WriteLine("  " + scene.Id + " — " + scene.Name + " (레이어 " + scene.Layers.Length + "개)" + (scene.Description.Length > 0 ? "\n      " + scene.Description : ""));
            output.WriteLine();
            output.WriteLine("[레이어 종류] layers\\<종류>.json + .hlsl");
            foreach (var type in SceneLibrary.LayerTypes(errors))
            {
                output.WriteLine("  " + type.Name + "  kind=" + type.Kind + " blend=" + type.Blend + " — " + type.Description);
                foreach (var parameter in type.Parameters)
                    output.WriteLine("      " + parameter.Name.PadRight(18) + (parameter.Instances ? "instances" : "P" + parameter.Slot + "." + new string(parameter.Components.Select(c => "xyzw"[c]).ToArray())).PadRight(10)
                        + " 기본 " + DefaultText(parameter).PadRight(10) + " " + parameter.Description
                        + (parameter.Values != null ? " [" + String.Join("|", parameter.Values.Keys) + "]" : ""));
            }
            output.WriteLine();
            output.WriteLine("모든 레이어 공통 값: type(필수), seed(숫자), blend(opaque|alpha|add), comment(메모)");
            output.WriteLine();
            output.WriteLine("[위젯] widgets\\<id>\\widget.json + widget.xaml");
            foreach (var widget in WidgetScript.All(errors))
            {
                output.WriteLine("  " + widget.Id + " — " + widget.Name + " (" + widget.Width + "×" + widget.Height + ", " + widget.Interval + "초마다" + (widget.Metrics ? ", 하드웨어 측정" : "") + ")"
                    + (widget.Description.Length > 0 ? "\n      " + widget.Description : ""));
                foreach (var parameter in widget.Parameters)
                    output.WriteLine("      " + parameter.Name.PadRight(14) + parameter.Type.PadRight(8) + " 기본 " + parameter.Default.PadRight(12) + " " + parameter.Label
                        + (parameter.Choices.Count > 0 ? " [" + String.Join("|", parameter.Choices.Select(c => c.Key)) + "]" : ""));
            }
            foreach (var error in errors) output.WriteLine("읽기 오류: " + error);
            return errors.Count == 0 ? 0 : 1;
        }

        private static string DefaultText(LayerParameter parameter)
        {
            switch (parameter.Default.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String: return parameter.Default.GetString();
                case System.Text.Json.JsonValueKind.Number: return parameter.Default.GetDouble().ToString(CultureInfo.InvariantCulture);
                case System.Text.Json.JsonValueKind.Undefined: return "-";
                default: return parameter.Default.GetRawText();
            }
        }

        private static int Validate(TextWriter output, string id)
        {
            var errors = new List<string>();
            var scenes = id != null ? new List<SceneDefinition> { LoadScene(id) } : SceneLibrary.Scenes(errors);
            foreach (var error in errors) output.WriteLine("실패  " + error);
            int failed = errors.Count;
            // 장면 id 를 주지 않으면 위젯도 모두 검사한다(정의를 읽고 XAML 로 화면 요소를 만들어 본다).
            if (id == null)
            {
                var widgetErrors = new List<string>();
                foreach (var widget in WidgetScript.All(widgetErrors))
                {
                    try
                    {
                        widget.Build(new Scripting.WidgetData());
                        output.WriteLine("통과  widgets\\" + widget.Id);
                    }
                    catch (Exception error) { failed++; output.WriteLine("실패  widgets\\" + widget.Id + ": " + error.Message); }
                }
                foreach (var error in widgetErrors) { failed++; output.WriteLine("실패  " + error); }
            }
            foreach (var scene in scenes)
            {
                try
                {
                    WallpaperScene.Validate(scene);
                    output.WriteLine("통과  " + scene.Id + " (레이어 " + scene.Layers.Length + "개)");
                }
                catch (Exception error)
                {
                    failed++;
                    output.WriteLine("실패  " + error.Message);
                }
            }
            return failed == 0 ? 0 : 1;
        }

        private static int Render(TextWriter output, List<string> arguments)
        {
            if (arguments.Count < 3) { output.Write(Usage); return 2; }
            string time = Option(arguments, "--time"), size = Option(arguments, "--size"), quality = Option(arguments, "--quality");
            int effect = quality == null || quality == "high" ? 2 : quality == "medium" ? 1 : quality == "low" ? 0 : -1;
            if (effect < 0) { output.WriteLine("--quality 는 high, medium, low 중 하나입니다."); return 2; }
            float seconds = time != null ? Single.Parse(time, CultureInfo.InvariantCulture) : 3;
            int width = 1920, height = 1080;
            if (size != null)
            {
                var parts = size.ToLowerInvariant().Split('x');
                width = Int32.Parse(parts[0], CultureInfo.InvariantCulture);
                height = Int32.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            var scene = LoadScene(arguments[1]);
            var path = Path.GetFullPath(arguments[2]);
            WallpaperScene.RenderToFile(scene, width, height, seconds, path, effect);
            output.WriteLine("저장  " + path + " (" + width + "x" + height + ", " + seconds.ToString(CultureInfo.InvariantCulture) + "초)");
            return 0;
        }

        private static int RenderWidget(TextWriter output, List<string> arguments)
        {
            var options = new Dictionary<string, string>();
            string option;
            while ((option = Option(arguments, "--option")) != null)
            {
                int equals = option.IndexOf('=');
                if (equals > 0) options[option.Substring(0, equals)] = option.Substring(equals + 1);
            }
            if (arguments.Count < 3) { output.Write(Usage); return 2; }
            var widget = WidgetScript.Load(arguments[1]);
            var data = new Scripting.WidgetData();
            data.SetParams(widget.Parameters, options);
            HardwareSnapshot snapshot = null;
            if (widget.Metrics)
            {
                var monitor = new HardwareMonitor();
                monitor.ReadSystem();
                System.Threading.Thread.Sleep(700);
                snapshot = new HardwareSnapshot { System = monitor.ReadSystem(), Gpu = GpuMonitor.Read() };
            }
            data.Update(DateTime.Now, snapshot);
            var view = widget.Build(data);
            // 그림자 여백 대신 32px 여백을 두고 어두운 배경 위에 그린다.
            var content = (System.Windows.FrameworkElement)view.Child;
            view.Child = null;
            var frame = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1B, 0x2E)),
                Padding = new System.Windows.Thickness(32), Child = content, DataContext = data
            };
            frame.Measure(new System.Windows.Size(Double.PositiveInfinity, Double.PositiveInfinity));
            frame.Arrange(new System.Windows.Rect(frame.DesiredSize));
            frame.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var path = Path.GetFullPath(arguments[2]);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            output.WriteLine("저장  " + path + " (" + bitmap.PixelWidth + "x" + bitmap.PixelHeight + ")");
            return 0;
        }

        // 장면 id(scenes 폴더) 또는 json 파일 경로
        private static SceneDefinition LoadScene(string name)
        {
            if (File.Exists(name)) return SceneDefinition.Load(Path.GetFullPath(name));
            var scene = SceneLibrary.Scene(name);
            if (scene == null) throw new FileNotFoundException("장면이 없습니다: " + name + " (scenes\\" + name + ".json)");
            return scene;
        }

        private static string Option(List<string> arguments, string name)
        {
            int index = arguments.FindIndex(a => String.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= arguments.Count) return null;
            var value = arguments[index + 1];
            arguments.RemoveRange(index, 2);
            return value;
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int process);
    }
}
