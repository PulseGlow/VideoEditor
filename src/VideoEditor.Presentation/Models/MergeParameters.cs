namespace VideoEditor.Presentation.Models
{
    public class MergeParameters
    {
        /// <summary>
        /// 使用concat demuxer快速无损合并（复制编码），要求所有文件编码一致
        /// </summary>
        public bool UseFastConcat { get; set; } = true;

        /// <summary>
        /// 是否在合并后自动清理临时文件
        /// </summary>
        public bool CleanupListFile { get; set; } = true;
    }
}

