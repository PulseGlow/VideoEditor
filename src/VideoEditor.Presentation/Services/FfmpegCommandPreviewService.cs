using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// FFmpeg命令预览服务 - 统一处理命令生成和显示到命令提示符
    /// </summary>
    public class FfmpegCommandPreviewService
    {
        /// <summary>
        /// 命令项定义
        /// </summary>
        public class CommandItem
        {
            /// <summary>
            /// 命令序号
            /// </summary>
            public int Index { get; set; }

            /// <summary>
            /// 总命令数
            /// </summary>
            public int Total { get; set; }

            /// <summary>
            /// 任务标识（用于显示）
            /// </summary>
            public string TaskId { get; set; } = string.Empty;

            /// <summary>
            /// 输入文件路径
            /// </summary>
            public string InputPath { get; set; } = string.Empty;

            /// <summary>
            /// 输出文件路径
            /// </summary>
            public string OutputPath { get; set; } = string.Empty;

            /// <summary>
            /// FFmpeg命令参数（不含ffmpeg可执行文件名）
            /// </summary>
            public string CommandArguments { get; set; } = string.Empty;
        }

        /// <summary>
        /// 命令预览配置
        /// </summary>
        public class PreviewConfig
        {
            /// <summary>
            /// 操作名称（用于标题）
            /// </summary>
            public string OperationName { get; set; } = "FFmpeg 命令";

            /// <summary>
            /// 操作图标（用于标题）
            /// </summary>
            public string OperationIcon { get; set; } = "💻";

            /// <summary>
            /// 摘要信息行（显示在标题下方）
            /// </summary>
            public List<string> SummaryLines { get; set; } = new List<string>();

            /// <summary>
            /// 输出命令的回调（用于命令提示符）
            /// </summary>
            public Action<string>? AppendOutput { get; set; }

            /// <summary>
            /// 输出命令到预览框的回调（用于命令预览标签页）
            /// </summary>
            public Action<string>? AppendToPreviewBox { get; set; }

            /// <summary>
            /// 更新命令说明的回调（用于命令预览标签页）
            /// </summary>
            public Action<string>? UpdateDescription { get; set; }

            /// <summary>
            /// 切换到命令提示符标签页的回调
            /// </summary>
            public Action? SwitchToCommandTab { get; set; }

            /// <summary>
            /// 设置播放器模式的回调（可选）
            /// </summary>
            public Action<bool>? SetPlayerMode { get; set; }
        }

        /// <summary>
        /// 显示命令预览
        /// </summary>
        public void ShowCommands(List<CommandItem> commands, PreviewConfig config)
        {
            if (commands == null || commands.Count == 0)
            {
                return;
            }

            config.SwitchToCommandTab?.Invoke();
            config.SetPlayerMode?.Invoke(false);

            // 构建命令预览文本
            var previewText = new System.Text.StringBuilder();
            previewText.AppendLine($"{config.OperationIcon} {config.OperationName}");
            previewText.AppendLine("=".PadRight(50, '='));
            previewText.AppendLine();

            // 输出摘要信息
            foreach (var line in config.SummaryLines)
            {
                previewText.AppendLine(line);
                config.AppendOutput?.Invoke(line);
            }
            previewText.AppendLine();
            config.AppendOutput?.Invoke("");

            // 输出标题（用于命令提示符）
            config.AppendOutput?.Invoke("\r\n" + "=".PadRight(50, '='));
            config.AppendOutput?.Invoke($"{config.OperationIcon} {config.OperationName}");
            config.AppendOutput?.Invoke("=".PadRight(50, '='));

            // 输出每个命令
            foreach (var cmd in commands)
            {
                var cmdText = $"[{cmd.Index}/{cmd.Total}] {cmd.TaskId}\r\n";
                if (!string.IsNullOrEmpty(cmd.InputPath))
                {
                    cmdText += $"📂 输入: {Path.GetFileName(cmd.InputPath)}\r\n";
                }
                if (!string.IsNullOrEmpty(cmd.OutputPath))
                {
                    cmdText += $"📁 输出: {Path.GetFileName(cmd.OutputPath)}\r\n";
                }
                cmdText += $"💻 命令: ffmpeg {cmd.CommandArguments}\r\n";
                
                previewText.AppendLine(cmdText.TrimEnd());
                config.AppendOutput?.Invoke($"[{cmd.Index}/{cmd.Total}] {cmd.TaskId}");
                if (!string.IsNullOrEmpty(cmd.InputPath))
                {
                    config.AppendOutput?.Invoke($"📂 输入: {Path.GetFileName(cmd.InputPath)}");
                }
                if (!string.IsNullOrEmpty(cmd.OutputPath))
                {
                    config.AppendOutput?.Invoke($"📁 输出: {Path.GetFileName(cmd.OutputPath)}");
                }
                config.AppendOutput?.Invoke($"💻 命令: ffmpeg {cmd.CommandArguments}");
                config.AppendOutput?.Invoke("");
            }

            // 输出使用说明
            var usageText = "\r\n💡 使用说明:\r\n" +
                           "1. 复制上面的FFmpeg命令\r\n" +
                           "2. 在命令提示符中执行\r\n" +
                           "3. 或者使用嵌入式命令提示符执行";
            previewText.AppendLine(usageText);
            config.AppendOutput?.Invoke("💡 使用说明:");
            config.AppendOutput?.Invoke("1. 复制上面的FFmpeg命令");
            config.AppendOutput?.Invoke("2. 在下面的命令输入框中粘贴并执行");
            config.AppendOutput?.Invoke("3. 或者逐个复制命令到命令提示符中执行");
            config.AppendOutput?.Invoke("=".PadRight(50, '='));
            config.AppendOutput?.Invoke("");

            // 构建命令说明文本
            var descriptionText = new System.Text.StringBuilder();
            descriptionText.AppendLine($"• 操作类型: {config.OperationName}");
            if (config.SummaryLines != null && config.SummaryLines.Count > 0)
            {
                foreach (var line in config.SummaryLines)
                {
                    // 将摘要信息转换为说明格式
                    var descLine = line.Replace("📁", "").Replace("📊", "").Replace("📐", "").Replace("🎬", "").Replace("📝", "").Replace("🖼️", "").Replace("📍", "").Replace("🎯", "").Replace("🎨", "").Replace("⚙️", "").Replace("🔗", "").Trim();
                    if (!string.IsNullOrWhiteSpace(descLine))
                    {
                        descriptionText.AppendLine($"• {descLine}");
                    }
                }
            }
            descriptionText.AppendLine($"• 命令数量: {commands.Count}");
            descriptionText.AppendLine("• 提示: 可以点击\"复制命令\"按钮复制命令，或点击\"编辑命令\"修改命令");

            // 输出到命令预览框和说明框
            config.AppendToPreviewBox?.Invoke(previewText.ToString());
            config.UpdateDescription?.Invoke(descriptionText.ToString());
        }
    }
}

