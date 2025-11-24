using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using VideoEditor.Presentation.Models;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.ViewModels
{
    /// <summary>
    /// 视频列表ViewModel
    /// </summary>
    public class VideoListViewModel : INotifyPropertyChanged
    {
        private readonly VideoInformationService _videoInformationService;
        private VideoFile? _selectedFile;
        private string _statusMessage = string.Empty;
        private static readonly string[] VideoExtensionList =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".ts", ".mts", ".m2ts", ".m4v", ".wmv", ".rmvb", ".rm",
            ".swf", ".flv", ".f4v", ".webm", ".ogm", ".ogv", ".ogx", ".3g2", ".3gp", ".3gp2", ".3gpp",
            ".asf", ".asx", ".flic", ".ivf", ".m1v", ".m2v", ".mpeg", ".mpg", ".mpe", ".mpv", ".qt",
            ".wm", ".wms", ".wmz", ".wpl", ".mp4v", ".dv", ".vob", ".hevc", ".h265", ".h264", ".y4m",
            ".mxf", ".ismv", ".tod", ".trp"
        };

        private static readonly string[] AudioExtensionList =
        {
            ".mp3", ".wav", ".wax", ".wma", ".wvx", ".wmx", ".midi", ".mid", ".m4a", ".aac", ".adt",
            ".adts", ".aif", ".aifc", ".aiff", ".au", ".cda", ".flac", ".m3u", ".mp2", ".mpa", ".rmi",
            ".snd", ".ogg", ".oga", ".opus", ".ac3", ".dts", ".ra", ".ape", ".wv", ".caf", ".amr",
            ".awb", ".spx"
        };

        private static readonly string[] ImageExtensionList =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico", ".svg",
            ".heic", ".heif", ".psd", ".tga", ".dds", ".raw", ".arw", ".cr2", ".nef", ".orf", ".rw2",
            ".dng"
        };

        public static readonly HashSet<string> SupportedVideoExtensions =
            new(VideoExtensionList, StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> SupportedAudioExtensions =
            new(AudioExtensionList, StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> SupportedImageExtensions =
            new(ImageExtensionList, StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> SupportedMediaExtensions =
            new(VideoExtensionList, StringComparer.OrdinalIgnoreCase);

        public static readonly string MediaFileDialogFilter;

        static VideoListViewModel()
        {
            foreach (var audioExt in SupportedAudioExtensions)
            {
                SupportedMediaExtensions.Add(audioExt);
            }
            foreach (var imageExt in SupportedImageExtensions)
            {
                SupportedMediaExtensions.Add(imageExt);
            }

            MediaFileDialogFilter = BuildFileDialogFilter();
        }

        private static string BuildFileDialogFilter()
        {
            static string BuildPattern(IEnumerable<string> extensions) =>
                string.Join(";", extensions.Select(ext => $"*{ext}"));

            var videoPattern = BuildPattern(VideoExtensionList);
            var audioPattern = BuildPattern(AudioExtensionList);
            var imagePattern = BuildPattern(ImageExtensionList);
            var mediaPattern = string.Join(";", SupportedMediaExtensions.Select(ext => $"*{ext}"));

            return $"媒体文件|{mediaPattern}|视频文件|{videoPattern}|音频文件|{audioPattern}|图像文件|{imagePattern}|所有文件|*.*";
        }

        private readonly PlayQueueManager _playQueueManager;
        private readonly PlayHistoryManager _playHistoryManager;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 视频文件列表
        /// </summary>
        public ObservableCollection<VideoFile> Files { get; } = new ObservableCollection<VideoFile>();

        /// <summary>
        /// 选中的文件
        /// </summary>
        public VideoFile? SelectedFile
        {
            get => _selectedFile;
            set
            {
                _selectedFile = value;
                OnPropertyChanged(nameof(SelectedFile));
            }
        }

        /// <summary>
        /// 状态消息
        /// </summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }

        /// <summary>
        /// 文件数量
        /// </summary>
        public int FileCount => Files.Count;

        /// <summary>
        /// 选中文件数量
        /// </summary>
        public int SelectedFileCount => Files.Count(f => f.IsSelected);

        /// <summary>
        /// 播放队列管理器
        /// </summary>
        public PlayQueueManager PlayQueueManager => _playQueueManager;

        /// <summary>
        /// 播放历史管理器
        /// </summary>
        public PlayHistoryManager PlayHistoryManager => _playHistoryManager;

        /// <summary>
        /// 当前播放模式
        /// </summary>
        public PlayMode CurrentPlayMode
        {
            get => _playQueueManager.CurrentMode;
            set => _playQueueManager.CurrentMode = value;
        }

        /// <summary>
        /// 播放队列状态
        /// </summary>
        public PlayQueueState PlayQueueState => _playQueueManager.State;

        /// <summary>
        /// 当前播放的视频
        /// </summary>
        public VideoFile? CurrentPlayingVideo => _playQueueManager.CurrentVideo;

        public VideoListViewModel(VideoInformationService videoInformationService)
        {
            _videoInformationService = videoInformationService ?? new VideoInformationService();
            _playQueueManager = new PlayQueueManager();
            _playHistoryManager = new PlayHistoryManager();
            StatusMessage = "就绪";
            
            // 订阅播放队列事件
            _playQueueManager.CurrentVideoChanged += OnCurrentVideoChanged;
            _playQueueManager.StateChanged += OnPlayQueueStateChanged;
            
            // 订阅文件集合变化事件
            Files.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(FileCount));
                OnPropertyChanged(nameof(SelectedFileCount));

                // 同步更新PlayQueueManager的播放队列
                SyncPlayQueueWithFiles();
                
                // 为新添加的文件订阅选择状态改变事件
                if (e.NewItems != null)
                {
                    foreach (VideoFile file in e.NewItems)
                    {
                        file.SelectionChanged += OnFileSelectionChanged;
                    }
                }
                
                // 为移除的文件取消订阅
                if (e.OldItems != null)
                {
                    foreach (VideoFile file in e.OldItems)
                    {
                        file.SelectionChanged -= OnFileSelectionChanged;
                    }
                }
            };
        }

        /// <summary>
        /// 同步播放队列与文件列表
        /// </summary>
        private void SyncPlayQueueWithFiles()
        {
            // 清除现有的播放队列
            _playQueueManager.PlayQueue.Clear();

            // 添加所有文件到播放队列
            foreach (var file in Files)
            {
                _playQueueManager.PlayQueue.Add(file);
            }

            // 如果有当前播放的文件，设置为播放队列的当前视频
            var currentPlayingFile = Files.FirstOrDefault(f => f.IsPlaying);
            if (currentPlayingFile != null)
            {
                _playQueueManager.SetCurrentVideo(currentPlayingFile);
            }
            else if (Files.Count > 0)
            {
                // 如果没有正在播放的文件，但有文件，则设置第一个为当前视频
                _playQueueManager.SetCurrentVideo(Files[0]);
            }
        }

        /// <summary>
        /// 文件选择状态改变事件处理
        /// </summary>
        private void OnFileSelectionChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(SelectedFileCount));
        }

        /// <summary>
        /// 添加文件
        /// </summary>
        /// <param name="filePaths">文件路径数组</param>
        public async Task AddFilesAsync(string[] filePaths)
        {
            try
            {
                StatusMessage = "正在添加文件...";
                
                var validFiles = filePaths.Where(path => 
                    File.Exists(path) && 
                    SupportedMediaExtensions.Contains(Path.GetExtension(path))).ToArray();

                if (validFiles.Length == 0)
                {
                    StatusMessage = "没有找到有效的视频文件";
                    return;
                }

                var addedFiles = new List<VideoFile>();
                foreach (var filePath in validFiles)
                {
                    // 检查是否已存在
                    if (Files.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    StatusMessage = $"正在获取信息: {Path.GetFileName(filePath)}";
                    
                    try
                    {
                    var videoFile = await _videoInformationService.GetVideoInformationAsync(filePath);
                        
                        // 确保文件路径和文件名已设置
                        if (string.IsNullOrWhiteSpace(videoFile.FilePath))
                        {
                            videoFile.FilePath = filePath;
                        }
                        if (string.IsNullOrWhiteSpace(videoFile.FileName))
                        {
                            videoFile.FileName = Path.GetFileName(filePath);
                        }
                        
                    Files.Add(videoFile);
                        addedFiles.Add(videoFile);
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogError($"获取文件信息失败: {filePath} - {ex.Message}");
                        // 即使获取信息失败，也创建一个基本文件对象
                        var basicFile = new Models.VideoFile(filePath);
                        Files.Add(basicFile);
                        addedFiles.Add(basicFile);
                    }
                }

                // 如果添加了文件且当前没有选中文件，自动选择第一个新添加的文件
                if (addedFiles.Count > 0 && SelectedFile == null)
                {
                    SelectedFile = addedFiles[0];
                }
                // 如果之前有选中文件，保持选中状态不变

                StatusMessage = $"已添加 {addedFiles.Count} 个文件";
                OnPropertyChanged(nameof(FileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加文件时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 添加文件夹
        /// </summary>
        /// <param name="folderPath">文件夹路径</param>
        public async Task AddFolderAsync(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    StatusMessage = "文件夹不存在";
                    return;
                }

                StatusMessage = "正在扫描文件夹...";
                
                var videoFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
                    .Where(file => SupportedMediaExtensions.Contains(Path.GetExtension(file)))
                    .ToArray();

                if (videoFiles.Length == 0)
                {
                    StatusMessage = "文件夹中没有找到视频文件";
                    return;
                }

                await AddFilesAsync(videoFiles);
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加文件夹时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 移除选中的文件
        /// </summary>
        public List<VideoFile> GetSelectedFilesForRemoval()
        {
            try
            {
                var selectedFiles = Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    StatusMessage = "请先选择要删除的文件";
                    return new List<VideoFile>();
                }

                return selectedFiles;
            }
            catch (Exception ex)
            {
                StatusMessage = $"获取要删除的文件时发生错误: {ex.Message}";
                return new List<VideoFile>();
            }
        }

        public void RemoveFilesFromList(List<VideoFile> filesToRemove)
        {
            try
            {
                foreach (var file in filesToRemove)
                {
                    Files.Remove(file);
                }

                StatusMessage = $"已删除 {filesToRemove.Count} 个文件";
                OnPropertyChanged(nameof(FileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"从列表中移除文件时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 清空所有文件
        /// </summary>
        public void ClearAllFiles()
        {
            try
            {
                Files.Clear();
                SelectedFile = null;
                StatusMessage = "已清空所有文件";
                OnPropertyChanged(nameof(FileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"清空文件时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 全选
        /// </summary>
        public void SelectAll()
        {
            try
            {
                foreach (var file in Files)
                {
                    file.IsSelected = true;
                }
                StatusMessage = $"已选择所有 {Files.Count} 个文件";
                OnPropertyChanged(nameof(SelectedFileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"全选时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 取消全选
        /// </summary>
        public void DeselectAll()
        {
            try
            {
                foreach (var file in Files)
                {
                    file.IsSelected = false;
                }
                StatusMessage = "已取消选择所有文件";
                OnPropertyChanged(nameof(SelectedFileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"取消全选时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 反选 (Ctrl+Shift+A)
        /// </summary>
        public void InvertSelection()
        {
            try
            {
                foreach (var file in Files)
                {
                    file.IsSelected = !file.IsSelected;
                }
                StatusMessage = $"已反选,当前选中 {SelectedFileCount} 个文件";
                OnPropertyChanged(nameof(SelectedFileCount));
            }
            catch (Exception ex)
            {
                StatusMessage = $"反选时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 定位到正在播放的文件 (Ctrl+Shift+L)
        /// </summary>
        public void LocateCurrentPlaying()
        {
            try
            {
                // 查找第一个正在播放的文件(IsPlaying为true)
                var playingFile = Files.FirstOrDefault(f => f.IsPlaying);
                if (playingFile != null)
                {
                    SelectedFile = playingFile;
                    StatusMessage = $"已定位到正在播放: {playingFile.FileName}";
                    Services.DebugLogger.LogInfo($"定位到正在播放: {playingFile.FileName}");
                }
                else
                {
                    StatusMessage = "没有正在播放的文件";
                    Services.DebugLogger.LogWarning("LocateCurrentPlaying: 没有正在播放的文件");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"定位时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"LocateCurrentPlaying: {ex.Message}");
            }
        }

        #region 列表排序

        /// <summary>
        /// 按文件名排序 (A→Z)
        /// </summary>
        public void SortByNameAscending()
        {
            try
            {
                var sortedList = Files.OrderBy(f => f.FileName).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按文件名排序 (A→Z)";
                Services.DebugLogger.LogInfo("列表已按文件名升序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortByNameAscending: {ex.Message}");
            }
        }

        /// <summary>
        /// 按文件名排序 (Z→A)
        /// </summary>
        public void SortByNameDescending()
        {
            try
            {
                var sortedList = Files.OrderByDescending(f => f.FileName).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按文件名排序 (Z→A)";
                Services.DebugLogger.LogInfo("列表已按文件名降序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortByNameDescending: {ex.Message}");
            }
        }

        /// <summary>
        /// 按文件大小排序 (大→小)
        /// </summary>
        public void SortBySizeDescending()
        {
            try
            {
                var sortedList = Files.OrderByDescending(f => f.FileSize).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按文件大小排序 (大→小)";
                Services.DebugLogger.LogInfo("列表已按文件大小降序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortBySizeDescending: {ex.Message}");
            }
        }

        /// <summary>
        /// 按文件大小排序 (小→大)
        /// </summary>
        public void SortBySizeAscending()
        {
            try
            {
                var sortedList = Files.OrderBy(f => f.FileSize).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按文件大小排序 (小→大)";
                Services.DebugLogger.LogInfo("列表已按文件大小升序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortBySizeAscending: {ex.Message}");
            }
        }

        /// <summary>
        /// 按时长排序 (长→短)
        /// </summary>
        public void SortByDurationDescending()
        {
            try
            {
                var sortedList = Files.OrderByDescending(f => f.Duration).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按时长排序 (长→短)";
                Services.DebugLogger.LogInfo("列表已按时长降序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortByDurationDescending: {ex.Message}");
            }
        }

        /// <summary>
        /// 按时长排序 (短→长)
        /// </summary>
        public void SortByDurationAscending()
        {
            try
            {
                var sortedList = Files.OrderBy(f => f.Duration).ToList();
                Files.Clear();
                foreach (var file in sortedList)
                {
                    Files.Add(file);
                }
                StatusMessage = "已按时长排序 (短→长)";
                Services.DebugLogger.LogInfo("列表已按时长升序排序");
            }
            catch (Exception ex)
            {
                StatusMessage = $"排序时发生错误: {ex.Message}";
                Services.DebugLogger.LogError($"SortByDurationAscending: {ex.Message}");
            }
        }

        #endregion

        #region 播放队列管理方法

        /// <summary>
        /// 添加选中文件到播放队列
        /// </summary>
        public void AddSelectedFilesToQueue()
        {
            try
            {
                var selectedFiles = Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    StatusMessage = "请先选择要添加到播放队列的文件";
                    return;
                }

                _playQueueManager.AddVideos(selectedFiles);
                StatusMessage = $"已将 {selectedFiles.Count} 个文件添加到播放队列";
                OnPropertyChanged(nameof(CurrentPlayingVideo));
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加到播放队列时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 添加所有文件到播放队列
        /// </summary>
        public void AddAllFilesToQueue()
        {
            try
            {
                if (Files.Count == 0)
                {
                    StatusMessage = "列表中没有文件";
                    return;
                }

                _playQueueManager.AddVideos(Files);
                StatusMessage = $"已将 {Files.Count} 个文件添加到播放队列";
                OnPropertyChanged(nameof(CurrentPlayingVideo));
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加到播放队列时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 清空播放队列
        /// </summary>
        public void ClearPlayQueue()
        {
            try
            {
                _playQueueManager.Clear();
                StatusMessage = "已清空播放队列";
                OnPropertyChanged(nameof(CurrentPlayingVideo));
            }
            catch (Exception ex)
            {
                StatusMessage = $"清空播放队列时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 播放当前视频
        /// </summary>
        public void PlayCurrentVideo()
        {
            try
            {
                if (_playQueueManager.CurrentVideo == null)
                {
                    StatusMessage = "播放队列为空";
                    return;
                }

                _playQueueManager.StartPlayback();
                StatusMessage = $"正在播放: {_playQueueManager.CurrentVideo.FileName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"播放视频时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 播放下一个视频
        /// </summary>
        public void PlayNextVideo()
        {
            try
            {
                _playQueueManager.PlayNext();
                if (_playQueueManager.CurrentVideo != null)
                {
                    StatusMessage = $"播放下一个: {_playQueueManager.CurrentVideo.FileName}";
                }
                else
                {
                    StatusMessage = "没有下一个视频";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"播放下一个视频时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 播放上一个视频
        /// </summary>
        public void PlayPreviousVideo()
        {
            try
            {
                _playQueueManager.PlayPrevious();
                if (_playQueueManager.CurrentVideo != null)
                {
                    StatusMessage = $"播放上一个: {_playQueueManager.CurrentVideo.FileName}";
                }
                else
                {
                    StatusMessage = "没有上一个视频";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"播放上一个视频时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 暂停播放
        /// </summary>
        public void PausePlayback()
        {
            try
            {
                _playQueueManager.PausePlayback();
                StatusMessage = "已暂停播放";
            }
            catch (Exception ex)
            {
                StatusMessage = $"暂停播放时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 停止播放
        /// </summary>
        public void StopPlayback()
        {
            try
            {
                _playQueueManager.StopPlayback();
                StatusMessage = "已停止播放";
            }
            catch (Exception ex)
            {
                StatusMessage = $"停止播放时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 设置播放模式
        /// </summary>
        public void SetPlayMode(PlayMode mode)
        {
            try
            {
                _playQueueManager.CurrentMode = mode;
                StatusMessage = $"播放模式已设置为: {GetPlayModeDescription(mode)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"设置播放模式时发生错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 获取播放模式描述
        /// </summary>
        private string GetPlayModeDescription(PlayMode mode)
        {
            return mode switch
            {
                PlayMode.Sequential => "顺序播放",
                PlayMode.Random => "随机播放",
                PlayMode.RepeatOne => "单曲循环",
                PlayMode.RepeatAll => "列表循环",
                PlayMode.Shuffle => "随机循环",
                _ => "未知模式"
            };
        }

        #endregion

        #region 事件处理方法

        /// <summary>
        /// 当前视频改变事件处理
        /// </summary>
        private void OnCurrentVideoChanged(object? sender, VideoFile video)
        {
            OnPropertyChanged(nameof(CurrentPlayingVideo));
        }

        /// <summary>
        /// 播放队列状态改变事件处理
        /// </summary>
        private void OnPlayQueueStateChanged(object? sender, PlayQueueState state)
        {
            OnPropertyChanged(nameof(PlayQueueState));
        }

        #endregion

        #region 播放状态管理

        /// <summary>
        /// 设置当前正在播放的文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        public void SetCurrentPlaying(string filePath)
        {
            // 清除所有文件的播放状态
            foreach (var file in Files)
            {
                file.IsPlaying = false;
            }
            
            // 设置当前播放文件的状态
            var playingFile = Files.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (playingFile != null)
            {
                playingFile.IsPlaying = true;
                Services.DebugLogger.LogInfo($"列表高亮: {playingFile.FileName}");
            }
        }

        /// <summary>
        /// 清除所有播放状态
        /// </summary>
        public void ClearAllPlayingStates()
        {
            foreach (var file in Files)
            {
                file.IsPlaying = false;
            }
        }

        #endregion

        #region 列表排序

        /// <summary>
        /// 上移选中的文件
        /// </summary>
        public void MoveUp(VideoFile file)
        {
            try
            {
                if (file == null) return;
                
                int index = Files.IndexOf(file);
                if (index <= 0) // 已经在最上面
                {
                    StatusMessage = "已经在列表顶部";
                    return;
                }
                
                Files.Move(index, index - 1);
                StatusMessage = $"已上移: {file.FileName}";
                DebugLogger.LogInfo($"上移文件: {file.FileName} (索引 {index} → {index - 1})");
            }
            catch (Exception ex)
            {
                StatusMessage = $"上移文件时发生错误: {ex.Message}";
                DebugLogger.LogError($"上移文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 下移选中的文件
        /// </summary>
        public void MoveDown(VideoFile file)
        {
            try
            {
                if (file == null) return;
                
                int index = Files.IndexOf(file);
                if (index < 0 || index >= Files.Count - 1) // 已经在最下面
                {
                    StatusMessage = "已经在列表底部";
                    return;
                }
                
                Files.Move(index, index + 1);
                StatusMessage = $"已下移: {file.FileName}";
                DebugLogger.LogInfo($"下移文件: {file.FileName} (索引 {index} → {index + 1})");
            }
            catch (Exception ex)
            {
                StatusMessage = $"下移文件时发生错误: {ex.Message}";
                DebugLogger.LogError($"下移文件失败: {ex.Message}");
            }
        }

        #region 格式验证功能

        /// <summary>
        /// 验证所有文件的格式支持情况
        /// </summary>
        public async Task<List<Models.FormatValidationResult>> ValidateAllFilesFormat()
        {
            if (Files.Count == 0)
            {
                StatusMessage = "列表中没有文件需要验证";
                return new List<Models.FormatValidationResult>();
            }

            try
            {
                StatusMessage = $"正在验证 {Files.Count} 个文件的格式支持...";

                // 获取所有文件路径
                var filePaths = Files.Select(f => f.FilePath).ToList();

                // 创建视频播放器实例进行验证（如果还没有的话）
                var videoPlayer = new VideoPlayerViewModel();
                videoPlayer.SetPlaylist(Files, this);

                // 批量验证格式
                var results = await videoPlayer.ValidateVideoFormatsBatch(filePaths);

                // 更新文件对象的验证状态
                foreach (var result in results)
                {
                    var file = Files.FirstOrDefault(f => f.FilePath.Equals(result.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (file != null)
                    {
                        file.IsFormatValidated = result.IsSupported;
                        file.FormatValidationMessage = result.ErrorMessage;
                        file.VideoCodec = result.VideoCodec;
                        file.AudioCodec = result.AudioCodec;
                        file.HasVideoTrack = result.HasVideo;
                        file.HasAudioTrack = result.HasAudio;
                    }
                }

                var supportedCount = results.Count(r => r.IsSupported);
                StatusMessage = $"格式验证完成: {supportedCount}/{Files.Count} 个文件支持播放";

                // 触发属性更新
                OnPropertyChanged(nameof(FileCount));
                OnPropertyChanged(nameof(SelectedFileCount));

                return results;
            }
            catch (Exception ex)
            {
                StatusMessage = $"格式验证失败: {ex.Message}";
                DebugLogger.LogError($"ValidateAllFilesFormat: {ex.Message}");
                return new List<Models.FormatValidationResult>();
            }
        }

        /// <summary>
        /// 验证选中文件的格式支持
        /// </summary>
        public async Task<List<Models.FormatValidationResult>> ValidateSelectedFilesFormat()
        {
            var selectedFiles = Files.Where(f => f.IsSelected).ToList();
            if (selectedFiles.Count == 0)
            {
                StatusMessage = "请先选择要验证的文件";
                return new List<Models.FormatValidationResult>();
            }

            try
            {
                StatusMessage = $"正在验证 {selectedFiles.Count} 个选中文件的格式...";

                var filePaths = selectedFiles.Select(f => f.FilePath).ToList();
                var videoPlayer = new VideoPlayerViewModel();
                videoPlayer.SetPlaylist(Files, this);

                var results = await videoPlayer.ValidateVideoFormatsBatch(filePaths);

                // 更新验证状态
                foreach (var result in results)
                {
                    var file = Files.FirstOrDefault(f => f.FilePath.Equals(result.FilePath, StringComparison.OrdinalIgnoreCase));
                    if (file != null)
                    {
                        file.IsFormatValidated = result.IsSupported;
                        file.FormatValidationMessage = result.ErrorMessage;
                        file.VideoCodec = result.VideoCodec;
                        file.AudioCodec = result.AudioCodec;
                        file.HasVideoTrack = result.HasVideo;
                        file.HasAudioTrack = result.HasAudio;
                    }
                }

                var supportedCount = results.Count(r => r.IsSupported);
                StatusMessage = $"选中文件验证完成: {supportedCount}/{selectedFiles.Count} 个文件支持播放";

                return results;
            }
            catch (Exception ex)
            {
                StatusMessage = $"验证选中文件失败: {ex.Message}";
                DebugLogger.LogError($"ValidateSelectedFilesFormat: {ex.Message}");
                return new List<Models.FormatValidationResult>();
            }
        }

        /// <summary>
        /// 获取格式验证统计信息
        /// </summary>
        public (int validated, int supported, int unsupported) GetFormatValidationStats()
        {
            var validated = Files.Count(f => f.IsFormatValidated.HasValue);
            var supported = Files.Count(f => f.IsFormatValidated == true);
            var unsupported = Files.Count(f => f.IsFormatValidated == false);

            return (validated, supported, unsupported);
        }

        #endregion

        #endregion

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
