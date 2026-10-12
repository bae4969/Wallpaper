using System;

namespace Wallpaper
{
    internal sealed class HardwareWidgetSettings
    {
        public string Id = "hardware-" + Guid.NewGuid().ToString("N");
        public string Name = "내 PC";
        public bool Enabled = true, Remote;
        public string Host = "", User = "", KeyFile = "", JumpHost = "";
        public int Port, IntervalSeconds = 2;
        public bool ShowCpu = true, ShowMemory = true, ShowGpu = true, ShowVram = true, ShowNetwork = true;
        public WidgetPlacement Placement;
        // 위젯 모양(배경 진하기·그라데이션 범위 등)
        public System.Collections.Generic.Dictionary<string, string> Options = new System.Collections.Generic.Dictionary<string, string>();

        public HardwareWidgetSettings Copy() { return (HardwareWidgetSettings)MemberwiseClone(); }
    }
}
