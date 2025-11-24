using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;
using VideoEditor.Presentation.ViewModels;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 批量AI字幕生成协调器
    /// </summary>
    public class BatchAiSubtitleCoordinator : INotifyPropertyChanged
    {
        private readonly VideoListViewModel? _videoListViewModel;
        private readonly ClipManager? _clipManager;
        private readonly HttpClient _httpClient;
        private string? _ffmpegPath;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isProcessing;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<BatchSubtitleProgressEventArgs>? ProgressUpdated;
        public event EventHandler<BatchSubtitleCompletedEventArgs>? BatchCompleted;

        /// <summary>
        /// 任务队列
        /// </summary>
        public ObservableCollection<BatchSubtitleTask> Tasks { get; } = new ObservableCollection<BatchSubtitleTask>();

        /// <summary>
        /// 是否正在处理
        /// </summary>
        public bool IsProcessing
        {
            get => _isProcessing;
            private set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged(nameof(IsProcessing));
                }
            }
        }

        /// <summary>
        /// 总进度（0-100）
        /// </summary>
        public double OverallProgress
        {
            get
            {
                if (Tasks.Count == 0) return 0;
                var completed = Tasks.Count(t => t.Status == BatchSubtitleTaskStatus.Completed);
                return (completed * 100.0) / Tasks.Count;
            }
        }

        /// <summary>
        /// 已完成数量
        /// </summary>
        public int CompletedCount => Tasks.Count(t => t.Status == BatchSubtitleTaskStatus.Completed);

        /// <summary>
        /// 失败数量
        /// </summary>
        public int FailedCount => Tasks.Count(t => t.Status == BatchSubtitleTaskStatus.Failed);

        /// <summary>
        /// 总数量
        /// </summary>
        public int TotalCount => Tasks.Count;

        public BatchAiSubtitleCoordinator(VideoListViewModel? videoListViewModel = null, ClipManager? clipManager = null, HttpClient? httpClient = null, string? ffmpegPath = null)
        {
            _videoListViewModel = videoListViewModel;
            _clipManager = clipManager;
            _httpClient = httpClient ?? new HttpClient();
            _ffmpegPath = ffmpegPath;
            Tasks.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(OverallProgress));
                OnPropertyChanged(nameof(CompletedCount));
                OnPropertyChanged(nameof(FailedCount));
                OnPropertyChanged(nameof(TotalCount));
            };
        }

        /// <summary>
        /// 设置FFmpeg路径（可在运行时更新）
        /// </summary>
        public void SetFFmpegPath(string? ffmpegPath)
        {
            _ffmpegPath = ffmpegPath;
        }

        /// <summary>
        /// 从播放列表添加选中的文件
        /// </summary>
        public void AddSelectedFilesFromPlaylist(AsrProvider provider)
        {
            if (_videoListViewModel == null) return;

            var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
            foreach (var file in selectedFiles)
            {
                // 验证文件信息是否完整
                if (string.IsNullOrWhiteSpace(file.FilePath))
                {
                    Services.DebugLogger.LogWarning($"跳过无效文件: 文件路径为空");
                    continue;
                }

                if (!File.Exists(file.FilePath))
                {
                    Services.DebugLogger.LogWarning($"跳过不存在的文件: {file.FilePath}");
                    continue;
                }

                if (Tasks.Any(t => t.SourceFilePath == file.FilePath && t.Provider == provider))
                    continue; // 已存在

                var task = new BatchSubtitleTask
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceFilePath = file.FilePath,
                    SourceFileName = !string.IsNullOrWhiteSpace(file.FileName) ? file.FileName : Path.GetFileName(file.FilePath),
                    Provider = provider,
                    TaskType = BatchSubtitleTaskType.PlaylistFile,
                    Status = BatchSubtitleTaskStatus.Pending
                };
                Tasks.Add(task);
            }
        }

        /// <summary>
        /// 从剪辑区域添加选中的片段
        /// </summary>
        public void AddSelectedClipsFromClipArea(AsrProvider provider)
        {
            if (_clipManager == null) return;

            var selectedClips = _clipManager.Clips.Where(c => c.IsSelected).ToList();
            foreach (var clip in selectedClips)
            {
                if (Tasks.Any(t => t.SourceFilePath == clip.SourceFilePath && 
                                   t.ClipStartTime == clip.StartTime && 
                                   t.ClipEndTime == clip.EndTime &&
                                   t.Provider == provider))
                    continue; // 已存在

                var task = new BatchSubtitleTask
                {
                    Id = Guid.NewGuid().ToString(),
                    SourceFilePath = clip.SourceFilePath,
                    SourceFileName = Path.GetFileName(clip.SourceFilePath),
                    ClipName = clip.Name,
                    ClipStartTime = clip.StartTime,
                    ClipEndTime = clip.EndTime,
                    Provider = provider,
                    TaskType = BatchSubtitleTaskType.Clip,
                    Status = BatchSubtitleTaskStatus.Pending
                };
                Tasks.Add(task);
            }
        }

        /// <summary>
        /// 开始批量处理
        /// </summary>
        public async Task StartBatchProcessingAsync()
        {
            if (IsProcessing)
            {
                throw new InvalidOperationException("批量处理已在进行中");
            }

            if (Tasks.Count == 0)
            {
                throw new InvalidOperationException("没有待处理的任务");
            }

            IsProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            try
            {
                var pendingTasks = Tasks.Where(t => t.Status == BatchSubtitleTaskStatus.Pending).ToList();
                int totalTasks = pendingTasks.Count;
                int completedTasks = 0;

                foreach (var task in pendingTasks)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        task.Status = BatchSubtitleTaskStatus.Processing;
                        task.Progress = 0;
                        OnPropertyChanged(nameof(OverallProgress));
                        ProgressUpdated?.Invoke(this, new BatchSubtitleProgressEventArgs(task, completedTasks, totalTasks));

                        // 执行字幕生成
                        await ProcessTaskAsync(task, cancellationToken);

                        task.Status = BatchSubtitleTaskStatus.Completed;
                        task.Progress = 100;
                        completedTasks++;
                    }
                    catch (OperationCanceledException)
                    {
                        task.Status = BatchSubtitleTaskStatus.Cancelled;
                        break;
                    }
                    catch (Exception ex)
                    {
                        task.Status = BatchSubtitleTaskStatus.Failed;
                        task.ErrorMessage = ex.Message;
                        Services.DebugLogger.LogError($"批量字幕任务失败: {task.SourceFileName} - {ex.Message}");
                    }
                    finally
                    {
                        OnPropertyChanged(nameof(OverallProgress));
                        OnPropertyChanged(nameof(CompletedCount));
                        OnPropertyChanged(nameof(FailedCount));
                        ProgressUpdated?.Invoke(this, new BatchSubtitleProgressEventArgs(task, completedTasks, totalTasks));
                    }
                }

                BatchCompleted?.Invoke(this, new BatchSubtitleCompletedEventArgs(completedTasks, FailedCount, totalTasks));
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// 处理单个任务
        /// </summary>
        private async Task ProcessTaskAsync(BatchSubtitleTask task, CancellationToken cancellationToken)
        {
            // 确定输出路径（与原媒体文件同名同路径）
            string outputSrtPath = GetOutputSrtPath(task);

            // 提取音频
            string audioFilePath;
            if (task.TaskType == BatchSubtitleTaskType.Clip && task.ClipStartTime.HasValue && task.ClipEndTime.HasValue)
            {
                // 从片段提取音频
                audioFilePath = await ExtractAudioFromClipAsync(
                    task.SourceFilePath,
                    task.ClipStartTime.Value,
                    task.ClipEndTime.Value,
                    cancellationToken);
            }
            else
            {
                // 从完整文件提取音频
                audioFilePath = await ExtractAudioFromFileAsync(task.SourceFilePath, cancellationToken);
            }

            try
            {
                // 生成字幕
                task.Progress = 20;
                ProgressUpdated?.Invoke(this, new BatchSubtitleProgressEventArgs(task, 0, 1));

                string srtContent;
                var progress = new Progress<(int progress, string message)>(p =>
                {
                    task.Progress = 20 + (p.progress * 0.7); // 20-90%
                    ProgressUpdated?.Invoke(this, new BatchSubtitleProgressEventArgs(task, 0, 1));
                });

                switch (task.Provider)
                {
                    case AsrProvider.Bcut:
                        var bcutService = new BcutAsrService(_httpClient);
                        srtContent = await bcutService.TranscribeAsync(audioFilePath, false, progress, cancellationToken);
                        break;

                    case AsrProvider.JianYing:
                        var jianYingService = new JianYingAsrService(_httpClient);
                        srtContent = await jianYingService.TranscribeAsync(audioFilePath, false, progress, cancellationToken);
                        break;

                    case AsrProvider.FasterWhisperCpu:
                    case AsrProvider.FasterWhisperGpu:
                        var fwService = CreateFasterWhisperService(
                            device: task.Provider == AsrProvider.FasterWhisperGpu ? "cuda" : "cpu");
                        srtContent = await fwService.TranscribeAsync(audioFilePath, progress, cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"不支持的ASR提供商: {task.Provider}");
                }

                if (string.IsNullOrWhiteSpace(srtContent))
                {
                    throw new InvalidOperationException("字幕生成失败：返回内容为空");
                }

                // 保存字幕文件
                task.Progress = 90;
                ProgressUpdated?.Invoke(this, new BatchSubtitleProgressEventArgs(task, 0, 1));

                var outputDirectory = Path.GetDirectoryName(outputSrtPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                await File.WriteAllTextAsync(outputSrtPath, srtContent, cancellationToken);
                task.OutputSrtPath = outputSrtPath;
                task.Progress = 100;
            }
            finally
            {
                // 清理临时音频文件
                if (File.Exists(audioFilePath))
                {
                    try
                    {
                        File.Delete(audioFilePath);
                    }
                    catch
                    {
                        // 忽略删除失败
                    }
                }
            }
        }

        /// <summary>
        /// 获取输出SRT文件路径（与原媒体文件同名同路径）
        /// </summary>
        private string GetOutputSrtPath(BatchSubtitleTask task)
        {
            string basePath = task.SourceFilePath;
            string directory = Path.GetDirectoryName(basePath) ?? string.Empty;
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(basePath);

            if (task.TaskType == BatchSubtitleTaskType.Clip && !string.IsNullOrEmpty(task.ClipName))
            {
                // 片段字幕：在原文件名基础上追加片段名称
                // 例如：movie.mp4 -> movie_片段1.srt
                var safeClipName = string.Join("_", task.ClipName.Split(Path.GetInvalidFileNameChars()));
                fileNameWithoutExtension = $"{fileNameWithoutExtension}_{safeClipName}";
            }

            return Path.Combine(directory, $"{fileNameWithoutExtension}.srt");
        }

        /// <summary>
        /// 从文件提取音频
        /// </summary>
        private async Task<string> ExtractAudioFromFileAsync(string videoFilePath, CancellationToken cancellationToken)
        {
            var audioChunker = new AudioChunker();
            // 如果提供了FFmpeg路径，设置给AudioChunker
            if (!string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                audioChunker.SetFFmpegPath(_ffmpegPath);
            }
            return await audioChunker.ExtractAudioAsync(videoFilePath, cancellationToken);
        }

        /// <summary>
        /// 从片段提取音频
        /// </summary>
        private async Task<string> ExtractAudioFromClipAsync(string videoFilePath, long startTime, long endTime, CancellationToken cancellationToken)
        {
            var audioChunker = new AudioChunker();
            // 如果提供了FFmpeg路径，设置给AudioChunker
            if (!string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                audioChunker.SetFFmpegPath(_ffmpegPath);
            }
            return await audioChunker.ExtractAudioSegmentAsync(videoFilePath, startTime, endTime, cancellationToken);
        }

        /// <summary>
        /// 创建FasterWhisper服务
        /// </summary>
        private FasterWhisperService? CreateFasterWhisperService(string device)
        {
            var programPath = Properties.Settings.Default.FasterWhisperProgramPath;
            var modelsRootDir = Properties.Settings.Default.FasterWhisperModelsRootDir;
            var selectedModel = Properties.Settings.Default.FasterWhisperSelectedModel;

            if (string.IsNullOrWhiteSpace(programPath) || string.IsNullOrWhiteSpace(modelsRootDir) || string.IsNullOrWhiteSpace(selectedModel))
            {
                throw new InvalidOperationException("FasterWhisper未配置，请先在菜单中配置");
            }

            // 规范化模型名称（移除 "faster-whisper-" 前缀等）
            var normalizedModelArg = NormalizeFasterWhisperModelArgument(selectedModel);
            if (string.IsNullOrWhiteSpace(normalizedModelArg))
            {
                throw new InvalidOperationException($"无法规范化模型名称: {selectedModel}");
            }

            var modelDir = Path.Combine(modelsRootDir, selectedModel);
            return new FasterWhisperService(
                programPath, 
                normalizedModelArg,  // 使用规范化后的模型名称
                modelsRootDir, 
                modelDir, 
                language: "zh",
                device: device);
        }

        /// <summary>
        /// 规范化FasterWhisper模型参数名称
        /// 将文件夹名称（如 "faster-whisper-large-v3-turbo"）转换为模型参数名称（如 "large-v3-turbo"）
        /// </summary>
        private static string NormalizeFasterWhisperModelArgument(string folderName)
        {
            var normalized = folderName.Trim();
            if (normalized.StartsWith("faster-whisper-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("faster-whisper-".Length);
            }
            else if (normalized.StartsWith("faster_whisper_", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("faster_whisper_".Length);
            }
            else if (normalized.StartsWith("fw-", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(3);
            }

            return normalized.Trim('-').Trim();
        }

        /// <summary>
        /// 取消批量处理
        /// </summary>
        public void CancelBatchProcessing()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// 移除任务
        /// </summary>
        public void RemoveTask(BatchSubtitleTask task)
        {
            if (task.Status == BatchSubtitleTaskStatus.Processing)
            {
                throw new InvalidOperationException("无法移除正在处理的任务");
            }
            Tasks.Remove(task);
        }

        /// <summary>
        /// 清空所有任务
        /// </summary>
        public void ClearAllTasks()
        {
            if (IsProcessing)
            {
                throw new InvalidOperationException("处理进行中，无法清空任务");
            }
            Tasks.Clear();
        }

        /// <summary>
        /// 移除已完成的任务
        /// </summary>
        public void RemoveCompletedTasks()
        {
            var completedTasks = Tasks.Where(t => t.Status == BatchSubtitleTaskStatus.Completed).ToList();
            foreach (var task in completedTasks)
            {
                Tasks.Remove(task);
            }
        }

        /// <summary>
        /// 移除失败的任务
        /// </summary>
        public void RemoveFailedTasks()
        {
            var failedTasks = Tasks.Where(t => t.Status == BatchSubtitleTaskStatus.Failed).ToList();
            foreach (var task in failedTasks)
            {
                Tasks.Remove(task);
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ASR提供商枚举
    /// </summary>
    public enum AsrProvider
    {
        Bcut,
        JianYing,
        FasterWhisperCpu,
        FasterWhisperGpu
    }

    /// <summary>
    /// 批量字幕任务类型
    /// </summary>
    public enum BatchSubtitleTaskType
    {
        PlaylistFile,  // 播放列表文件
        Clip           // 剪辑片段
    }

    /// <summary>
    /// 批量字幕任务状态
    /// </summary>
    public enum BatchSubtitleTaskStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// 批量字幕任务
    /// </summary>
    public class BatchSubtitleTask : INotifyPropertyChanged
    {
        private BatchSubtitleTaskStatus _status;
        private double _progress;
        private string _errorMessage = string.Empty;

        public string Id { get; set; } = string.Empty;
        public string SourceFilePath { get; set; } = string.Empty;
        public string SourceFileName { get; set; } = string.Empty;
        public string? ClipName { get; set; }
        public long? ClipStartTime { get; set; }
        public long? ClipEndTime { get; set; }
        public AsrProvider Provider { get; set; }
        public BatchSubtitleTaskType TaskType { get; set; }
        public string? OutputSrtPath { get; set; }

        public BatchSubtitleTaskStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) > 0.01)
                {
                    _progress = value;
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged(nameof(ErrorMessage));
                }
            }
        }

        public string StatusText => Status switch
        {
            BatchSubtitleTaskStatus.Pending => "等待中",
            BatchSubtitleTaskStatus.Processing => "处理中",
            BatchSubtitleTaskStatus.Completed => "已完成",
            BatchSubtitleTaskStatus.Failed => "失败",
            BatchSubtitleTaskStatus.Cancelled => "已取消",
            _ => "未知"
        };

        public string ProviderText => Provider switch
        {
            AsrProvider.Bcut => "B接口",
            AsrProvider.JianYing => "J接口",
            AsrProvider.FasterWhisperCpu => "FasterWhisper (CPU)",
            AsrProvider.FasterWhisperGpu => "FasterWhisper (GPU)",
            _ => "未知"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 批量字幕进度事件参数
    /// </summary>
    public class BatchSubtitleProgressEventArgs : EventArgs
    {
        public BatchSubtitleTask Task { get; }
        public int CompletedCount { get; }
        public int TotalCount { get; }

        public BatchSubtitleProgressEventArgs(BatchSubtitleTask task, int completedCount, int totalCount)
        {
            Task = task;
            CompletedCount = completedCount;
            TotalCount = totalCount;
        }
    }

    /// <summary>
    /// 批量字幕完成事件参数
    /// </summary>
    public class BatchSubtitleCompletedEventArgs : EventArgs
    {
        public int CompletedCount { get; }
        public int FailedCount { get; }
        public int TotalCount { get; }

        public BatchSubtitleCompletedEventArgs(int completedCount, int failedCount, int totalCount)
        {
            CompletedCount = completedCount;
            FailedCount = failedCount;
            TotalCount = totalCount;
        }
    }

}

