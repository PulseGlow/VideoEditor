using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// GIF质量模式枚举
    /// </summary>
    public enum GifQualityMode
    {
        /// <summary>
        /// 低质量（小文件）
        /// </summary>
        Low,
        
        /// <summary>
        /// 中等质量（推荐）
        /// </summary>
        Medium,
        
        /// <summary>
        /// 高质量（大文件）
        /// </summary>
        High
    }

    /// <summary>
    /// GIF制作参数
    /// </summary>
    public class GifParameters
    {
        /// <summary>
        /// 开始时间
        /// </summary>
        public TimeSpan StartTime { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// 结束时间
        /// </summary>
        public TimeSpan EndTime { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// FPS（帧率）
        /// </summary>
        public int FPS { get; set; } = 10;

        /// <summary>
        /// 宽度（像素）
        /// </summary>
        public int Width { get; set; } = 480;

        /// <summary>
        /// 质量模式
        /// </summary>
        public GifQualityMode QualityMode { get; set; } = GifQualityMode.Medium;
    }
}

