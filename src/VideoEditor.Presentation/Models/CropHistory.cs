using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 裁剪历史记录
    /// </summary>
    public class CropHistory
    {
        /// <summary>
        /// 记录ID
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 操作时间
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 原始视频文件路径
        /// </summary>
        public string OriginalVideoPath { get; set; } = string.Empty;

        /// <summary>
        /// 输出视频文件路径
        /// </summary>
        public string OutputVideoPath { get; set; } = string.Empty;

        /// <summary>
        /// 裁剪参数
        /// </summary>
        public CropParameters Parameters { get; set; } = new CropParameters();

        /// <summary>
        /// 处理状态
        /// </summary>
        public CropStatus Status { get; set; } = CropStatus.Pending;

        /// <summary>
        /// 处理用时（毫秒）
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// 错误信息（如果失败）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 输出文件大小（字节）
        /// </summary>
        public long OutputFileSize { get; set; }

        /// <summary>
        /// 原始文件名（不含路径）
        /// </summary>
        public string OriginalFileName => string.IsNullOrEmpty(OriginalVideoPath)
            ? "未知文件"
            : System.IO.Path.GetFileName(OriginalVideoPath);

        /// <summary>
        /// 输出文件名（不含路径）
        /// </summary>
        public string OutputFileName => string.IsNullOrEmpty(OutputVideoPath)
            ? "未知文件"
            : System.IO.Path.GetFileName(OutputVideoPath);

        /// <summary>
        /// 裁剪区域描述
        /// </summary>
        public string CropRegionDescription => $"{Parameters.Width}x{Parameters.Height} (位置: {Parameters.X},{Parameters.Y})";
    }

    /// <summary>
    /// 裁剪参数
    /// </summary>
    public class CropParameters
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 裁剪状态
    /// </summary>
    public enum CropStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cancelled
    }
}
