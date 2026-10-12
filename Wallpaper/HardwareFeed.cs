using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Wallpaper
{
    internal sealed class HardwareFeed : IDisposable
    {
        private readonly HardwareWidgetSettings settings;
        private readonly ManualResetEvent stopped = new ManualResetEvent(false);
        private readonly object gate = new object();
        private Process process;
        private bool disposed;
        public event Action<HardwareSnapshot> Updated;

        public HardwareFeed(HardwareWidgetSettings options) { settings = options.Copy(); }

        public void Start() { Task.Run((Action)Run); }

        private void Run()
        {
            var local = new HardwareMonitor();
            try
            {
                while (!stopped.WaitOne(0))
                {
                    try
                    {
                        if (settings.Remote) ReadRemote();
                        else Publish(local.Read(settings.ShowGpu || settings.ShowVram));
                    }
                    catch (Exception error)
                    {
                        if (!stopped.WaitOne(0)) Publish(Failure(error is IOException ? error.Message : "정보 읽기 실패"));
                    }
                    if (stopped.WaitOne(TimeSpan.FromSeconds(settings.IntervalSeconds))) return;
                }
            }
            finally { stopped.Dispose(); }
        }

        private void Publish(HardwareSnapshot snapshot)
        {
            if (stopped.WaitOne(0)) return;
            var handler = Updated;
            if (handler != null) handler(snapshot);
        }

        private void ReadRemote()
        {
            string invalid = Validate(settings);
            if (invalid != null) throw new IOException(invalid);
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (!File.Exists(executable)) throw new IOException("OpenSSH 클라이언트 필요");
            Process session = null;
            Task<string> errors = null;
            try
            {
                session = new Process { StartInfo = new ProcessStartInfo(executable, Arguments(settings))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }};
                lock (gate)
                {
                    if (disposed) return;
                    process = session;
                    session.Start();
                }
                errors = session.StandardError.ReadToEndAsync();
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Wallpaper.LinuxMetrics.py"))
                using (var reader = new StreamReader(resource, Encoding.UTF8))
                using (var input = new StreamWriter(session.StandardInput.BaseStream, new UTF8Encoding(false)))
                    input.Write(reader.ReadToEnd().Replace("\r\n", "\n"));
                while (!stopped.WaitOne(0))
                {
                    var line = session.StandardOutput.ReadLineAsync();
                    if (!line.Wait(TimeSpan.FromSeconds(Math.Max(12, settings.IntervalSeconds * 3))))
                        throw new IOException("응답 지연");
                    if (line.Result == null)
                    {
                        string detail = errors.Wait(1000) ? errors.Result : "";
                        throw new IOException(ConnectionError(detail));
                    }
                    Publish(Parse(line.Result));
                }
            }
            catch (Exception error)
            {
                if (!stopped.WaitOne(0) && errors != null && errors.Wait(300) && !String.IsNullOrEmpty(errors.Result))
                    throw new IOException(ConnectionError(errors.Result));
                if (error is IOException && error.Message == "응답 지연") throw;
                throw new IOException("연결 끊김");
            }
            finally
            {
                lock (gate)
                {
                    if (session != null) StopProcess(session);
                    if (process == session) process = null;
                    if (session != null) session.Dispose();
                }
            }
        }

        public static string Validate(HardwareWidgetSettings options)
        {
            if (String.IsNullOrWhiteSpace(options.Name)) return "위젯 이름을 입력해 주세요.";
            if (options.IntervalSeconds < 1 || options.IntervalSeconds > 60) return "갱신 간격은 1~60초로 설정해 주세요.";
            if (!options.ShowCpu && !options.ShowMemory && !options.ShowGpu && !options.ShowVram && !options.ShowNetwork) return "표시할 항목을 하나 이상 선택해 주세요.";
            if (!options.Remote) return null;
            if (String.IsNullOrWhiteSpace(options.Host) || options.Host.StartsWith("-") || !Regex.IsMatch(options.Host, @"^[a-zA-Z0-9_.:\-]+$")) return "SSH 주소 또는 설정에 등록된 호스트 이름을 입력해 주세요.";
            if (options.User.Length > 0 && !Regex.IsMatch(options.User, @"^[a-zA-Z0-9_.\-]+$")) return "SSH 사용자 이름을 확인해 주세요.";
            if (options.Port < 0 || options.Port > 65535) return "포트는 1~65535 또는 빈 값으로 설정해 주세요.";
            if (options.KeyFile.Length > 0 && !File.Exists(options.KeyFile)) return "SSH 키 파일을 찾을 수 없습니다.";
            if (options.JumpHost.Length > 0 && !JumpMatch(options.JumpHost).Success) return "경유 SSH 호스트를 확인해 주세요. 예: nas 또는 사용자@주소:포트";
            if (options.JumpHost.Length > 0)
            {
                var jump = JumpMatch(options.JumpHost);
                int jumpPort;
                if (jump.Groups["port"].Success && (!Int32.TryParse(jump.Groups["port"].Value, out jumpPort) || jumpPort < 1 || jumpPort > 65535))
                    return "경유 SSH 포트는 1~65535로 설정해 주세요.";
            }
            return null;
        }

        private static Match JumpMatch(string value)
        {
            return Regex.Match(value, @"^(?:(?<user>[a-zA-Z0-9_.\-]+)@)?(?<host>[a-zA-Z0-9_][a-zA-Z0-9_.\-]*|\[[0-9a-fA-F:]+\])(?::(?<port>[0-9]+))?$");
        }

        internal static string Arguments(HardwareWidgetSettings options)
        {
            var args = SshOptions();
            string command = "python3 -u - " + options.IntervalSeconds + " " + (options.ShowGpu || options.ShowVram ? "1" : "0");
            if (options.JumpHost.Length == 0)
            {
                if (options.Port > 0) args.AddRange(new[] { "-p", options.Port.ToString() });
                if (options.User.Length > 0) args.AddRange(new[] { "-l", options.User });
            }
            else
            {
                var jump = JumpMatch(options.JumpHost);
                if (jump.Groups["port"].Success) args.AddRange(new[] { "-p", jump.Groups["port"].Value });
                if (jump.Groups["user"].Success) args.AddRange(new[] { "-l", jump.Groups["user"].Value });
                var nested = SshOptions();
                if (options.Port > 0) nested.AddRange(new[] { "-p", options.Port.ToString() });
                if (options.User.Length > 0) nested.AddRange(new[] { "-l", options.User });
                nested.Add(options.Host); nested.Add(command);
                command = "exec ssh " + String.Join(" ", nested.Select(RemoteQuote));
            }
            if (options.KeyFile.Length > 0) args.AddRange(new[] { "-i", options.KeyFile });
            args.Add(options.JumpHost.Length == 0 ? options.Host : JumpMatch(options.JumpHost).Groups["host"].Value.Trim('[', ']'));
            args.Add(command);
            return String.Join(" ", args.Select(Quote));
        }

        private static List<string> SshOptions()
        {
            return new List<string> { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5",
                "-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=1" };
        }

        private static string RemoteQuote(string value) { return "'" + value.Replace("'", "'\"'\"'") + "'"; }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static string ConnectionError(string detail)
        {
            if (detail.IndexOf("Host key verification failed", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("REMOTE HOST IDENTIFICATION", StringComparison.OrdinalIgnoreCase) >= 0) return "SSH 호스트 키 확인 필요";
            if (detail.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0) return "SSH 인증 실패";
            if (detail.IndexOf("administratively prohibited", StringComparison.OrdinalIgnoreCase) >= 0) return "NAS SSH 포트 전달 허용 필요";
            if (detail.IndexOf("python3", StringComparison.OrdinalIgnoreCase) >= 0 && (detail.Contains("not found") || detail.Contains("No such file"))) return "원격 PC에 Python 3 필요";
            return "연결 끊김";
        }

        public static HardwareSnapshot Parse(string json)
        {
            Dictionary<string, object> values;
            using (var document = JsonDocument.Parse(json)) values = Plain(document.RootElement) as Dictionary<string, object>;
            if (values == null || Number(values, "memoryTotal") <= 0 || !Number(values, "memoryTotal").HasValue) throw new IOException("모니터링 응답을 읽을 수 없습니다.");
            var gpuValues = values.ContainsKey("gpu") ? values["gpu"] as System.Collections.IEnumerable : null;
            var gpus = new List<GpuSample>();
            if (gpuValues != null)
                foreach (var item in gpuValues)
                {
                    var gpu = item as Dictionary<string, object>;
                    if (gpu == null || !Number(gpu, "index").HasValue) continue;
                    gpus.Add(new GpuSample { Index = (int)Number(gpu, "index").Value, Name = gpu.ContainsKey("name") ? Convert.ToString(gpu["name"]) : "GPU",
                        Utilization = Number(gpu, "utilization"), MemoryUsed = Number(gpu, "memoryUsed"), MemoryTotal = Number(gpu, "memoryTotal"),
                        Temperature = Number(gpu, "temperature"), Power = Number(gpu, "power") });
                }
            return new HardwareSnapshot
            {
                System = new SystemSnapshot { Cpu = Number(values, "cpu"), MemoryPercent = Number(values, "memoryPercent"),
                    MemoryUsedBytes = Number(values, "memoryUsed"), MemoryTotalBytes = Number(values, "memoryTotal"),
                    DownloadBytes = Number(values, "download"), UploadBytes = Number(values, "upload") },
                Gpu = new GpuSnapshot { Devices = gpus.ToArray(), Error = values.ContainsKey("gpuError") && values["gpuError"] != null ? Convert.ToString(values["gpuError"]) : null }
            };
        }

        private static object Plain(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object: return element.EnumerateObject().ToDictionary(property => property.Name, property => Plain(property.Value));
                case JsonValueKind.Array: return element.EnumerateArray().Select(Plain).ToList();
                case JsonValueKind.Number: return element.GetDouble();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }

        private static double? Number(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null) return null;
            double number;
            return Double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) && number >= 0 ? (double?)number : null;
        }

        private static HardwareSnapshot Failure(string message)
        {
            return new HardwareSnapshot { Error = message, Gpu = new GpuSnapshot { Error = message } };
        }

        private static void StopProcess(Process session)
        {
            try { if (!session.HasExited) session.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopped.Set();
                if (process != null) StopProcess(process);
            }
        }
    }
}
