using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Wallpaper
{
    internal sealed class SystemSnapshot
    {
        public double? Cpu, MemoryPercent, MemoryUsedBytes, MemoryTotalBytes, DownloadBytes, UploadBytes;
    }

    internal sealed class HardwareSnapshot
    {
        public string Error;
        public SystemSnapshot System = new SystemSnapshot();
        public GpuSnapshot Gpu = new GpuSnapshot();
    }

    internal sealed class HardwareMonitor
    {
        private long lastIdle, lastTotal, lastNetworkTime;
        private bool hasCpuSample;
        private readonly Dictionary<string, long[]> networkCounters = new Dictionary<string, long[]>();

        public HardwareSnapshot Read(bool includeGpu = true)
        {
            var system = ReadSystem();
            return new HardwareSnapshot { System = system, Gpu = includeGpu ? GpuMonitor.Read() : new GpuSnapshot() };
        }

        public SystemSnapshot ReadSystem()
        {
            var sample = new SystemSnapshot();
            long idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                long total = kernel + user;
                if (hasCpuSample) sample.Cpu = CpuPercent(idle - lastIdle, total - lastTotal);
                lastIdle = idle; lastTotal = total; hasCpuSample = true;
            }
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref memory))
            {
                sample.MemoryPercent = memory.Load;
                sample.MemoryTotalBytes = memory.TotalPhysical;
                sample.MemoryUsedBytes = memory.TotalPhysical - memory.AvailablePhysical;
            }
            ReadNetwork(sample);
            return sample;
        }

        private void ReadNetwork(SystemSnapshot sample)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - lastNetworkTime) / (double)Stopwatch.Frequency;
            var current = new Dictionary<string, long[]>();
            long received = 0, sent = 0;
            bool hasPrevious = false;
            try
            {
                var connected = NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet || adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)).ToArray();
                var internet = connected.Where(adapter => adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                    !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any))).ToArray();
                foreach (var adapter in internet.Length > 0 ? internet : connected)
                {
                    var statistics = adapter.GetIPStatistics();
                    var counters = new[] { statistics.BytesReceived, statistics.BytesSent };
                    current[adapter.Id] = counters;
                    long[] previous;
                    if (networkCounters.TryGetValue(adapter.Id, out previous) && counters[0] >= previous[0] && counters[1] >= previous[1])
                    {
                        received += counters[0] - previous[0]; sent += counters[1] - previous[1]; hasPrevious = true;
                    }
                }
                if (hasPrevious && seconds > 0)
                {
                    sample.DownloadBytes = received / seconds;
                    sample.UploadBytes = sent / seconds;
                }
            }
            catch (NetworkInformationException) { }
            networkCounters.Clear();
            foreach (var entry in current) networkCounters[entry.Key] = entry.Value;
            lastNetworkTime = now;
        }

        public static double? CpuPercent(long idleDelta, long totalDelta)
        {
            return totalDelta > 0 && idleDelta >= 0 && idleDelta <= totalDelta
                ? (double?)(100.0 * (totalDelta - idleDelta) / totalDelta) : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    }
}
