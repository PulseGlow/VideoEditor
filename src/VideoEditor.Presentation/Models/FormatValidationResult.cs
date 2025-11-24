using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 视频格式验证结果
    /// </summary>
    public class FormatValidationResult
    {
        /// <summary>
        /// 文件路径
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// 是否支持该格式
        /// </summary>
        public bool IsSupported { get; set; }

        /// <summary>
        /// 错误消息（如果不支持）
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// 视频时长（毫秒）
        /// </summary>
        public long Duration { get; set; }

        /// <summary>
        /// 视频编解码器
        /// </summary>
        public string VideoCodec { get; set; } = string.Empty;

        /// <summary>
        /// 音频编解码器
        /// </summary>
        public string AudioCodec { get; set; } = string.Empty;

        /// <summary>
        /// 是否包含视频轨道
        /// </summary>
        public bool HasVideo { get; set; }

        /// <summary>
        /// 是否包含音频轨道
        /// </summary>
        public bool HasAudio { get; set; }

        /// <summary>
        /// 验证耗时（毫秒）
        /// </summary>
        public long ValidationTime { get; set; }

        /// <summary>
        /// 格式化的文件大小
        /// </summary>
        public string FormattedFileSize
        {
            get
            {
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                double len = FileSize;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }

        /// <summary>
        /// 格式化的时长
        /// </summary>
        public string FormattedDuration
        {
            get
            {
                if (Duration <= 0) return "未知";
                var timeSpan = TimeSpan.FromMilliseconds(Duration);
                return timeSpan.ToString(@"hh\:mm\:ss\.fff");
            }
        }

        /// <summary>
        /// 获取验证状态描述
        /// </summary>
        public string StatusDescription
        {
            get
            {
                if (IsSupported)
                {
                    return HasVideo && HasAudio ? "视频+音频" :
                           HasVideo ? "仅视频" :
                           HasAudio ? "仅音频" : "未知媒体类型";
                }
                else
                {
                    return $"不支持: {ErrorMessage}";
                }
            }
        }

        /// <summary>
        /// 获取简短的状态图标
        /// </summary>
        public string StatusIcon => IsSupported ? "✓" : "✗";

        /// <summary>
        /// 获取状态颜色（用于UI显示）
        /// </summary>
        public string StatusColor => IsSupported ? "#4CAF50" : "#F44336";
    }
}
