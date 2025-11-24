using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// FFmpeg批量处理服务 - 统一处理批量执行、进度更新、日志记录等共同逻辑
    /// </summary>
    public class FfmpegBatchProcessor
    {
        private readonly Dispatcher _dispatcher;

        public FfmpegBatchProcessor(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>
        /// 批量处理任务定义
        /// </summary>
        public class BatchTask
        {
            /// <summary>
            /// 任务标识（用于日志显示）
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
            /// 任务描述（用于日志显示）
            /// </summary>
            public string Description { get; set; } = string.Empty;

            /// <summary>
            /// 执行任务的函数
            /// </summary>
            public Func<string, string, CancellationToken, Task<VideoProcessingResult>> ExecuteTask { get; set; } = null!;

            /// <summary>
            /// 预计处理时长（用于进度估算，可选）
            /// </summary>
            public TimeSpan? EstimatedDuration { get; set; }
        }

        /// <summary>
        /// 批量处理配置
        /// </summary>
        public class BatchConfig
        {
            /// <summary>
            /// 操作名称（用于日志标题）
            /// </summary>
            public string OperationName { get; set; } = "批量处理";

            /// <summary>
            /// 操作图标（用于状态栏）
            /// </summary>
            public string OperationIcon { get; set; } = "⚙️";

            /// <summary>
            /// 操作颜色（用于状态栏）
            /// </summary>
            public string OperationColor { get; set; } = "#2196F3";

            /// <summary>
            /// 日志标题信息（额外信息行）
            /// </summary>
            public List<string> LogHeaderLines { get; set; } = new List<string>();

            /// <summary>
            /// 状态栏更新回调
            /// </summary>
            public Action<string, string, string, string>? UpdateStatusBar { get; set; }

            /// <summary>
            /// 进度条更新回调
            /// </summary>
            public Action<double, string>? UpdateProgress { get; set; }

            /// <summary>
            /// 日志追加回调
            /// </summary>
            public Action<string>? AppendLog { get; set; }

            /// <summary>
            /// 切换到执行日志标签页的回调
            /// </summary>
            public Action? SwitchToLogTab { get; set; }

            /// <summary>
            /// 初始化日志的回调（清空并设置标题）
            /// </summary>
            public Action<string>? InitializeLog { get; set; }
        }

        /// <summary>
        /// 批量处理结果
        /// </summary>
        public class BatchResult
        {
            public int TotalTasks { get; set; }
            public int SuccessCount { get; set; }
            public int FailCount { get; set; }
            public TimeSpan TotalTime { get; set; }
            public bool WasCancelled { get; set; }
        }

        /// <summary>
        /// 执行批量处理
        /// </summary>
        public async Task<BatchResult> ExecuteBatchAsync(
            List<BatchTask> tasks,
            BatchConfig config,
            CancellationToken cancellationToken)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return new BatchResult { TotalTasks = 0 };
            }

            var result = new BatchResult
            {
                TotalTasks = tasks.Count
            };

            var batchStartTime = DateTime.Now;
            var processedCount = 0;

            // 初始化UI
            _dispatcher.Invoke(() =>
            {
                config.SwitchToLogTab?.Invoke();
                config.UpdateProgress?.Invoke(0, "准备开始...");

                var logHeader = $"{config.OperationIcon} 开始{config.OperationName}\r\n" +
                               $"📊 待处理任务数: {tasks.Count}\r\n";
                
                foreach (var line in config.LogHeaderLines)
                {
                    logHeader += $"{line}\r\n";
                }
                
                logHeader += $"⏰ 开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                            $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n";
                
                config.InitializeLog?.Invoke(logHeader);
                config.UpdateStatusBar?.Invoke($"处理中: 0/{tasks.Count}", config.OperationIcon, config.OperationColor, "准备中");
            });

            // 逐个处理任务
            foreach (var task in tasks)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.WasCancelled = true;
                    _dispatcher.Invoke(() =>
                    {
                        config.AppendLog?.Invoke("\r\n⚠️ 批量操作已取消\r\n");
                        config.UpdateStatusBar?.Invoke("操作已取消", "⚠️", "#FF9800", "空闲");
                    });
                    break;
                }

                processedCount++;
                var taskStartTime = DateTime.Now;

                // 更新状态栏
                _dispatcher.Invoke(() =>
                {
                    var statusMessage = $"{config.OperationName}中: {processedCount}/{tasks.Count} - {task.TaskId}";
                    config.UpdateStatusBar?.Invoke(statusMessage, config.OperationIcon, config.OperationColor, $"正在处理：{task.TaskId}");
                    
                    config.AppendLog?.Invoke($"\r\n📂 [{processedCount}/{tasks.Count}] {task.Description}\r\n");
                    if (!string.IsNullOrEmpty(task.OutputPath))
                    {
                        config.AppendLog?.Invoke($"📁 输出: {Path.GetFileName(task.OutputPath)}\r\n");
                    }
                });

                try
                {
                    // 执行任务
                    var taskResult = await task.ExecuteTask(task.InputPath, task.OutputPath, cancellationToken);

                    var taskProcessingTime = DateTime.Now - taskStartTime;

                    // 更新结果
                    if (taskResult.Success)
                    {
                        result.SuccessCount++;
                        _dispatcher.Invoke(() =>
                        {
                            // 尝试获取输出文件大小（如果输出路径有效）
                            string successMessage = $"✅ 成功 | 时间: {taskProcessingTime.TotalSeconds:F1}秒";
                            if (!string.IsNullOrEmpty(task.OutputPath) && File.Exists(task.OutputPath))
                            {
                                try
                                {
                                    var fileSize = new FileInfo(task.OutputPath).Length;
                                    successMessage += $" | 大小: {FormatFileSize(fileSize)}";
                                }
                                catch
                                {
                                    // 忽略文件大小获取错误
                                }
                            }
                            config.AppendLog?.Invoke($"{successMessage}\r\n");
                        });
                    }
                    else
                    {
                        result.FailCount++;
                        _dispatcher.Invoke(() =>
                        {
                            config.AppendLog?.Invoke($"❌ 失败 | 错误: {taskResult.ErrorMessage}\r\n");
                        });
                    }

                    // 更新进度
                    var overallProgress = (double)processedCount / tasks.Count;
                    _dispatcher.Invoke(() =>
                    {
                        var elapsed = DateTime.Now - batchStartTime;
                        var progressText = $"{overallProgress * 100:F1}% | {elapsed.TotalSeconds:F1}s | {task.TaskId}";
                        config.UpdateProgress?.Invoke(overallProgress * 100, progressText);
                    });
                }
                catch (OperationCanceledException)
                {
                    result.WasCancelled = true;
                    _dispatcher.Invoke(() =>
                    {
                        config.AppendLog?.Invoke("⚠️ 任务处理被取消\r\n");
                        config.UpdateStatusBar?.Invoke("操作已取消", "⚠️", "#FF9800", "空闲");
                    });
                    break;
                }
                catch (Exception ex)
                {
                    result.FailCount++;
                    _dispatcher.Invoke(() =>
                    {
                        config.AppendLog?.Invoke($"❌ 任务处理异常: {ex.Message}\r\n");
                        Services.DebugLogger.LogError($"批量处理任务异常: {ex.Message}");
                    });
                }
            }

            result.TotalTime = DateTime.Now - batchStartTime;

            // 显示批量处理结果
            _dispatcher.Invoke(() =>
            {
                var summary = $"\r\n🎯 {config.OperationName}完成!\r\n" +
                             $"📊 总任务数: {result.TotalTasks}\r\n" +
                             $"✅ 成功: {result.SuccessCount}\r\n" +
                             $"❌ 失败: {result.FailCount}\r\n" +
                             $"⏱️ 总处理时间: {result.TotalTime.TotalSeconds:F1}秒\r\n";
                
                if (result.TotalTasks > 0)
                {
                    summary += $"📈 平均速度: {result.TotalTime.TotalSeconds / result.TotalTasks:F1}秒/任务\r\n";
                }
                
                summary += $"⏰ 结束时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                          $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\r\n";
                
                config.AppendLog?.Invoke(summary);

                var statusIcon = result.FailCount == 0 ? "✅" : "⚠️";
                var statusColor = result.FailCount == 0 ? "#4CAF50" : "#FF9800";
                var statusMessage = $"完成: 成功 {result.SuccessCount} / 失败 {result.FailCount}";
                config.UpdateStatusBar?.Invoke(statusMessage, statusIcon, statusColor, "空闲");

                config.UpdateProgress?.Invoke(100, "处理完成");
            });

            return result;
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}

