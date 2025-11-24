using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 水印类型
    /// </summary>
    public enum WatermarkType
    {
        None,      // 无
        Image,     // 图片水印
        Text       // 文字水印
    }

    /// <summary>
    /// 水印参数
    /// </summary>
    public class WatermarkParameters
    {
        /// <summary>
        /// 水印类型
        /// </summary>
        public WatermarkType Type { get; set; } = WatermarkType.None;

        // 图片水印参数
        /// <summary>
        /// 水印图片路径
        /// </summary>
        public string ImagePath { get; set; } = string.Empty;

        /// <summary>
        /// 图片透明度 (0-100)
        /// </summary>
        public double ImageOpacity { get; set; } = 80;

        // 文字水印参数
        /// <summary>
        /// 水印文字
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// 字体大小
        /// </summary>
        public int FontSize { get; set; } = 24;

        /// <summary>
        /// 文字颜色（FFmpeg格式，如 white, red, #FFFFFF）
        /// </summary>
        public string TextColor { get; set; } = "white";

        /// <summary>
        /// 文字透明度 (0-100)
        /// </summary>
        public double TextOpacity { get; set; } = 80;

        // 位置参数
        /// <summary>
        /// X坐标
        /// </summary>
        public int X { get; set; } = 10;

        /// <summary>
        /// Y坐标
        /// </summary>
        public int Y { get; set; } = 10;
    }

    /// <summary>
    /// 移除水印参数（模糊化处理）
    /// </summary>
    public class RemoveWatermarkParameters
    {
        /// <summary>
        /// 移除区域X坐标
        /// </summary>
        public int X { get; set; } = 0;

        /// <summary>
        /// 移除区域Y坐标
        /// </summary>
        public int Y { get; set; } = 0;

        /// <summary>
        /// 移除区域宽度
        /// </summary>
        public int Width { get; set; } = 100;

        /// <summary>
        /// 移除区域高度
        /// </summary>
        public int Height { get; set; } = 50;
    }
}

