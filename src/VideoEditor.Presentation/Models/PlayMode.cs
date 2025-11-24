using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 播放模式枚举
    /// </summary>
    public enum PlayMode
    {
        /// <summary>
        /// 顺序播放
        /// </summary>
        Sequential,
        
        /// <summary>
        /// 随机播放
        /// </summary>
        Random,
        
        /// <summary>
        /// 单曲循环
        /// </summary>
        RepeatOne,
        
        /// <summary>
        /// 列表循环
        /// </summary>
        RepeatAll,
        
        /// <summary>
        /// 随机循环
        /// </summary>
        Shuffle
    }

    /// <summary>
    /// 播放队列状态
    /// </summary>
    public enum PlayQueueState
    {
        /// <summary>
        /// 空队列
        /// </summary>
        Empty,
        
        /// <summary>
        /// 准备播放
        /// </summary>
        Ready,
        
        /// <summary>
        /// 正在播放
        /// </summary>
        Playing,
        
        /// <summary>
        /// 已暂停
        /// </summary>
        Paused,
        
        /// <summary>
        /// 播放完成
        /// </summary>
        Completed,
        
        /// <summary>
        /// 播放错误
        /// </summary>
        Error
    }
}

