using System;

namespace Wallpaper
{
    // 하드웨어 상태 위젯의 연결·표시 항목. 위젯 id 와 모니터별 자리는 WidgetSettings 가 가진다.
    internal sealed class HardwareWidgetSettings
    {
        public string Name = "내 PC";
        public bool Remote;
        public string Host = "", User = "", KeyFile = "", JumpHost = "";
        public int Port, IntervalSeconds = 2;
        public bool ShowCpu = true, ShowMemory = true, ShowGpu = true, ShowVram = true, ShowNetwork = true;
        // 위젯 모양(배경 진하기·그라데이션 범위 등)
        public System.Collections.Generic.Dictionary<string, string> Options = new System.Collections.Generic.Dictionary<string, string>();

        public HardwareWidgetSettings Copy() { return (HardwareWidgetSettings)MemberwiseClone(); }
    }
}
