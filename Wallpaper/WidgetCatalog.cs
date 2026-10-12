namespace Wallpaper
{
    internal static class WidgetCatalog
    {
        public static readonly WidgetDefinition[] All =
        {
            new WidgetDefinition("clock", "시계", "큰 디지털 시계 · 초 단위 플립"),
            new WidgetDefinition("calendar", "달력", "월간 달력 · 오늘 날짜 표시"),
            new WidgetDefinition("gpu", "하드웨어 상태", "CPU · GPU · RAM\n다운로드 · 업로드")
        };
    }

    internal sealed class WidgetDefinition
    {
        public readonly string Id, Name, Description;
        public WidgetDefinition(string id, string name, string description)
        { Id = id; Name = name; Description = description; }
    }
}
