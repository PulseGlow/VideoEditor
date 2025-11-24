using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 音频参数
    /// </summary>
    public class AudioParameters
    {
        /// <summary>
        /// 音量大小 (0-200，100为原始音量)
        /// </summary>
        public double Volume { get; set; } = 100;

        /// <summary>
        /// 淡入时长（秒）
        /// </summary>
        public double FadeIn { get; set; } = 0;

        /// <summary>
        /// 淡出时长（秒）
        /// </summary>
        public double FadeOut { get; set; } = 0;

        /// <summary>
        /// 音频格式 (AAC, MP3, WAV, FLAC, 复制原格式)
        /// </summary>
        public string Format { get; set; } = "AAC";

        /// <summary>
        /// 比特率 (128k, 192k, 256k, 320k)
        /// </summary>
        public string Bitrate { get; set; } = "192k";
    }
}

