using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 去重模式
    /// </summary>
    public enum DeduplicateMode
    {
        Off,      // 关闭
        Light,    // 轻度
        Medium,   // 中度
        Heavy     // 重度
    }

    /// <summary>
    /// 去重参数
    /// </summary>
    public class DeduplicateParameters
    {
        /// <summary>
        /// 去重模式
        /// </summary>
        public DeduplicateMode Mode { get; set; } = DeduplicateMode.Off;

        // 色彩调整参数
        /// <summary>
        /// 亮度调整 (-10 到 10，百分比)
        /// </summary>
        public double Brightness { get; set; } = 0;

        /// <summary>
        /// 对比度调整 (-10 到 10，百分比)
        /// </summary>
        public double Contrast { get; set; } = 0;

        /// <summary>
        /// 饱和度调整 (-10 到 10，百分比)
        /// </summary>
        public double Saturation { get; set; } = 0;

        // 高级效果参数
        /// <summary>
        /// 噪点强度 (0 到 30)
        /// </summary>
        public double Noise { get; set; } = 0;

        /// <summary>
        /// 模糊程度 (0 到 10)
        /// </summary>
        public double Blur { get; set; } = 0;

        /// <summary>
        /// 边缘裁剪 (0 到 10，像素)
        /// </summary>
        public double CropEdge { get; set; } = 0;

        /// <summary>
        /// 根据模式自动设置参数
        /// </summary>
        public void ApplyMode()
        {
            switch (Mode)
            {
                case DeduplicateMode.Off:
                    Brightness = 0;
                    Contrast = 0;
                    Saturation = 0;
                    Noise = 0;
                    Blur = 0;
                    CropEdge = 0;
                    break;

                case DeduplicateMode.Light:
                    // 轻度：亮度±3%, 对比度±3%
                    Brightness = 3;
                    Contrast = 3;
                    Saturation = 0;
                    Noise = 0;
                    Blur = 0;
                    CropEdge = 0;
                    break;

                case DeduplicateMode.Medium:
                    // 中度：亮度±5%, 对比度±5%, 噪点
                    Brightness = 5;
                    Contrast = 5;
                    Saturation = 2;
                    Noise = 10;
                    Blur = 1;
                    CropEdge = 1;
                    break;

                case DeduplicateMode.Heavy:
                    // 重度：综合处理+模糊+裁剪
                    Brightness = 7;
                    Contrast = 7;
                    Saturation = 5;
                    Noise = 20;
                    Blur = 3;
                    CropEdge = 3;
                    break;
            }
        }
    }
}

