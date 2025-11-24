namespace VideoEditor.Presentation.Models
{
    public enum SubtitlePosition
    {
        Top,
        Center,
        Bottom
    }

    public class SubtitleParameters
    {
        public string SubtitleFilePath { get; set; } = string.Empty;
        public string FontFamily { get; set; } = "微软雅黑";
        public int FontSize { get; set; } = 24;
        public string FontColor { get; set; } = "white";
        public SubtitlePosition Position { get; set; } = SubtitlePosition.Bottom;
        public double OutlineWidth { get; set; } = 2.0;
        public bool EnableShadow { get; set; } = true;
        public double TimeOffset { get; set; } = 0.0; // 秒
    }
}

