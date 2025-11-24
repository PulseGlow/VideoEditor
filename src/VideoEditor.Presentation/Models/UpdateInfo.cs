using System;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 更新信息模型
    /// </summary>
    public class UpdateInfo
    {
        /// <summary>
        /// 版本号（如：1.2.0）
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// 版本标签（如：v1.2.0）
        /// </summary>
        public string TagName { get; set; } = string.Empty;

        /// <summary>
        /// 版本名称（如：版本 1.2.0）
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 更新说明（Release Notes）
        /// </summary>
        public string ReleaseNotes { get; set; } = string.Empty;

        /// <summary>
        /// 下载页面 URL
        /// </summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// 发布日期
        /// </summary>
        public DateTime? ReleaseDate { get; set; }

        /// <summary>
        /// 是否为预发布版本
        /// </summary>
        public bool IsPrerelease { get; set; }

        /// <summary>
        /// 是否有可用更新
        /// </summary>
        public bool HasUpdate { get; set; }

        /// <summary>
        /// 当前版本号
        /// </summary>
        public string CurrentVersion { get; set; } = string.Empty;
    }
}

