using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Wallpaper
{
    internal sealed class GpuSample
    {
        public int Index;
        public string Name;
        public double? Utilization, MemoryUsed, MemoryTotal, Temperature, Power;
    }

    internal sealed class GpuSnapshot
    {
        public GpuSample[] Devices = new GpuSample[0];
        public string Error;
    }

    internal static class GpuMonitor
    {
        public static GpuSnapshot Read()
        {
            string executable = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
            }.FirstOrDefault(File.Exists);
            if (executable == null) return Failed("NVIDIA 드라이버와 nvidia-smi를 찾을 수 없습니다.");
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(executable,
                        "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    process.Start();
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(2500))
                    {
                        process.Kill();
                        process.WaitForExit();
                        output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
                        return Failed("GPU 정보 응답이 지연되고 있습니다.");
                    }
                    string text = output.GetAwaiter().GetResult();
                    error.GetAwaiter().GetResult();
                    if (process.ExitCode != 0) return Failed("GPU 정보를 읽을 수 없습니다. NVIDIA 드라이버를 확인하세요.");
                    return Parse(text);
                }
            }
            catch (Win32Exception) { return Failed("GPU 정보 프로그램을 실행할 수 없습니다."); }
            catch (IOException) { return Failed("GPU 정보를 읽을 수 없습니다."); }
            catch (InvalidOperationException) { return Failed("GPU 정보 응답이 중단되었습니다."); }
        }

        public static GpuSnapshot Parse(string text)
        {
            var devices = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(',').Select(field => field.Trim()).ToArray())
                .Where(fields => fields.Length >= 7)
                .Select(fields =>
                {
                    int index;
                    if (!Int32.TryParse(fields[0], out index)) return null;
                    int metrics = fields.Length - 5;
                    return new GpuSample
                    {
                        Index = index, Name = String.Join(", ", fields.Skip(1).Take(fields.Length - 6).ToArray()).Trim('"'),
                        Utilization = Number(fields[metrics]), MemoryUsed = Number(fields[metrics + 1]),
                        MemoryTotal = Number(fields[metrics + 2]), Temperature = Number(fields[metrics + 3]),
                        Power = Number(fields[metrics + 4])
                    };
                }).Where(device => device != null).ToArray();
            return devices.Length == 0 ? Failed("NVIDIA GPU 정보가 없습니다.") : new GpuSnapshot { Devices = devices };
        }

        private static double? Number(string text)
        {
            double value;
            return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 ? (double?)value : null;
        }

        public static GpuSnapshot Failed(string message) { return new GpuSnapshot { Error = message }; }
    }

    internal sealed class HardwareView : Border
    {
        private readonly StackPanel devices;
        private const double ContentWidth = 224;
        private static readonly Brush Warning = Theme.Brush("#FFF0C674");

        private GpuSample[] knownGpus = new GpuSample[0];
        private readonly HardwareWidgetSettings settings;

        public HardwareView(HardwareWidgetSettings options = null)
        {
            settings = options ?? new HardwareWidgetSettings { Name = "하드웨어 상태" };
            Width = ContentWidth + 256; Height = 568; Padding = new Thickness(128);
            Background = Theme.Fade(Width, Height);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            layout.RowDefinitions.Add(new RowDefinition());
            var heading = Label(settings.Name, 22, Theme.White);
            heading.FontWeight = FontWeights.SemiBold;
            heading.TextTrimming = TextTrimming.CharacterEllipsis;
            heading.Width = ContentWidth;
            layout.Children.Add(heading);
            devices = new StackPanel();
            devices.Children.Add(Label("하드웨어 정보를 불러오는 중…", 13, Theme.Muted));
            Grid.SetRow(devices, 1); layout.Children.Add(devices);
            Child = layout;
        }

        public bool Update(HardwareSnapshot snapshot)
        {
            if (snapshot.Gpu.Devices.Length > 0) knownGpus = snapshot.Gpu.Devices;
            var gpus = snapshot.Gpu.Error == null ? snapshot.Gpu.Devices : knownGpus.Select(gpu =>
                new GpuSample { Index = gpu.Index, Name = gpu.Name }).ToArray();
            int rows = (settings.ShowCpu ? 1 : 0) + (settings.ShowMemory ? 1 : 0) +
                gpus.Length * ((settings.ShowGpu ? 1 : 0) + (settings.ShowVram ? 1 : 0));
            double height = 256 + 42 + rows * 36 + (settings.ShowNetwork ? 54 : 0);
            bool resized = Height != height;
            if (resized) { Height = height; Background = Theme.Fade(Width, Height); }
            devices.Children.Clear();
            if (snapshot.Error != null)
            {
                devices.Children.Add(Label(snapshot.Error, 12, Theme.Muted));
                return resized;
            }
            if (settings.ShowCpu) Metric("CPU", snapshot.System.Cpu, Format(snapshot.System.Cpu, "0", "%"), "HardwareCpu");
            if (settings.ShowMemory) Metric("메모리", snapshot.System.MemoryPercent, Format(snapshot.System.MemoryPercent, "0", "%") + " · " +
                Format(snapshot.System.MemoryUsedBytes / 1073741824, "0.0", "") + "/" +
                Format(snapshot.System.MemoryTotalBytes / 1073741824, "0.0", " GB"), "HardwareMemory");
            foreach (var gpu in settings.ShowGpu ? gpus : new GpuSample[0])
            {
                Metric("GPU" + gpu.Index, gpu.Utilization, Format(gpu.Utilization, "0", "%") + " · " +
                    Format(gpu.Temperature, "0", "°C") + " · " + Format(gpu.Power, "0", "W"), "GpuUtilization_" + gpu.Index);
            }
            foreach (var gpu in settings.ShowVram ? gpus : new GpuSample[0])
            {
                double? percent = gpu.MemoryTotal > 0 ? gpu.MemoryUsed / gpu.MemoryTotal * 100 : null;
                Metric("VRAM" + gpu.Index, percent, Format(percent, "0", "%") + " · " +
                    Format(gpu.MemoryUsed / 1024, "0.0", "") + "/" + Format(gpu.MemoryTotal / 1024, "0.0", " GB"), "GpuMemory_" + gpu.Index);
            }
            if (settings.ShowNetwork)
            {
                Rate("다운로드", snapshot.System.DownloadBytes, "NetworkDownload", true);
                Rate("업로드", snapshot.System.UploadBytes, "NetworkUpload", false);
            }
            return resized;
        }

        private void Metric(string name, double? percent, string text, string id)
        {
            var row = new Grid { Height = 36 };
            var heading = Label(name, 13, Theme.White);
            heading.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(heading);
            var color = percent >= 90 ? Theme.Sunday : percent >= 70 ? Warning : Theme.Saturday;
            var value = Label("", 12, Theme.White);
            int end = text.IndexOf('%') + 1;
            if (end > 0)
            {
                value.Inlines.Add(new Run(text.Substring(0, end)) { Foreground = percent >= 70 ? color : Theme.White });
                value.Inlines.Add(new Run(text.Substring(end)) { Foreground = Theme.Muted });
            }
            else { value.Text = text; value.Foreground = Theme.Muted; }
            value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetAutomationId(value, id);
            AutomationProperties.SetName(value, text);
            row.Children.Add(value);
            var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 0, 0), Background = Theme.Brush("#20FFFFFF") };
            track.Children.Add(new Border
            {
                Background = color, HorizontalAlignment = HorizontalAlignment.Left,
                Width = ContentWidth * Math.Max(0, Math.Min(100, percent ?? 0)) / 100
            });
            row.Children.Add(track); devices.Children.Add(row);
        }

        private void Rate(string name, double? bytes, string id, bool first)
        {
            var row = new Grid { Height = 23, Margin = new Thickness(0, first ? 8 : 0, 0, 0) };
            row.Children.Add(Label(name, 12, Theme.Muted));
            var value = Label(FormatRate(bytes), 13, Theme.White);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetAutomationId(value, id); row.Children.Add(value);
            devices.Children.Add(row);
        }

        public static string FormatRate(double? bytes)
        {
            if (!bytes.HasValue) return "—";
            if (bytes >= 1048576) return (bytes.Value / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytes >= 1024) return (bytes.Value / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            return bytes.Value.ToString("0", CultureInfo.InvariantCulture) + " B/s";
        }

        private static TextBlock Label(string text, double size, Brush brush)
        {
            var label = Theme.Text(text, size, brush);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            return label;
        }

        private static string Format(double? value, string format, string unit)
        { return value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) + unit : "—"; }
    }
}
