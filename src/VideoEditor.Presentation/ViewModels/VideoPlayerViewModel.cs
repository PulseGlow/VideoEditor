using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;
using VideoEditor.Presentation.Commands;
using VideoEditor.Presentation.Models;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.ViewModels
{
    /// <summary>
    /// 视频播放器ViewModel - 基于LibVLC
    /// </summary>
    public class VideoPlayerViewModel : INotifyPropertyChanged, IDisposable
    {
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private Media? _currentMedia;
        private bool _isPlaying;
        private bool _isPaused;
        private long _currentPosition;
        private long _duration;
        private float _volume = 50f;
        private bool _isMuted;
        private float _playbackRate = 1.0f; // 播放速度 (0.25x - 2.0x)
        private string _currentFilePath = string.Empty;
        private string _currentFileName = "未加载视频";
        private bool _hasVideo;
        private bool _hasVideoLoaded; // 视频是否真正加载完成并可播放
        private double _placeholderOpacity = 1.0; // 占位符透明度（初始为1.0，启动时立即显示黑色背景+Logo）
        private double _videoViewOffsetX = 0.0; // VideoView的X偏移量（用于在Logo动画期间移到屏幕外）
        private double _videoViewOffsetY = 0.0; // VideoView的Y偏移量（用于在Logo动画期间移到屏幕外）
        private bool _isVideoViewVisible = false; // VideoView是否可见（没有视频时隐藏，避免白色背景）
        private int _videoWidth;
        private int _videoHeight;
        
        // 播放列表管理
        private System.Collections.ObjectModel.ObservableCollection<Models.VideoFile>? _playlist;
        private int _currentVideoIndex = -1;
        private bool _isLoopEnabled = false; // 循环播放选项
        private bool _isSinglePlayMode = true; // 是否为单曲播放模式（默认true，与初始状态一致）
        private VideoListViewModel? _videoListViewModel; // 列表ViewModel引用(用于更新高亮状态)
        
        /// <summary>
        /// 是否启用循环播放
        /// </summary>
        public bool IsLoopEnabled
        {
            get => _isLoopEnabled;
            set
            {
                if (_isLoopEnabled != value)
                {
                    _isLoopEnabled = value;
                    OnPropertyChanged(nameof(IsLoopEnabled));
                    DebugLogger.LogInfo($"循环播放: {(_isLoopEnabled ? "已启用" : "已禁用")}");
                }
            }
        }

        /// <summary>
        /// 是否为单曲播放模式
        /// true: 播放结束后停止，不自动播放下一首
        /// false: 播放结束后自动播放下一首（根据PlayQueueManager.CurrentMode决定播放规则）
        /// </summary>
        public bool IsSinglePlayMode
        {
            get => _isSinglePlayMode;
            set
            {
                if (_isSinglePlayMode != value)
                {
                    _isSinglePlayMode = value;
                    OnPropertyChanged(nameof(IsSinglePlayMode));
                    DebugLogger.LogInfo($"播放模式: {(_isSinglePlayMode ? "单曲播放" : "连续播放")}");
                }
            }
        }
        
        // 统一定时器 - 同时处理进度更新和出点监控
        private System.Timers.Timer? _playbackTimer; // 播放定时器(50ms)
        private bool _isUpdatingFromUI = false; // 标记是否来自UI更新(防止回弹)
        private bool _isSeekingByUser = false; // 标记用户正在拖拽进度条
        private bool _isMonitoringOutPoint = false; // 是否正在监控出点

        // Logo动画相关
        
        // 入出点标记
        private long _inPoint = -1;
        private long _outPoint = -1;
        private bool _hasInPoint;
        private bool _hasOutPoint;
        
        public event PropertyChangedEventHandler? PropertyChanged;

        #region 属性

        /// <summary>
        /// MediaPlayer实例（用于绑定到VideoView）
        /// </summary>
        public MediaPlayer? MediaPlayer => _mediaPlayer;

        /// <summary>
        /// 是否已加载视频
        /// </summary>
        public bool HasVideo
        {
            get => _hasVideo;
            set
            {
                if (_hasVideo != value)
                {
                    _hasVideo = value;
                    OnPropertyChanged(nameof(HasVideo));
                }
            }
        }

        /// <summary>
        /// 视频是否真正加载完成并可播放（用于UI显示，避免闪烁）
        /// </summary>
        public bool HasVideoLoaded
        {
            get => _hasVideoLoaded;
            set
            {
                if (_hasVideoLoaded != value)
                {
                    _hasVideoLoaded = value;
                    OnPropertyChanged(nameof(HasVideoLoaded));
                    
                    // 当视频加载时，显示VideoView并淡出占位符；当视频卸载时，隐藏VideoView并显示占位符
                    if (_hasVideoLoaded)
                    {
                        VideoViewOffsetX = 0.0;
                        VideoViewOffsetY = 0.0;
                        IsVideoViewVisible = true; // 显示VideoView
                        PlaceholderOpacity = 0.0; // 淡出占位符
                        Debug.WriteLine("视频已加载，VideoView已显示，占位符已淡出");
                    }
                    else
                    {
                        VideoViewOffsetX = 0.0; // 复位到原位（虽然不可见）
                        VideoViewOffsetY = 0.0;
                        IsVideoViewVisible = false; // 隐藏VideoView，避免白色背景
                        PlaceholderOpacity = 1.0; // 显示占位符（黑色背景+Logo）
                        Debug.WriteLine("视频已卸载，VideoView已隐藏，占位符已显示");
                    }
                }
            }
        }

        /// <summary>
        /// 占位符透明度（用于logo淡入动画）
        /// </summary>
        public double PlaceholderOpacity
        {
            get => _placeholderOpacity;
            set
            {
                if (_placeholderOpacity != value)
                {
                    _placeholderOpacity = value;
                    OnPropertyChanged(nameof(PlaceholderOpacity));
                }
            }
        }

        /// <summary>
        /// VideoView的X偏移量（用于在Logo动画期间移到屏幕外，避免白色闪烁）
        /// </summary>
        public double VideoViewOffsetX
        {
            get => _videoViewOffsetX;
            set
            {
                if (_videoViewOffsetX != value)
                {
                    _videoViewOffsetX = value;
                    OnPropertyChanged(nameof(VideoViewOffsetX));
                }
            }
        }

        /// <summary>
        /// VideoView的Y偏移量（用于在Logo动画期间移到屏幕外，避免白色闪烁）
        /// </summary>
        public double VideoViewOffsetY
        {
            get => _videoViewOffsetY;
            set
            {
                if (_videoViewOffsetY != value)
                {
                    _videoViewOffsetY = value;
                    OnPropertyChanged(nameof(VideoViewOffsetY));
                }
            }
        }

        /// <summary>
        /// VideoView是否可见（没有视频时隐藏，避免白色背景）
        /// </summary>
        public bool IsVideoViewVisible
        {
            get => _isVideoViewVisible;
            set
            {
                if (_isVideoViewVisible != value)
                {
                    _isVideoViewVisible = value;
                    OnPropertyChanged(nameof(IsVideoViewVisible));
                }
            }
        }

        /// <summary>
        /// 当前文件名
        /// </summary>
        public string CurrentFileName
        {
            get => _currentFileName;
            private set
            {
                if (_currentFileName != value)
                {
                    _currentFileName = value;
                    OnPropertyChanged(nameof(CurrentFileName));
                }
            }
        }

        /// <summary>
        /// 当前文件完整路径
        /// </summary>
        public string CurrentFilePath
        {
            get => _currentFilePath;
            private set
            {
                if (_currentFilePath != value)
                {
                    _currentFilePath = value;
                    OnPropertyChanged(nameof(CurrentFilePath));
                }
            }
        }

        /// <summary>
        /// 是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged(nameof(IsPlaying));
                    OnPropertyChanged(nameof(PlayPauseButtonText));
                }
            }
        }

        /// <summary>
        /// 是否暂停
        /// </summary>
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (_isPaused != value)
                {
                    _isPaused = value;
                    OnPropertyChanged(nameof(IsPaused));
                }
            }
        }

        /// <summary>
        /// 当前播放位置（毫秒）
        /// </summary>
        public long CurrentPosition
        {
            get => _currentPosition;
            set
            {
                // 边界检查：确保位置在有效范围内
                long clampedValue = Math.Clamp(value, 0, Math.Max(0, _duration));

                if (_currentPosition != clampedValue)
                {
                    _currentPosition = clampedValue;

                    // 如果有MediaPlayer且可定位,直接设置VLC时间
                    if (_mediaPlayer != null && _mediaPlayer.IsSeekable && !_isUpdatingFromUI)
                    {
                        _mediaPlayer.Time = clampedValue;
                        Debug.WriteLine($"🎯 UI设置位置: {FormatTime(clampedValue)} (原始: {FormatTime(value)})");
                    }

                    OnPropertyChanged(nameof(CurrentPosition));
                    OnPropertyChanged(nameof(FormattedCurrentTime));
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        /// <summary>
        /// 视频总时长（毫秒）
        /// </summary>
        public long Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(FormattedDuration));
                    OnPropertyChanged(nameof(ProgressPercentage));
                    OnPropertyChanged(nameof(InPointPercentage));
                    OnPropertyChanged(nameof(OutPointPercentage));
                    OnPropertyChanged(nameof(MarkedRegionPercentage));
                }
            }
        }

        /// <summary>
        /// 音量（0-100）
        /// </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                if (Math.Abs(_volume - value) > 0.01f)
                {
                    _volume = Math.Clamp(value, 0f, 100f);
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.Volume = (int)_volume;
                    }
                    
                    // 保存音量设置
                    Properties.Settings.Default.LastVolume = _volume;
                    Properties.Settings.Default.Save();
                    
                    OnPropertyChanged(nameof(Volume));
                }
            }
        }

        /// <summary>
        /// 是否静音
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted != value)
                {
                    _isMuted = value;
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.Mute = value;
                    }
                    
                    // 保存静音状态
                    Properties.Settings.Default.LastMuted = _isMuted;
                    Properties.Settings.Default.Save();
                    
                    OnPropertyChanged(nameof(IsMuted));
                    OnPropertyChanged(nameof(VolumeButtonText));
                    OnPropertyChanged(nameof(VolumeIcon));
                }
            }
        }

        /// <summary>
        /// 视频宽度（像素）
        /// </summary>
        public int VideoWidth
        {
            get => _videoWidth;
            set
            {
                if (_videoWidth != value)
                {
                    _videoWidth = value;
                    OnPropertyChanged(nameof(VideoWidth));
                }
            }
        }

        /// <summary>
        /// 视频高度（像素）
        /// </summary>
        public int VideoHeight
        {
            get => _videoHeight;
            set
            {
                if (_videoHeight != value)
                {
                    _videoHeight = value;
                    OnPropertyChanged(nameof(VideoHeight));
                }
            }
        }

        /// <summary>
        /// 获取视频在播放器中的实际显示矩形
        /// </summary>
        public System.Windows.Rect GetVideoDisplayRect()
        {
            if (!HasVideo || VideoWidth <= 0 || VideoHeight <= 0)
                return new System.Windows.Rect(0, 0, 1920, 1080); // 默认全屏

            double containerWidth = 1920;
            double containerHeight = 1080;
            double containerRatio = containerWidth / containerHeight; // 16:9 ≈ 1.777
            double videoRatio = (double)VideoWidth / VideoHeight;

            double displayWidth, displayHeight, offsetX = 0, offsetY = 0;

            if (videoRatio > containerRatio)
            {
                // 视频更宽：上下黑边 (letterboxing)
                displayWidth = containerWidth;
                displayHeight = containerWidth / videoRatio;
                offsetY = (containerHeight - displayHeight) / 2;
            }
            else
            {
                // 视频更窄：左右黑边 (pillarboxing)
                displayHeight = containerHeight;
                displayWidth = containerHeight * videoRatio;
                offsetX = (containerWidth - displayWidth) / 2;
            }

            return new System.Windows.Rect(offsetX, offsetY, displayWidth, displayHeight);
        }

        /// <summary>
        /// 入点位置（毫秒）
        /// </summary>
        public long InPoint
        {
            get => _inPoint;
            set
            {
                if (_inPoint != value)
                {
                    _inPoint = value;
                    _hasInPoint = value >= 0;
                    OnPropertyChanged(nameof(InPoint));
                    OnPropertyChanged(nameof(HasInPoint));
                    OnPropertyChanged(nameof(FormattedInPoint));
                    OnPropertyChanged(nameof(InPointPercentage));
                    OnPropertyChanged(nameof(MarkedRegionPercentage));
                }
            }
        }

        /// <summary>
        /// 出点位置（毫秒）
        /// </summary>
        public long OutPoint
        {
            get => _outPoint;
            set
            {
                if (_outPoint != value)
                {
                    _outPoint = value;
                    _hasOutPoint = value >= 0;
                    OnPropertyChanged(nameof(OutPoint));
                    OnPropertyChanged(nameof(HasOutPoint));
                    OnPropertyChanged(nameof(FormattedOutPoint));
                    OnPropertyChanged(nameof(OutPointPercentage));
                    OnPropertyChanged(nameof(MarkedRegionPercentage));
                }
            }
        }

        /// <summary>
        /// 是否已设置入点
        /// </summary>
        public bool HasInPoint
        {
            get => _hasInPoint;
            set
            {
                if (_hasInPoint != value)
                {
                    _hasInPoint = value;
                    OnPropertyChanged(nameof(HasInPoint));
                }
            }
        }

        /// <summary>
        /// 是否已设置出点
        /// </summary>
        public bool HasOutPoint
        {
            get => _hasOutPoint;
            set
            {
                if (_hasOutPoint != value)
                {
                    _hasOutPoint = value;
                    OnPropertyChanged(nameof(HasOutPoint));
                }
            }
        }

        /// <summary>
        /// 格式化的当前时间
        /// </summary>
        public string FormattedCurrentTime => FormatTime(_currentPosition);

        /// <summary>
        /// 格式化的总时长
        /// </summary>
        public string FormattedDuration => FormatTime(_duration);

        /// <summary>
        /// 格式化的入点时间
        /// </summary>
        public string FormattedInPoint => FormatTime(_inPoint);

        /// <summary>
        /// 格式化的出点时间
        /// </summary>
        public string FormattedOutPoint => FormatTime(_outPoint);

        /// <summary>
        /// 播放进度百分比（0-100）
        /// </summary>
        public double ProgressPercentage
        {
            get
            {
                if (_duration <= 0) return 0;
                return (_currentPosition / (double)_duration) * 100.0;
            }
        }

        /// <summary>
        /// 入点百分比位置 (0-100)
        /// </summary>
        public double InPointPercentage
        {
            get
            {
                if (_duration <= 0 || _inPoint < 0) return 0;
                return (_inPoint / (double)_duration) * 100.0;
            }
        }

        /// <summary>
        /// 出点百分比位置 (0-100)
        /// </summary>
        public double OutPointPercentage
        {
            get
            {
                if (_duration <= 0 || _outPoint < 0) return 0;
                return (_outPoint / (double)_duration) * 100.0;
            }
        }

        /// <summary>
        /// 标记区间宽度百分比 (0-100)
        /// </summary>
        public double MarkedRegionPercentage
        {
            get
            {
                if (_duration <= 0 || _inPoint < 0 || _outPoint < 0) return 0;
                return OutPointPercentage - InPointPercentage;
            }
        }

        /// <summary>
        /// 播放/暂停按钮文本
        /// </summary>
        public string PlayPauseButtonText => _isPlaying ? "⏸" : "▶";

        /// <summary>
        /// 音量按钮文本
        /// </summary>
        public string VolumeButtonText => _isMuted ? "🔇" : "🔊";

        /// <summary>
        /// 音量图标
        /// </summary>
        public string VolumeIcon => _isMuted ? "🔇" : "🔊";

        /// <summary>
        /// 播放速度 (0.25x - 2.0x)
        /// </summary>
        public float PlaybackRate
        {
            get => _playbackRate;
            set
            {
                var clampedValue = Math.Clamp(value, 0.25f, 2.0f);
                if (Math.Abs(_playbackRate - clampedValue) > 0.01f)
                {
                    _playbackRate = clampedValue;
                    
                    // 应用到VLC
                    if (_mediaPlayer != null)
                    {
                        _mediaPlayer.SetRate(_playbackRate);
                    }
                    
                    OnPropertyChanged(nameof(PlaybackRate));
                    OnPropertyChanged(nameof(PlaybackRateText));
                    DebugLogger.LogInfo($"播放速度: {_playbackRate}x");
                }
            }
        }

        /// <summary>
        /// 播放速度文本显示
        /// </summary>
        public string PlaybackRateText => $"{_playbackRate:F2}x";

        // 进度条宽度(用于计算入出点标记位置)
        private double _progressBarWidth = 0;
        public double ProgressBarWidth
        {
            get => _progressBarWidth;
            set
            {
                if (Math.Abs(_progressBarWidth - value) > 0.1)
                {
                    _progressBarWidth = value;
                    OnPropertyChanged(nameof(ProgressBarWidth));
                    OnPropertyChanged(nameof(InPointPixelPosition));
                    OnPropertyChanged(nameof(OutPointPixelPosition));
                    OnPropertyChanged(nameof(MarkedRegionWidth));
                }
            }
        }

        /// <summary>
        /// 入点在进度条上的像素位置
        /// </summary>
        public double InPointPixelPosition
        {
            get
            {
                if (_duration <= 0 || _progressBarWidth <= 0 || !HasInPoint) 
                    return 0;
                return (_inPoint / (double)_duration) * _progressBarWidth;
            }
        }

        /// <summary>
        /// 出点在进度条上的像素位置
        /// </summary>
        public double OutPointPixelPosition
        {
            get
            {
                if (_duration <= 0 || _progressBarWidth <= 0 || !HasOutPoint) 
                    return 0;
                return (_outPoint / (double)_duration) * _progressBarWidth;
            }
        }

        /// <summary>
        /// 标记区间宽度
        /// </summary>
        public double MarkedRegionWidth
        {
            get
            {
                if (!HasInPoint || !HasOutPoint || _duration <= 0 || _progressBarWidth <= 0) 
                    return 0;
                return OutPointPixelPosition - InPointPixelPosition;
            }
        }

        /// <summary>
        /// 是否有完整的入出点对
        /// </summary>
        public bool HasBothPoints => HasInPoint && HasOutPoint;

        #endregion

        #region 命令

        public ICommand PlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand SeekForwardCommand { get; } // 快进5秒
        public ICommand SeekBackwardCommand { get; } // 快退5秒
        public ICommand SeekForwardFastCommand { get; } // 快进100毫秒
        public ICommand SeekBackwardFastCommand { get; } // 快退100毫秒
        public ICommand MarkInPointCommand { get; }
        public ICommand MarkOutPointCommand { get; }
        public ICommand ClearInPointCommand { get; }
        public ICommand ClearOutPointCommand { get; }
        public ICommand PlayMarkedRegionCommand { get; }
        public ICommand MuteCommand { get; }
        public ICommand VolumeUpCommand { get; }
        public ICommand VolumeDownCommand { get; }
        public ICommand ToggleLoopCommand { get; }
        public ICommand SpeedUpCommand { get; } // 加速
        public ICommand SpeedDownCommand { get; } // 减速
        public ICommand ResetSpeedCommand { get; } // 重置速度

        #endregion

        public VideoPlayerViewModel()
        {
            // 初始化命令
            PlayPauseCommand = new RelayCommand(PlayPause, CanPlayPause);
            StopCommand = new RelayCommand(Stop, CanStop);
            SeekForwardCommand = new RelayCommand(() => Seek(_currentPosition + 5000), CanSeek);
            SeekBackwardCommand = new RelayCommand(() => Seek(_currentPosition - 5000), CanSeek);
            SeekForwardFastCommand = new RelayCommand(() => Seek(_currentPosition + 100), CanSeek);
            SeekBackwardFastCommand = new RelayCommand(() => Seek(_currentPosition - 100), CanSeek);
            MarkInPointCommand = new RelayCommand(MarkInPoint, CanMarkInPoint);
            MarkOutPointCommand = new RelayCommand(MarkOutPoint, CanMarkOutPoint);
            ClearInPointCommand = new RelayCommand(ClearInPoint, () => true); // 原: () => _hasInPoint
            ClearOutPointCommand = new RelayCommand(ClearOutPoint, () => true); // 原: () => _hasOutPoint
            PlayMarkedRegionCommand = new RelayCommand(PlayMarkedRegion, CanPlayMarkedRegion);
            MuteCommand = new RelayCommand(ToggleMute, CanToggleMute);
            VolumeUpCommand = new RelayCommand(() => Volume += 5, CanToggleMute);
            VolumeDownCommand = new RelayCommand(() => Volume -= 5, CanToggleMute);
            ToggleLoopCommand = new RelayCommand(() => IsLoopEnabled = !IsLoopEnabled, () => true);
            SpeedUpCommand = new RelayCommand(() => PlaybackRate += 0.25f, () => true);
            SpeedDownCommand = new RelayCommand(() => PlaybackRate -= 0.25f, () => true);
            ResetSpeedCommand = new RelayCommand(() => PlaybackRate = 1.0f, () => true);

            // 恢复上次的音量设置
            RestoreVolumeSettings();

            // 初始化显示状态：显示占位符（黑色背景+Logo），VideoView隐藏
            PlaceholderOpacity = 1.0;
            VideoViewOffsetX = 0.0;
            VideoViewOffsetY = 0.0;
            IsVideoViewVisible = false; // 初始时隐藏VideoView，避免白色背景

            Debug.WriteLine("VideoPlayerViewModel 已创建");
        }


        /// <summary>
        /// 初始化LibVLC（延迟初始化，在需要时调用）
        /// </summary>
        public void InitializeLibVLC()
        {
            if (_libVLC != null) return;

            try
            {
                Debug.WriteLine("正在初始化 LibVLC...");
                
                // 创建 LibVLC 实例（带硬件加速）
                // 注意: Core.Initialize() 已在 App.xaml.cs 的 OnStartup 中调用
                _libVLC = new LibVLC("--avcodec-hw=any", "--file-caching=300");
                
                // 创建 MediaPlayer
                _mediaPlayer = new MediaPlayer(_libVLC);
                
                // 应用恢复的音量设置
                _mediaPlayer.Volume = (int)_volume;
                _mediaPlayer.Mute = _isMuted;
                
                DebugLogger.LogInfo($"应用音量设置到播放器: 音量={_volume}, 静音={_isMuted}");
                Debug.WriteLine($"应用音量设置到播放器: 音量={_volume}, 静音={_isMuted}");
                
                // 订阅事件 (不再订阅TimeChanged,改用定时器轮询)
                _mediaPlayer.LengthChanged += OnLengthChanged;
                _mediaPlayer.Playing += OnPlaying;
                _mediaPlayer.Paused += OnPaused;
                _mediaPlayer.Stopped += OnStopped;
                _mediaPlayer.EndReached += OnEndReached;
                
                // 启动统一播放定时器(处理进度更新和出点监控)
                StartPlaybackTimer();
                
                OnPropertyChanged(nameof(MediaPlayer));
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(VolumeButtonText));
                OnPropertyChanged(nameof(VolumeIcon));
                
                Debug.WriteLine("✅ LibVLC 初始化成功");
                
                // VideoView初始化完成后，延迟一小段时间后准备显示（确保VideoView已完成初始化）
                Application.Current?.Dispatcher?.InvokeAsync(async () =>
                {
                    // 等待200ms，确保VideoView已完成初始化
                    await System.Threading.Tasks.Task.Delay(200);
                    
                    // 根据视频加载状态设置VideoView可见性
                    if (!HasVideoLoaded)
                    {
                        // 没有视频时，VideoView保持隐藏，占位符保持显示
                        IsVideoViewVisible = false;
                        VideoViewOffsetX = 0.0;
                        VideoViewOffsetY = 0.0;
                        Debug.WriteLine("VideoView初始化完成，保持隐藏（无视频，占位符保持显示）");
                    }
                    else
                    {
                        // 有视频时，显示VideoView，占位符淡出（这个情况应该很少，因为通常先初始化再加载视频）
                        IsVideoViewVisible = true;
                        VideoViewOffsetX = 0.0;
                        VideoViewOffsetY = 0.0;
                        PlaceholderOpacity = 0.0;
                        Debug.WriteLine("VideoView初始化完成，已显示（有视频，占位符已淡出）");
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ LibVLC 初始化失败: {ex.Message}");
                MessageBox.Show($"视频播放器初始化失败:\n{ex.Message}", "错误", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 播放控制方法

        /// <summary>
        /// 加载视频
        /// </summary>
        public void LoadVideo(string filePath)
        {
            // 1. 验证文件路径
            if (string.IsNullOrEmpty(filePath))
            {
                DebugLogger.LogError("文件路径为空");
                Services.ToastNotification.ShowError("文件路径无效");
                HasVideo = false;
                return;
            }

            if (!File.Exists(filePath))
            {
                DebugLogger.LogError($"文件不存在: {filePath}");
                Services.ToastNotification.ShowError($"文件不存在:\n{Path.GetFileName(filePath)}");
                HasVideo = false;
                return;
            }

            try
            {
                // 2. 验证文件格式
                var extension = Path.GetExtension(filePath).ToLower();
                var supportedFormats = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts" };
                if (!supportedFormats.Contains(extension))
                {
                    DebugLogger.LogWarning($"不支持的文件格式: {extension}");
                    Services.ToastNotification.ShowWarning($"可能不支持的格式: {extension}\n将尝试播放...");
                }

                // 3. 验证文件可读
                try
                {
                    using (var fs = File.OpenRead(filePath))
                    {
                        if (fs.Length == 0)
                        {
                            DebugLogger.LogError("文件大小为0");
                            Services.ToastNotification.ShowError("文件已损坏(大小为0)");
                            HasVideo = false;
                            return;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    DebugLogger.LogError($"无权限访问文件: {filePath}");
                    Services.ToastNotification.ShowError("无权限访问该文件");
                    HasVideo = false;
                    return;
                }
                catch (IOException ioEx)
                {
                    DebugLogger.LogError($"文件读取错误: {ioEx.Message}");
                    Services.ToastNotification.ShowError($"文件读取失败:\n{ioEx.Message}");
                    HasVideo = false;
                    return;
                }

                // 4. 确保 LibVLC 已初始化
                InitializeLibVLC();
                
                if (_mediaPlayer == null)
                {
                    DebugLogger.LogError("MediaPlayer 未初始化");
                    Services.ToastNotification.ShowError("播放器初始化失败");
                    HasVideo = false;
                    return;
                }

                // 5. 停止当前播放（确保在UI线程上执行）
                // 注意：如果外部已经调用了Stop()，这里可能不需要再次停止
                // 但为了确保状态一致，我们仍然更新状态
                // 如果IsPlaying已经是false，说明外部已经调用了Stop()，我们只需要更新状态，不需要再次调用Stop()
                if (_mediaPlayer != null && (_isPlaying || _isPaused))
                {
                    try
                    {
                        DebugLogger.LogInfo("LoadVideo: 停止当前播放");
                        
                        // 先更新状态
                        IsPlaying = false;
                        IsPaused = false;
                        _isMonitoringOutPoint = false;
                        
                        // 然后停止MediaPlayer（使用更安全的方式：先暂停再停止）
                        try
                        {
                            // 先尝试暂停（如果正在播放）
                            if (_isPlaying)
                            {
                                try
                                {
                                    _mediaPlayer.Pause();
                                    System.Threading.Thread.Sleep(50); // 等待暂停完成
                                }
                                catch (Exception pauseEx)
                                {
                                    DebugLogger.LogWarning($"LoadVideo: 暂停失败: {pauseEx.Message}");
                                }
                            }
                            
                            // 然后调用Stop()
                            _mediaPlayer.Stop();
                            DebugLogger.LogInfo("LoadVideo: MediaPlayer.Stop() 调用成功");
                        }
                        catch (Exception stopEx)
                        {
                            // MediaPlayer.Stop()可能在某些状态下失败（比如正在释放或已被停止）
                            DebugLogger.LogWarning($"LoadVideo: MediaPlayer.Stop() 失败，但状态已更新: {stopEx.GetType().Name} - {stopEx.Message}");
                        }
                        
                        // 等待MediaPlayer完全停止
                        System.Threading.Thread.Sleep(100);
                        DebugLogger.LogInfo("LoadVideo: 播放已停止");
                    }
                    catch (Exception stopEx)
                    {
                        DebugLogger.LogError($"LoadVideo: 停止播放时发生错误: {stopEx.GetType().Name} - {stopEx.Message}\n{stopEx.StackTrace}");
                        // 确保状态已更新
                        IsPlaying = false;
                        IsPaused = false;
                        _isMonitoringOutPoint = false;
                    }
                }
                else
                {
                    // 即使没有播放，也确保状态正确
                    IsPlaying = false;
                    IsPaused = false;
                    _isMonitoringOutPoint = false;
                }

                // 6. 清理旧媒体（先移除Media引用，再释放）
                if (_currentMedia != null)
                {
                    try
                    {
                        DebugLogger.LogInfo("LoadVideo: 清理旧媒体");
                        // 先移除Media引用，让MediaPlayer释放对旧Media的引用
                        _mediaPlayer.Media = null;
                        // 等待MediaPlayer释放旧Media
                        System.Threading.Thread.Sleep(50);
                        
                        // 然后释放旧Media
                        _currentMedia.Dispose();
                        _currentMedia = null;
                        DebugLogger.LogInfo("LoadVideo: 旧媒体已清理");
                    }
                    catch (Exception disposeEx)
                    {
                        DebugLogger.LogError($"释放旧媒体失败: {disposeEx.Message}\n{disposeEx.StackTrace}");
                    }
                }

                // 7. 创建新媒体
                try
                {
                    DebugLogger.LogInfo($"LoadVideo: 创建新媒体 - {Path.GetFileName(filePath)}");
                    _currentMedia = new Media(_libVLC, new Uri(filePath));
                    
                    // 禁用VLC的自动字幕加载，以便使用我们自己的字幕预览功能
                    _currentMedia.AddOption(":no-sub-autodetect-file");
                    
                    _mediaPlayer.Media = _currentMedia;
                    CurrentFilePath = filePath;
                    DebugLogger.LogInfo("LoadVideo: 新媒体已设置到MediaPlayer");

                    // 8. 解析媒体信息
                    _currentMedia.Parse();
                    DebugLogger.LogInfo("LoadVideo: 媒体信息已解析");
                }
                catch (Exception mediaEx)
                {
                    DebugLogger.LogError($"创建或设置新媒体失败: {mediaEx.Message}\n{mediaEx.StackTrace}");
                    throw; // 重新抛出异常，让外层catch处理
                }

                // 9. 清除入出点
                ClearInPoint();
                ClearOutPoint();

                // 10. 标记已加载视频（用于逻辑判断）
                HasVideo = true;

                // 11. 更新当前文件名
                CurrentFileName = Path.GetFileName(filePath);

                // 12. 重置视频加载状态（占位符仍显示）
                HasVideoLoaded = false;

                // 成功提示
                Services.ToastNotification.ShowSuccess($"已加载: {Path.GetFileName(filePath)}");

                // 同步PlayQueueManager的当前视频
                var loadedVideo = _videoListViewModel?.Files.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (loadedVideo != null)
                {
                    _videoListViewModel?.PlayQueueManager?.SetCurrentVideo(loadedVideo);
                }

                // 更新列表高亮状态
                _videoListViewModel?.SetCurrentPlaying(filePath);

                Debug.WriteLine($"✅ 视频已加载: {CurrentFileName}");
            }
            catch (UnauthorizedAccessException)
            {
                HasVideo = false;
                DebugLogger.LogError("加载视频失败: 访问被拒绝");
                Services.ToastNotification.ShowError("访问被拒绝,请检查文件权限");
            }
            catch (FileNotFoundException)
            {
                HasVideo = false;
                DebugLogger.LogError("加载视频失败: 文件未找到");
                Services.ToastNotification.ShowError("文件未找到或已被删除");
            }
            catch (Exception ex)
            {
                HasVideo = false;
                DebugLogger.LogError($"加载视频失败: {ex.Message}\n{ex.StackTrace}");
                Services.ToastNotification.ShowError($"加载失败:\n{ex.Message}");
            }
        }

        /// <summary>
        /// 设置播放列表
        /// </summary>
        public void SetPlaylist(System.Collections.ObjectModel.ObservableCollection<Models.VideoFile> playlist, VideoListViewModel videoListViewModel)
        {
            _playlist = playlist;
            _videoListViewModel = videoListViewModel;
            DebugLogger.LogInfo($"播放列表已设置,共 {playlist?.Count ?? 0} 个文件");
        }

        /// <summary>
        /// 加载指定索引的视频
        /// </summary>
        public void LoadVideoByIndex(int index)
        {
            if (_playlist == null || index < 0 || index >= _playlist.Count)
            {
                DebugLogger.LogWarning($"无效的视频索引: {index}");
                return;
            }

            _currentVideoIndex = index;
            var videoFile = _playlist[index];
            LoadVideo(videoFile.FilePath);
            
            DebugLogger.LogInfo($"加载视频 [{index + 1}/{_playlist.Count}]: {videoFile.FileName}");
        }

        /// <summary>
        /// 播放下一个视频
        /// </summary>
        public void PlayNext()
        {
            if (_playlist == null || _playlist.Count == 0)
            {
                DebugLogger.LogWarning("播放列表为空,无法播放下一个");
                return;
            }

            int nextIndex = _currentVideoIndex + 1;
            
            if (nextIndex < _playlist.Count)
            {
                LoadVideoByIndex(nextIndex);
                Play();
                DebugLogger.LogSuccess($"切换到下一个视频: {_playlist[nextIndex].FileName}");
            }
            else if (_isLoopEnabled)
            {
                // 循环播放
                LoadVideoByIndex(0);
                Play();
                DebugLogger.LogSuccess("循环播放,从第一个视频开始");
            }
            else
            {
                DebugLogger.LogInfo("已到达播放列表末尾");
            }
        }

        /// <summary>
        /// 播放上一个视频
        /// </summary>
        public void PlayPrevious()
        {
            if (_playlist == null || _playlist.Count == 0)
            {
                DebugLogger.LogWarning("播放列表为空,无法播放上一个");
                return;
            }

            int prevIndex = _currentVideoIndex - 1;
            
            if (prevIndex >= 0)
            {
                LoadVideoByIndex(prevIndex);
                Play();
                DebugLogger.LogSuccess($"切换到上一个视频: {_playlist[prevIndex].FileName}");
            }
            else
            {
                DebugLogger.LogInfo("已在播放列表开头");
            }
        }

        /// <summary>
        /// 播放/暂停切换
        /// </summary>
        public void PlayPause()
        {
            if (_mediaPlayer == null) return;

            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        /// <summary>
        /// 播放
        /// </summary>
        public void Play()
        {
            if (_mediaPlayer == null)
            {
                DebugLogger.LogWarning("Play: MediaPlayer 为 null,无法播放");
                return;
            }
            
            DebugLogger.Log($"▶ 开始播放 - HasOutPoint={HasOutPoint}, 出点={FormattedOutPoint}");
            _mediaPlayer.Play();
            
            // 立即更新状态(同步),不依赖事件回调
            IsPlaying = true;
            IsPaused = false;
            
            // 如果设置了出点,启用监控标志(由统一定时器处理)
            if (HasOutPoint)
            {
                _isMonitoringOutPoint = true;
                DebugLogger.Log("启用出点监控");
            }
        }

        /// <summary>
        /// 播放上一个视频
        /// </summary>
        public void Previous()
        {
            // 调用 VideoListViewModel 的方法
            // 这个方法应该在 MainWindow 中协调
            DebugLogger.LogInfo("Previous() 被调用,应由 MainWindow 协调");
        }

        /// <summary>
        /// 播放下一个视频
        /// </summary>
        public void Next()
        {
            // 调用 VideoListViewModel 的方法
            // 这个方法应该在 MainWindow 中协调
            DebugLogger.LogInfo("Next() 被调用,应由 MainWindow 协调");
        }

        /// <summary>
        /// 设置播放速度
        /// </summary>
        /// <param name="speed">播放速度 (0.25 ~ 4.0)</param>
        public void SetPlaybackSpeed(float speed)
        {
            if (_mediaPlayer == null)
            {
                DebugLogger.LogWarning("SetPlaybackSpeed: MediaPlayer 为 null");
                return;
            }

            if (speed < 0.25f || speed > 4.0f)
            {
                DebugLogger.LogWarning($"播放速度超出范围: {speed}x,有效范围 0.25x ~ 4.0x");
                return;
            }

            try
            {
                _mediaPlayer.SetRate(speed);
                _playbackRate = speed;
                OnPropertyChanged(nameof(PlaybackRate));
                OnPropertyChanged(nameof(PlaybackRateText));
                DebugLogger.LogSuccess($"播放速度设置为: {speed}x");
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"设置播放速度失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 截取当前帧并保存为图片
        /// </summary>
        /// <param name="savePath">保存路径(包含文件名),如果为null则自动生成</param>
        /// <returns>保存的文件路径,失败返回null</returns>
        public string? TakeScreenshot(string? savePath = null)
        {
            if (_mediaPlayer == null)
            {
                DebugLogger.LogWarning("TakeScreenshot: MediaPlayer 为 null");
                Services.ToastNotification.ShowWarning("截图失败:播放器未初始化");
                return null;
            }

            if (!HasVideo)
            {
                DebugLogger.LogWarning("TakeScreenshot: 没有视频");
                Services.ToastNotification.ShowWarning("截图失败:请先加载视频");
                return null;
            }

            try
            {
                // 如果未指定保存路径,自动生成
                if (string.IsNullOrEmpty(savePath))
                {
                    var screenshotsFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), 
                        "VideoEditor_Screenshots");
                    
                    // 确保文件夹存在
                    Directory.CreateDirectory(screenshotsFolder);
                    
                    // 生成文件名: 视频名_时间码_时间戳.png
                    var videoName = Path.GetFileNameWithoutExtension(_currentFileName);
                    var timeCode = FormattedCurrentTime.Replace(":", "-").Replace(".", "-");
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"{videoName}_{timeCode}_{timestamp}.png";
                    
                    savePath = Path.Combine(screenshotsFolder, fileName);
                }

                // 确保目录存在
                var directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 使用LibVLC的截图功能
                // 参数: 视频轨道编号(0=第一个), 文件路径, 宽度(0=原始), 高度(0=原始)
                var success = _mediaPlayer.TakeSnapshot(0, savePath, 0, 0);

                if (success)
                {
                    DebugLogger.LogSuccess($"截图已保存: {savePath}");
                    var fileName = Path.GetFileName(savePath);
                    Services.ToastNotification.ShowSuccess($"📷 截图成功: {fileName}");
                    
                    // 可选: 打开文件所在文件夹
                    // System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{savePath}\"");
                    
                    return savePath;
                }
                else
                {
                    DebugLogger.LogError("TakeSnapshot 返回 false");
                    Services.ToastNotification.ShowError("截图失败:无法保存图片");
                    return null;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"截图失败: {ex.Message}\n{ex.StackTrace}");
                Services.ToastNotification.ShowError($"截图失败:{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 暂停
        /// </summary>
        public void Pause()
        {
            if (_mediaPlayer == null) return;
            
            _mediaPlayer.Pause();
            
            // 立即更新状态(同步)
            IsPlaying = false;
            IsPaused = true;
            
            // 停止出点监控
            _isMonitoringOutPoint = false;
            
            Debug.WriteLine("⏸ 视频已暂停");
        }

        /// <summary>
        /// 停止
        /// </summary>
        public void Stop()
        {
            // 注意：调用者应该确保在UI线程上调用此方法
            // 不进行线程检查，避免Dispatcher.Invoke()可能导致死锁
            
            if (_mediaPlayer == null)
            {
                DebugLogger.LogWarning("Stop(): MediaPlayer 为 null，仅更新状态");
                // 即使MediaPlayer为null，也要更新状态
                IsPlaying = false;
                IsPaused = false;
                _isMonitoringOutPoint = false;
                return;
            }
            
            // 关键修复：在更新状态之前，先保存当前播放状态
            // 这样可以在后续检查时知道是否真的在播放
            bool wasPlaying = _isPlaying;
            bool wasPaused = _isPaused;
            
            try
            {
                DebugLogger.LogInfo($"Stop(): 开始停止播放 (wasPlaying={wasPlaying}, wasPaused={wasPaused})");
                
                // 更安全的停止方式：
                // 1. 先尝试暂停（如果正在播放），避免在播放状态下直接调用Stop()
                // 2. 然后调用Stop()
                // 注意：不移除Media引用，保留媒体和入出点
                try
                {
                    // 先尝试暂停（如果正在播放）
                    // 这样可以避免在播放状态下直接调用Stop()导致的崩溃
                    if (wasPlaying)
                    {
                        try
                        {
                            // 检查MediaPlayer的状态
                            var currentMedia = _mediaPlayer.Media;
                            if (currentMedia != null)
                            {
                                DebugLogger.LogInfo("Stop(): 先暂停播放（更安全）");
                                _mediaPlayer.Pause();
                                System.Threading.Thread.Sleep(100); // 增加等待时间，确保暂停完成
                                DebugLogger.LogInfo("Stop(): 暂停完成");
                            }
                            else
                            {
                                DebugLogger.LogInfo("Stop(): Media为null，跳过暂停");
                            }
                        }
                        catch (Exception pauseEx)
                        {
                            DebugLogger.LogWarning($"Stop(): 暂停失败，继续停止: {pauseEx.GetType().Name} - {pauseEx.Message}");
                        }
                    }
                    else
                    {
                        DebugLogger.LogInfo("Stop(): 未在播放，跳过暂停步骤");
                    }
                    
                    // 调用Stop()
                    // 注意：即使暂停失败，也尝试调用Stop()
                    try
                    {
                        DebugLogger.LogInfo("Stop(): 准备调用 MediaPlayer.Stop()");
                        _mediaPlayer.Stop();
                        DebugLogger.LogInfo("Stop(): MediaPlayer.Stop() 调用成功");
                    }
                    catch (ObjectDisposedException disposedEx)
                    {
                        // MediaPlayer已被释放
                        DebugLogger.LogWarning($"Stop(): MediaPlayer 已被释放: {disposedEx.Message}");
                        _mediaPlayer = null; // 清除引用
                    }
                    catch (InvalidOperationException invalidOpEx)
                    {
                        // MediaPlayer处于无效状态
                        DebugLogger.LogWarning($"Stop(): MediaPlayer 处于无效状态: {invalidOpEx.Message}");
                    }
                    catch (Exception stopEx)
                    {
                        // 其他异常（可能是LibVLC底层错误）
                        // 记录详细错误信息，但不抛出异常，避免崩溃
                        DebugLogger.LogWarning($"Stop(): MediaPlayer.Stop() 失败: {stopEx.GetType().Name} - {stopEx.Message}");
                        DebugLogger.LogWarning($"Stop(): 堆栈跟踪: {stopEx.StackTrace}");
                    }
                }
                catch (Exception ex)
                {
                    // 捕获所有未预期的异常，防止崩溃
                    DebugLogger.LogError($"Stop() 内部操作发生错误: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
                }
                
                // 现在更新状态（在Stop()调用之后）
                IsPlaying = false;
                IsPaused = false;
                _isMonitoringOutPoint = false;
                CurrentPosition = 0;
                
                DebugLogger.LogInfo("⏹ 视频已停止 (保留媒体和入出点)");
            }
            catch (Exception ex)
            {
                // 捕获所有未预期的异常，防止崩溃
                DebugLogger.LogError($"Stop() 发生未预期的错误: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
                // 确保状态已更新，即使发生异常
                IsPlaying = false;
                IsPaused = false;
                _isMonitoringOutPoint = false;
            }
        }

        /// <summary>
        /// 清空媒体 (完全停止,显示占位符)
        /// </summary>
        public void ClearMedia()
        {
            Stop();

            _mediaPlayer.Media = null;
            HasVideo = false;
            HasVideoLoaded = false; // 重置加载状态，显示占位符
            CurrentFilePath = string.Empty;
            CurrentFileName = "未加载视频";

            // 清除列表高亮
            _videoListViewModel?.ClearAllPlayingStates();

            // 可选: 是否清除入出点
            // ClearInPoint();
            // ClearOutPoint();

            Debug.WriteLine("⏹ 已清空媒体");
        }

        /// <summary>
        /// 跳转到指定位置
        /// </summary>
        public void Seek(long milliseconds)
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsSeekable) return;

            // 限制范围
            milliseconds = Math.Clamp(milliseconds, 0, _duration);
            
            _mediaPlayer.Time = milliseconds;
            CurrentPosition = milliseconds;
            
            Debug.WriteLine($"⏩ 跳转到: {FormatTime(milliseconds)}");
        }

        /// <summary>
        /// 开始拖拽进度条 (停止自动更新)
        /// </summary>
        public void BeginSeek()
        {
            _isSeekingByUser = true;
            DebugLogger.LogInfo("▶ 开始拖拽进度条");
        }

        /// <summary>
        /// 结束拖拽进度条 (恢复自动更新并跳转)
        /// </summary>
        public void EndSeek(long targetPosition)
        {
            _isSeekingByUser = false;
            Seek(targetPosition);
            DebugLogger.LogInfo($"■ 拖拽结束,跳转到: {FormatTime(targetPosition)}");
        }


        /// <summary>
        /// 设置播放进度（0-100）
        /// </summary>
        public void SetProgress(double percentage)
        {
            if (_duration <= 0) return;
            
            var position = (long)((_duration * percentage) / 100.0);
            Seek(position);
        }

        /// <summary>
        /// 切换静音
        /// </summary>
        public void ToggleMute()
        {
            IsMuted = !IsMuted;
            Debug.WriteLine($"🔊 静音: {IsMuted}");
        }

        #endregion

        #region 入出点管理

        /// <summary>
        /// 标记入点
        /// </summary>
        public void MarkInPoint()
        {
            if (_mediaPlayer == null || _duration <= 0)
            {
                DebugLogger.LogWarning("MarkInPoint: 无有效视频,忽略操作");
                return;
            }
            
            var currentPos = _currentPosition;
            
            // 如果有出点且当前位置在出点之后,则交换入出点
            if (_hasOutPoint && currentPos >= _outPoint)
            {
                // 将原出点设为入点,当前位置设为出点
                InPoint = _outPoint;
                OutPoint = currentPos;
                DebugLogger.LogInfo($"🔄 位置交换: 入点={FormattedInPoint}, 出点={FormattedOutPoint}");
            }
            else
            {
                // 正常设置入点
                InPoint = currentPos;
                
                // 如果出点未设置,自动设置为视频结尾
                if (!_hasOutPoint)
                {
                    OutPoint = _duration;
                }
            }
            
            // 触发位置更新
            OnPropertyChanged(nameof(InPointPixelPosition));
            OnPropertyChanged(nameof(OutPointPixelPosition));
            OnPropertyChanged(nameof(MarkedRegionWidth));
            OnPropertyChanged(nameof(HasBothPoints));
            
            DebugLogger.LogSuccess($"🎯 入点已标记: {FormattedInPoint} ({_inPoint}ms)");
            Debug.WriteLine($"🎯 入点已标记: {FormattedInPoint}");
        }

        /// <summary>
        /// 标记出点
        /// </summary>
        public void MarkOutPoint()
        {
            if (_mediaPlayer == null || _duration <= 0)
            {
                DebugLogger.LogWarning("MarkOutPoint: 无有效视频,忽略操作");
                return;
            }
            
            var currentPos = _currentPosition;
            
            // 如果有入点且当前位置在入点之前,则交换入出点
            if (_hasInPoint && currentPos <= _inPoint)
            {
                // 将原入点设为出点,当前位置设为入点
                OutPoint = _inPoint;
                InPoint = currentPos;
                DebugLogger.LogInfo($"🔄 位置交换: 入点={FormattedInPoint}, 出点={FormattedOutPoint}");
            }
            else
            {
                // 正常设置出点
                OutPoint = currentPos;
                
                // 如果入点未设置,自动设置为视频开始
                if (!_hasInPoint)
                {
                    InPoint = 0;
                }
            }
            
            // 触发位置更新
            OnPropertyChanged(nameof(InPointPixelPosition));
            OnPropertyChanged(nameof(OutPointPixelPosition));
            OnPropertyChanged(nameof(MarkedRegionWidth));
            OnPropertyChanged(nameof(HasBothPoints));
            
            DebugLogger.LogSuccess($"🎯 出点已标记: {FormattedOutPoint} ({_outPoint}ms)");
            Debug.WriteLine($"🎯 出点已标记: {FormattedOutPoint}");
        }

        /// <summary>
        /// 清除入点
        /// </summary>
        public void ClearInPoint()
        {
            InPoint = -1;
            HasInPoint = false;
            
            // 触发可视化更新
            OnPropertyChanged(nameof(InPointPixelPosition));
            OnPropertyChanged(nameof(MarkedRegionWidth));
            OnPropertyChanged(nameof(HasBothPoints));
            
            DebugLogger.LogInfo("❌ 入点已清除");
            Debug.WriteLine("❌ 入点已清除");
        }

        /// <summary>
        /// 清除出点
        /// </summary>
        public void ClearOutPoint()
        {
            OutPoint = -1;
            HasOutPoint = false;
            
            // 触发可视化更新
            OnPropertyChanged(nameof(OutPointPixelPosition));
            OnPropertyChanged(nameof(MarkedRegionWidth));
            OnPropertyChanged(nameof(HasBothPoints));
            
            DebugLogger.LogInfo("❌ 出点已清除");
            Debug.WriteLine("❌ 出点已清除");
        }

        /// <summary>
        /// 播放标记区间
        /// </summary>
        public void PlayMarkedRegion()
        {
            if (!_hasInPoint || !_hasOutPoint || _mediaPlayer == null)
                return;

            // 跳转到入点
            Seek(_inPoint);
            
            // 开始播放(Play方法会自动启用出点监控)
            Play();

            Debug.WriteLine($"🚩 播放标记区间: {FormattedInPoint} → {FormattedOutPoint}");
        }

        // 旧的StartOutPointMonitoring方法已删除 - 现在由统一的OnPlaybackTimerTick处理

        // 旧的OnOutPointTimerTick方法已删除 - 现在由统一的OnPlaybackTimerTick处理

        #endregion

        #region 命令判断方法

        // 所有按钮始终可用,内部有保护逻辑,避免报错崩溃
        private bool CanPlayPause() => true; // 原: _mediaPlayer != null && _currentMedia != null;
        private bool CanStop() => true; // 原: _mediaPlayer != null && (_isPlaying || _isPaused);
        private bool CanSeek() => _mediaPlayer != null && _mediaPlayer.IsSeekable && _duration > 0;
        private bool CanMarkInPoint() => true; // 原: _mediaPlayer != null && _duration > 0;
        private bool CanMarkOutPoint() => true; // 原: _mediaPlayer != null && _duration > 0;
        private bool CanPlayMarkedRegion() => true; // 原: _hasInPoint && _hasOutPoint && _inPoint < _outPoint;
        private bool CanToggleMute() => true; // 原: _mediaPlayer != null;

        /// <summary>
        /// 启动统一播放定时器(处理进度更新和出点监控)
        /// </summary>
        private void StartPlaybackTimer()
        {
            // 停止旧定时器
            _playbackTimer?.Stop();
            _playbackTimer?.Dispose();
            
            // 创建新定时器,每50ms更新一次
            _playbackTimer = new System.Timers.Timer(50);
            _playbackTimer.Elapsed += OnPlaybackTimerTick;
            _playbackTimer.Start();
            
            Debug.WriteLine("✅ 播放定时器已启动");
        }
        
        /// <summary>
        /// 播放定时器回调 - 统一处理进度更新和出点监控
        /// </summary>
        private void OnPlaybackTimerTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                if (_mediaPlayer == null || _isUpdatingFromUI || _isSeekingByUser) return; // 拖拽时不更新
                
                var vlcTime = _mediaPlayer.Time;
                
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    // 1. 更新进度条
                    if (_currentPosition != vlcTime)
                    {
                        _isUpdatingFromUI = true;
                        _currentPosition = vlcTime;
                        OnPropertyChanged(nameof(CurrentPosition));
                        OnPropertyChanged(nameof(FormattedCurrentTime));
                        OnPropertyChanged(nameof(ProgressPercentage));
                        _isUpdatingFromUI = false;
                    }
                    
                    // 2. 出点监控(如果启用)
                    if (_isMonitoringOutPoint && HasOutPoint && _currentPosition >= _outPoint)
                    {
                        DebugLogger.Log($"⏸ 到达出点: {FormattedOutPoint}, 当前: {FormatTime(_currentPosition)}");
                        
                        // 先定位到出点,再暂停
                        if (_mediaPlayer != null && _mediaPlayer.IsSeekable)
                        {
                            try
                            {
                                _mediaPlayer.Time = _outPoint;
                            }
                            catch (Exception seekEx)
                            {
                                DebugLogger.LogError($"定位出点失败: {seekEx.Message}");
                            }
                        }
                        
                        // 暂停播放
                        if (IsPlaying)
                        {
                            Pause();
                            _isMonitoringOutPoint = false;
                            DebugLogger.LogSuccess($"⏸ 到达出点,自动暂停: {FormattedOutPoint}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 播放定时器错误: {ex.Message}");
                DebugLogger.LogError($"播放定时器错误: {ex.Message}");
            }
        }

        #endregion

        #region LibVLC 事件处理

        private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Duration = e.Length;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ OnLengthChanged 错误: {ex.Message}");
            }
        }

        private void OnPlaying(object? sender, EventArgs e)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsPlaying = true;
                    IsPaused = false;

                    // 标记视频已真正加载完成，隐藏占位符
                    HasVideoLoaded = true;

                    // 获取视频分辨率
                    if (_mediaPlayer != null)
                    {
                        try
                        {
                            uint px = 0, py = 0;
                            _mediaPlayer.Size(0, ref px, ref py);
                            if (px > 0 && py > 0)
                            {
                                VideoWidth = (int)px;
                                VideoHeight = (int)py;
                                DebugLogger.LogInfo($"视频分辨率: {VideoWidth}x{VideoHeight}");
                            }
                        }
                        catch
                        {
                            // 获取失败时使用默认值
                            if (VideoWidth == 0) VideoWidth = 1920;
                            if (VideoHeight == 0) VideoHeight = 1080;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ OnPlaying 错误: {ex.Message}");
            }
        }

        private void OnPaused(object? sender, EventArgs e)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsPlaying = false;
                    IsPaused = true;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ OnPaused 错误: {ex.Message}");
            }
        }

        private void OnStopped(object? sender, EventArgs e)
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    IsPlaying = false;
                    IsPaused = false;
                    CurrentPosition = 0;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ OnStopped 错误: {ex.Message}");
            }
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            try
            {
                // 使用BeginInvoke代替Invoke,避免阻塞UI线程
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    DebugLogger.LogError("❌ Dispatcher为空,无法处理播放结束事件");
                    return;
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        DebugLogger.LogInfo("📺 视频播放结束");
                        Debug.WriteLine("📺 视频播放结束");

                        // 根据播放模式决定是否自动播放下一首
                        // IsSinglePlayMode=true: 单曲播放，播放结束后停止
                        // IsSinglePlayMode=false: 连续播放，自动播放下一首
                        if (IsSinglePlayMode)
                        {
                            // 单曲播放模式：只停止，不自动播放下一首
                            Stop();
                            DebugLogger.LogInfo("单曲播放模式：播放结束，停止播放");
                        }
                        else
                        {
                            // 其他播放模式：自动播放下一首（顺序/循环/随机）
                            DebugLogger.LogInfo("连续播放模式：自动播放下一首");
                            AutoPlayNext();
                        }
                    }
                    catch (Exception innerEx)
                    {
                        DebugLogger.LogError($"OnEndReached 内部错误: {innerEx.Message}\n{innerEx.StackTrace}");
                        Debug.WriteLine($"❌ OnEndReached 内部错误: {innerEx.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"OnEndReached 错误: {ex.Message}\n{ex.StackTrace}");
                Debug.WriteLine($"❌ OnEndReached 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 自动播放下一个视频（支持多种播放模式）
        /// </summary>
        private void AutoPlayNext()
        {
            try
            {
                // 优先使用PlayQueueManager的播放模式逻辑
                if (_videoListViewModel != null && _videoListViewModel.PlayQueueManager != null)
                {
                    var playQueueManager = _videoListViewModel.PlayQueueManager;

                    // 检查是否有下一个视频
                    if (playQueueManager.HasNext)
                    {
                        var nextVideo = playQueueManager.GetNextVideo();
                        if (nextVideo != null)
                        {
                            // 更新播放队列状态
                            playQueueManager.PlayNext();

                            // 加载并播放下一个视频
                            LoadVideo(nextVideo.FilePath);
                            Play();

                            // 更新UI选中状态
                            _videoListViewModel.SelectedFile = nextVideo;

                            DebugLogger.LogSuccess($"自动播放下一个: {nextVideo.FileName}");
                            return;
                        }
                    }
                }

                // 如果没有PlayQueueManager或没有下一个视频，使用简单逻辑
                if (_playlist != null && _playlist.Count > 0 && _currentVideoIndex >= 0)
                {
                    int nextIndex;

                    // 根据循环模式决定下一个索引
                    if (_isLoopEnabled)
                    {
                        nextIndex = (_currentVideoIndex + 1) % _playlist.Count;
                        DebugLogger.LogSuccess($"循环播放: [{nextIndex + 1}/{_playlist.Count}] {_playlist[nextIndex].FileName}");
                    }
                    else
                    {
                        nextIndex = _currentVideoIndex + 1;
                        if (nextIndex >= _playlist.Count)
                        {
                            DebugLogger.LogInfo("播放列表已播放完毕");
                            Stop();
                            return;
                        }
                        DebugLogger.LogSuccess($"自动播放下一个: [{nextIndex + 1}/{_playlist.Count}] {_playlist[nextIndex].FileName}");
                    }

                    // 使用Task.Run避免阻塞UI
                    Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(100); // 短暂延迟,确保上一个视频完全结束
                        Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            try
                            {
                                LoadVideoByIndex(nextIndex);
                                Play();

                                // 更新UI选中状态
                                if (_playlist.Count > nextIndex)
                                {
                                    _videoListViewModel.SelectedFile = _playlist[nextIndex];
                                }
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.LogError($"自动播放下一个视频失败: {ex.Message}");
                            }
                        });
                    });
                }
                else
                {
                    DebugLogger.LogInfo("无播放列表,停止播放");
                    Stop();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"AutoPlayNext 错误: {ex.Message}");
                Stop();
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 格式化时间（毫秒 → HH:mm:ss.fff）
        /// </summary>
        private string FormatTime(long milliseconds)
        {
            if (milliseconds < 0) return "00:00:00.000";

            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            return timeSpan.ToString(@"hh\:mm\:ss\.fff");
        }

        /// <summary>
        /// 验证视频文件解码支持
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>验证结果</returns>
        public async Task<FormatValidationResult> ValidateVideoFormatSupport(string filePath)
        {
            var result = new FormatValidationResult
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                IsSupported = false,
                ErrorMessage = string.Empty
            };

            try
            {
                // 1. 基本文件检查
                if (!File.Exists(filePath))
                {
                    result.ErrorMessage = "文件不存在";
                    return result;
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length == 0)
                {
                    result.ErrorMessage = "文件大小为0";
                    return result;
                }

                result.FileSize = fileInfo.Length;

                // 2. 扩展名检查
                var extension = Path.GetExtension(filePath).ToLower();
                var supportedExtensions = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts" };
                if (!supportedExtensions.Contains(extension))
                {
                    result.ErrorMessage = $"不支持的文件格式: {extension}";
                    return result;
                }

                // 3. 初始化VLC（如果还没初始化）
                InitializeLibVLC();
                if (_libVLC == null || _mediaPlayer == null)
                {
                    result.ErrorMessage = "VLC播放器初始化失败";
                    return result;
                }

                // 4. 创建临时媒体对象进行格式验证
                using (var tempMedia = new Media(_libVLC, new Uri(filePath)))
                {
                    // 设置媒体解析选项
                    tempMedia.AddOption(":no-video"); // 不解码视频，只检查格式
                    tempMedia.AddOption(":no-audio"); // 不解码音频，只检查格式

                    // 解析媒体信息
                    var parseResult = await Task.Run(() =>
                    {
                        try
                        {
                            tempMedia.Parse();
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!parseResult)
                    {
                        result.ErrorMessage = "媒体解析失败";
                        return result;
                    }

                    // 等待解析完成
                    var timeout = 5000; // 5秒超时
                    var startTime = DateTime.Now;
                    while (tempMedia.State == VLCState.NothingSpecial && (DateTime.Now - startTime).TotalMilliseconds < timeout)
                    {
                        await Task.Delay(100);
                    }

                    // 检查解析结果
                    if (tempMedia.State == VLCState.Error)
                    {
                        result.ErrorMessage = "VLC无法识别文件格式";
                        return result;
                    }

                    // 获取媒体信息
                    result.Duration = tempMedia.Duration;
                    result.VideoCodec = GetCodecName(tempMedia, LibVLCSharp.Shared.TrackType.Video);
                    result.AudioCodec = GetCodecName(tempMedia, LibVLCSharp.Shared.TrackType.Audio);

                    // 检查是否有视频轨道
                    var tracks = tempMedia.Tracks;
                    var hasVideoTrack = tracks.Any(t => t.TrackType == LibVLCSharp.Shared.TrackType.Video);
                    var hasAudioTrack = tracks.Any(t => t.TrackType == LibVLCSharp.Shared.TrackType.Audio);

                    if (!hasVideoTrack && !hasAudioTrack)
                    {
                        result.ErrorMessage = "文件中不包含视频或音频轨道";
                        return result;
                    }

                    result.HasVideo = hasVideoTrack;
                    result.HasAudio = hasAudioTrack;

                    // 验证成功
                    result.IsSupported = true;
                    result.ErrorMessage = "格式验证通过";

                    DebugLogger.LogSuccess($"格式验证成功: {result.FileName} ({result.VideoCodec}/{result.AudioCodec})");
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"验证过程出错: {ex.Message}";
                DebugLogger.LogError($"格式验证失败: {filePath}, 错误: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取编解码器名称
        /// </summary>
        private string GetCodecName(Media media, LibVLCSharp.Shared.TrackType trackType)
        {
            try
            {
                var tracks = media.Tracks;
                var track = tracks.FirstOrDefault(t => t.TrackType == trackType);
                if (tracks.Contains(track))
                {
                    // 简化版：直接返回编解码器描述
                    return track.Codec.ToString("X8");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogWarning($"获取编解码器名称失败: {ex.Message}");
            }

            return trackType == LibVLCSharp.Shared.TrackType.Video ? "未知视频编解码器" : "未知音频编解码器";
        }

        /// <summary>
        /// 批量验证视频格式支持
        /// </summary>
        public async Task<List<FormatValidationResult>> ValidateVideoFormatsBatch(IEnumerable<string> filePaths)
        {
            var results = new List<FormatValidationResult>();

            foreach (var filePath in filePaths)
            {
                var result = await ValidateVideoFormatSupport(filePath);
                results.Add(result);

                // 更新进度
                DebugLogger.LogInfo($"格式验证进度: {results.Count}/{filePaths.Count()} - {result.FileName}: {(result.IsSupported ? "✓" : "✗")} {result.ErrorMessage}");
            }

            var supportedCount = results.Count(r => r.IsSupported);
            var totalCount = results.Count;

            DebugLogger.LogSuccess($"批量格式验证完成: {supportedCount}/{totalCount} 个文件支持播放");

            return results;
        }

        /// <summary>
        /// 获取支持的视频格式列表
        /// </summary>
        public string[] GetSupportedFormats()
        {
            return new[]
            {
                "MP4 (.mp4) - MPEG-4 Part 14",
                "AVI (.avi) - Audio Video Interleave",
                "MKV (.mkv) - Matroska Video",
                "MOV (.mov) - QuickTime Movie",
                "WMV (.wmv) - Windows Media Video",
                "FLV (.flv) - Flash Video",
                "WebM (.webm) - Web Media",
                "M4V (.m4v) - MPEG-4 Video",
                "MPG/MPEG (.mpg/.mpeg) - MPEG Video",
                "TS (.ts) - MPEG Transport Stream",
                "M2TS (.m2ts) - MPEG-2 Transport Stream"
            };
        }

        /// <summary>
        /// 获取支持的编解码器列表
        /// </summary>
        public string[] GetSupportedCodecs()
        {
            return new[]
            {
                "视频编解码器: H.264/AVC, H.265/HEVC, MPEG-4, MPEG-2, VP8, VP9, AV1, WMV",
                "音频编解码器: AAC, MP3, AC3, WMA, PCM, Vorbis"
            };
        }

        /// <summary>
        /// 解析时间字符串并跳转
        /// 支持格式: HH:mm:ss.fff 或 mm:ss.fff 或 ss.fff
        /// </summary>
        public bool ParseAndSeekToTime(string timeString, out string error)
        {
            error = string.Empty;
            
            if (string.IsNullOrWhiteSpace(timeString))
            {
                error = "时间不能为空";
                return false;
            }
            
            // 尝试解析时间
            if (TimeSpan.TryParse(timeString, out TimeSpan time))
            {
                long milliseconds = (long)time.TotalMilliseconds;
                
                if (milliseconds < 0)
                {
                    error = "时间不能为负数";
                    return false;
                }
                
                if (milliseconds > Duration)
                {
                    error = $"时间超出视频长度 ({FormattedDuration})";
                    return false;
                }
                
                Seek(milliseconds);
                Debug.WriteLine($"⏩ 跳转到用户输入的时间: {FormatTime(milliseconds)}");
                return true;
            }
            
            error = "时间格式错误,请使用 HH:mm:ss.fff 格式\n例如: 00:01:30.000 或 01:30.000 或 90.000";
            return false;
        }

        /// <summary>
        /// 恢复上次保存的音量设置
        /// </summary>
        private void RestoreVolumeSettings()
        {
            try
            {
                float lastVolume = Properties.Settings.Default.LastVolume;
                bool lastMuted = Properties.Settings.Default.LastMuted;
                
                // 直接设置私有字段,避免触发 Save
                _volume = Math.Clamp(lastVolume, 0f, 100f);
                _isMuted = lastMuted;
                
                DebugLogger.LogInfo($"恢复音量设置: 音量={_volume}, 静音={_isMuted}");
                Debug.WriteLine($"恢复音量设置: 音量={_volume}, 静音={_isMuted}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogWarning($"恢复音量设置失败: {ex.Message}");
                Debug.WriteLine($"恢复音量设置失败: {ex.Message}");
                
                // 使用默认值
                _volume = 50f;
                _isMuted = false;
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            DebugLogger.LogInfo("正在释放 VideoPlayerViewModel 资源...");
            Debug.WriteLine("正在释放 VideoPlayerViewModel 资源...");
            
            try
            {
                // 1. 停止并释放统一播放定时器
                try
                {
                    if (_playbackTimer != null)
                    {
                        _playbackTimer.Stop();
                        _playbackTimer.Elapsed -= OnPlaybackTimerTick;
                        _playbackTimer.Dispose();
                        _playbackTimer = null;
                        DebugLogger.Log("播放定时器已释放");
                    }
                }
                catch (Exception timerEx)
                {
                    DebugLogger.LogWarning($"释放播放定时器时出错: {timerEx.Message}");
                }
                
                // 3. 先停止播放 (在取消事件订阅之前)
                if (_mediaPlayer != null)
                {
                    try
                    {
                        if (_mediaPlayer.IsPlaying)
                        {
                            DebugLogger.Log("停止正在播放的视频");
                            _mediaPlayer.Stop();
                            // 等待播放器完全停止
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch (Exception stopEx)
                    {
                        DebugLogger.LogWarning($"停止播放时出错: {stopEx.Message}");
                    }
                }
                
                // 3. 取消 MediaPlayer 事件订阅
                if (_mediaPlayer != null)
                {
                    DebugLogger.Log("取消 MediaPlayer 事件订阅");
                    try
                    {
                        _mediaPlayer.LengthChanged -= OnLengthChanged;
                        _mediaPlayer.Playing -= OnPlaying;
                        _mediaPlayer.Paused -= OnPaused;
                        _mediaPlayer.Stopped -= OnStopped;
                        _mediaPlayer.EndReached -= OnEndReached;
                        DebugLogger.LogSuccess("事件订阅已取消");
                    }
                    catch (Exception unsubEx)
                    {
                        DebugLogger.LogWarning($"取消事件订阅时出错: {unsubEx.Message}");
                    }
                }
                
                // 4. 释放媒体资源
                DebugLogger.Log("释放媒体资源");
                try
                {
                    _currentMedia?.Dispose();
                    _currentMedia = null;
                }
                catch (Exception mediaEx)
                {
                    DebugLogger.LogWarning($"释放媒体时出错: {mediaEx.Message}");
                }
                
                // 5. 释放播放器
                DebugLogger.Log("释放 MediaPlayer");
                try
                {
                    _mediaPlayer?.Dispose();
                    _mediaPlayer = null;
                }
                catch (Exception playerEx)
                {
                    DebugLogger.LogWarning($"释放播放器时出错: {playerEx.Message}");
                }
                
                // 6. 释放 LibVLC
                DebugLogger.Log("释放 LibVLC");
                try
                {
                    _libVLC?.Dispose();
                    _libVLC = null;
                }
                catch (Exception vlcEx)
                {
                    DebugLogger.LogWarning($"释放LibVLC时出错: {vlcEx.Message}");
                }
                
                DebugLogger.LogSuccess("✅ VideoPlayerViewModel 资源已完全释放");
                Debug.WriteLine("✅ VideoPlayerViewModel 资源已释放");
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"释放资源时发生错误: {ex.Message}\n{ex.StackTrace}");
                Debug.WriteLine($"❌ 释放资源时发生错误: {ex.Message}");
            }
        }

        #endregion
    }
}

