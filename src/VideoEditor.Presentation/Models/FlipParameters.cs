using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 翻转类型
    /// </summary>
    public enum FlipType
    {
        None,           // 无翻转
        Horizontal,     // 水平翻转 (hflip)
        Vertical,       // 垂直翻转 (vflip)
        Both            // 水平+垂直翻转
    }

    /// <summary>
    /// 旋转类型
    /// </summary>
    public enum RotateType
    {
        None,           // 无旋转
        Rotate90,       // 顺时针90度
        Rotate180,      // 180度
        Rotate270,      // 逆时针90度（270度）
        Custom,         // 自定义角度
        Auto            // 自动检测（根据视频元数据）
    }

    /// <summary>
    /// 转置类型
    /// </summary>
    public enum TransposeType
    {
        None,           // 无转置
        Transpose0,     // Transpose 0: 顺时针90°+垂直翻转
        Transpose1,     // Transpose 1: 逆时针90°
        Transpose2,     // Transpose 2: 顺时针90°
        Transpose3      // Transpose 3: 逆时针90°+垂直翻转
    }

    /// <summary>
    /// 翻转参数
    /// </summary>
    public class FlipParameters
    {
        /// <summary>
        /// 翻转类型
        /// </summary>
        public FlipType FlipType { get; set; } = FlipType.None;

        /// <summary>
        /// 旋转类型
        /// </summary>
        public RotateType RotateType { get; set; } = RotateType.None;

        /// <summary>
        /// 自定义旋转角度（0-360度）
        /// </summary>
        public double CustomRotateAngle { get; set; } = 0;

        /// <summary>
        /// 转置类型
        /// </summary>
        public TransposeType TransposeType { get; set; } = TransposeType.None;
    }
}

