using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 时间码片段
    /// </summary>
    public class TimecodeSegment
    {
        /// <summary>
        /// 开始时间（毫秒）
        /// </summary>
        public long StartTime { get; set; }

        /// <summary>
        /// 结束时间（毫秒）
        /// </summary>
        public long EndTime { get; set; }

        /// <summary>
        /// 片段序号
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// 格式化显示（用于输出框）
        /// </summary>
        public string ToDisplayString()
        {
            return $"{FormatTime(StartTime)} - {FormatTime(EndTime)}";
        }

        /// <summary>
        /// 格式化时间（毫秒转 HH:mm:ss.fff）
        /// </summary>
        private static string FormatTime(long milliseconds)
        {
            var totalSeconds = milliseconds / 1000.0;
            var hours = (int)(totalSeconds / 3600);
            var minutes = (int)((totalSeconds % 3600) / 60);
            var seconds = (int)(totalSeconds % 60);
            var ms = milliseconds % 1000;

            return $"{hours:D2}:{minutes:D2}:{seconds:D2}.{ms:D3}";
        }
    }
}

