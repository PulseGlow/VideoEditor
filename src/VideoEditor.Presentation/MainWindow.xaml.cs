using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VideoEditor.Presentation.ViewModels;
using VideoEditor.Presentation.Models;
using VideoEditor.Presentation.Services;
using VideoEditor.Presentation.Services.AiSubtitle;
using Forms = System.Windows.Forms;

namespace VideoEditor.Presentation
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private VideoListViewModel _videoListViewModel;
        private VideoPlayerViewModel _videoPlayerViewModel;
        private bool _isMediaInfoAnalyzing;
        private ScreenRecordingSession? _screenRecordingSession;
        private bool _isAiSubtitleGenerating;
        private CancellationTokenSource? _aiSubtitleCts;
        private readonly HttpClient _httpClient = new();
        private Services.AiSubtitle.AiSubtitleService? _aiSubtitleService;
        private Services.AiSubtitle.BcutAsrService? _bcutAsrService;
        private Services.AiSubtitle.JianYingAsrService? _jianYingAsrService;
        private Services.AiSubtitle.BatchAiSubtitleCoordinator? _batchSubtitleCoordinator;
        private readonly Services.UpdateCheckerService _updateCheckerService = new();
        private readonly ObservableCollection<TaskProgressItem> _taskProgressItems = new();
        private TaskProgressItem? _currentScreenRecordingTask;
        private TaskProgressItem? _currentAiSubtitleTask;
        private List<AiSubtitleProviderProfile> _aiSubtitleProviders = new();
        private const int MaxRecentProjects = 8;
        private readonly ObservableCollection<RecentProjectItem> _recentProjects = new();
        public ObservableCollection<RecentProjectItem> RecentProjects => _recentProjects;
        private const string ProjectFileFilter = "VideoEditor 项目 (*.veproj)|*.veproj|所有文件|*.*";
        private const string ProjectFileExtension = ".veproj";
        private const string BaseWindowTitle = "视频编辑器 - 6 区域布局";
        private readonly JsonSerializerOptions _projectJsonOptions = CreateProjectJsonOptions();
        private string? _currentProjectFilePath;
        private bool _isLoadingProject;

        // 视图模式控制：0=播放器模式，1=图片模式，2=命令提示符模式
        private int _viewMode = 0; // 0=播放器，1=图片，2=命令提示符

        // 为XAML绑定暴露的属性
        public VideoListViewModel VideoListViewModel => _videoListViewModel;
        public VideoPlayerViewModel VideoPlayerViewModel => _videoPlayerViewModel;
        public ClipManager ClipManager => _clipManager;
        public ObservableCollection<MergeItem> MergeItems => _mergeItems;
        public Services.AiSubtitle.BatchAiSubtitleCoordinator? BatchSubtitleCoordinator => _batchSubtitleCoordinator;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public bool IsPlayerMode
        {
            get => _viewMode == 0;
        }

        public bool IsImageMode
        {
            get => _viewMode == 1;
        }

        public bool IsCommandPromptMode
        {
            get => _viewMode == 2;
        }

        private enum InterfaceDisplayMode
        {
            PlayerOnly,
            Editor
        }

        private InterfaceDisplayMode _interfaceMode = InterfaceDisplayMode.Editor;

        private GridLength _cachedLeftColumnWidth = new GridLength(280);
        private GridLength _cachedLeftSplitterWidth = new GridLength(5);
        private GridLength _cachedRightColumnWidth = new GridLength(280);
        private GridLength _cachedRightSplitterWidth = new GridLength(5);
        private GridLength _cachedBottomRowHeight = new GridLength(1, GridUnitType.Star);
        private GridLength _cachedMiddleSplitterHeight = new GridLength(5);
        private GridLength _cachedBottomStatusRowHeight = GridLength.Auto;
        private bool _interfaceLayoutCached;

        private const double PlayerModeViewportWidth = 854d;
        private const double PlayerModeViewportHeight = 481d;
        private const double PlayerModeControlsHeight = 65d;
        private const double PlayerModeWindowEdgePadding = 2d;
        private double PlayerModeWindowWidth => PlayerModeViewportWidth + PlayerModeWindowEdgePadding * 2;
        private double PlayerModeWindowHeight => PlayerModeViewportHeight + PlayerModeControlsHeight + PlayerModeWindowEdgePadding;
        private double? _playerModeLastWindowWidth;
        private double? _playerModeLastWindowHeight;
        private bool _isApplyingPlayerModeWindowSize;
        private bool _isRestoringPlayerModeSize;
        private double? _playerModeWindowWidthBeforeFullScreen;
        private double? _playerModeWindowHeightBeforeFullScreen;

        private enum LayoutPreset
        {
            Standard,
            Compact
        }

        private ApplicationTheme _currentTheme = ApplicationTheme.Dark;
        private LayoutPreset _currentLayoutPreset = LayoutPreset.Standard;

        private bool _isCommandPreviewVisible = true;
        private bool _isExecutionLogVisible = true;

        private GridLength _rightColumnDefaultWidth = new GridLength(280);
        private GridLength _rightSplitterDefaultWidth = new GridLength(5);

        private bool _windowSizeCached;
        private double _cachedWindowWidth;
        private double _cachedWindowHeight;
        private SizeToContent _cachedSizeToContent = SizeToContent.Manual;

        public bool IsEditorInterfaceMode => _interfaceMode == InterfaceDisplayMode.Editor;
        public bool IsPlayerInterfaceMode => _interfaceMode == InterfaceDisplayMode.PlayerOnly;

        private void SetViewMode(int mode)
            {
            if (_viewMode != mode)
                {
                if (_isFlipPreviewActive && mode != 1)
                {
                    ResetFlipPreviewState();
                }

                _viewMode = mode;
                    OnPropertyChanged(nameof(IsPlayerMode));
                OnPropertyChanged(nameof(IsImageMode));
                OnPropertyChanged(nameof(IsCommandPromptMode));
                UpdateViewModeButton();
            }
        }

        private void SetInterfaceDisplayMode(InterfaceDisplayMode mode)
        {
            if (_interfaceMode == mode)
            {
                return;
            }

            if (_interfaceMode == InterfaceDisplayMode.Editor && mode == InterfaceDisplayMode.PlayerOnly)
            {
                CacheInterfaceLayoutSizes();
                CacheWindowSize();
            }

            _interfaceMode = mode;
            OnPropertyChanged(nameof(IsEditorInterfaceMode));
            OnPropertyChanged(nameof(IsPlayerInterfaceMode));

            ApplyInterfaceModeLayout();
            UpdatePlayerModeWindowSizing();
            UpdatePlayerLayoutForMode();
            ApplyOutputPanelVisibility();
        }

        private void CacheInterfaceLayoutSizes()
        {
            if (LeftColumn == null || RightColumn == null || LeftSplitter == null || RightSplitter == null || BottomRow == null || MiddleSplitter == null || BottomStatusRow == null)
            {
                return;
            }

            _cachedLeftColumnWidth = LeftColumn.Width;
            _cachedLeftSplitterWidth = LeftSplitter.Width;
            _cachedRightColumnWidth = RightColumn.Width;
            _cachedRightSplitterWidth = RightSplitter.Width;
            _cachedBottomRowHeight = BottomRow.Height;
            _cachedMiddleSplitterHeight = MiddleSplitter.Height;
            _cachedBottomStatusRowHeight = BottomStatusRow.Height;
            _interfaceLayoutCached = true;
        }

        private void RestoreInterfaceLayoutSizes()
        {
            if (!_interfaceLayoutCached)
            {
                _cachedLeftColumnWidth = new GridLength(280);
                _cachedLeftSplitterWidth = new GridLength(5);
                _cachedRightColumnWidth = new GridLength(280);
                _cachedRightSplitterWidth = new GridLength(5);
                _cachedBottomRowHeight = new GridLength(1, GridUnitType.Star);
                _cachedMiddleSplitterHeight = new GridLength(5);
                _cachedBottomStatusRowHeight = GridLength.Auto;
            }

            LeftColumn.Width = _cachedLeftColumnWidth;
            LeftSplitter.Width = _cachedLeftSplitterWidth;
            RightColumn.Width = _cachedRightColumnWidth;
            RightSplitter.Width = _cachedRightSplitterWidth;
            BottomRow.Height = _cachedBottomRowHeight;
            MiddleSplitter.Height = _cachedMiddleSplitterHeight;
            BottomStatusRow.Height = _cachedBottomStatusRowHeight;
        }

        private void ApplyInterfaceModeLayout()
        {
            if (LeftColumn == null || RightColumn == null || LeftSplitter == null || RightSplitter == null || BottomRow == null || MiddleSplitter == null || BottomStatusRow == null)
            {
                return;
            }

            if (_interfaceMode == InterfaceDisplayMode.PlayerOnly)
            {
                LeftColumn.Width = new GridLength(0);
                LeftSplitter.Width = new GridLength(0);
                RightColumn.Width = new GridLength(0);
                RightSplitter.Width = new GridLength(0);
                BottomRow.Height = new GridLength(0);
                MiddleSplitter.Height = new GridLength(0);
                BottomStatusRow.Height = new GridLength(0);
                if (PlayerContentSplitter != null)
                {
                    PlayerContentSplitter.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                RestoreInterfaceLayoutSizes();
                if (PlayerContentSplitter != null)
                {
                    PlayerContentSplitter.Visibility = Visibility.Visible;
                }
            }
        }

        private void CacheWindowSize()
        {
            if (!IsLoaded)
            {
                return;
            }

            _cachedWindowWidth = Width;
            _cachedWindowHeight = Height;

            if (!_windowSizeCached)
            {
                _cachedSizeToContent = SizeToContent;
                _windowSizeCached = true;
            }
        }

        private void RestoreWindowSize()
        {
            if (!_windowSizeCached)
            {
                return;
            }

            SizeToContent = _cachedSizeToContent;
            Width = Math.Max(_cachedWindowWidth, 1200);
            Height = Math.Max(_cachedWindowHeight, 800);
        }

        private void UpdatePlayerModeWindowSizing()
        {
            if (!IsLoaded || _isFullScreen)
            {
                Services.DebugLogger.LogInfo($"[PlayerModeSize] Update skipped: isLoaded={IsLoaded}, isFullScreen={_isFullScreen}");
                return;
            }

            if (_interfaceMode == InterfaceDisplayMode.PlayerOnly)
            {
                var targetWidth = Math.Max(_playerModeLastWindowWidth ?? PlayerModeWindowWidth, PlayerModeWindowWidth);
                var targetHeight = Math.Max(_playerModeLastWindowHeight ?? PlayerModeWindowHeight, PlayerModeWindowHeight);
                Services.DebugLogger.LogInfo($"[PlayerModeSize] Applying player mode size -> targetWidth={targetWidth:F0}, targetHeight={targetHeight:F0}, lastStored=({_playerModeLastWindowWidth?.ToString("F0") ?? "null"}, {_playerModeLastWindowHeight?.ToString("F0") ?? "null"})");

                _isApplyingPlayerModeWindowSize = true;
                try
                {
                    SizeToContent = SizeToContent.Manual;
                    Width = targetWidth;
                    Height = targetHeight;
                    MinWidth = targetWidth;
                    MinHeight = targetHeight;
                }
                finally
                {
                    _isApplyingPlayerModeWindowSize = false;
                }

                _playerModeLastWindowWidth = targetWidth;
                _playerModeLastWindowHeight = targetHeight;
            }
            else
            {
                RestoreWindowSize();
                MinWidth = 1200;
                MinHeight = 800;
                Services.DebugLogger.LogInfo("[PlayerModeSize] Restored editor mode minimum window size");
            }
        }

        private void UpdatePlayerLayoutForMode()
        {
            if (PlayerContainer == null)
            {
                return;
            }

            if (_interfaceMode == InterfaceDisplayMode.PlayerOnly)
            {
                PlayerContainer.Width = PlayerModeViewportWidth;
                PlayerContainer.Height = PlayerModeViewportHeight;
            }
            else
            {
                PlayerContainer.Width = 1920;
                PlayerContainer.Height = 1080;
            }
        }

        private void ApplyOutputPanelVisibility()
        {
            if (!IsLoaded)
            {
                return;
            }

            if (_interfaceMode != InterfaceDisplayMode.Editor)
            {
                if (OutputInfoContainer != null)
                {
                    OutputInfoContainer.Visibility = Visibility.Collapsed;
                }

                if (RightColumn != null)
                {
                    RightColumn.Width = new GridLength(0);
                }

                if (RightSplitter != null)
                {
                    RightSplitter.Width = new GridLength(0);
                }

                return;
            }

            if (ExecutionLogTabItem != null)
            {
                ExecutionLogTabItem.Visibility = _isExecutionLogVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CommandPreviewTabItem != null)
            {
                CommandPreviewTabItem.Visibility = _isCommandPreviewVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            var outputVisible = _isExecutionLogVisible || _isCommandPreviewVisible;

            if (RightColumn != null)
            {
                RightColumn.Width = outputVisible ? _rightColumnDefaultWidth : new GridLength(0);
            }

            if (RightSplitter != null)
            {
                RightSplitter.Width = outputVisible ? _rightSplitterDefaultWidth : new GridLength(0);
            }

            if (OutputInfoContainer != null)
            {
                OutputInfoContainer.Visibility = outputVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateLayoutMenuChecks()
        {
            if (MenuLayoutStandard != null)
            {
                MenuLayoutStandard.IsChecked = _currentLayoutPreset == LayoutPreset.Standard;
            }

            if (MenuLayoutCompact != null)
            {
                MenuLayoutCompact.IsChecked = _currentLayoutPreset == LayoutPreset.Compact;
            }
        }

        private void ApplyLayoutPreset(LayoutPreset preset)
        {
            _currentLayoutPreset = preset;

            if (LeftColumn != null)
            {
                LeftColumn.Width = preset == LayoutPreset.Standard ? new GridLength(280) : new GridLength(220);
            }

            if (preset == LayoutPreset.Standard)
            {
                _rightColumnDefaultWidth = new GridLength(280);
                _rightSplitterDefaultWidth = new GridLength(5);
                _isCommandPreviewVisible = true;
                _isExecutionLogVisible = true;
            }
            else
            {
                _rightColumnDefaultWidth = new GridLength(220);
                _rightSplitterDefaultWidth = new GridLength(4);
                _isCommandPreviewVisible = false;
                _isExecutionLogVisible = false;
            }

            UpdateLayoutMenuChecks();
            ApplyOutputPanelVisibility();
        }

        private void SetTheme(ApplicationTheme mode)
        {
            _currentTheme = mode;

            if (MenuThemeLight != null)
            {
                MenuThemeLight.IsChecked = mode == ApplicationTheme.Light;
            }

            if (MenuThemeDark != null)
            {
                MenuThemeDark.IsChecked = mode == ApplicationTheme.Dark;
            }

            ThemeManager.ApplyTheme(mode);
        }
        private Services.VideoProcessingService _videoProcessingService;
        private Services.VideoInformationService _videoInformationService;
        private Services.CropHistoryService _cropHistoryService;
        private Services.DplPlaylistService _dplPlaylistService;
        private ClipManager _clipManager;
        private CancellationTokenSource? _cropCancellationTokenSource;
        private CancellationTokenSource? _clipCutCancellationTokenSource;
        private CancellationTokenSource? _watermarkCancellationTokenSource;
        private CancellationTokenSource? _deduplicateCancellationTokenSource;
        private CancellationTokenSource? _transcodeCancellationTokenSource;
        private CancellationTokenSource? _flipCancellationTokenSource;
        private CancellationTokenSource? _filterCancellationTokenSource;
        
        // 翻转参数及预览状态
        private Models.FlipParameters _currentFlipParameters = new Models.FlipParameters();
        private readonly string _flipPreviewTempDir = Path.Combine(Path.GetTempPath(), "VideoEditor_FlipPreview");
        private string? _flipPreviewBaseImagePath;
        private string? _flipPreviewProcessedImagePath;
        private bool _isFlipPreviewActive;
        private bool _isFlipPreviewBusy;
        
        // 滤镜参数
        private Models.FilterParameters _currentFilterParameters = Models.FilterParameters.CreateDefault();
        private bool _isFilterInitializing;
        private bool _filterPreviewPending;
        private Services.FfmpegBatchProcessor _ffmpegBatchProcessor;
        private Services.FfmpegCommandPreviewService _ffmpegCommandPreviewService;
        private Services.FFmpegCommandHelpService _ffmpegCommandHelpService;
        private bool _isSinglePlayMode; // 是否为单曲播放模式（播放完后不自动播放下一首）

        // 合并列表
        private ObservableCollection<MergeItem> _mergeItems = new();

        private static JsonSerializerOptions CreateProjectJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
        private CancellationTokenSource? _mergeCancellationTokenSource;
        
        // 拖动框选相关变量
        private bool _isSelecting = false;
        private Point _selectionStart;
        private Point _selectionEnd;
        
        // 全屏状态
        private bool _isFullScreen = false;
        private WindowState _previousWindowState;
        private WindowStyle _previousWindowStyle;
        private ResizeMode _previousResizeMode;
        

        public MainWindow()
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Services.DebugLogger.LogInfo("开始创建 MainWindow...");
                
                // 初始化ViewModels (在InitializeComponent之前)
                Services.DebugLogger.LogInfo("创建 VideoInformationService...");
                _videoInformationService = new Services.VideoInformationService();

                Services.DebugLogger.LogInfo("创建 DplPlaylistService...");
                _dplPlaylistService = new Services.DplPlaylistService();

                Services.DebugLogger.LogInfo("创建 VideoListViewModel...");
                _videoListViewModel = new VideoListViewModel(_videoInformationService);
                
                Services.DebugLogger.LogInfo("创建 VideoPlayerViewModel...");
                _videoPlayerViewModel = new VideoPlayerViewModel();

                Services.DebugLogger.LogInfo("创建 VideoProcessingService...");
                _videoProcessingService = new Services.VideoProcessingService();

                Services.DebugLogger.LogInfo("创建 CropHistoryService...");
                _cropHistoryService = new Services.CropHistoryService();

                Services.DebugLogger.LogInfo("创建 ClipManager...");
                _clipManager = new ClipManager();
                
                LoadRecentProjectsFromSettings();
                
                Services.DebugLogger.LogInfo("调用 InitializeComponent...");
                InitializeComponent();

                if (TaskProgressListView != null)
                {
                    TaskProgressListView.ItemsSource = _taskProgressItems;
                }
                LoadAiSubtitleProviders();
                
                // 初始化批量处理和命令预览服务
                Services.DebugLogger.LogInfo("创建 FfmpegBatchProcessor...");
                _ffmpegBatchProcessor = new Services.FfmpegBatchProcessor(Dispatcher);
                
                Services.DebugLogger.LogInfo("创建 FfmpegCommandPreviewService...");
                _ffmpegCommandPreviewService = new Services.FfmpegCommandPreviewService();
                Services.DebugLogger.LogInfo("创建 FFmpegCommandHelpService...");
                _ffmpegCommandHelpService = new Services.FFmpegCommandHelpService();
                
                // 监听片段列表变化和选中状态变化，更新按钮状态
                _clipManager.Clips.CollectionChanged += (s, e) =>
                {
                    UpdateMoveButtonsState();
                    // 为新添加的片段添加PropertyChanged监听
                    if (e.NewItems != null)
                    {
                        foreach (VideoClip clip in e.NewItems)
                        {
                            clip.PropertyChanged += (sender, args) =>
                            {
                                if (args.PropertyName == nameof(VideoClip.IsSelected))
                                {
                                    UpdateMoveButtonsState();
                                }
                            };
                        }
                    }
                };
                
                // 为现有片段添加PropertyChanged监听
                foreach (var clip in _clipManager.Clips)
                {
                    clip.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(VideoClip.IsSelected))
                        {
                            UpdateMoveButtonsState();
                        }
                    };
                }
                
                // 初始化按钮状态
                UpdateMoveButtonsState();
                
                Services.DebugLogger.LogInfo("设置 DataContext...");
                // 设置DataContext
                DataContext = this;

                // 默认应用剪辑模式布局
                ApplyInterfaceModeLayout();

                Services.DebugLogger.LogInfo("自动搜索 FFmpeg 路径...");
                // 自动搜索并设置FFmpeg路径（已优化：优先检查已知位置，限制递归深度）
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AutoFindFFmpegPath();
                sw.Stop();
                Services.DebugLogger.LogInfo($"FFmpeg 路径搜索耗时: {sw.ElapsedMilliseconds}ms");
                
                Services.DebugLogger.LogInfo("初始化 LibVLC...");
                // 初始化LibVLC
                _videoPlayerViewModel.InitializeLibVLC();
                
                // 设置播放列表
                Services.DebugLogger.LogInfo("设置播放列表...");
                _videoPlayerViewModel.SetPlaylist(_videoListViewModel.Files, _videoListViewModel);

                // 初始化播放模式为单曲播放
                Services.DebugLogger.LogInfo("初始化播放模式...");
                _videoListViewModel.PlayQueueManager.CurrentMode = PlayMode.Sequential;
                _videoPlayerViewModel.IsSinglePlayMode = true; // 默认单曲播放模式
                _isSinglePlayMode = true;
                _videoPlayerViewModel.IsLoopEnabled = false; // 单曲播放不循环
                UpdatePlayModeMenuDisplay("Single");

                Services.DebugLogger.LogInfo("订阅事件...");
                // 订阅键盘事件
                this.PreviewKeyDown += MainWindow_PreviewKeyDown;

                // 监听窗口尺寸变化，用于记录播放器模式的用户尺寸
                this.SizeChanged += MainWindow_SizeChanged;

                // 订阅窗口激活状态事件（用于控制裁剪框显示）
                this.Activated += MainWindow_Activated;
                this.Deactivated += MainWindow_Deactivated;
                this.LocationChanged += MainWindow_LocationChanged;

                // 订阅ViewModel的PropertyChanged事件，用于更新状态栏
                _videoListViewModel.PropertyChanged += VideoListViewModel_PropertyChanged;
                _videoPlayerViewModel.PropertyChanged += VideoPlayerViewModel_PropertyChanged;

                // 初始化视图模式按钮
                UpdateViewModeButton();

                // 初始化字幕样式监听器
                SetupSubtitleStyleListeners();

                // 初始化系统监控
                InitializeSystemMonitoring();
                InitializeFilterControls();
                UpdateMergeSummary();
                UpdateMergeCommandPreview();

                UpdateWindowTitleWithProject();

                // 初始化 AI 字幕服务
                InitializeAiSubtitleService();

                // 初始化批量AI字幕协调器
                Services.DebugLogger.LogInfo("创建 BatchAiSubtitleCoordinator...");
                // 获取FFmpeg路径
                string? ffmpegPath = null;
                if (EmbeddedFFmpegPathTextBox != null && !string.IsNullOrWhiteSpace(EmbeddedFFmpegPathTextBox.Text))
                {
                    ffmpegPath = EmbeddedFFmpegPathTextBox.Text.Trim();
                }
                else
                {
                    // 尝试从VideoProcessingService获取
                    ffmpegPath = _videoProcessingService?.GetFFmpegPath();
                }
                
                _batchSubtitleCoordinator = new Services.AiSubtitle.BatchAiSubtitleCoordinator(
                    _videoListViewModel,
                    _clipManager,
                    _httpClient,
                    ffmpegPath);
                _batchSubtitleCoordinator.ProgressUpdated += BatchSubtitleCoordinator_ProgressUpdated;
                _batchSubtitleCoordinator.BatchCompleted += BatchSubtitleCoordinator_BatchCompleted;
                OnPropertyChanged(nameof(BatchSubtitleCoordinator));

                // 订阅进度条事件 (暂时移除复杂的拖动检测)
                // ProgressSlider的点击和拖动都通过IsMoveToPointEnabled + 双向绑定自动处理

                totalSw.Stop();
                Services.DebugLogger.LogSuccess($"MainWindow 创建完成! 总耗时: {totalSw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                totalSw.Stop();
                Services.DebugLogger.LogError($"MainWindow 构造函数异常: {ex.Message}\n{ex.StackTrace}");
                Services.DebugLogger.LogError($"异常发生前耗时: {totalSw.ElapsedMilliseconds}ms");
                throw;
            }
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Services.DebugLogger.LogSuccess("MainWindow Loaded 事件触发!");
            
            // 确保窗口可见并激活
            this.Show();
            this.Activate();
            this.Focus();
            this.WindowState = WindowState.Normal;
            
            Services.DebugLogger.LogInfo($"窗口状态: WindowState={this.WindowState}, IsVisible={this.IsVisible}, IsActive={this.IsActive}");
            
            if (RightColumn != null)
            {
                _rightColumnDefaultWidth = RightColumn.Width;
            }

            if (RightSplitter != null)
            {
                _rightSplitterDefaultWidth = RightSplitter.Width;
            }

            CacheWindowSize();
            UpdatePlayerModeWindowSizing();
            UpdatePlayerLayoutForMode();
            UpdateLayoutMenuChecks();
            ApplyOutputPanelVisibility();
            SetTheme(_currentTheme);

            // 初始化状态栏显示
            UpdateStatusBarFileCount();
            UpdateStatusBarDuration();

            // 初始化当前任务状态为空闲
            if (StatusCurrentTask != null)
            {
                StatusCurrentTask.Text = "空闲";
            }

            // 进度条拖拽优化
            ProgressSlider.PreviewMouseLeftButtonDown += ProgressSlider_PreviewMouseDown;
            ProgressSlider.PreviewMouseLeftButtonUp += ProgressSlider_PreviewMouseUp;

            UpdateScreenRecorderMenuState();

            // 处理启动参数（命令行）
            try
            {
                ProcessStartupArguments(App.StartupArgs);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"处理启动参数时发生错误: {ex.Message}");
            }
        }

        private void ProcessStartupArguments(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return;
            }

            var mediaPaths = new List<string>();
            bool switchToPlayerMode = false;
            bool switchToEditorMode = false;
            bool showFileDialog = false;

            foreach (var rawArg in args)
            {
                if (string.IsNullOrWhiteSpace(rawArg))
                {
                    continue;
                }

                var arg = rawArg.Trim();
                if (arg.StartsWith("/") || arg.StartsWith("-"))
                {
                    var option = arg.TrimStart('/', '-').ToLowerInvariant();
                    switch (option)
                    {
                        case "filedlg":
                            showFileDialog = true;
                            break;
                        case "player":
                            switchToPlayerMode = true;
                            break;
                        case "editor":
                            switchToEditorMode = true;
                            break;
                        default:
                            // 其它参数暂未实现，保留扩展空间
                            break;
                    }
                }
                else
                {
                    mediaPaths.Add(arg);
                }
            }

            if (switchToPlayerMode && !switchToEditorMode)
            {
                SetInterfaceDisplayMode(InterfaceDisplayMode.PlayerOnly);
            }
            else if (switchToEditorMode)
            {
                SetInterfaceDisplayMode(InterfaceDisplayMode.Editor);
            }

            if (mediaPaths.Count > 0)
            {
                // 支持相对路径
                var normalizedPaths = mediaPaths.Select(path =>
                {
                    try
                    {
                        return System.IO.Path.GetFullPath(path);
                    }
                    catch
                    {
                        return path;
                    }
                }).ToArray();

                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await LoadStartupMediaAsync(normalizedPaths);
                });
            }
            else if (showFileDialog)
            {
                // 没有直接指定文件时，显示“打开文件”对话框
                AddFilesButton_Click(this, new RoutedEventArgs());
            }
        }

        private async Task LoadStartupMediaAsync(string[] normalizedPaths)
        {
            try
            {
                await _videoListViewModel.AddFilesAsync(normalizedPaths);

                var targetFile = FindFirstLoadedMedia(normalizedPaths);
                if (targetFile == null)
                {
                    targetFile = _videoListViewModel.Files.FirstOrDefault();
                }

                if (targetFile != null)
                {
                    _videoListViewModel.SelectedFile = targetFile;
                    PreparePlaybackViewState(isPlayRequested: true);
                    _videoPlayerViewModel.LoadVideo(targetFile.FilePath);
                    _videoPlayerViewModel.Play();
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"加载启动媒体失败: {ex.Message}");
            }
        }

        private Models.VideoFile? FindFirstLoadedMedia(string[] targetPaths)
        {
            if (targetPaths == null || targetPaths.Length == 0)
            {
                return null;
            }

            foreach (var path in targetPaths)
            {
                var match = _videoListViewModel.Files.FirstOrDefault(f =>
                    f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private void ProgressSlider_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 更新进度条宽度供ViewModel计算入出点位置
            _videoPlayerViewModel.ProgressBarWidth = e.NewSize.Width;
        }

        private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _videoPlayerViewModel.BeginSeek();
        }

        private void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            var targetPosition = (long)ProgressSlider.Value;
            _videoPlayerViewModel.EndSeek(targetPosition);
        }

        private void TimeDisplay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_videoPlayerViewModel.HasVideo) return;

            // 显示输入框,隐藏显示
            TimeDisplayBorder.Visibility = Visibility.Collapsed;
            TimeInputBorder.Visibility = Visibility.Visible;

            // 设置当前时间到输入框（不全选）
            TimeInputBox.Text = _videoPlayerViewModel.FormattedCurrentTime;
            TimeInputBox.Focus();
            // 移除SelectAll()，不全选文字

            // 根据当前播放状态决定是否暂停
            // 如果正在播放，点击标签时暂停；如果是暂停状态，保持暂停
            if (_videoPlayerViewModel.IsPlaying)
            {
                _videoPlayerViewModel.Pause();
            }
            // 如果已经是暂停状态，不需要额外调用Pause()
        }

        private void TimeInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyTimeInput();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTimeInput();
                e.Handled = true;
            }
        }

        private void TimeInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CancelTimeInput();
        }

        private void TimeInputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // 只允许数字、冒号、点号
            if (!System.Text.RegularExpressions.Regex.IsMatch(e.Text, @"[0-9:.]"))
            {
                e.Handled = true;
            }
        }

        private void ApplyTimeInput()
        {
            try
            {
                var input = TimeInputBox.Text.Trim();
                
                // 尝试解析时间格式 HH:mm:ss.fff
                if (TimeSpan.TryParseExact(input, @"hh\:mm\:ss\.fff", null, out var timeSpan))
                {
                    var timeMs = (long)timeSpan.TotalMilliseconds;
                    
                    // 限制在视频时长范围内
                    if (timeMs > _videoPlayerViewModel.Duration)
                    {
                        timeMs = _videoPlayerViewModel.Duration;
                    }
                    
                    _videoPlayerViewModel.Seek(timeMs);

                    // 根据当前播放状态决定是否暂停
                    // 如果正在播放，跳转后暂停；如果是暂停状态，保持暂停
                    if (_videoPlayerViewModel.IsPlaying)
                    {
                        _videoPlayerViewModel.Pause();
                    }
                    // 如果已经是暂停状态，不需要额外调用Pause()

                    Services.DebugLogger.LogInfo($"手动跳转到: {timeSpan:hh\\:mm\\:ss\\.fff}");
                    Services.ToastNotification.ShowSuccess($"已跳转到: {timeSpan:hh\\:mm\\:ss\\.fff}");
                }
                else
                {
                    Services.ToastNotification.ShowWarning("时间格式错误,请使用 HH:mm:ss.fff");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"时间输入解析失败: {ex.Message}");
                Services.ToastNotification.ShowError("时间格式错误");
            }
            finally
            {
                // 隐藏输入框,显示时间
                TimeInputBorder.Visibility = Visibility.Collapsed;
                TimeDisplayBorder.Visibility = Visibility.Visible;
            }
        }

        private void CancelTimeInput()
        {
            // 隐藏输入框,显示时间
            TimeInputBorder.Visibility = Visibility.Collapsed;
            TimeDisplayBorder.Visibility = Visibility.Visible;
        }

        private void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            Services.DebugLogger.LogSuccess("MainWindow ContentRendered 事件触发!");
        }

        protected override void OnClosed(EventArgs e)
        {
            Services.DebugLogger.LogInfo("MainWindow OnClosed 事件触发");

            // 停止字幕预览定时器
            StopSubtitlePreviewTimer();
            
            // 隐藏字幕预览Popup
            HideSubtitlePreviewPopup();

            // 取消任何正在进行的裁剪操作
            _cropCancellationTokenSource?.Cancel();
            _cropCancellationTokenSource?.Dispose();

            base.OnClosed(e);
            
            // 清理裁剪框相关资源 - Popup版本
            CropOverlayPopup.IsOpen = false;

            // 清理其他资源
            _videoPlayerViewModel?.Dispose();
        }

        #region 工具栏按钮事件

        /// <summary>
        /// 添加文件按钮点击事件
        /// </summary>
        private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择媒体文件",
                Filter = VideoListViewModel.MediaFileDialogFilter,
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                await _videoListViewModel.AddFilesAsync(openFileDialog.FileNames);
            }
        }

        /// <summary>
        /// 添加到片段队列按钮点击事件
        /// </summary>
        private void AddToQueueButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentVideo = _videoListViewModel.CurrentPlayingVideo;
                if (currentVideo == null || !_videoPlayerViewModel.HasVideo)
                {
                    Services.ToastNotification.ShowWarning("请先加载并播放一个视频文件");
                    return;
                }

                if (string.IsNullOrWhiteSpace(currentVideo.FilePath))
                {
                    Services.ToastNotification.ShowWarning("当前播放文件路径无效");
                    return;
                }

                if (!TryParseTimelineInput(InPointTimeBox?.Text, out var inPointMs))
                {
                    Services.ToastNotification.ShowWarning("入点时间格式无效，请输入 HH:mm:ss.fff");
                    return;
                }

                if (!TryParseTimelineInput(OutPointTimeBox?.Text, out var outPointMs))
                {
                    Services.ToastNotification.ShowWarning("出点时间格式无效，请输入 HH:mm:ss.fff");
                    return;
                }

                var duration = _videoPlayerViewModel.Duration;
                if (duration > 0)
                {
                    inPointMs = Math.Clamp(inPointMs, 0, duration);
                    outPointMs = Math.Clamp(outPointMs, 0, duration);
                }

                if (outPointMs <= inPointMs)
                {
                    Services.ToastNotification.ShowWarning("出点时间必须晚于入点时间");
                    return;
                }

                _videoPlayerViewModel.InPoint = inPointMs;
                _videoPlayerViewModel.OutPoint = outPointMs;

                var (success, errorMessage) = _clipManager.TryAddClip(null, inPointMs, outPointMs, currentVideo.FilePath);
                if (success)
                {
                    var inLabel = FormatTimeForDisplay(inPointMs);
                    var outLabel = FormatTimeForDisplay(outPointMs);
                    Services.DebugLogger.LogInfo($"成功添加片段: {inLabel} → {outLabel}");
                    Services.ToastNotification.ShowSuccess($"已添加片段: {inLabel} → {outLabel}");
                }
                else
                {
                    Services.ToastNotification.ShowError(string.IsNullOrWhiteSpace(errorMessage) ? "添加片段失败" : errorMessage);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"添加片段到队列时发生错误: {ex.Message}");
                Services.ToastNotification.ShowError("添加片段失败，请检查日志");
            }
        }

        private bool TryParseTimelineInput(string? input, out long milliseconds)
        {
            milliseconds = 0;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var normalized = input.Trim();
            string[] formats = { "hh\\:mm\\:ss\\.fff", "hh\\:mm\\:ss", "mm\\:ss\\.fff", "mm\\:ss", "ss\\.fff", "ss" };

            foreach (var format in formats)
            {
                if (TimeSpan.TryParseExact(normalized, format, null, out var timeSpan))
                {
                    milliseconds = Math.Max(0, (long)timeSpan.TotalMilliseconds);
                    return true;
                }
            }

            if (TimeSpan.TryParse(normalized, out var fallback))
            {
                milliseconds = Math.Max(0, (long)fallback.TotalMilliseconds);
                return true;
            }

            return false;
        }

        private static string FormatTimeForDisplay(long milliseconds)
        {
            if (milliseconds < 0)
            {
                milliseconds = 0;
            }

            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            return timeSpan.ToString("hh\\:mm\\:ss\\.fff");
        }

        /// <summary>
        /// 片段标题文本框按键处理 - 支持回车确认编辑
        /// </summary>
        private void ClipTitleTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                    Keyboard.ClearFocus();
                    e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (textBox.DataContext is VideoClip clip)
                    {
                    textBox.Text = clip.EditableTitle;
                    }

                    Keyboard.ClearFocus();
                    e.Handled = true;
            }
        }

        /// <summary>
        /// 双击片段标题 - 开始编辑
        /// </summary>
        private void ClipTitleTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                if (textBox.DataContext is VideoClip clip && string.IsNullOrWhiteSpace(clip.CustomTitle))
                {
                    textBox.Clear();
                }

                textBox.SelectAll();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 上移片段项按钮点击事件
        /// </summary>
        private void MoveClipUpItemButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var clip = button?.CommandParameter as VideoClip;

            if (clip != null)
            {
                // 确保片段被选中
                clip.IsSelected = true;
                
                if (_clipManager.MoveClipUp(clip))
                {
                    // 移动成功，滚动到目标位置
                    ScrollToClip(clip);
                    // 更新操作区按钮状态
                    UpdateMoveButtonsState();
                }
                    // 已经在最顶部，静默处理
            }
        }

        /// <summary>
        /// 下移片段项按钮点击事件
        /// </summary>
        private void MoveClipDownItemButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var clip = button?.CommandParameter as VideoClip;

            if (clip != null)
            {
                // 确保片段被选中
                clip.IsSelected = true;
                
                if (_clipManager.MoveClipDown(clip))
                {
                    // 移动成功，滚动到目标位置
                    ScrollToClip(clip);
                    // 更新操作区按钮状态
                    UpdateMoveButtonsState();
                }
                    // 已经在最底部，静默处理
                }
            }

        /// <summary>
        /// 滚动到指定的片段
        /// </summary>
        private void ScrollToClip(VideoClip clip)
        {
            if (ClipListView == null || clip == null)
                return;

            // 使用 Dispatcher 延迟执行，确保 ListView 的 ItemContainerGenerator 已经更新
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 找到对应的ListViewItem
                    var container = ClipListView.ItemContainerGenerator.ContainerFromItem(clip) as ListViewItem;
                    if (container != null)
                    {
                        container.BringIntoView();
                        // 保持选中状态（同步到 VideoClip.IsSelected）
                        clip.IsSelected = true;
                        container.IsSelected = true;
                    }
                    else
                    {
                        // 如果容器还没生成，尝试刷新并重试
                        ClipListView.UpdateLayout();
                        container = ClipListView.ItemContainerGenerator.ContainerFromItem(clip) as ListViewItem;
                        if (container != null)
                        {
                            container.BringIntoView();
                            clip.IsSelected = true;
                            container.IsSelected = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 静默处理异常，避免影响用户体验
                    Services.DebugLogger.LogError($"滚动到片段时出错: {ex.Message}");
                }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// ListView 选中状态发生变化时，同步到 ClipManager
        /// </summary>
        private void ClipListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_clipManager == null)
            {
                return;
            }

            if (e.RemovedItems != null)
            {
                foreach (var item in e.RemovedItems.OfType<VideoClip>())
                {
                    item.IsSelected = false;
                }
            }

            if (e.AddedItems != null)
            {
                foreach (var item in e.AddedItems.OfType<VideoClip>())
                {
                    item.IsSelected = true;
                }
            }

            UpdateMoveButtonsState();
        }

        private static bool HasDuplicateClipTitles(VideoClip[] clips, out string duplicateNames)
        {
            duplicateNames = string.Empty;
            var duplicates = clips
                .Select(GetClipOutputTitle)
                .GroupBy(title => title, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            if (duplicates.Count > 0)
            {
                duplicateNames = string.Join("、", duplicates);
                return true;
            }

            return false;
        }

        private static string GetClipOutputTitle(VideoClip clip)
        {
            var title = clip.EditableTitle?.Trim();
            return string.IsNullOrWhiteSpace(title) ? clip.DisplayName : title;
        }

        private bool TryPrepareClipCutTasks(VideoFile selectedFile, VideoClip[] clips, OutputSettings settings, out List<ClipCutTask> tasks, out string errorMessage)
        {
            tasks = new List<ClipCutTask>();
            errorMessage = string.Empty;

            if (clips.Length == 0)
            {
                errorMessage = "请至少选择一个片段。";
                return false;
            }

            var mediaDurationMs = selectedFile.Duration.TotalMilliseconds;
            var sanitizedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extension = Path.GetExtension(selectedFile.FilePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".mp4";
            }

            foreach (var clip in clips.OrderBy(c => c.Order))
            {
                if (string.IsNullOrWhiteSpace(clip.SourceFilePath))
                {
                    errorMessage = $"片段 \"{GetClipOutputTitle(clip)}\" 缺少源文件信息，请重新添加该片段。";
                    return false;
                }

                if (!string.Equals(clip.SourceFilePath, selectedFile.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = $"片段 \"{GetClipOutputTitle(clip)}\" 与当前选中的源文件不匹配。";
                    return false;
                }

                if (clip.EndTime <= clip.StartTime)
                {
                    errorMessage = $"片段 \"{GetClipOutputTitle(clip)}\" 的结束时间必须大于开始时间。";
                    return false;
                }

                if (mediaDurationMs > 0 && clip.EndTime > mediaDurationMs)
                {
                    errorMessage = $"片段 \"{GetClipOutputTitle(clip)}\" 超出源文件时长。";
                    return false;
                }

                var baseName = GetClipOutputTitle(clip);
                var sanitized = SanitizeFileName(baseName);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    sanitized = $"clip_{clip.Order}";
                }

                if (!sanitizedNames.Add(sanitized))
                {
                    errorMessage = $"片段标题 \"{baseName}\" 与其他片段冲突，请先重命名。";
                    return false;
                }

                var outputPath = Path.Combine(settings.OutputPath, sanitized + extension);
                if (File.Exists(outputPath))
                {
                    errorMessage = $"输出目录中已存在文件 \"{Path.GetFileName(outputPath)}\"，请更改片段标题或删除该文件。";
                    return false;
                }

                tasks.Add(new ClipCutTask
                {
                    Clip = clip,
                    Start = TimeSpan.FromMilliseconds(clip.StartTime),
                    End = TimeSpan.FromMilliseconds(clip.EndTime),
                    OutputPath = outputPath
                });
            }

            return true;
        }

        private void ShowClipCutCommands(VideoFile selectedFile, List<ClipCutTask> tasks, string customArgs)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();
            
            for (int i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                var args = Services.VideoProcessingService.BuildClipCutArguments(
                    selectedFile.FilePath,
                    task.OutputPath,
                    task.Start,
                    task.End,
                    customArgs);

                commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                {
                    Index = i + 1,
                    Total = tasks.Count,
                    TaskId = GetClipOutputTitle(task.Clip),
                    InputPath = selectedFile.FilePath,
                    OutputPath = task.OutputPath,
                    CommandArguments = args
                });
            }

            var config = new Services.FfmpegCommandPreviewService.PreviewConfig
            {
                OperationName = "FFmpeg 剪切命令列表",
                OperationIcon = "✂️",
                SummaryLines = new List<string>
                {
                    $"📂 源文件: {selectedFile.FileName}",
                    $"📊 片段数量: {tasks.Count}"
                },
                AppendOutput = (text) => EmbeddedAppendOutput(text),
                AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1, // 命令预览现在是第2个标签页（索引1）
                SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
            };

            _ffmpegCommandPreviewService.ShowCommands(commands, config);
        }

        private async Task ExecuteClipCutBatchAsync(VideoFile selectedFile, List<ClipCutTask> tasks, OutputSettings settings, CancellationToken cancellationToken)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            var batchTasks = tasks.Select(task =>
            {
                var clipTitle = GetClipOutputTitle(task.Clip);
                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = clipTitle,
                    InputPath = selectedFile.FilePath,
                    OutputPath = task.OutputPath,
                    Description = $"片段: {clipTitle}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        return await _videoProcessingService.CutClipAsync(
                            input,
                            output,
                            task.Clip.StartTime,
                            task.Clip.EndTime,
                            settings.CustomArgs,
                            null, // 进度由批量处理器统一处理
                            ct);
                    },
                    EstimatedDuration = TimeSpan.FromMilliseconds(task.Clip.EndTime - task.Clip.StartTime)
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量剪切片段",
                OperationIcon = "✂️",
                OperationColor = "#2196F3",
                LogHeaderLines = new List<string>
                {
                    $"📂 源文件: {selectedFile.FileName}",
                    $"📁 输出目录: {settings.OutputPath}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0, // 执行日志现在是第1个标签页（索引0）
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            // 显示结果提示（如果需要）
            if (result.SuccessCount == result.TotalTasks)
            {
                // 全部成功，静默处理（已在日志中显示）
            }
            else if (result.SuccessCount > 0)
            {
                // 部分成功，已在日志中显示
            }
            else
            {
                // 全部失败，已在日志中显示
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// 更新操作区上移/下移按钮的禁用状态
        /// </summary>
        private void UpdateMoveButtonsState()
        {
            if (MoveClipUpButton == null || MoveClipDownButton == null)
                return;

            var selectedClips = _clipManager.GetSelectedClips();
            
            // 如果没有选中片段或选中多个片段，禁用按钮
            if (selectedClips.Length != 1)
            {
                MoveClipUpButton.IsEnabled = false;
                MoveClipDownButton.IsEnabled = false;
                return;
            }

            var clip = selectedClips[0];
            // 根据片段位置启用/禁用按钮
            MoveClipUpButton.IsEnabled = !_clipManager.IsAtTop(clip);
            MoveClipDownButton.IsEnabled = !_clipManager.IsAtBottom(clip);
        }

        /// <summary>
        /// 编辑片段标题按钮点击事件
        /// </summary>
        private void EditClipTitleButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var clip = button?.CommandParameter as VideoClip;

            if (clip != null)
            {
                // 找到对应的TextBox并让它获得焦点
                var listViewItem = FindParent<ListViewItem>(button);
                if (listViewItem != null)
                {
                    var textBox = FindVisualChild<TextBox>(listViewItem, "ClipTitleTextBox");
                    if (textBox != null)
                    {
                        textBox.Focus();
                        textBox.SelectAll();
                    }
                }
            }
        }

        /// <summary>
        /// 删除单个片段按钮点击事件
        /// </summary>
        private void DeleteClipItemButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var clip = button?.CommandParameter as VideoClip;

            if (clip != null)
            {
                var result = MessageBox.Show($"确定要删除片段 \"{clip.EditableTitle}\" 吗？",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _clipManager.RemoveClip(clip);
                    // 删除成功，不显示提示
                }
            }
        }

        /// <summary>
        /// 全选片段按钮点击事件
        /// </summary>
        private void SelectAllClipsButton_Click(object sender, RoutedEventArgs e)
        {
            _clipManager.SelectAllClips();
            // 全选完成，不显示提示
        }

        /// <summary>
        /// 全消片段按钮点击事件
        /// </summary>
        private void DeselectAllClipsButton_Click(object sender, RoutedEventArgs e)
        {
            _clipManager.DeselectAllClips();
            // 全消完成，不显示提示
        }

        /// <summary>
        /// 上移片段按钮点击事件
        /// </summary>
        private void MoveClipUpButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = _clipManager.GetSelectedClips();
            if (selectedClips.Length == 0)
            {
                Services.ToastNotification.ShowWarning("请选择一个片段后再试");
                return;
            }

            if (selectedClips.Length > 1)
            {
                Services.ToastNotification.ShowInfo("一次只支持移动一个片段");
                return;
            }

            var clip = selectedClips[0];
            if (_clipManager.MoveClipUp(clip))
            {
                // 移动成功，滚动到目标位置
                ScrollToClip(clip);
                UpdateMoveButtonsState();
            }
            else
            {
                Services.ToastNotification.ShowInfo("该片段已经位于顶部");
            }
        }

        /// <summary>
        /// 下移片段按钮点击事件
        /// </summary>
        private void MoveClipDownButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = _clipManager.GetSelectedClips();
            if (selectedClips.Length == 0)
            {
                Services.ToastNotification.ShowWarning("请选择一个片段后再试");
                return;
            }

            if (selectedClips.Length > 1)
            {
                Services.ToastNotification.ShowInfo("一次只支持移动一个片段");
                return;
            }

            var clip = selectedClips[0];
            if (_clipManager.MoveClipDown(clip))
            {
                // 移动成功，滚动到目标位置
                ScrollToClip(clip);
                UpdateMoveButtonsState();
            }
            else
            {
                Services.ToastNotification.ShowInfo("该片段已经位于底部");
            }
        }

        /// <summary>
        /// 删除片段按钮点击事件
        /// </summary>
        private void DeleteClipButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = _clipManager.GetSelectedClips();
            if (selectedClips.Length == 0)
            {
                Services.ToastNotification.ShowWarning("请选择要删除的片段");
                return;
            }

            var message = selectedClips.Length == 1
                ? $"确定要删除片段 \"{selectedClips[0].Name}\" 吗？"
                : $"确定要删除选中的 {selectedClips.Length} 个片段吗？";

            var result = MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _clipManager.RemoveSelectedClips();
                // 删除成功，不显示提示
            }
        }

        /// <summary>
        /// 清空片段按钮点击事件
        /// </summary>
        private void ClearClipsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_clipManager.ClipCount == 0)
            {
                Services.ToastNotification.ShowInfo("片段列表已经为空");
                return;
            }

            var result = MessageBox.Show($"确定要清空所有 {_clipManager.ClipCount} 个片段吗？",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _clipManager.ClearAllClips();
                // 清空成功，不显示提示
            }
        }

        /// <summary>
        /// 添加文件夹按钮点击事件
        /// </summary>
        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new Forms.FolderBrowserDialog
            {
                Description = "选择包含视频文件的文件夹"
            };

            if (folderDialog.ShowDialog() == Forms.DialogResult.OK)
            {
                await _videoListViewModel.AddFolderAsync(folderDialog.SelectedPath);
            }
        }

        private void MenuLayoutStandard_Click(object sender, RoutedEventArgs e)
        {
            ApplyLayoutPreset(LayoutPreset.Standard);
        }

        private void MenuLayoutCompact_Click(object sender, RoutedEventArgs e)
        {
            ApplyLayoutPreset(LayoutPreset.Compact);
        }

        private void MenuLayoutReset_Click(object sender, RoutedEventArgs e)
        {
            ApplyLayoutPreset(LayoutPreset.Standard);
        }

        private void MenuThemeLight_Click(object sender, RoutedEventArgs e)
        {
            SetTheme(ApplicationTheme.Light);
        }

        private void MenuThemeDark_Click(object sender, RoutedEventArgs e)
        {
            SetTheme(ApplicationTheme.Dark);
        }

        private async void ImportDplPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入 PotPlayer 播放列表",
                    Filter = "PotPlayer 播放列表 (*.dpl)|*.dpl|所有文件|*.*",
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var mediaPaths = await _dplPlaylistService.LoadAsync(dialog.FileName);
                if (mediaPaths.Count == 0)
                {
                    Services.ToastNotification.ShowInfo("播放列表中没有可用的媒体文件");
                    return;
                }

                await _videoListViewModel.AddFilesAsync(mediaPaths.ToArray());
                Services.ToastNotification.ShowSuccess($"已从播放列表导入 {mediaPaths.Count} 个文件");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"导入 DPL 播放列表失败: {ex.Message}");
                MessageBox.Show($"导入播放列表失败:\n{ex.Message}", "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ExportDplPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_videoListViewModel.Files.Count == 0)
                {
                    Services.ToastNotification.ShowInfo("当前列表为空，无法导出");
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出为 PotPlayer 播放列表",
                    Filter = "PotPlayer 播放列表 (*.dpl)|*.dpl|所有文件|*.*",
                    DefaultExt = ".dpl",
                    FileName = $"VideoEditor_{DateTime.Now:yyyyMMdd_HHmm}.dpl"
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                await _dplPlaylistService.SaveAsync(dialog.FileName, _videoListViewModel.Files);
                Services.ToastNotification.ShowSuccess($"播放列表已导出到 {System.IO.Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"导出 DPL 播放列表失败: {ex.Message}");
                MessageBox.Show($"导出播放列表失败:\n{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void OpenProjectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "打开 VideoEditor 项目",
                    Filter = ProjectFileFilter,
                    DefaultExt = ProjectFileExtension,
                    CheckFileExists = true,
                    InitialDirectory = !string.IsNullOrWhiteSpace(_currentProjectFilePath)
                        ? Path.GetDirectoryName(_currentProjectFilePath)
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                await LoadProjectFromFileAsync(dialog.FileName);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"打开项目失败: {ex.Message}");
                MessageBox.Show($"打开项目失败：{ex.Message}", "打开项目", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveProjectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SaveProjectAsync(forcePickPath: false);
        }

        private async void SaveProjectAsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await SaveProjectAsync(forcePickPath: true);
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 全选按钮点击事件
        /// </summary>
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SelectAll();
        }

        /// <summary>
        /// 取消全选按钮点击事件
        /// </summary>
        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.DeselectAll();
        }

        /// <summary>
        /// 删除选中按钮点击事件
        /// </summary>
        private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
            if (selectedFiles.Count == 0) return;

            var result = MessageBox.Show($"确定要删除选中的 {selectedFiles.Count} 个文件吗？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                RemoveFilesWithResourceCleanup(selectedFiles);
            }
        }

        /// <summary>
        /// 清空列表按钮点击事件
        /// </summary>
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            var allFiles = _videoListViewModel.Files.ToList();
            if (allFiles.Count == 0) return;

            var result = MessageBox.Show("确定要清空所有文件吗？", "确认", 
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                RemoveFilesWithResourceCleanup(allFiles);
            }
        }

        /// <summary>
        /// 删除文件时释放资源
        /// </summary>
        private void RemoveFilesWithResourceCleanup(List<VideoFile> filesToRemove)
        {
            try
            {
                // 检查是否有正在播放的文件需要停止
                bool needToStopPlayback = false;
                foreach (var file in filesToRemove)
                {
                    if (_videoPlayerViewModel.CurrentFilePath == file.FilePath)
                    {
                        needToStopPlayback = true;
                        break;
                    }
                }

                // 如果当前播放的文件在要删除的列表中，先停止播放并清理媒体
                if (needToStopPlayback)
                {
                    _videoPlayerViewModel.Stop();
                    _videoPlayerViewModel.ClearMedia();

                    // 等待一段时间让资源释放
                    System.Threading.Thread.Sleep(500);
                }

                // 强制垃圾回收以释放文件句柄
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // 从列表中移除文件
                foreach (var file in filesToRemove)
                {
                    _videoListViewModel.Files.Remove(file);
                }

                // 更新状态
                _videoListViewModel.StatusMessage = $"已删除 {filesToRemove.Count} 个文件";
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"删除文件时释放资源失败: {ex.Message}");
                Services.ToastNotification.ShowError($"删除文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 浏览输出路径按钮点击事件
        /// </summary>
        private void BrowseOutputPathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Forms.FolderBrowserDialog
                {
                    Description = "选择输出文件夹",
                    ShowNewFolderButton = true
                };

                // 如果当前有路径，则设置为初始目录
                if (!string.IsNullOrEmpty(OutputPathBox.Text) && System.IO.Directory.Exists(OutputPathBox.Text))
                {
                    dialog.SelectedPath = OutputPathBox.Text;
                }

                var result = dialog.ShowDialog();
                if (result == Forms.DialogResult.OK)
                {
                    OutputPathBox.Text = dialog.SelectedPath;
                    Services.DebugLogger.LogInfo($"输出路径已更改为: {dialog.SelectedPath}");
                    Services.ToastNotification.ShowSuccess($"输出路径已设置为: {System.IO.Path.GetFileName(dialog.SelectedPath)}");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"浏览输出路径失败: {ex.Message}");
                Services.ToastNotification.ShowError($"浏览文件夹失败: {ex.Message}");
            }
        }

        #endregion

        #region 项目文件操作

        private async Task SaveProjectAsync(bool forcePickPath)
        {
            try
            {
                if (forcePickPath || string.IsNullOrWhiteSpace(_currentProjectFilePath))
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Title = "保存 VideoEditor 项目",
                        Filter = ProjectFileFilter,
                        DefaultExt = ProjectFileExtension,
                        FileName = GenerateDefaultProjectFileName(),
                        AddExtension = true,
                        InitialDirectory = !string.IsNullOrWhiteSpace(_currentProjectFilePath)
                            ? Path.GetDirectoryName(_currentProjectFilePath)
                            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }

                    _currentProjectFilePath = dialog.FileName;
                }

                if (string.IsNullOrWhiteSpace(_currentProjectFilePath))
                {
                    return;
                }

                var snapshot = CaptureProjectSnapshot();
                await SaveProjectSnapshotAsync(_currentProjectFilePath, snapshot);
                AddToRecentProjects(_currentProjectFilePath);
                UpdateWindowTitleWithProject();
                Services.ToastNotification.ShowSuccess($"项目已保存: {Path.GetFileName(_currentProjectFilePath)}");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"保存项目失败: {ex.Message}");
                MessageBox.Show($"保存项目失败：{ex.Message}", "保存项目", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RecentProjectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.DataContext is not RecentProjectItem item || item.IsPlaceholder)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
            {
                var result = MessageBox.Show(
                    $"无法找到项目文件：\n{item.FilePath}\n\n是否将其从“最近打开”列表中移除？",
                    "最近打开",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _recentProjects.Remove(item);
                    EnsureRecentProjectPlaceholder();
                    SaveRecentProjectsToSettings();
                }
                return;
            }

            await LoadProjectFromFileAsync(item.FilePath);
        }

        private static string GenerateDefaultProjectFileName()
        {
            return $"VideoEditorProject_{DateTime.Now:yyyyMMdd_HHmmss}{ProjectFileExtension}";
        }

        private async Task SaveProjectSnapshotAsync(string filePath, ProjectSnapshot snapshot)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, _projectJsonOptions);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
            Services.DebugLogger.LogInfo($"项目已保存到 {filePath}");
        }

        private async Task LoadProjectFromFileAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    MessageBox.Show("项目文件不存在，请确认路径后重试。", "打开项目", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ProjectSnapshot? snapshot;
                await using (var stream = File.OpenRead(filePath))
                {
                    snapshot = await JsonSerializer.DeserializeAsync<ProjectSnapshot>(stream, _projectJsonOptions);
                }

                if (snapshot == null)
                {
                    throw new InvalidDataException("项目文件格式无效或已损坏。");
                }

                await ApplyProjectSnapshotAsync(snapshot);
                _currentProjectFilePath = filePath;
                UpdateWindowTitleWithProject();
                AddToRecentProjects(filePath);
                Services.ToastNotification.ShowSuccess($"已打开项目: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"打开项目失败: {ex.Message}");
                MessageBox.Show($"打开项目失败：{ex.Message}", "打开项目", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ApplyProjectSnapshotAsync(ProjectSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            _isLoadingProject = true;
            try
            {
                await RestoreMediaFilesAsync(snapshot.MediaFiles);
                RestoreClipSnapshots(snapshot.ClipTasks);
                RestoreMergeItems(snapshot.MergeTasks);
                ApplyMergeParametersSnapshot(snapshot.MergeParameters);
                ApplyOutputSettingsSnapshot(snapshot.OutputSettings);
                ApplyCropSnapshot(snapshot.CropTask);
                ApplyWatermarkSnapshot(snapshot.WatermarkParameters);
                ApplyRemoveWatermarkSnapshot(snapshot.RemoveWatermark);
                ApplyDeduplicateSnapshot(snapshot.DeduplicateParameters);
                ApplyAudioSnapshot(snapshot.AudioParameters);
                ApplyTranscodeSnapshot(snapshot.TranscodeParameters);
                ApplySubtitleSnapshot(snapshot.SubtitleParameters);
                ApplyFilterSnapshot(snapshot.FilterParameters);
                ApplyFlipSnapshot(snapshot.FlipParameters);
                ApplyGifSnapshot(snapshot.GifTask);
                ApplyPlayerStateSnapshot(snapshot.PlayerState);
                ApplyInterfaceStateSnapshot(snapshot.InterfaceState);
                ApplyCommandPanelSnapshot(snapshot.CommandPanel);
                ApplyBatchSubtitleTasksSnapshot(snapshot.BatchSubtitleTasks);

                if (UseCommandPromptForCropCheckBox != null)
                {
                    UseCommandPromptForCropCheckBox.IsChecked = snapshot.UseCommandPromptForOperations;
                }

                _isCommandPreviewVisible = snapshot.IsCommandPreviewVisible;
                _isExecutionLogVisible = snapshot.IsExecutionLogVisible;
                ApplyOutputPanelVisibility();
            }
            finally
            {
                _isLoadingProject = false;
            }
        }

        private async Task RestoreMediaFilesAsync(List<ProjectMediaItemSnapshot> mediaFiles)
        {
            if (_videoListViewModel == null)
            {
                return;
            }

            _videoListViewModel.ClearAllFiles();

            if (mediaFiles == null || mediaFiles.Count == 0)
            {
                return;
            }

            var validPaths = mediaFiles
                .Where(m => !string.IsNullOrWhiteSpace(m.FilePath) && File.Exists(m.FilePath))
                .Select(m => m.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (validPaths.Length > 0)
            {
                await _videoListViewModel.AddFilesAsync(validPaths);
            }

            var missingCount = mediaFiles.Count - validPaths.Length;
            if (missingCount > 0)
            {
                Services.DebugLogger.LogWarning($"加载项目时有 {missingCount} 个媒体文件缺失。");
            }

            foreach (var media in mediaFiles)
            {
                var match = _videoListViewModel.Files.FirstOrDefault(f =>
                    f.FilePath.Equals(media.FilePath, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    match.IsSelected = media.IsSelected;
                    match.IsPlaying = media.IsPlaying;
                }
            }
        }

        private void RestoreClipSnapshots(List<ProjectClipSnapshot> clips)
        {
            if (_clipManager == null)
            {
                return;
            }

            _clipManager.ClearAllClips();

            if (clips == null || clips.Count == 0)
            {
                return;
            }

            foreach (var clipSnapshot in clips.OrderBy(c => c.Order))
            {
                var clip = _clipManager.AddClip(clipSnapshot.Name, clipSnapshot.StartTime, clipSnapshot.EndTime, clipSnapshot.SourceFilePath);
                if (!string.IsNullOrWhiteSpace(clipSnapshot.CustomTitle))
                {
                    clip.CustomTitle = clipSnapshot.CustomTitle;
                }
                clip.IsSelected = clipSnapshot.IsSelected;
            }
        }

        private void RestoreMergeItems(List<ProjectMergeItemSnapshot> mergeItems)
        {
            _mergeItems.Clear();

            if (mergeItems == null || mergeItems.Count == 0)
            {
                UpdateMergeSummary();
                UpdateMergeCommandPreview();
                return;
            }

            foreach (var itemSnapshot in mergeItems.OrderBy(m => m.Order))
            {
                var mergeItem = new MergeItem(itemSnapshot.FilePath)
                {
                    Order = itemSnapshot.Order,
                    Duration = itemSnapshot.Duration,
                    VideoCodec = itemSnapshot.VideoCodec,
                    AudioCodec = itemSnapshot.AudioCodec,
                    Resolution = itemSnapshot.Resolution
                };
                _mergeItems.Add(mergeItem);
            }

            RefreshMergeOrder();
            UpdateMergeSummary();
            UpdateMergeCommandPreview();
        }

        private void ApplyMergeParametersSnapshot(Models.MergeParameters? parameters)
        {
            if (parameters == null)
            {
                if (MergeFastModeRadio != null)
                {
                    MergeFastModeRadio.IsChecked = true;
                }
                return;
            }

            if (MergeFastModeRadio != null)
            {
                MergeFastModeRadio.IsChecked = parameters.UseFastConcat;
            }

            if (MergeReencodeModeRadio != null)
            {
                MergeReencodeModeRadio.IsChecked = !parameters.UseFastConcat;
            }
        }

        private void LoadRecentProjectsFromSettings()
        {
            try
            {
                _recentProjects.Clear();
                var raw = Properties.Settings.Default.RecentProjects ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var entries = raw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var entry in entries)
                    {
                        AddRecentProjectEntry(entry.Trim(), saveSettings: false);
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"加载最近项目列表失败: {ex.Message}");
            }
            finally
            {
                EnsureRecentProjectPlaceholder();
            }
        }

        private void AddToRecentProjects(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            AddRecentProjectEntry(filePath, saveSettings: true);
        }

        private void AddRecentProjectEntry(string path, bool saveSettings)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch
            {
                normalizedPath = path;
            }

            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return;
            }

            var placeholder = _recentProjects.FirstOrDefault(p => p.IsPlaceholder);
            if (placeholder != null)
            {
                _recentProjects.Remove(placeholder);
            }

            var existing = _recentProjects.FirstOrDefault(p =>
                !p.IsPlaceholder && p.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                _recentProjects.Remove(existing);
            }

            _recentProjects.Insert(0, RecentProjectItem.Create(normalizedPath));

            while (_recentProjects.Count(p => !p.IsPlaceholder) > MaxRecentProjects)
            {
                var last = _recentProjects.LastOrDefault(p => !p.IsPlaceholder);
                if (last != null)
                {
                    _recentProjects.Remove(last);
                }
                else
                {
                    break;
                }
            }

            EnsureRecentProjectPlaceholder();

            if (saveSettings)
            {
                SaveRecentProjectsToSettings();
            }
        }

        private void EnsureRecentProjectPlaceholder()
        {
            var placeholder = _recentProjects.FirstOrDefault(p => p.IsPlaceholder);
            var hasRealItems = _recentProjects.Any(p => !p.IsPlaceholder);

            if (!hasRealItems)
            {
                if (placeholder == null)
                {
                    _recentProjects.Add(RecentProjectItem.CreatePlaceholder());
                }
            }
            else if (placeholder != null)
            {
                _recentProjects.Remove(placeholder);
            }
        }

        private void SaveRecentProjectsToSettings()
        {
            var serialized = string.Join("|", _recentProjects
                .Where(p => !p.IsPlaceholder)
                .Select(p => p.FilePath));

            Properties.Settings.Default.RecentProjects = serialized;
            Properties.Settings.Default.Save();
        }

        private void ApplyOutputSettingsSnapshot(ProjectOutputSettingsSnapshot? settings)
        {
            if (settings == null)
            {
                return;
            }

            if (OutputPathBox != null)
            {
                OutputPathBox.Text = settings.OutputPath ?? string.Empty;
            }

            ApplyFileNamingMode(settings.FileNamingMode);

            if (CustomPrefixBox != null)
            {
                CustomPrefixBox.Text = settings.CustomPrefix ?? string.Empty;
            }

            if (CustomSuffixBox != null)
            {
                CustomSuffixBox.Text = settings.CustomSuffix ?? string.Empty;
            }

            SelectComboBoxItemByContent(OutputFormatBox, settings.OutputFormat);

            var videoCodec = settings.VideoCodec ?? string.Empty;
            if (CopyCodecRadio != null && videoCodec.Contains("复制", StringComparison.OrdinalIgnoreCase))
            {
                CopyCodecRadio.IsChecked = true;
            }
            else if (H264CodecRadio != null && videoCodec.Contains("264", StringComparison.OrdinalIgnoreCase))
            {
                H264CodecRadio.IsChecked = true;
            }
            else if (H265CodecRadio != null && videoCodec.Contains("265", StringComparison.OrdinalIgnoreCase))
            {
                H265CodecRadio.IsChecked = true;
            }

            if (QualitySlider != null)
            {
                var clamped = Math.Clamp(settings.Quality, (int)QualitySlider.Minimum, (int)QualitySlider.Maximum);
                QualitySlider.Value = clamped;
            }

            SelectComboBoxItemByContent(AudioCodecBox, settings.AudioCodec);
            SelectComboBoxItemByContent(AudioBitrateBox, settings.AudioBitrate);

            if (CustomArgsBox != null)
            {
                CustomArgsBox.Text = settings.CustomArgs ?? string.Empty;
            }
        }

        private void ApplyFileNamingMode(string? mode)
        {
            switch (mode)
            {
                case "自定义前缀":
                    if (CustomPrefixRadio != null)
                    {
                        CustomPrefixRadio.IsChecked = true;
                    }
                    break;
                case "自定义后缀":
                    if (CustomSuffixRadio != null)
                    {
                        CustomSuffixRadio.IsChecked = true;
                    }
                    break;
                default:
                    if (OriginalNameRadio != null)
                    {
                        OriginalNameRadio.IsChecked = true;
                    }
                    break;
            }
        }

        private void ApplyCropSnapshot(CropTaskSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (CropXTextBox != null)
            {
                CropXTextBox.Text = snapshot.X ?? CropXTextBox.Text;
            }

            if (CropYTextBox != null)
            {
                CropYTextBox.Text = snapshot.Y ?? CropYTextBox.Text;
            }

            if (CropWTextBox != null)
            {
                CropWTextBox.Text = snapshot.Width ?? CropWTextBox.Text;
            }

            if (CropHTextBox != null)
            {
                CropHTextBox.Text = snapshot.Height ?? CropHTextBox.Text;
            }
        }

        private void ApplyWatermarkSnapshot(Models.WatermarkParameters? parameters)
        {
            if (parameters == null || parameters.Type == Models.WatermarkType.None)
            {
                if (RadioImageWatermark != null)
                {
                    RadioImageWatermark.IsChecked = false;
                }
                if (RadioTextWatermark != null)
                {
                    RadioTextWatermark.IsChecked = false;
                }
                return;
            }

            if (parameters.Type == Models.WatermarkType.Image)
            {
                if (RadioImageWatermark != null)
                {
                    RadioImageWatermark.IsChecked = true;
                }
                if (txtWatermarkImagePath != null)
                {
                    txtWatermarkImagePath.Text = parameters.ImagePath ?? string.Empty;
                }
                if (sliderImageOpacity != null)
                {
                    sliderImageOpacity.Value = parameters.ImageOpacity;
                }
            }
            else if (parameters.Type == Models.WatermarkType.Text)
            {
                if (RadioTextWatermark != null)
                {
                    RadioTextWatermark.IsChecked = true;
                }
                if (txtWatermarkText != null)
                {
                    txtWatermarkText.Text = parameters.Text ?? string.Empty;
                }
                if (txtFontSize != null)
                {
                    txtFontSize.Text = parameters.FontSize.ToString(CultureInfo.InvariantCulture);
                }
                if (txtTextColor != null)
                {
                    txtTextColor.Text = parameters.TextColor ?? "white";
                }
                if (sliderTextOpacity != null)
                {
                    sliderTextOpacity.Value = parameters.TextOpacity;
                }
            }

            if (txtWatermarkX != null)
            {
                txtWatermarkX.Text = parameters.X.ToString(CultureInfo.InvariantCulture);
            }

            if (txtWatermarkY != null)
            {
                txtWatermarkY.Text = parameters.Y.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void ApplyRemoveWatermarkSnapshot(WatermarkRemovalSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (txtRemoveX != null)
            {
                txtRemoveX.Text = snapshot.X ?? txtRemoveX.Text;
            }

            if (txtRemoveY != null)
            {
                txtRemoveY.Text = snapshot.Y ?? txtRemoveY.Text;
            }

            if (txtRemoveW != null)
            {
                txtRemoveW.Text = snapshot.Width ?? txtRemoveW.Text;
            }

            if (txtRemoveH != null)
            {
                txtRemoveH.Text = snapshot.Height ?? txtRemoveH.Text;
            }
        }

        private void ApplyDeduplicateSnapshot(Models.DeduplicateParameters? parameters)
        {
            if (parameters == null)
            {
                if (RadioOffMode != null)
                {
                    RadioOffMode.IsChecked = true;
                }
                return;
            }

            switch (parameters.Mode)
            {
                case Models.DeduplicateMode.Light:
                    if (RadioLightMode != null) RadioLightMode.IsChecked = true;
                    break;
                case Models.DeduplicateMode.Medium:
                    if (RadioMediumMode != null) RadioMediumMode.IsChecked = true;
                    break;
                case Models.DeduplicateMode.Heavy:
                    if (RadioHeavyMode != null) RadioHeavyMode.IsChecked = true;
                    break;
                default:
                    if (RadioOffMode != null) RadioOffMode.IsChecked = true;
                    break;
            }

            if (sliderBrightness != null) sliderBrightness.Value = parameters.Brightness;
            if (sliderContrast != null) sliderContrast.Value = parameters.Contrast;
            if (sliderSaturation != null) sliderSaturation.Value = parameters.Saturation;
            if (sliderNoise != null) sliderNoise.Value = parameters.Noise;
            if (sliderBlur != null) sliderBlur.Value = parameters.Blur;
            if (sliderCropEdge != null) sliderCropEdge.Value = parameters.CropEdge;
        }

        private void ApplyAudioSnapshot(Models.AudioParameters? parameters)
        {
            if (parameters == null)
            {
                return;
            }

            if (sliderVolume != null)
            {
                sliderVolume.Value = Math.Clamp(parameters.Volume, sliderVolume.Minimum, sliderVolume.Maximum);
            }

            if (txtFadeIn != null)
            {
                txtFadeIn.Text = parameters.FadeIn.ToString("0.##", CultureInfo.InvariantCulture);
            }

            if (txtFadeOut != null)
            {
                txtFadeOut.Text = parameters.FadeOut.ToString("0.##", CultureInfo.InvariantCulture);
            }

            SelectComboBoxItemByContent(cboAudioFormat, parameters.Format);
            SelectComboBoxItemByContent(cboBitrate, parameters.Bitrate);
        }

        private void ApplyTranscodeSnapshot(Models.TranscodeParameters? parameters)
        {
            if (parameters == null)
            {
                return;
            }

            switch (parameters.Mode)
            {
                case Models.TranscodeMode.Fast:
                    if (RadioFastTranscode != null) RadioFastTranscode.IsChecked = true;
                    break;
                case Models.TranscodeMode.HighQuality:
                    if (RadioHighQualityTranscode != null) RadioHighQualityTranscode.IsChecked = true;
                    break;
                case Models.TranscodeMode.Compress:
                    if (RadioCompressTranscode != null) RadioCompressTranscode.IsChecked = true;
                    break;
                default:
                    if (RadioStandardTranscode != null) RadioStandardTranscode.IsChecked = true;
                    break;
            }

            SelectComboBoxItemByContent(cboTranscodeFormat, parameters.OutputFormat);
            SelectComboBoxItemByContent(cboVideoCodec, parameters.VideoCodec);
            SelectComboBoxItemByContent(cboAudioCodec, parameters.AudioCodec);
            SelectComboBoxItemByContent(cboTranscodeBitrate, parameters.AudioBitrate);

            if (sliderTranscodeCRF != null)
            {
                sliderTranscodeCRF.Value = Math.Clamp(parameters.CRF, sliderTranscodeCRF.Minimum, sliderTranscodeCRF.Maximum);
            }

            if (CheckBoxDualPassTranscode != null)
            {
                CheckBoxDualPassTranscode.IsChecked = parameters.DualPass;
            }

            if (CheckBoxHardwareAccelTranscode != null)
            {
                CheckBoxHardwareAccelTranscode.IsChecked = parameters.HardwareAcceleration;
            }

            if (CheckBoxKeepMetadataTranscode != null)
            {
                CheckBoxKeepMetadataTranscode.IsChecked = parameters.KeepMetadata;
            }
        }

        private void ApplySubtitleSnapshot(Models.SubtitleParameters? parameters)
        {
            if (parameters == null)
            {
                return;
            }

            if (SubtitlePathBox != null)
            {
                SubtitlePathBox.Text = parameters.SubtitleFilePath ?? string.Empty;
            }

            SelectComboBoxItemByContent(cboSubtitleFont, parameters.FontFamily);

            if (txtSubtitleSize != null)
            {
                txtSubtitleSize.Text = parameters.FontSize.ToString(CultureInfo.InvariantCulture);
            }

            if (txtSubtitleColor != null)
            {
                txtSubtitleColor.Text = parameters.FontColor ?? "white";
            }

            if (sliderSubtitleOutline != null)
            {
                sliderSubtitleOutline.Value = Math.Clamp(parameters.OutlineWidth, sliderSubtitleOutline.Minimum, sliderSubtitleOutline.Maximum);
            }

            if (chkSubtitleShadow != null)
            {
                chkSubtitleShadow.IsChecked = parameters.EnableShadow;
            }

            if (sliderSubtitleOffset != null)
            {
                sliderSubtitleOffset.Value = Math.Clamp(parameters.TimeOffset, sliderSubtitleOffset.Minimum, sliderSubtitleOffset.Maximum);
            }

            var positionLabel = parameters.Position switch
            {
                Models.SubtitlePosition.Top => "顶部",
                Models.SubtitlePosition.Center => "居中",
                _ => "底部"
            };
            SelectComboBoxItemByContent(cboSubtitlePosition, positionLabel);
        }

        private void ApplyFilterSnapshot(Models.FilterParameters? parameters)
        {
            _currentFilterParameters = parameters?.Clone() ?? Models.FilterParameters.CreateDefault();
            ApplyFilterParametersToSliders(_currentFilterParameters);
            UpdateFilterCommandPreview();
        }

        private void ApplyFlipSnapshot(Models.FlipParameters? parameters)
        {
            _currentFlipParameters = parameters != null
                ? new Models.FlipParameters
                {
                    FlipType = parameters.FlipType,
                    RotateType = parameters.RotateType,
                    CustomRotateAngle = parameters.CustomRotateAngle,
                    TransposeType = parameters.TransposeType
                }
                : new Models.FlipParameters();

            UpdateFlipCommandPreview();
        }

        private void ApplyGifSnapshot(GifTaskSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (txtGifStartTime != null)
            {
                txtGifStartTime.Text = snapshot.StartTime ?? txtGifStartTime.Text;
            }

            if (txtGifEndTime != null)
            {
                txtGifEndTime.Text = snapshot.EndTime ?? txtGifEndTime.Text;
            }

            if (txtGifFPS != null)
            {
                txtGifFPS.Text = snapshot.FramesPerSecond ?? txtGifFPS.Text;
            }

            if (txtGifWidth != null)
            {
                txtGifWidth.Text = snapshot.Width ?? txtGifWidth.Text;
            }

            SelectComboBoxItemByContent(cboGifQuality, snapshot.QualityOption);
        }

        private void ApplyPlayerStateSnapshot(PlayerStateSnapshot? snapshot)
        {
            if (snapshot == null || _videoPlayerViewModel == null)
            {
                return;
            }

            _videoPlayerViewModel.Volume = (float)Math.Clamp(snapshot.Volume, 0, 200);
            _videoPlayerViewModel.IsMuted = snapshot.IsMuted;
            _videoPlayerViewModel.PlaybackRate = snapshot.PlaybackRate <= 0 ? 1.0f : snapshot.PlaybackRate;
            _videoPlayerViewModel.IsLoopEnabled = snapshot.IsLoopEnabled;
            _videoPlayerViewModel.IsSinglePlayMode = snapshot.IsSinglePlayMode;
            _videoPlayerViewModel.InPoint = snapshot.InPoint;
            _videoPlayerViewModel.OutPoint = snapshot.OutPoint;

            if (!string.IsNullOrWhiteSpace(snapshot.CurrentFilePath) &&
                File.Exists(snapshot.CurrentFilePath) &&
                _videoListViewModel != null)
            {
                var target = _videoListViewModel.Files.FirstOrDefault(f =>
                    f.FilePath.Equals(snapshot.CurrentFilePath, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    _videoListViewModel.SelectedFile = target;
                    _videoPlayerViewModel.LoadVideo(target.FilePath);
                    _videoPlayerViewModel.CurrentPosition = snapshot.CurrentPosition;
                }
            }
        }

        private void ApplyInterfaceStateSnapshot(InterfaceStateSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (Enum.TryParse(snapshot.InterfaceMode, out InterfaceDisplayMode mode))
            {
                SetInterfaceDisplayMode(mode);
            }

            if (Enum.TryParse(snapshot.Theme, out ApplicationTheme theme) && theme != _currentTheme)
            {
                SetTheme(theme);
            }

            if (Enum.TryParse(snapshot.LayoutPreset, out LayoutPreset preset) && preset != _currentLayoutPreset)
            {
                ApplyLayoutPreset(preset);
            }

            if (LeftColumn != null && snapshot.LeftColumn != null)
            {
                LeftColumn.Width = ToGridLength(snapshot.LeftColumn);
            }

            if (RightColumn != null && snapshot.RightColumn != null)
            {
                RightColumn.Width = ToGridLength(snapshot.RightColumn);
            }

            if (BottomRow != null && snapshot.BottomRow != null)
            {
                BottomRow.Height = ToGridLength(snapshot.BottomRow);
            }

            if (BottomStatusRow != null && snapshot.BottomStatusRow != null)
            {
                BottomStatusRow.Height = ToGridLength(snapshot.BottomStatusRow);
            }

            if (snapshot.WindowWidth > 0)
            {
                Width = snapshot.WindowWidth;
            }

            if (snapshot.WindowHeight > 0)
            {
                Height = snapshot.WindowHeight;
            }

            _isCommandPreviewVisible = snapshot.CommandPreviewVisible;
            _isExecutionLogVisible = snapshot.ExecutionLogVisible;
        }

        private void ApplyCommandPanelSnapshot(CommandPanelStateSnapshot? snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            if (EmbeddedFFmpegPathTextBox != null)
            {
                EmbeddedFFmpegPathTextBox.Text = snapshot.EmbeddedFfmpegPath ?? EmbeddedFFmpegPathTextBox.Text;
            }

            if (EmbeddedCommandTextBox != null)
            {
                EmbeddedCommandTextBox.Text = snapshot.EmbeddedCommand ?? string.Empty;
            }

            if (EmbeddedOutputTextBox != null)
            {
                EmbeddedOutputTextBox.Text = snapshot.EmbeddedConsoleOutput ?? EmbeddedOutputTextBox.Text;
            }

            if (LogOutputBox != null)
            {
                LogOutputBox.Text = snapshot.ExecutionLog ?? LogOutputBox.Text;
            }

            if (CommandPreviewBox != null)
            {
                CommandPreviewBox.Text = snapshot.CommandPreview ?? CommandPreviewBox.Text;
            }

            if (CommandDescriptionBox != null)
            {
                CommandDescriptionBox.Text = snapshot.CommandDescription ?? CommandDescriptionBox.Text;
            }
        }

        private void ApplyBatchSubtitleTasksSnapshot(List<BatchSubtitleTaskSnapshot>? tasks)
        {
            if (_batchSubtitleCoordinator == null || tasks == null || tasks.Count == 0)
            {
                return;
            }

            // 清空现有任务（但保留正在处理的任务）
            var processingTasks = _batchSubtitleCoordinator.Tasks
                .Where(t => t.Status == Services.AiSubtitle.BatchSubtitleTaskStatus.Processing)
                .ToList();

            _batchSubtitleCoordinator.Tasks.Clear();

            // 恢复已保存的任务
            foreach (var taskSnapshot in tasks)
            {
                // 解析枚举值
                if (!Enum.TryParse<Services.AiSubtitle.AsrProvider>(taskSnapshot.Provider, out var provider))
                {
                    Services.DebugLogger.LogWarning($"无法解析批量字幕任务提供商: {taskSnapshot.Provider}");
                    continue;
                }

                if (!Enum.TryParse<Services.AiSubtitle.BatchSubtitleTaskType>(taskSnapshot.TaskType, out var taskType))
                {
                    Services.DebugLogger.LogWarning($"无法解析批量字幕任务类型: {taskSnapshot.TaskType}");
                    continue;
                }

                if (!Enum.TryParse<Services.AiSubtitle.BatchSubtitleTaskStatus>(taskSnapshot.Status, out var status))
                {
                    Services.DebugLogger.LogWarning($"无法解析批量字幕任务状态: {taskSnapshot.Status}");
                    status = Services.AiSubtitle.BatchSubtitleTaskStatus.Pending;
                }

                // 如果任务正在处理中，跳过恢复（避免冲突）
                if (status == Services.AiSubtitle.BatchSubtitleTaskStatus.Processing)
                {
                    continue;
                }

                var task = new Services.AiSubtitle.BatchSubtitleTask
                {
                    Id = taskSnapshot.Id,
                    SourceFilePath = taskSnapshot.SourceFilePath,
                    SourceFileName = taskSnapshot.SourceFileName,
                    ClipName = taskSnapshot.ClipName,
                    ClipStartTime = taskSnapshot.ClipStartTime,
                    ClipEndTime = taskSnapshot.ClipEndTime,
                    Provider = provider,
                    TaskType = taskType,
                    Status = status,
                    Progress = taskSnapshot.Progress,
                    OutputSrtPath = taskSnapshot.OutputSrtPath,
                    ErrorMessage = taskSnapshot.ErrorMessage
                };

                _batchSubtitleCoordinator.Tasks.Add(task);
            }

            // 如果有正在处理的任务，重新添加它们
            foreach (var processingTask in processingTasks)
            {
                _batchSubtitleCoordinator.Tasks.Add(processingTask);
            }

            OnPropertyChanged(nameof(BatchSubtitleCoordinator));
            Services.DebugLogger.LogInfo($"已恢复 {_batchSubtitleCoordinator.Tasks.Count} 个批量字幕任务");
        }

        private static GridLength ToGridLength(GridLengthSnapshot snapshot)
        {
            return snapshot.UnitType switch
            {
                GridUnitType.Auto => GridLength.Auto,
                GridUnitType.Star => new GridLength(snapshot.Value, GridUnitType.Star),
                GridUnitType.Pixel => new GridLength(snapshot.Value, GridUnitType.Pixel),
                _ => new GridLength(snapshot.Value, snapshot.UnitType)
            };
        }

        private void SelectComboBoxItemByContent(ComboBox? comboBox, string? targetContent)
        {
            if (comboBox == null || string.IsNullOrWhiteSpace(targetContent))
            {
                return;
            }

            var normalizedTarget = targetContent.Trim();

            foreach (var item in comboBox.Items)
            {
                if (item is ComboBoxItem comboBoxItem)
                {
                    var content = comboBoxItem.Content?.ToString() ?? string.Empty;
                    if (string.Equals(content, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(content) && normalizedTarget.StartsWith(content, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(content) && content.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase)))
                    {
                        comboBox.SelectedItem = comboBoxItem;
                        return;
                    }
                }
            }
        }

        private void UpdateWindowTitleWithProject()
        {
            Title = string.IsNullOrWhiteSpace(_currentProjectFilePath)
                ? BaseWindowTitle
                : $"{BaseWindowTitle} [{Path.GetFileName(_currentProjectFilePath)}]";
        }

        #endregion

        #region 拖放事件

        /// <summary>
        /// 拖放进入事件
        /// </summary>
        private void VideoFileListBox_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        /// <summary>
        /// 拖放释放事件
        /// </summary>
        private async void VideoFileListBox_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                try
            {
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                var files = new List<string>();
                var folders = new List<string>();

                // 区分文件和文件夹
                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        files.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        folders.Add(path);
                    }
                }

                    // 确保FFprobe路径已设置（如果还未设置，尝试自动查找）
                    if (_videoInformationService != null)
                    {
                        var ffprobePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe");
                        if (File.Exists(ffprobePath))
                        {
                            _videoInformationService.SetFFprobePath(ffprobePath);
                            Services.DebugLogger.LogInfo($"拖放文件时设置FFprobe路径: {ffprobePath}");
                        }
                        else
                        {
                            // 尝试从FFmpeg路径推断FFprobe路径
                            var ffmpegPath = EmbeddedFFmpegPathTextBox?.Text?.Trim();
                            if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                            {
                                var inferredFfprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                                if (File.Exists(inferredFfprobePath))
                                {
                                    _videoInformationService.SetFFprobePath(inferredFfprobePath);
                                    Services.DebugLogger.LogInfo($"拖放文件时从FFmpeg路径推断FFprobe路径: {inferredFfprobePath}");
                                }
                            }
                        }
                    }

                // 先添加文件夹（递归扫描）
                foreach (var folder in folders)
                {
                    await _videoListViewModel.AddFolderAsync(folder);
                }

                // 再添加单个文件
                if (files.Count > 0)
                {
                    await _videoListViewModel.AddFilesAsync(files.ToArray());
                }

                // 显示结果统计
                var totalItems = files.Count + folders.Count;
                    
                    // 确保文件信息正确显示
                    if (_videoListViewModel.SelectedFile != null)
                    {
                        OnPropertyChanged(nameof(VideoListViewModel));
                    }
                    
                if (totalItems > 0)
                {
                        var message = $"已添加 {files.Count} 个文件";
                        if (folders.Count > 0)
                        {
                            message += $"，从 {folders.Count} 个文件夹";
                        }
                        _videoListViewModel.StatusMessage = message;
                        Services.ToastNotification.ShowSuccess(message);
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"拖放文件失败: {ex.Message}");
                    Services.ToastNotification.ShowError($"拖放文件失败: {ex.Message}");
                }
            }
        }

        #endregion

        #region 列表双击事件

        /// <summary>
        /// 列表项双击事件 - 加载并播放视频
        /// </summary>
        private void VideoFileListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                PreparePlaybackViewState(isPlayRequested: true);

                // 获取选中文件的索引
                int index = _videoListViewModel.Files.IndexOf(_videoListViewModel.SelectedFile);
                if (index >= 0)
                {
                    // 通过索引加载视频
                    _videoPlayerViewModel.LoadVideoByIndex(index);
                    // 开始播放
                    _videoPlayerViewModel.Play();
                }
            }
        }

        /// <summary>
        /// 文件列表选择改变事件
        /// </summary>
        private void VideoFileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 确保文件信息区域更新
            if (_videoListViewModel.SelectedFile != null)
            {
                OnPropertyChanged(nameof(VideoListViewModel));
                Services.DebugLogger.LogInfo($"文件选择改变: {_videoListViewModel.SelectedFile.FileName}");
            }
        }

        #endregion

        #region 右键菜单事件

        /// <summary>
        /// 播放菜单项点击事件
        /// </summary>
        private void ContextMenu_Play_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                // 获取选中文件的索引
                int index = _videoListViewModel.Files.IndexOf(_videoListViewModel.SelectedFile);
                if (index >= 0)
                {
                    // 通过索引加载视频
                    _videoPlayerViewModel.LoadVideoByIndex(index);
                    // 开始播放
                    _videoPlayerViewModel.Play();
                }
            }
        }

        /// <summary>
        /// 在文件管理器中显示菜单项点击事件
        /// </summary>
        private void ContextMenu_ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
        {
            try
            {
                    var filePath = _videoListViewModel.SelectedFile.FilePath;
                    var directory = Path.GetDirectoryName(filePath);
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                    MessageBox.Show($"打开文件管理器失败: {ex.Message}", "错误", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 删除菜单项点击事件
        /// </summary>
        private void ContextMenu_Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                var result = MessageBox.Show($"确定要删除文件 \"{_videoListViewModel.SelectedFile.FileName}\" 吗？", 
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    RemoveFilesWithResourceCleanup(new List<VideoFile> { _videoListViewModel.SelectedFile });
                }
            }
        }

        /// <summary>
        /// 上移菜单项点击事件
        /// </summary>
        private void ContextMenu_MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                _videoListViewModel.MoveUp(_videoListViewModel.SelectedFile);
            }
        }

        /// <summary>
        /// 下移菜单项点击事件
        /// </summary>
        private void ContextMenu_MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                _videoListViewModel.MoveDown(_videoListViewModel.SelectedFile);
            }
        }

        /// <summary>
        /// 清空列表菜单项点击事件
        /// </summary>
        private void ContextMenu_Clear_Click(object sender, RoutedEventArgs e)
        {
            ClearAllButton_Click(sender, e);
        }

        /// <summary>
        /// 反选菜单项点击事件
        /// </summary>
        private void ContextMenu_InvertSelection_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.InvertSelection();
        }

        /// <summary>
        /// 定位到正在播放菜单项点击事件
        /// </summary>
        private void ContextMenu_LocateCurrentPlaying_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.LocateCurrentPlaying();
        }

        /// <summary>
        /// 按文件名升序排序
        /// </summary>
        private void ContextMenu_SortByNameAsc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortByNameAscending();
        }

        /// <summary>
        /// 按文件名降序排序
        /// </summary>
        private void ContextMenu_SortByNameDesc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortByNameDescending();
        }

        /// <summary>
        /// 按文件大小降序排序
        /// </summary>
        private void ContextMenu_SortBySizeDesc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortBySizeDescending();
        }

        /// <summary>
        /// 按文件大小升序排序
        /// </summary>
        private void ContextMenu_SortBySizeAsc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortBySizeAscending();
        }

        /// <summary>
        /// 按时长降序排序
        /// </summary>
        private void ContextMenu_SortByDurationDesc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortByDurationDescending();
        }

        /// <summary>
        /// 按时长升序排序
        /// </summary>
        private void ContextMenu_SortByDurationAsc_Click(object sender, RoutedEventArgs e)
        {
            _videoListViewModel.SortByDurationAscending();
        }

        #endregion

        #region 拖动框选事件

        /// <summary>
        /// 鼠标左键按下事件
        /// </summary>
        private void VideoFileListBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 检查是否点击在空白区域
            var hitTestResult = VisualTreeHelper.HitTest(VideoFileListBox, e.GetPosition(VideoFileListBox));
            var listViewItem = hitTestResult?.VisualHit as ListViewItem;
            
            if (listViewItem == null) // 点击在空白区域
            {
                _isSelecting = true;
                _selectionStart = e.GetPosition(SelectionCanvas);
                _selectionEnd = _selectionStart;
                
                // 显示选择框
                SelectionRectangle.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelectionRectangle, _selectionStart.X);
                Canvas.SetTop(SelectionRectangle, _selectionStart.Y);
                SelectionRectangle.Width = 0;
                SelectionRectangle.Height = 0;
                
                // 捕获鼠标
                VideoFileListBox.CaptureMouse();
            e.Handled = true;
            }
        }

        /// <summary>
        /// 鼠标移动事件
        /// </summary>
        private void VideoFileListBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _selectionEnd = e.GetPosition(SelectionCanvas);
                
                // 更新选择框位置和大小
                var left = Math.Min(_selectionStart.X, _selectionEnd.X);
                var top = Math.Min(_selectionStart.Y, _selectionEnd.Y);
                var width = Math.Abs(_selectionEnd.X - _selectionStart.X);
                var height = Math.Abs(_selectionEnd.Y - _selectionStart.Y);
                
                Canvas.SetLeft(SelectionRectangle, left);
                Canvas.SetTop(SelectionRectangle, top);
                SelectionRectangle.Width = width;
                SelectionRectangle.Height = height;
                
                // 更新选择状态
                UpdateSelectionFromRectangle();
            }
        }

        /// <summary>
        /// 鼠标左键释放事件
        /// </summary>
        private void VideoFileListBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
                
                // 隐藏选择框
                SelectionRectangle.Visibility = Visibility.Collapsed;
                
                // 释放鼠标捕获
                VideoFileListBox.ReleaseMouseCapture();
                
                // 最终更新选择状态
                UpdateSelectionFromRectangle();
                
                e.Handled = true;
            }
        }

        /// <summary>
        /// 根据选择框更新文件选择状态
        /// </summary>
        private void UpdateSelectionFromRectangle()
        {
            if (!_isSelecting && SelectionRectangle.Visibility != Visibility.Visible)
                return;

            var selectionRect = new Rect(
                Canvas.GetLeft(SelectionRectangle),
                Canvas.GetTop(SelectionRectangle),
                SelectionRectangle.Width,
                SelectionRectangle.Height);

            // 遍历所有ListViewItem，检查是否在选择框内
            for (int i = 0; i < VideoFileListBox.Items.Count; i++)
            {
                var container = VideoFileListBox.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                if (container != null && container.IsVisible)
            {
                try
                {
                        // 使用更安全的方法获取容器位置
                        var containerPoint = container.TranslatePoint(new Point(0, 0), SelectionCanvas);
                        var itemRect = new Rect(containerPoint.X, containerPoint.Y, container.ActualWidth, container.ActualHeight);
                        
                        var isInSelection = selectionRect.IntersectsWith(itemRect);
                        var videoFile = container.DataContext as VideoFile;
                        
                        if (videoFile != null)
                        {
                            videoFile.IsSelected = isInSelection;
                        }
                    }
                    catch (Exception)
                    {
                        // 如果无法获取位置，跳过
                        continue;
                    }
                }
            }
        }

        #endregion

        #region 播放控制事件

        /// <summary>
        /// 上一个按钮点击事件
        /// </summary>
        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("上一个功能将在集成 VLC 播放器后实现", "提示", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }


        /// <summary>
        /// 全屏按钮点击事件
        /// </summary>
        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("全屏功能将在集成 VLC 播放器后实现", "提示", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region 文件信息折叠功能

        // 文件信息已移至输出信息标签页，不再需要折叠功能
        // private void ToggleFileInfo_Click(object sender, RoutedEventArgs e)
        // {
        //     // 已删除
        // }

        #endregion

        #region 键盘快捷键

        /// <summary>
        /// 键盘快捷键处理
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 如果焦点在TextBox上，不处理快捷键
            if (e.OriginalSource is TextBox textBox && !textBox.IsReadOnly)
            {
                return;
            }

            bool handled = true;

            // Ctrl键组合
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.D1: // Ctrl + 1 播放器模式
                        ViewMode_PlayerOnlyMenuItem_Click(null, null);
                        handled = true;
                        break;
                    case Key.D2: // Ctrl + 2 剪辑模式
                        ViewMode_EditorMenuItem_Click(null, null);
                        handled = true;
                        break;
                    case Key.I: // Ctrl + I 跳转到入点
                        if (_videoPlayerViewModel.HasInPoint)
                        {
                            _videoPlayerViewModel.Seek(_videoPlayerViewModel.InPoint);
                            Services.DebugLogger.LogInfo($"跳转到入点: {_videoPlayerViewModel.FormattedInPoint}");
                        }
                        break;
                    case Key.O: // Ctrl + O 跳转到出点
                        if (_videoPlayerViewModel.HasOutPoint)
                        {
                            _videoPlayerViewModel.Seek(_videoPlayerViewModel.OutPoint);
                            Services.DebugLogger.LogInfo($"跳转到出点: {_videoPlayerViewModel.FormattedOutPoint}");
                        }
                        break;
                    case Key.F: // Ctrl + F 验证选中文件格式
                        ValidateSelectedFilesFormat_Click(null, null);
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            // Ctrl+Shift键组合
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                switch (e.Key)
                {
                    case Key.A: // Ctrl + Shift + A 反选
                        _videoListViewModel.InvertSelection();
                        handled = true;
                        break;
                    case Key.L: // Ctrl + Shift + L 定位到正在播放
                        _videoListViewModel.LocateCurrentPlaying();
                        handled = true;
                        break;
                    case Key.F: // Ctrl + Shift + F 验证所有文件格式
                        ValidateAllFilesFormat_Click(null, null);
                        handled = true;
                        break;
                    case Key.R: // Ctrl + Shift + R 全屏录制
                        ScreenRecorderFullMenuItem_Click(null, null);
                        handled = true;
                        break;
                    case Key.OemQuestion: // Ctrl + Shift + / (即 Ctrl + ?) 快捷键列表
                        ShowShortcutHelp();
                        handled = true;
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            // Ctrl+Shift+Alt键组合
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt))
            {
                switch (e.Key)
                {
                    case Key.R: // Ctrl + Shift + Alt + R 区域录制
                        ScreenRecorderRegionMenuItem_Click(null, null);
                        handled = true;
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            // Shift键组合
            else if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                switch (e.Key)
                {
                    case Key.Left:  // Shift + ← 快退100毫秒
                        _videoPlayerViewModel.SeekBackwardFastCommand.Execute(null);
                        break;
                    case Key.Right: // Shift + → 快进100毫秒
                        _videoPlayerViewModel.SeekForwardFastCommand.Execute(null);
                        break;
                    case Key.Space: // Shift + Space 播放标记区间
                        _videoPlayerViewModel.PlayMarkedRegionCommand.Execute(null);
                        break;
                    case Key.I: // Shift + I 清除入点
                        _videoPlayerViewModel.ClearInPointCommand.Execute(null);
                        break;
                    case Key.O: // Shift + O 清除出点
                        _videoPlayerViewModel.ClearOutPointCommand.Execute(null);
                        break;
                    case Key.X: // Shift + X 清除所有标记
                        _videoPlayerViewModel.ClearInPointCommand.Execute(null);
                        _videoPlayerViewModel.ClearOutPointCommand.Execute(null);
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            else // 无修饰键
            {
                switch (e.Key)
                {
                    case Key.Space: // Space 播放/暂停
                        _videoPlayerViewModel.PlayPauseCommand.Execute(null);
                        break;
                    case Key.S: // S 停止
                        _videoPlayerViewModel.StopCommand.Execute(null);
                        break;
                    case Key.Left: // ← 快退5秒
                        _videoPlayerViewModel.SeekBackwardCommand.Execute(null);
                        break;
                    case Key.Right: // → 快进5秒
                        _videoPlayerViewModel.SeekForwardCommand.Execute(null);
                        break;
                    case Key.Up: // ↑ 音量+
                        _videoPlayerViewModel.VolumeUpCommand.Execute(null);
                        break;
                    case Key.Down: // ↓ 音量-
                        _videoPlayerViewModel.VolumeDownCommand.Execute(null);
                        break;
                    case Key.M: // M 静音
                        _videoPlayerViewModel.MuteCommand.Execute(null);
                        break;
                    case Key.L: // L 循环播放
                        _videoPlayerViewModel.ToggleLoopCommand.Execute(null);
                        break;
                    case Key.OemOpenBrackets: // [ 减速
                        _videoPlayerViewModel.SpeedDownCommand.Execute(null);
                        break;
                    case Key.OemCloseBrackets: // ] 加速
                        _videoPlayerViewModel.SpeedUpCommand.Execute(null);
                        break;
                    case Key.R: // R 重置速度
                        _videoPlayerViewModel.ResetSpeedCommand.Execute(null);
                        break;
                    case Key.I: // I 标记入点
                        _videoPlayerViewModel.MarkInPointCommand.Execute(null);
                        break;
                    case Key.O: // O 标记出点
                        _videoPlayerViewModel.MarkOutPointCommand.Execute(null);
                        break;
                    case Key.Delete: // Delete 清除所有标记
                        _videoPlayerViewModel.ClearInPointCommand.Execute(null);
                        _videoPlayerViewModel.ClearOutPointCommand.Execute(null);
                        break;
                    case Key.PageUp: // Page Up 上一个视频
                        _videoPlayerViewModel.PlayPrevious();
                        break;
                    case Key.PageDown: // Page Down 下一个视频
                        _videoPlayerViewModel.PlayNext();
                        break;
                    case Key.F1: // F1 使用文档
                        ShowHelpDocument();
                        handled = true;
                        break;
                    case Key.F5: // F5 截图
                        _videoPlayerViewModel.TakeScreenshot();
                        handled = true;
                        break;
                    case Key.F12: // F12 关于
                        AboutMenuItem_Click(null, null);
                        handled = true;
                        break;
                    case Key.F11: // F11 全屏
                    case Key.F: // F 全屏
                        ToggleFullScreen();
                        break;
                    case Key.Escape: // Esc 退出全屏
                        if (_isFullScreen)
                        {
                            ExitFullScreen();
                        }
                        break;
                    case Key.OemQuestion: // ? 快捷键列表（Shift + / = ?）
                        if (Keyboard.Modifiers == ModifierKeys.Shift) // Shift + /  = ?
                        {
                            ShowShortcutHelp();
                            handled = true;
                        }
                        break;
                    case Key.Home: // Home 跳转到开始
                        _videoPlayerViewModel.Seek(0);
                        Services.DebugLogger.LogInfo("跳转到视频开始");
                        break;
                    case Key.End: // End 跳转到结束
                        if (_videoPlayerViewModel.Duration > 0)
                        {
                            _videoPlayerViewModel.Seek(_videoPlayerViewModel.Duration - 100); // 留100ms避免触发结束事件
                            Services.DebugLogger.LogInfo("跳转到视频结束");
                        }
                        break;
                    default:
                        handled = false;
                        break;
                }
            }

            e.Handled = handled;
        }

        #endregion

        #region 播放器区域拖拽

        /// <summary>
        /// 播放器区域拖放进入事件
        /// </summary>
        private void VideoPlayerArea_DragOver(object sender, DragEventArgs e)
        {
            // 如果正在处理拖放，不允许新的拖放操作
            if (_isHandlingVideoPlayerAreaDrop)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        // 防止拖放事件重复处理的标志
        private bool _isHandlingVideoPlayerAreaDrop = false;
        private readonly object _videoPlayerAreaDropLock = new object();

        /// <summary>
        /// 播放器区域拖放事件
        /// </summary>
        private async void VideoPlayerArea_Drop(object sender, DragEventArgs e)
        {
            // 标记事件已处理，防止事件继续传播
            e.Handled = true;
            
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            // 防止重复调用：如果正在处理拖放，忽略新的拖放事件
            lock (_videoPlayerAreaDropLock)
            {
                if (_isHandlingVideoPlayerAreaDrop)
                {
                    Services.DebugLogger.LogWarning("VideoPlayerArea_Drop: 正在处理拖放，忽略重复调用");
                    return;
                }
                _isHandlingVideoPlayerAreaDrop = true;
            }

            try
            {
                Services.DebugLogger.LogInfo($"VideoPlayerArea_Drop: 开始处理拖放，文件数: {paths.Length}");
                
                // 确保在UI线程上执行所有LibVLC操作
                if (Application.Current?.Dispatcher?.CheckAccess() == false)
                {
                    // 不在UI线程，切换到UI线程执行
                    // 使用Invoke而不是InvokeAsync，确保同步执行，避免并发问题
                    Application.Current.Dispatcher.Invoke(async () =>
                    {
                        await HandleVideoPlayerAreaDropAsync(paths);
                    });
                }
                else
                {
                    // 已在UI线程，直接执行
                    await HandleVideoPlayerAreaDropAsync(paths);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"VideoPlayerArea_Drop: 处理拖放时发生错误: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // 释放锁，允许下次拖放
                lock (_videoPlayerAreaDropLock)
                {
                    _isHandlingVideoPlayerAreaDrop = false;
                }
                Services.DebugLogger.LogInfo("VideoPlayerArea_Drop: 拖放处理完成");
            }
        }

        /// <summary>
        /// 处理播放器区域的拖放操作（在UI线程上执行）
        /// </summary>
        private async Task HandleVideoPlayerAreaDropAsync(string[] paths)
        {
            // 双重检查：确保不在并发执行
            if (_isHandlingVideoPlayerAreaDrop == false)
            {
                Services.DebugLogger.LogWarning("HandleVideoPlayerAreaDropAsync: 调用时未设置处理标志，可能存在并发问题");
            }
            
            try
            {
                Services.DebugLogger.LogInfo($"HandleVideoPlayerAreaDropAsync: 开始处理，路径数: {paths.Length}");
                var files = new List<string>();
                var folders = new List<string>();

                // 区分文件和文件夹
                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        files.Add(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        folders.Add(path);
                    }
                }

                // 确保FFprobe路径已设置（如果还未设置，尝试自动查找）
                if (_videoInformationService != null)
                {
                    var ffprobePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe");
                    if (File.Exists(ffprobePath))
                    {
                        _videoInformationService.SetFFprobePath(ffprobePath);
                        Services.DebugLogger.LogInfo($"拖放文件时设置FFprobe路径: {ffprobePath}");
                    }
                    else
                    {
                        // 尝试从FFmpeg路径推断FFprobe路径
                        var ffmpegPath = EmbeddedFFmpegPathTextBox?.Text?.Trim();
                        if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
                        {
                            var inferredFfprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                            if (File.Exists(inferredFfprobePath))
                            {
                                _videoInformationService.SetFFprobePath(inferredFfprobePath);
                                Services.DebugLogger.LogInfo($"拖放文件时从FFmpeg路径推断FFprobe路径: {inferredFfprobePath}");
                            }
                        }
                    }
                }

                // 先处理文件夹（递归扫描）
                foreach (var folder in folders)
                {
                    await _videoListViewModel.AddFolderAsync(folder);
                }

                // 过滤支持的媒体文件
                var supportedFiles = files
                    .Where(f => VideoListViewModel.SupportedMediaExtensions.Contains(Path.GetExtension(f)))
                    .ToArray();

                if (supportedFiles.Length > 0)
                {
                    Services.DebugLogger.LogSuccess($"拖拽到播放器: {supportedFiles.Length} 个媒体文件");

                    // 折中方案：如果正在播放，只添加到播放列表，不立即加载和播放
                    // 这样可以避免在播放时调用Stop()导致的崩溃问题
                    if (_videoPlayerViewModel.IsPlaying)
                    {
                        Services.DebugLogger.LogInfo("检测到正在播放，为避免崩溃，只添加到播放列表，不立即播放");
                        
                        // 所有文件都添加到列表，不立即播放
                        await _videoListViewModel.AddFilesAsync(supportedFiles);
                        Services.DebugLogger.LogInfo($"已将 {supportedFiles.Length} 个文件添加到播放列表");
                        
                        // 显示提示信息
                        var message = $"已添加 {supportedFiles.Length} 个文件到播放列表（当前正在播放，请稍后手动切换）";
                        _videoListViewModel.StatusMessage = message;
                        Services.ToastNotification.ShowInfo(message);
                    }
                    else
                    {
                        // 没有播放，可以安全地加载和播放
                        try
                        {
                            // 加载第一个文件（LoadVideo内部会确保停止和清理）
                            Services.DebugLogger.LogInfo($"加载视频: {Path.GetFileName(supportedFiles[0])}");
                            _videoPlayerViewModel.LoadVideo(supportedFiles[0]);
                            
                            // 等待一小段时间，确保Media已加载
                            await Task.Delay(100);
                            
                            // 播放
                            _videoPlayerViewModel.Play();
                            Services.DebugLogger.LogSuccess($"开始播放: {Path.GetFileName(supportedFiles[0])}");

                            // 其余文件添加到列表
                            if (supportedFiles.Length > 1)
                            {
                                var remainingFiles = supportedFiles.Skip(1).ToArray();
                                await _videoListViewModel.AddFilesAsync(remainingFiles);
                                Services.DebugLogger.LogInfo($"剩余 {remainingFiles.Length} 个文件已添加到列表");
                            }
                        }
                        catch (Exception loadEx)
                        {
                            Services.DebugLogger.LogError($"加载视频时发生错误: {loadEx.GetType().Name} - {loadEx.Message}\n{loadEx.StackTrace}");
                            // 如果加载失败，至少添加到列表
                            await _videoListViewModel.AddFilesAsync(supportedFiles);
                            Services.ToastNotification.ShowWarning($"加载视频失败，已添加到播放列表: {loadEx.Message}");
                        }
                    }

                    // 显示统计信息（如果还没有显示过）
                    if (!_videoPlayerViewModel.IsPlaying || supportedFiles.Length == 0)
                    {
                        var totalItems = supportedFiles.Length + folders.Count;
                        var message = $"已添加 {supportedFiles.Length} 个文件";
                        if (folders.Count > 0)
                        {
                            message += $"，从 {folders.Count} 个文件夹";
                        }
                        _videoListViewModel.StatusMessage = message;
                        Services.ToastNotification.ShowSuccess(message);
                    }
                }
                else if (folders.Count > 0)
                {
                    // 只有文件夹，没有文件
                    var message = $"已从 {folders.Count} 个文件夹添加文件";
                    _videoListViewModel.StatusMessage = message;
                    Services.ToastNotification.ShowSuccess(message);
                }
                else
                {
                    Services.DebugLogger.LogWarning("拖拽的文件中没有支持的媒体格式");
                    MessageBox.Show("没有找到支持的媒体文件!", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"HandleVideoPlayerAreaDropAsync: 处理拖放时发生错误: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}");
                Services.ToastNotification.ShowError($"拖放文件失败: {ex.Message}");
            }
            finally
            {
                Services.DebugLogger.LogInfo("HandleVideoPlayerAreaDropAsync: 方法执行完成");
            }
        }

        #endregion

        #region 上下曲按钮

        /// <summary>
        /// 上一个视频按钮点击
        /// </summary>
        private void PreviousVideoButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.PlayPrevious();
        }

        /// <summary>
        /// 下一个视频按钮点击
        /// </summary>
        private void NextVideoButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.PlayNext();
        }

        #endregion

        #region 格式验证功能

        /// <summary>
        /// 切换视图模式按钮点击事件（图片/播放器/命令提示符）
        /// </summary>
        private void SwitchViewModeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 循环切换：播放器(0) -> 图片(1) -> 命令提示符(2) -> 播放器(0)
                int nextMode = (_viewMode + 1) % 3;
                SetViewMode(nextMode);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"切换视图模式失败: {ex.Message}");
                MessageBox.Show($"切换视图模式失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ViewMode_PlayerOnlyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetInterfaceDisplayMode(InterfaceDisplayMode.PlayerOnly);
        }

        private void ViewMode_EditorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetInterfaceDisplayMode(InterfaceDisplayMode.Editor);
        }

        /// <summary>
        /// 更新视图模式按钮显示
        /// </summary>
        private void UpdateViewModeButton()
        {
            if (SwitchViewModeButtonText == null) return;

            switch (_viewMode)
            {
                case 0: // 播放器模式
                    SwitchViewModeButtonText.Text = "🎬";
                    SwitchViewModeButton.ToolTip = "当前：播放器 | 点击切换到图片模式";
                    break;
                case 1: // 图片模式
                    SwitchViewModeButtonText.Text = "🖼️";
                    SwitchViewModeButton.ToolTip = "当前：图片 | 点击切换到命令提示符模式";
                    break;
                case 2: // 命令提示符模式
                    SwitchViewModeButtonText.Text = "💻";
                    SwitchViewModeButton.ToolTip = "当前：命令提示符 | 点击切换到播放器模式";
                    break;
            }
        }

        /// <summary>
        /// FFmpeg 命令提示符菜单项点击事件
        /// </summary>
        private void OpenCommandPromptMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var commandPromptWindow = new Views.CommandPromptWindow
                {
                    Owner = this
                };
                commandPromptWindow.Show();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"打开命令提示符窗口失败: {ex.Message}");
                MessageBox.Show($"打开命令提示符窗口失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 打开执行日志窗口
        /// </summary>
        private void OpenLogWindowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logWindow = new Views.LogWindow
                {
                    Owner = this
                };
                logWindow.Show();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"打开执行日志窗口失败: {ex.Message}");
                MessageBox.Show($"打开执行日志窗口失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// FFmpeg 配置
        /// </summary>
        private void FFmpegConfigMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var configWindow = new Views.FFmpegConfigWindow
                {
                    Owner = this
                };

                // 加载当前FFmpeg路径
                string? currentFFmpegPath = null;
                if (EmbeddedFFmpegPathTextBox != null && !string.IsNullOrWhiteSpace(EmbeddedFFmpegPathTextBox.Text))
                {
                    currentFFmpegPath = EmbeddedFFmpegPathTextBox.Text.Trim();
                }
                else
                {
                    currentFFmpegPath = _videoProcessingService?.GetFFmpegPath();
                }

                if (!string.IsNullOrWhiteSpace(currentFFmpegPath))
                {
                    var textBox = configWindow.FindName("FFmpegPathTextBox") as System.Windows.Controls.TextBox;
                    if (textBox != null)
                    {
                        textBox.Text = currentFFmpegPath;
                    }
                }

                // 加载当前FFprobe路径（通常与FFmpeg在同一目录）
                if (!string.IsNullOrWhiteSpace(currentFFmpegPath))
                {
                    var ffprobePath = Path.Combine(Path.GetDirectoryName(currentFFmpegPath) ?? "", "ffprobe.exe");
                    if (File.Exists(ffprobePath))
                    {
                        var textBox = configWindow.FindName("FFprobePathTextBox") as System.Windows.Controls.TextBox;
                        if (textBox != null)
                        {
                            textBox.Text = ffprobePath;
                        }
                    }
                }

                if (configWindow.ShowDialog() == true)
                {
                    // 应用配置
                    if (!string.IsNullOrWhiteSpace(configWindow.FFmpegPath))
                    {
                        if (EmbeddedFFmpegPathTextBox != null)
                        {
                            EmbeddedFFmpegPathTextBox.Text = configWindow.FFmpegPath;
                        }
                        _videoProcessingService?.SetFFmpegPath(configWindow.FFmpegPath);
                        Services.DebugLogger.LogInfo($"已设置FFmpeg路径: {configWindow.FFmpegPath}");
                    }

                    if (!string.IsNullOrWhiteSpace(configWindow.FFprobePath))
                    {
                        _videoInformationService?.SetFFprobePath(configWindow.FFprobePath);
                        Services.DebugLogger.LogInfo($"已设置FFprobe路径: {configWindow.FFprobePath}");
                    }

                    // 更新BatchAiSubtitleCoordinator的FFmpeg路径
                    if (_batchSubtitleCoordinator != null && !string.IsNullOrWhiteSpace(configWindow.FFmpegPath))
                    {
                        _batchSubtitleCoordinator.SetFFmpegPath(configWindow.FFmpegPath);
                    }

                    MessageBox.Show("FFmpeg 配置已保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"FFmpeg配置失败: {ex.Message}");
                MessageBox.Show($"FFmpeg配置失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清理日志与缓存
        /// </summary>
        private void ClearLogsAndCacheMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "确定要清理日志和缓存吗？\n\n" +
                    "将执行以下操作：\n" +
                    "1. 删除当前日志文件\n" +
                    "2. 清理临时文件\n" +
                    "3. 清理缓存目录\n\n" +
                    "此操作不可撤销。",
                    "确认清理",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                int deletedCount = 0;
                long freedSpace = 0;

                // 1. 删除日志文件
                try
                {
                    var logFilePath = Services.DebugLogger.GetLogFilePath();
                    if (File.Exists(logFilePath))
                    {
                        var fileInfo = new FileInfo(logFilePath);
                        freedSpace += fileInfo.Length;
                        File.Delete(logFilePath);
                        deletedCount++;
                        Services.DebugLogger.LogInfo("已删除日志文件");
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogWarning($"删除日志文件失败: {ex.Message}");
                }

                // 2. 清理临时文件（FFmpeg相关）
                try
                {
                    var tempDir = Path.GetTempPath();
                    var tempFiles = Directory.GetFiles(tempDir, "ve_*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.Contains("ve_fw_") || f.Contains("ve_asr_"))
                        .ToList();

                    foreach (var file in tempFiles)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            freedSpace += fileInfo.Length;
                            File.Delete(file);
                            deletedCount++;
                        }
                        catch
                        {
                            // 忽略单个文件删除失败
                        }
                    }

                    // 清理临时目录
                    var tempDirs = Directory.GetDirectories(tempDir, "ve_*", SearchOption.TopDirectoryOnly)
                        .Where(d => d.Contains("ve_fw_") || d.Contains("ve_asr_"))
                        .ToList();

                    foreach (var dir in tempDirs)
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            freedSpace += dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                            Directory.Delete(dir, true);
                            deletedCount++;
                        }
                        catch
                        {
                            // 忽略单个目录删除失败
                        }
                    }

                    if (tempFiles.Count > 0 || tempDirs.Count > 0)
                    {
                        Services.DebugLogger.LogInfo($"已清理 {tempFiles.Count} 个临时文件和 {tempDirs.Count} 个临时目录");
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogWarning($"清理临时文件失败: {ex.Message}");
                }

                // 3. 清理缓存目录（如果有）
                try
                {
                    var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    var cacheDir = Path.Combine(appDirectory, "cache");
                    if (Directory.Exists(cacheDir))
                    {
                        var cacheFiles = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
                        foreach (var file in cacheFiles)
                        {
                            try
                            {
                                var fileInfo = new FileInfo(file);
                                freedSpace += fileInfo.Length;
                                File.Delete(file);
                                deletedCount++;
                            }
                            catch
                            {
                                // 忽略单个文件删除失败
                            }
                        }

                        // 删除空目录
                        var emptyDirs = Directory.GetDirectories(cacheDir, "*", SearchOption.AllDirectories)
                            .Where(d => !Directory.GetFileSystemEntries(d).Any())
                            .ToList();
                        foreach (var dir in emptyDirs)
                        {
                            try
                            {
                                Directory.Delete(dir);
                            }
                            catch
                            {
                                // 忽略
                            }
                        }

                        if (cacheFiles.Length > 0)
                        {
                            Services.DebugLogger.LogInfo($"已清理缓存目录: {cacheFiles.Length} 个文件");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogWarning($"清理缓存目录失败: {ex.Message}");
                }

                // 重新初始化日志系统
                Services.DebugLogger.Initialize();

                var freedSpaceMB = freedSpace / (1024.0 * 1024.0);
                MessageBox.Show(
                    $"清理完成！\n\n" +
                    $"已删除: {deletedCount} 个项目\n" +
                    $"释放空间: {freedSpaceMB:F2} MB",
                    "清理完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Services.DebugLogger.LogSuccess($"清理完成: 删除 {deletedCount} 个项目，释放 {freedSpaceMB:F2} MB");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"清理日志与缓存失败: {ex.Message}");
                MessageBox.Show($"清理失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ValidateAllFilesFormat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var results = await _videoListViewModel.ValidateAllFilesFormat();
                var stats = _videoListViewModel.GetFormatValidationStats();

                // 显示验证结果摘要
                var message = $"格式验证完成!\n\n" +
                             $"总文件数: {results.Count}\n" +
                             $"已验证: {stats.validated}\n" +
                             $"支持播放: {stats.supported}\n" +
                             $"不支持: {stats.unsupported}";

                if (stats.unsupported > 0)
                {
                    message += "\n\n不支持的文件将无法正常播放。";
                }

                MessageBox.Show(message, "格式验证结果", MessageBoxButton.OK,
                               stats.unsupported > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"格式验证失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 验证选中文件格式
        /// </summary>
        private async void ValidateSelectedFilesFormat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var results = await _videoListViewModel.ValidateSelectedFilesFormat();
                var supportedCount = results.Count(r => r.IsSupported);

                var message = $"选中文件验证完成!\n\n" +
                             $"选中文件数: {results.Count}\n" +
                             $"支持播放: {supportedCount}\n" +
                             $"不支持: {results.Count - supportedCount}";

                if (results.Any(r => !r.IsSupported))
                {
                    message += "\n\n不支持的文件:\n" + string.Join("\n",
                        results.Where(r => !r.IsSupported)
                               .Select(r => $"- {r.FileName}: {r.ErrorMessage}"));
                }

                MessageBox.Show(message, "选中文件验证结果", MessageBoxButton.OK,
                               results.Any(r => !r.IsSupported) ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"验证选中文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 时间框编辑功能

        /// <summary>
        /// 时间框按键事件 - 支持回车跳转
        /// </summary>
        private void TimeBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var textBox = sender as TextBox;
                if (textBox != null)
                {
                    if (_videoPlayerViewModel.ParseAndSeekToTime(textBox.Text, out string error))
                    {
                        // 跳转成功,失去焦点
                        Keyboard.ClearFocus();
                    }
                    else
                    {
                        // 显示错误提示
                        MessageBox.Show(error, "时间格式错误", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        textBox.SelectAll();
                    }
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// ScrollViewer鼠标滚轮事件处理 - 确保滚轮滚动正常工作
        /// </summary>
        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                // 手动处理滚轮滚动
                if (e.Delta > 0)
                {
                    scrollViewer.LineUp();
                }
                else
                {
                    scrollViewer.LineDown();
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// 双击入点时间框 - 跳转到入点
        /// </summary>
        private void InPointTimeBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_videoPlayerViewModel.HasInPoint)
            {
                _videoPlayerViewModel.Seek(_videoPlayerViewModel.InPoint);
                System.Diagnostics.Debug.WriteLine($"双击跳转到入点: {_videoPlayerViewModel.FormattedInPoint}");
            }
        }

        /// <summary>
        /// 双击出点时间框 - 跳转到出点
        /// </summary>
        private void OutPointTimeBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_videoPlayerViewModel.HasOutPoint)
            {
                _videoPlayerViewModel.Seek(_videoPlayerViewModel.OutPoint);
                System.Diagnostics.Debug.WriteLine($"双击跳转到出点: {_videoPlayerViewModel.FormattedOutPoint}");
            }
        }

        #endregion

        #region 全屏功能

        /// <summary>
        /// 切换全屏模式
        /// </summary>
        private void ToggleFullScreen()
        {
            if (_isFullScreen)
            {
                // 退出全屏
                ExitFullScreen();
            }
            else
            {
                // 进入全屏
                EnterFullScreen();
            }
        }

        /// <summary>
        /// 进入全屏
        /// </summary>
        private void EnterFullScreen()
        {
            try
            {
                if (_interfaceMode == InterfaceDisplayMode.PlayerOnly && !_isFullScreen)
                {
                    _playerModeWindowWidthBeforeFullScreen = Width;
                    _playerModeWindowHeightBeforeFullScreen = Height;
                    Services.DebugLogger.LogInfo($"[FullScreen] Cached player mode size before entering fullscreen: width={_playerModeWindowWidthBeforeFullScreen:F0}, height={_playerModeWindowHeightBeforeFullScreen:F0}");
                }
                else
                {
                    _playerModeWindowWidthBeforeFullScreen = null;
                    _playerModeWindowHeightBeforeFullScreen = null;
                }

                CacheInterfaceLayoutSizes();

                // 保存当前状态
                _previousWindowState = WindowState;
                _previousWindowStyle = WindowStyle;
                _previousResizeMode = ResizeMode;

                // 设置全屏
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                ResizeMode = ResizeMode.NoResize;

                // 隐藏左侧文件列表和右侧信息区域(通过命名直接访问)
                LeftColumn.Width = new GridLength(0);
                LeftSplitter.Width = new GridLength(0);
                RightSplitter.Width = new GridLength(0);
                RightColumn.Width = new GridLength(0);
                
                // 隐藏菜单栏和状态栏
                TopMenuBar.Visibility = Visibility.Collapsed;
                BottomStatusBar.Visibility = Visibility.Collapsed;
                
                // 隐藏中间列的下半部分(剪辑选项)
                MiddleSplitter.Height = new GridLength(0);
                BottomRow.Height = new GridLength(0);

                _isFullScreen = true;
                Services.DebugLogger.LogInfo("进入全屏模式");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"进入全屏失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 退出全屏
        /// </summary>
        private void ExitFullScreen()
        {
            try
            {
                Services.DebugLogger.LogInfo($"[ExitFullScreen] Begin: interfaceMode={_interfaceMode}, windowState={WindowState}, width={Width:F0}, height={Height:F0}, lastPlayerSize=({_playerModeLastWindowWidth?.ToString("F0") ?? "null"}, {_playerModeLastWindowHeight?.ToString("F0") ?? "null"})");

                if (_interfaceMode == InterfaceDisplayMode.PlayerOnly)
                {
                    _isRestoringPlayerModeSize = true;
                    Services.DebugLogger.LogInfo("[ExitFullScreen] Player mode detected, set _isRestoringPlayerModeSize = true");
                }

                // 恢复窗口状态
                WindowStyle = _previousWindowStyle;
                WindowState = _previousWindowState;
                ResizeMode = _previousResizeMode;
                Services.DebugLogger.LogInfo($"[ExitFullScreen] Window chrome restored: windowState={WindowState}, windowStyle={WindowStyle}, resizeMode={ResizeMode}");

                // 恢复菜单栏和状态栏
                TopMenuBar.Visibility = Visibility.Visible;
                BottomStatusBar.Visibility = Visibility.Visible;
                Services.DebugLogger.LogInfo("[ExitFullScreen] Menu and status bar restored");

                _isFullScreen = false;
                Services.DebugLogger.LogInfo("[ExitFullScreen] _isFullScreen set to false, scheduling layout refresh");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Services.DebugLogger.LogInfo("[ExitFullScreen] Dispatcher invoked, applying layout and player sizing");

                    if (_playerModeWindowWidthBeforeFullScreen.HasValue || _playerModeWindowHeightBeforeFullScreen.HasValue)
                    {
                        Services.DebugLogger.LogInfo($"[ExitFullScreen] Restoring cached player mode size from before fullscreen: width={_playerModeWindowWidthBeforeFullScreen?.ToString("F0") ?? "null"}, height={_playerModeWindowHeightBeforeFullScreen?.ToString("F0") ?? "null"}");
                        if (_playerModeWindowWidthBeforeFullScreen.HasValue)
                        {
                            var restoreWidth = Math.Max(_playerModeWindowWidthBeforeFullScreen.Value, PlayerModeWindowWidth);
                            if (_playerModeLastWindowWidth != restoreWidth)
                            {
                                Services.DebugLogger.LogInfo($"[ExitFullScreen] Override _playerModeLastWindowWidth: {_playerModeLastWindowWidth?.ToString("F0") ?? "null"} -> {restoreWidth:F0}");
                            }
                            _playerModeLastWindowWidth = restoreWidth;
                        }
                        if (_playerModeWindowHeightBeforeFullScreen.HasValue)
                        {
                            var restoreHeight = Math.Max(_playerModeWindowHeightBeforeFullScreen.Value, PlayerModeWindowHeight);
                            if (_playerModeLastWindowHeight != restoreHeight)
                            {
                                Services.DebugLogger.LogInfo($"[ExitFullScreen] Override _playerModeLastWindowHeight: {_playerModeLastWindowHeight?.ToString("F0") ?? "null"} -> {restoreHeight:F0}");
                            }
                            _playerModeLastWindowHeight = restoreHeight;
                        }
                    }

                    ApplyInterfaceModeLayout();
                    ApplyOutputPanelVisibility();
                    UpdatePlayerModeWindowSizing();
                    UpdatePlayerLayoutForMode();
                    _isRestoringPlayerModeSize = false;
                    _playerModeWindowWidthBeforeFullScreen = null;
                    _playerModeWindowHeightBeforeFullScreen = null;
                    Services.DebugLogger.LogInfo($"[ExitFullScreen] Layout applied, _isRestoringPlayerModeSize reset. Current size: {Width:F0}x{Height:F0}");
                }), DispatcherPriority.ApplicationIdle);
                Services.DebugLogger.LogInfo("退出全屏模式");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"退出全屏失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示快捷键帮助对话框
        /// </summary>
        private void ShowShortcutHelp()
        {
            var dialog = new Views.ShortcutHelpDialog();
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        /// <summary>
        /// 显示使用文档
        /// </summary>
        private void ShowHelpDocument()
        {
            var dialog = new Views.HelpDocumentWindow();
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        /// <summary>
        /// 全屏按钮点击
        /// </summary>
        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        /// <summary>
        /// 菜单栏全屏播放器点击
        /// </summary>
        private void MenuFullScreenPlayer_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        #endregion

        #region 帮助菜单

        /// <summary>
        /// 显示使用文档菜单项点击
        /// </summary>
        private void ShowHelpDocumentMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowHelpDocument();
        }

        /// <summary>
        /// 显示快捷键列表菜单项点击
        /// </summary>
        private void ShowShortcutHelpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowShortcutHelp();
        }

        /// <summary>
        /// 检查更新菜单项点击
        /// </summary>
        private async void CheckUpdateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.UpdateDialog();
            dialog.Owner = this;
            dialog.ShowLoading();
            dialog.Show();

            try
            {
                var updateInfo = await _updateCheckerService.CheckForUpdatesAsync();
                
                if (updateInfo.HasUpdate)
                {
                    dialog.ShowUpdateInfo(updateInfo);
                }
                else
                {
                    dialog.ShowUpToDate(updateInfo);
                }
            }
            catch (Exception ex)
            {
                dialog.ShowError(ex.Message);
                Services.DebugLogger.LogError($"检查更新失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 关于菜单项点击
        /// </summary>
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.AboutWindow();
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        #endregion

        #region 工具菜单

        private async void MediaInfoAnalyzerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isMediaInfoAnalyzing)
            {
                Services.ToastNotification.ShowInfo("MediaInfo 正在处理中，请稍候...");
                return;
            }

            try
            {
                var targets = CollectMediaFilesForAnalysis();
                if (targets.Count == 0)
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择要分析的媒体文件",
                        Filter = VideoListViewModel.MediaFileDialogFilter,
                        Multiselect = true,
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                    };

                    if (dialog.ShowDialog() != true)
                    {
                        Services.ToastNotification.ShowInfo("未选择任何媒体文件。");
                        return;
                    }

                    targets = dialog.FileNames.ToList();
                }

                await AnalyzeFilesWithMediaInfoAsync(targets);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"MediaInfo 扫描失败: {ex.Message}");
                MessageBox.Show($"MediaInfo 扫描失败：{ex.Message}", "媒体信息", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task AnalyzeFilesWithMediaInfoAsync(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
            {
                Services.ToastNotification.ShowWarning("没有可供分析的媒体文件。");
                return;
            }

            var existingFiles = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (existingFiles.Count == 0)
            {
                Services.ToastNotification.ShowWarning("所选文件不存在或已被移动。");
                return;
            }

            var mediaInfoPath = ResolveMediaInfoExecutablePath();
            if (string.IsNullOrWhiteSpace(mediaInfoPath) || !File.Exists(mediaInfoPath))
            {
                var message = "未找到 MediaInfo.exe，请确认 tools/MediaInfo 目录存在或手动指定路径。";
                Services.DebugLogger.LogError(message);
                MessageBox.Show(message, "媒体信息", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isMediaInfoAnalyzing = true;
            try
            {
                EnsureExecutionLogPanelVisible();
                AppendExecutionLogLine($"[MediaInfo] 使用工具: {mediaInfoPath}");

                foreach (var filePath in existingFiles)
                {
                    AppendExecutionLogLine($"[MediaInfo] 开始分析: {filePath}");
                    var result = await Task.Run(() => RunMediaInfoProcess(mediaInfoPath, filePath));

                    if (!result.Success)
                    {
                        var errorSnippet = string.IsNullOrWhiteSpace(result.Error)
                            ? $"退出代码 {result.ExitCode}"
                            : result.Error.Trim();
                        AppendExecutionLogLine($"[MediaInfo] ❌ {Path.GetFileName(filePath)} - {errorSnippet}");
                        continue;
                    }

                    var summary = BuildMediaInfoSummary(filePath, result.Output);
                    AppendExecutionLogLine(summary);
                }

                Services.ToastNotification.ShowSuccess($"MediaInfo 已完成 {existingFiles.Count} 个文件的扫描");
            }
            finally
            {
                _isMediaInfoAnalyzing = false;
            }
        }

        private static MediaInfoProcessResult RunMediaInfoProcess(string mediaInfoExePath, string targetFile)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mediaInfoExePath,
                    Arguments = $"--Output=JSON \"{targetFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new MediaInfoProcessResult(false, string.Empty, "无法启动 MediaInfo 进程。", -1);
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                var success = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                return new MediaInfoProcessResult(success, output, error, process.ExitCode);
            }
            catch (Exception ex)
            {
                return new MediaInfoProcessResult(false, string.Empty, ex.Message, -1);
            }
        }

        private string BuildMediaInfoSummary(string filePath, string mediaInfoJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(mediaInfoJson);
                if (!doc.RootElement.TryGetProperty("media", out var mediaElement))
                {
                    return $"[MediaInfo] {Path.GetFileName(filePath)} -> 无法解析输出。";
                }

                if (!mediaElement.TryGetProperty("track", out var trackElement))
                {
                    return $"[MediaInfo] {Path.GetFileName(filePath)} -> 没有 track 信息。";
                }

                string? generalFormat = null;
                string? generalDuration = null;
                string? generalSize = null;
                string? generalBitrate = null;
                string? videoCodec = null;
                string? width = null;
                string? height = null;
                string? frameRate = null;
                string? videoBitrate = null;
                string? audioCodec = null;
                string? audioChannels = null;
                string? audioSampleRate = null;
                string? audioBitrate = null;

                void Extract(JsonElement track)
                {
                    var type = GetJsonValue(track, "@type");
                    if (string.Equals(type, "General", StringComparison.OrdinalIgnoreCase))
                    {
                        generalFormat ??= GetJsonValue(track, "Format/String", "Format");
                        generalDuration ??= GetJsonValue(track, "Duration/String", "Duration");
                        generalSize ??= GetJsonValue(track, "FileSize/String", "FileSize");
                        generalBitrate ??= GetJsonValue(track, "OverallBitRate/String", "OverallBitRate");
                    }
                    else if (string.Equals(type, "Video", StringComparison.OrdinalIgnoreCase))
                    {
                        videoCodec ??= GetJsonValue(track, "Format/String", "Format_Profile", "CodecID");
                        width ??= GetJsonValue(track, "Width/String", "Width");
                        height ??= GetJsonValue(track, "Height/String", "Height");
                        frameRate ??= GetJsonValue(track, "FrameRate/String", "FrameRate");
                        videoBitrate ??= GetJsonValue(track, "BitRate/String", "BitRate");
                    }
                    else if (string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audioCodec ??= GetJsonValue(track, "Format/String", "CodecID");
                        audioChannels ??= GetJsonValue(track, "Channel(s)_Original", "Channel(s)/String", "Channel(s)");
                        audioSampleRate ??= GetJsonValue(track, "SamplingRate/String", "SamplingRate");
                        audioBitrate ??= GetJsonValue(track, "BitRate/String", "BitRate");
                    }
                }

                if (trackElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var track in trackElement.EnumerateArray())
                    {
                        Extract(track);
                    }
                }
                else if (trackElement.ValueKind == JsonValueKind.Object)
                {
                    Extract(trackElement);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[MediaInfo] {Path.GetFileName(filePath)}");
                if (!string.IsNullOrWhiteSpace(generalFormat) || !string.IsNullOrWhiteSpace(generalDuration) || !string.IsNullOrWhiteSpace(generalSize))
                {
                    sb.AppendLine($"  • 封装: {generalFormat ?? "-"} | 时长: {generalDuration ?? "-"} | 大小: {generalSize ?? "-"} | 总码率: {generalBitrate ?? "-"}");
                }
                if (!string.IsNullOrWhiteSpace(videoCodec) || !string.IsNullOrWhiteSpace(width) || !string.IsNullOrWhiteSpace(height))
                {
                    sb.AppendLine($"  • 视频: {videoCodec ?? "-"} | 分辨率: {width ?? "?"} x {height ?? "?"} | 帧率: {frameRate ?? "-"} | 码率: {videoBitrate ?? "-"}");
                }
                if (!string.IsNullOrWhiteSpace(audioCodec) || !string.IsNullOrWhiteSpace(audioChannels))
                {
                    sb.AppendLine($"  • 音频: {audioCodec ?? "-"} | 声道: {audioChannels ?? "-"} | 采样率: {audioSampleRate ?? "-"} | 码率: {audioBitrate ?? "-"}");
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"[MediaInfo] {Path.GetFileName(filePath)} -> 无法解析输出 ({ex.Message})";
            }
        }

        private static string? GetJsonValue(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var property))
                {
                    continue;
                }

                var value = property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Number => property.ToString(),
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private List<string> CollectMediaFilesForAnalysis()
        {
            var result = new List<string>();

            if (_videoListViewModel != null)
            {
                result.AddRange(_videoListViewModel.Files
                    .Where(f => f.IsSelected)
                    .Select(f => f.FilePath));

                if (result.Count == 0 && _videoListViewModel.SelectedFile != null)
                {
                    result.Add(_videoListViewModel.SelectedFile.FilePath);
                }

                if (result.Count == 0 && _videoListViewModel.CurrentPlayingVideo != null)
                {
                    result.Add(_videoListViewModel.CurrentPlayingVideo.FilePath);
                }
            }

            return result
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string? ResolveMediaInfoExecutablePath()
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "MediaInfo.exe"),
                Path.Combine(baseDir, "MediaInfoCLI.exe"),
                Path.Combine(baseDir, "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(baseDir, "..", "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(baseDir, "..", "..", "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(baseDir, "..", "..", "..", "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(baseDir, "..", "..", "..", "..", "..", "tools", "MediaInfo", "MediaInfo.exe"),
                Path.Combine(Environment.CurrentDirectory, "tools", "MediaInfo", "MediaInfo.exe"),
                @"D:\VideoEditor\tools\MediaInfo\MediaInfo.exe"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch
                {
                    // 忽略路径解析异常
                }
            }

            return null;
        }

        private async void ScreenRecorderFullMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await HandleScreenRecordingRequestAsync(ScreenRecordingMode.FullScreen);
        }

        private async void ScreenRecorderRegionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await HandleScreenRecordingRequestAsync(ScreenRecordingMode.Region);
        }

        private async void AiSubtitleGeneratorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await GenerateSubtitlesWithAiAsync();
        }

        private async void BcutAsrMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await GenerateSubtitlesWithBcutAsync();
        }

        private async void JianYingAsrMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await GenerateSubtitlesWithJianYingAsync();
        }

        private async void FasterWhisperAsrMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await GenerateSubtitlesWithFasterWhisperAsync();
        }

        private void ConfigureFasterWhisperMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.FasterWhisperConfigWindow
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        private BcutAsrService GetOrCreateBcutService()
        {
            _bcutAsrService ??= new BcutAsrService(_httpClient);
            return _bcutAsrService;
        }

        private JianYingAsrService GetOrCreateJianYingService()
        {
            _jianYingAsrService ??= new JianYingAsrService(_httpClient);
            return _jianYingAsrService;
        }

        private async Task GenerateSubtitlesWithBcutAsync()
        {
            await GenerateSubtitlesWithSpecificAsrAsync(
                "B 接口 (BcutASR)",
                async (audioPath, progress, token) =>
                {
                    var service = GetOrCreateBcutService();
                    return await service.TranscribeAsync(audioPath, false, progress, token);
                });
        }

        private async Task GenerateSubtitlesWithJianYingAsync()
        {
            await GenerateSubtitlesWithSpecificAsrAsync(
                "J 接口 (JianYingASR)",
                async (audioPath, progress, token) =>
                {
                    var service = GetOrCreateJianYingService();
                    return await service.TranscribeAsync(audioPath, false, progress, token);
                });
        }

        private async Task GenerateSubtitlesWithFasterWhisperAsync()
        {
            var programPath = Properties.Settings.Default.FasterWhisperProgramPath;

            if (string.IsNullOrWhiteSpace(programPath) || !File.Exists(programPath))
            {
                MessageBox.Show("请先在「配置 Faster Whisper...」中设置程序路径。", "Faster Whisper 字幕",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                ConfigureFasterWhisperMenuItem_Click(this, new RoutedEventArgs());
                return;
            }

            if (!TryResolveFasterWhisperModelInfo(out var modelInfo))
            {
                MessageBox.Show("请先在「配置 Faster Whisper...」中设置模型库根目录和模型。", "Faster Whisper 字幕",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                ConfigureFasterWhisperMenuItem_Click(this, new RoutedEventArgs());
                return;
            }

            await GenerateSubtitlesWithSpecificAsrAsync(
                "Faster Whisper (本地)",
                async (audioPath, progress, token) =>
                {
                    var device = Properties.Settings.Default.FasterWhisperDevice;
                    if (string.IsNullOrWhiteSpace(device))
                    {
                        device = "cpu";
                    }

                    var service = new FasterWhisperService(
                        programPath,
                        modelInfo.ModelArgument,
                        modelInfo.ModelsRootDir,
                        modelInfo.ModelDirectory,
                        language: "zh",
                        device: device,
                        needWordTimeStamp: false,
                        vadFilter: true,
                        vadThreshold: 0.4,
                        vadMethod: null,
                        prompt: null);

                    return await service.TranscribeAsync(audioPath, progress, token);
                });
        }

        private sealed record FasterWhisperModelInfo(string ModelsRootDir, string ModelDirectory, string ModelArgument);

        private bool TryResolveFasterWhisperModelInfo(out FasterWhisperModelInfo info)
        {
            var root = Properties.Settings.Default.FasterWhisperModelsRootDir;
            var selectedFolder = Properties.Settings.Default.FasterWhisperSelectedModel;

            if (!string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(selectedFolder))
            {
                if (TryBuildFasterWhisperModelInfo(root, selectedFolder, out info))
                {
                    return true;
                }
            }

            var legacyDir = Properties.Settings.Default.FasterWhisperModelDir;
            if (!string.IsNullOrWhiteSpace(legacyDir) && Directory.Exists(legacyDir))
            {
                var folderName = new DirectoryInfo(legacyDir).Name;
                var legacyRoot = Directory.GetParent(legacyDir)?.FullName ?? legacyDir;
                if (TryBuildFasterWhisperModelInfo(legacyRoot, folderName, out info))
                {
                    return true;
                }
            }

            info = null!;
            return false;
        }

        private bool TryBuildFasterWhisperModelInfo(string rootDir, string folderName, out FasterWhisperModelInfo info)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(folderName))
            {
                info = null!;
                return false;
            }

            var normalizedRoot = Path.GetFullPath(rootDir);
            var normalizedFolder = folderName.Trim();
            var modelArg = NormalizeFasterWhisperModelArgument(normalizedFolder);
            if (string.IsNullOrWhiteSpace(modelArg))
            {
                info = null!;
                return false;
            }

            var preferredDir = Path.Combine(normalizedRoot, $"faster-whisper-{modelArg}");
            string? actualModelDir = null;

            if (Directory.Exists(preferredDir))
            {
                actualModelDir = preferredDir;
            }
            else
            {
                var candidate = Path.Combine(normalizedRoot, normalizedFolder);
                if (Directory.Exists(candidate) &&
                    normalizedFolder.StartsWith("faster-whisper-", StringComparison.OrdinalIgnoreCase))
                {
                    actualModelDir = candidate;
                    preferredDir = candidate;
                }
            }

            if (actualModelDir == null)
            {
                info = null!;
                return false;
            }

            var modelsRootForCli = Directory.GetParent(actualModelDir)?.FullName;
            if (string.IsNullOrWhiteSpace(modelsRootForCli))
            {
                modelsRootForCli = normalizedRoot;
            }

            info = new FasterWhisperModelInfo(modelsRootForCli, actualModelDir, modelArg);
            return true;
        }

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

        private async Task GenerateSubtitlesWithSpecificAsrAsync(
            string asrName,
            Func<string, IProgress<(int progress, string message)>?, CancellationToken, Task<string>> transcribeFunc)
        {
            if (_isAiSubtitleGenerating)
            {
                Services.ToastNotification.ShowInfo("已有 AI 字幕生成任务在进行中，请稍候...");
                return;
            }

            var mediaFile = ResolveSubtitleTargetFile();
            if (string.IsNullOrWhiteSpace(mediaFile))
            {
                Services.ToastNotification.ShowWarning("未选择任何媒体文件。");
                return;
            }

            var ffmpegPath = ResolveScreenRecorderFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg，可在工具 → FFmpeg 配置中设置。", "AI 字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _aiSubtitleCts = new CancellationTokenSource();
            _isAiSubtitleGenerating = true;

            _currentAiSubtitleTask = CreateTaskProgress("AI 字幕", Path.GetFileName(mediaFile) ?? mediaFile, $"ASR: {asrName}");
            UpdateTaskProgress(_currentAiSubtitleTask, 0, "准备中", "正在提取音频...");

            try
            {
                EnsureExecutionLogPanelVisible();
                AppendExecutionLogLine($"[AI Subtitle/{asrName}] 目标文件: {mediaFile}");

                // 1. 提取音频为临时文件（参考 VideoCaptioner：先统一音频）
                var tempAudio = Path.Combine(Path.GetTempPath(), $"ve_asr_{Guid.NewGuid():N}.wav");

                var ffmpegArgs = $"-y -i \"{mediaFile}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{tempAudio}\"";
                await RunFfmpegForAsrAsync(ffmpegPath, ffmpegArgs, _currentAiSubtitleTask, _aiSubtitleCts.Token);

                if (!File.Exists(tempAudio))
                {
                    throw new FileNotFoundException("音频提取失败，未生成中间音频文件。");
                }

                UpdateTaskProgress(_currentAiSubtitleTask, 40, "调用 ASR", $"正在使用 {asrName} 转录...");

                // 2. 调用具体 ASR 服务
                var subtitleContent = await transcribeFunc(
                    tempAudio,
                    new Progress<(int progress, string message)>(p =>
                    {
                        UpdateTaskProgress(_currentAiSubtitleTask, p.progress, p.message, string.Empty);
                        AppendExecutionLogLine($"[AI Subtitle/{asrName}] {p.message} ({p.progress}%)");
                    }),
                    _aiSubtitleCts.Token);

                // 3. 写入 SRT 文件
                var outputPath = Path.Combine(
                    Path.GetDirectoryName(mediaFile) ?? Environment.CurrentDirectory,
                    $"{Path.GetFileNameWithoutExtension(mediaFile)}.srt");

                UpdateTaskProgress(_currentAiSubtitleTask, 95, "写入文件", "生成 SRT 文件...");
                await File.WriteAllTextAsync(outputPath, subtitleContent, Encoding.UTF8, _aiSubtitleCts.Token);

                Dispatcher.Invoke(() =>
                {
                    if (SubtitlePathBox != null)
                    {
                        SubtitlePathBox.Text = outputPath;
                    }

                    LoadSubtitleFile(outputPath);
                });

                AppendExecutionLogLine($"[AI Subtitle/{asrName}] 成功生成字幕文件：{outputPath}");
                Services.ToastNotification.ShowSuccess($"{asrName} 字幕生成完成：{outputPath}");
                CompleteTaskProgress(_currentAiSubtitleTask, true, "字幕生成完成");
            }
            catch (OperationCanceledException)
            {
                Services.ToastNotification.ShowWarning("已取消 AI 字幕生成任务。");
                AppendExecutionLogLine($"[AI Subtitle/{asrName}] 任务被用户取消。");
                CompleteTaskProgress(_currentAiSubtitleTask, false, "任务已取消");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"{asrName} 字幕生成失败: {ex}");
                MessageBox.Show($"{asrName} 字幕生成失败：{ex.Message}", "AI 字幕", MessageBoxButton.OK, MessageBoxImage.Error);
                CompleteTaskProgress(_currentAiSubtitleTask, false, $"失败：{ex.Message}");
            }
            finally
            {
                _aiSubtitleCts?.Dispose();
                _aiSubtitleCts = null;
                _isAiSubtitleGenerating = false;
                _currentAiSubtitleTask = null;
            }
        }

        private async Task RunFfmpegForAsrAsync(
            string ffmpegPath,
            string arguments,
            TaskProgressItem task,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            AppendExecutionLogLine($"[AI Subtitle/FFmpeg] {ffmpegPath} {arguments}");

            process.Start();

            await Task.Run(async () =>
            {
                while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        // 这里可以解析 ffmpeg 进度，但先简单记录日志
                        AppendExecutionLogLine($"[AI Subtitle/FFmpeg] {line}");
                    }
                    else
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
            }, cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 执行失败，退出码: {process.ExitCode}");
            }

            UpdateTaskProgress(task, 35, "音频已提取", "准备调用 ASR 服务...");
        }

        private void ManageAiProvidersMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ShowAiProviderManagerDialog();
        }

        private void AiSubtitleOptionsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.AiSubtitleOptionsWindow
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                Services.ToastNotification.ShowSuccess("AI 字幕选项已保存");
            }
        }

        private void ConfigureScreenRecorderAudioMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.ScreenRecorderAudioConfigWindow
            {
                Owner = this,
                SystemAudioDevice = Properties.Settings.Default.ScreenRecorderSystemAudioDevice,
                MicrophoneDevice = Properties.Settings.Default.ScreenRecorderMicrophoneDevice
            };

            if (dialog.ShowDialog() == true)
            {
                Properties.Settings.Default.ScreenRecorderSystemAudioDevice = dialog.SystemAudioDevice ?? string.Empty;
                Properties.Settings.Default.ScreenRecorderMicrophoneDevice = dialog.MicrophoneDevice ?? string.Empty;
                Properties.Settings.Default.Save();
                Services.ToastNotification.ShowSuccess("屏幕录制音频设备已保存。");
            }
        }

        private void ShowPlannedToolToast(string featureName, string detail)
        {
            Services.ToastNotification.ShowInfo($"{featureName} 功能正在规划中：{detail}");
        }

        private enum ScreenRecordingMode
        {
            FullScreen,
            Region
        }

        private record struct ScreenCaptureArea(int Left, int Top, int Width, int Height);

        private sealed class ScreenRecordingSession : IDisposable
        {
            public ScreenRecordingSession(ScreenRecordingMode mode, string outputPath, ScreenCaptureArea area)
            {
                Mode = mode;
                OutputPath = outputPath;
                CaptureArea = area;
                Cancellation = new CancellationTokenSource();
            }

            public ScreenRecordingMode Mode { get; }
            public string OutputPath { get; }
            public ScreenCaptureArea CaptureArea { get; }
            public Process? Process { get; set; }
            public bool IsStopping { get; set; }
            public CancellationTokenSource Cancellation { get; }

            public void Dispose()
            {
                try
                {
                    if (!Cancellation.IsCancellationRequested)
                    {
                        Cancellation.Cancel();
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    Cancellation.Dispose();
                    Process?.Dispose();
                }
            }
        }

        private async Task HandleScreenRecordingRequestAsync(ScreenRecordingMode mode)
        {
            if (_screenRecordingSession != null)
            {
                var result = MessageBox.Show(
                    "当前已经有一个屏幕录制任务在进行，是否立即停止？",
                    "屏幕录制",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await StopScreenRecordingAsync("用户手动停止");
                }

                return;
            }

            await StartScreenRecordingAsync(mode);
        }

        private async Task StartScreenRecordingAsync(ScreenRecordingMode mode)
        {
            var ffmpegPath = ResolveScreenRecorderFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg，可在工具 → FFmpeg 配置中设置，或确保 tools/ffmpeg 目录存在。", "屏幕录制", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = mode == ScreenRecordingMode.FullScreen ? "保存全屏录制" : "保存区域录制",
                Filter = "MP4 文件 (*.mp4)|*.mp4|MKV 文件 (*.mkv)|*.mkv|所有文件 (*.*)|*.*",
                FileName = $"ScreenCapture_{DateTime.Now:yyyyMMdd_HHmmss}.mp4",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (saveDialog.ShowDialog() != true)
            {
                return;
            }

            ScreenCaptureArea captureArea;
            if (mode == ScreenRecordingMode.FullScreen)
            {
                var primary = Forms.Screen.PrimaryScreen;
                var bounds = primary?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
                captureArea = new ScreenCaptureArea(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            }
            else
            {
                var region = PromptScreenRegion();
                if (region == null)
                {
                    Services.ToastNotification.ShowInfo("已取消区域录制。");
                    return;
                }

                captureArea = region.Value;
            }

            var session = new ScreenRecordingSession(mode, saveDialog.FileName, captureArea);

            EnsureExecutionLogPanelVisible();
            AppendExecutionLogLine($"[ScreenRecorder] 录制模式: {mode}");
            AppendExecutionLogLine($"[ScreenRecorder] 输出文件: {session.OutputPath}");

            _currentScreenRecordingTask = CreateTaskProgress("屏幕录制",
                Path.GetFileName(session.OutputPath) ?? session.OutputPath,
                "准备启动 FFmpeg...");
            UpdateTaskProgress(_currentScreenRecordingTask, 10, "初始化", "正在创建录制进程...");

            try
            {
                _screenRecordingSession = session;
                UpdateScreenRecorderMenuState();
                StartScreenRecorderProcess(ffmpegPath!, session);
                UpdateTaskProgress(_currentScreenRecordingTask, 20, "录制中", "FFmpeg 已启动，正在录制...");
                Services.ToastNotification.ShowSuccess("屏幕录制已开始，再次点击菜单可停止录制。");
            }
            catch (Exception ex)
            {
                session.Dispose();
                _screenRecordingSession = null;
                UpdateScreenRecorderMenuState();
                Services.DebugLogger.LogError($"启动屏幕录制失败: {ex.Message}");
                MessageBox.Show($"启动屏幕录制失败：{ex.Message}", "屏幕录制", MessageBoxButton.OK, MessageBoxImage.Error);
                CompleteTaskProgress(_currentScreenRecordingTask, false, "启动失败", ex.Message);
                _currentScreenRecordingTask = null;
            }
        }

        private string? ResolveScreenRecorderFfmpegPath()
        {
            var manualPath = EmbeddedFFmpegPathTextBox?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(manualPath) && File.Exists(manualPath))
            {
                return manualPath;
            }

            return FindFFmpegExecutable();
        }

        private void StartScreenRecorderProcess(string ffmpegPath, ScreenRecordingSession session)
        {
            var arguments = BuildScreenRecorderArguments(session);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += ScreenRecorderDataReceived;
            process.ErrorDataReceived += ScreenRecorderDataReceived;
            process.Exited += (_, __) => Dispatcher.Invoke(() => HandleScreenRecorderExited(session));

            if (!process.Start())
            {
                throw new InvalidOperationException("无法启动 FFmpeg 进程。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            session.Process = process;

            AppendExecutionLogLine($"[ScreenRecorder] 命令：{psi.FileName} {psi.Arguments}");
        }

        private void ScreenRecorderDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            AppendExecutionLogLine($"[ScreenRecorder] {e.Data}");
        }

        private string BuildScreenRecorderArguments(ScreenRecordingSession session)
        {
            var area = session.CaptureArea;
            const int fps = 30;

            var builder = new StringBuilder();
            builder.Append("-y ");
            builder.AppendFormat(CultureInfo.InvariantCulture,
                "-f gdigrab -framerate {0} -offset_x {1} -offset_y {2} -video_size {3}x{4} -draw_mouse 1 -i desktop ",
                fps, area.Left, area.Top, area.Width, area.Height);

            var (systemAudio, microphone) = GetScreenRecorderAudioDevices();
            var nextInputIndex = 1;
            var audioIndexes = new List<int>();

            if (!string.IsNullOrWhiteSpace(systemAudio))
            {
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "-f dshow -i audio=\"{0}\" ",
                    EscapeFfmpegDeviceName(systemAudio));
                audioIndexes.Add(nextInputIndex++);
            }

            if (!string.IsNullOrWhiteSpace(microphone))
            {
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "-f dshow -i audio=\"{0}\" ",
                    EscapeFfmpegDeviceName(microphone));
                audioIndexes.Add(nextInputIndex++);
            }

            if (audioIndexes.Count == 2)
            {
                builder.AppendFormat(CultureInfo.InvariantCulture,
                    "-filter_complex \"[{0}:a][{1}:a]amix=inputs=2:duration=longest[outa]\" ",
                    audioIndexes[0], audioIndexes[1]);
                builder.Append("-map 0:v -map \"[outa]\" ");
            }
            else if (audioIndexes.Count == 1)
            {
                builder.Append("-map 0:v ");
                builder.AppendFormat(CultureInfo.InvariantCulture, "-map {0}:a ", audioIndexes[0]);
            }
            else
            {
                builder.Append("-map 0:v ");
            }

            builder.Append("-c:v libx264 -preset veryfast -pix_fmt yuv420p ");
            builder.AppendFormat(CultureInfo.InvariantCulture, "-r {0} -crf 20 ", fps);

            if (audioIndexes.Count > 0)
            {
                builder.Append("-c:a aac -b:a 192k ");
            }
            else
            {
                builder.Append("-an ");
            }

            builder.AppendFormat("\"{0}\"", session.OutputPath);

            return builder.ToString();
        }

        private async Task StopScreenRecordingAsync(string? reason = null)
        {
            var session = _screenRecordingSession;
            if (session == null)
            {
                Services.ToastNotification.ShowInfo("当前没有正在进行的屏幕录制。");
                return;
            }

            session.IsStopping = true;
            var process = session.Process;

            try
            {
                if (process != null && !process.HasExited)
                {
                    try
                    {
                        if (process.StartInfo.RedirectStandardInput && process.StandardInput.BaseStream.CanWrite)
                        {
                            await process.StandardInput.WriteLineAsync("q");
                            await process.StandardInput.FlushAsync();
                        }

                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try
                        {
                            await process.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            process.Kill(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogWarning($"发送停止命令失败，将强制终止：{ex.Message}");
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                }
            }
            finally
            {
                AppendExecutionLogLine($"[ScreenRecorder] 用户停止录制 -> {session.OutputPath}");
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    AppendExecutionLogLine($"[ScreenRecorder] 停止原因：{reason}");
                }

                Services.ToastNotification.ShowSuccess($"屏幕录制结束，文件已保存：{session.OutputPath}");
                session.Process = null;
                session.Dispose();
                _screenRecordingSession = null;
                UpdateScreenRecorderMenuState();
                CompleteTaskProgress(_currentScreenRecordingTask, true, "录制已停止", reason ?? "手动停止");
                _currentScreenRecordingTask = null;
            }
        }

        private void HandleScreenRecorderExited(ScreenRecordingSession session)
        {
            var exitCode = session.Process?.ExitCode ?? 0;
            AppendExecutionLogLine($"[ScreenRecorder] FFmpeg 退出，代码: {exitCode}");

            if (session.IsStopping)
            {
                return;
            }

            if (_screenRecordingSession == session)
            {
                _screenRecordingSession = null;
                UpdateScreenRecorderMenuState();
            }

            if (exitCode == 0)
            {
                Services.ToastNotification.ShowSuccess($"屏幕录制完成：{session.OutputPath}");
                CompleteTaskProgress(_currentScreenRecordingTask, true, "录制完成", "FFmpeg 正常退出");
            }
            else
            {
                Services.ToastNotification.ShowWarning($"屏幕录制异常退出 (代码 {exitCode})，请检查执行日志。");
                CompleteTaskProgress(_currentScreenRecordingTask, false, $"异常退出 ({exitCode})", "请检查执行日志");
            }

            session.Dispose();
            _currentScreenRecordingTask = null;
        }

        private void UpdateScreenRecorderMenuState()
        {
            if (MenuScreenRecordFull == null || MenuScreenRecordRegion == null)
            {
                return;
            }

            if (_screenRecordingSession == null)
            {
                MenuScreenRecordFull.Header = "🖥️ 全屏录制";
                MenuScreenRecordFull.IsEnabled = true;
                MenuScreenRecordRegion.Header = "🔲 区域录制";
                MenuScreenRecordRegion.IsEnabled = true;
                return;
            }

            if (_screenRecordingSession.Mode == ScreenRecordingMode.FullScreen)
            {
                MenuScreenRecordFull.Header = "🛑 停止全屏录制";
                MenuScreenRecordFull.IsEnabled = true;
                MenuScreenRecordRegion.Header = "🔲 区域录制（录制中）";
                MenuScreenRecordRegion.IsEnabled = false;
            }
            else
            {
                MenuScreenRecordRegion.Header = "🛑 停止区域录制";
                MenuScreenRecordRegion.IsEnabled = true;
                MenuScreenRecordFull.Header = "🖥️ 全屏录制（录制中）";
                MenuScreenRecordFull.IsEnabled = false;
            }
        }

        private ScreenCaptureArea? PromptScreenRegion()
        {
            var picker = new Views.ScreenRegionPickerWindow
            {
                Owner = this
            };

            var result = picker.ShowDialog();
            if (result == true && picker.SelectedRegion.HasValue)
            {
                var rect = picker.SelectedRegion.Value;
                return new ScreenCaptureArea(
                    (int)Math.Max(0, Math.Round(rect.X)),
                    (int)Math.Max(0, Math.Round(rect.Y)),
                    (int)Math.Max(1, Math.Round(rect.Width)),
                    (int)Math.Max(1, Math.Round(rect.Height)));
            }

            return null;
        }

        private void InitializeAiSubtitleService()
        {
            try
            {
                var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppData", "cache");
                var audioChunker = new Services.AiSubtitle.AudioChunker();
                var chunkMerger = new Services.AiSubtitle.ChunkMerger();
                var retryPolicy = new Services.AiSubtitle.RetryPolicy();
                var cacheManager = new Services.AiSubtitle.CacheManager(cacheDir);
                var subtitleOptimizer = new Services.AiSubtitle.SubtitleOptimizer(_httpClient);

                _aiSubtitleService = new Services.AiSubtitle.AiSubtitleService(
                    audioChunker,
                    chunkMerger,
                    retryPolicy,
                    cacheManager,
                    subtitleOptimizer,
                    _httpClient);

                // 异步清理过期缓存（不阻塞启动）
                _ = Task.Run(() =>
                {
                    try
                    {
                        cacheManager.CleanExpiredCache();
                        Services.DebugLogger.LogInfo("缓存清理完成");
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogError($"清理过期缓存失败: {ex.Message}");
                    }
                });

                Services.DebugLogger.LogInfo("AI 字幕服务初始化完成（缓存清理已异步执行）");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"AI 字幕服务初始化失败: {ex.Message}");
            }
        }

        private async Task GenerateSubtitlesWithAiAsync()
        {
            if (_isAiSubtitleGenerating)
            {
                Services.ToastNotification.ShowInfo("AI 字幕生成任务正在进行中，请稍候...");
                return;
            }

            var mediaFile = ResolveSubtitleTargetFile();
            if (string.IsNullOrWhiteSpace(mediaFile))
            {
                Services.ToastNotification.ShowWarning("未选择任何媒体文件。");
                return;
            }

            var provider = PromptAiSubtitleProvider();
            if (provider == null)
            {
                return;
            }

            var ffmpegPath = ResolveScreenRecorderFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg，可在工具 → FFmpeg 配置中设置。", "AI 字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 确保服务已初始化
            if (_aiSubtitleService == null)
            {
                InitializeAiSubtitleService();
                if (_aiSubtitleService == null)
                {
                    MessageBox.Show("AI 字幕服务初始化失败，请重试。", "AI 字幕", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            _aiSubtitleCts = new CancellationTokenSource();
            _isAiSubtitleGenerating = true;

            _currentAiSubtitleTask = CreateTaskProgress("AI 字幕", Path.GetFileName(mediaFile) ?? mediaFile, $"供应商: {provider.DisplayName}");
            UpdateTaskProgress(_currentAiSubtitleTask, 0, "准备中", "正在初始化...");

            try
            {
                EnsureExecutionLogPanelVisible();
                AppendExecutionLogLine($"[AI Subtitle] 目标文件: {mediaFile}");
                AppendExecutionLogLine($"[AI Subtitle] 使用供应商: {provider.DisplayName} ({provider.BaseUrl})");

                var options = new Services.AiSubtitle.TranscriptionOptions
                {
                    EnableChunking = true,
                    EnableCache = true,
                    EnableOptimization = Properties.Settings.Default.AiSubtitleEnableOptimization,
                    OptimizationPrompt = Properties.Settings.Default.AiSubtitleOptimizationPrompt,
                    ChunkLengthSeconds = 600, // 10分钟
                    ChunkOverlapSeconds = 10  // 10秒重叠
                };

                Services.ToastNotification.ShowInfo($"正在使用 {provider.DisplayName} 生成字幕...");
                
                var subtitleContent = await _aiSubtitleService.GenerateSubtitlesAsync(
                    mediaFile,
                    ffmpegPath,
                    provider,
                    options,
                    new Progress<(int progress, string message)>(p =>
                    {
                        UpdateTaskProgress(_currentAiSubtitleTask, p.progress, p.message, string.Empty);
                        AppendExecutionLogLine($"[AI Subtitle] {p.message} ({p.progress}%)");
                    }),
                    _aiSubtitleCts.Token);

                var outputPath = Path.Combine(Path.GetDirectoryName(mediaFile) ?? Environment.CurrentDirectory,
                    $"{Path.GetFileNameWithoutExtension(mediaFile)}.ai.srt");

                UpdateTaskProgress(_currentAiSubtitleTask, 95, "写入文件", "生成 SRT 文件...");
                await File.WriteAllTextAsync(outputPath, subtitleContent, Encoding.UTF8, _aiSubtitleCts.Token);
                
                Dispatcher.Invoke(() =>
                {
                    if (SubtitlePathBox != null)
                    {
                        SubtitlePathBox.Text = outputPath;
                    }

                    LoadSubtitleFile(outputPath);
                });

                AppendExecutionLogLine($"[AI Subtitle] 成功生成字幕文件：{outputPath}");
                Services.ToastNotification.ShowSuccess($"AI 字幕生成完成：{outputPath}");
                CompleteTaskProgress(_currentAiSubtitleTask, true, "字幕生成完成");
            }
            catch (OperationCanceledException)
            {
                Services.ToastNotification.ShowWarning("已取消 AI 字幕生成任务。");
                AppendExecutionLogLine("[AI Subtitle] 任务被用户取消。");
                CompleteTaskProgress(_currentAiSubtitleTask, false, "任务已取消");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"AI 字幕生成失败: {ex}");
                MessageBox.Show($"AI 字幕生成失败：{ex.Message}", "AI 字幕", MessageBoxButton.OK, MessageBoxImage.Error);
                CompleteTaskProgress(_currentAiSubtitleTask, false, $"失败：{ex.Message}");
            }
            finally
            {
                _aiSubtitleCts?.Dispose();
                _aiSubtitleCts = null;
                _isAiSubtitleGenerating = false;
                _currentAiSubtitleTask = null;
            }
        }

        private string? ResolveSubtitleTargetFile()
        {
            if (_videoListViewModel?.SelectedFile?.FilePath is string selected && File.Exists(selected))
            {
                return selected;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要生成字幕的媒体文件",
                Filter = VideoListViewModel.MediaFileDialogFilter,
                Multiselect = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        private (string SystemAudio, string Microphone) GetScreenRecorderAudioDevices()
        {
            var systemAudio = Properties.Settings.Default.ScreenRecorderSystemAudioDevice?.Trim() ?? string.Empty;
            var microphone = Properties.Settings.Default.ScreenRecorderMicrophoneDevice?.Trim() ?? string.Empty;
            return (systemAudio, microphone);
        }

        private static string EscapeFfmpegDeviceName(string deviceName)
        {
            return deviceName
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        private void LoadAiSubtitleProviders()
        {
            try
            {
                var json = Properties.Settings.Default.AiSubtitleProvidersJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    _aiSubtitleProviders = JsonSerializer.Deserialize<List<AiSubtitleProviderProfile>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<AiSubtitleProviderProfile>();
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogWarning($"加载 AI 供应商配置失败，已重置：{ex.Message}");
                _aiSubtitleProviders = new List<AiSubtitleProviderProfile>();
            }

            if (_aiSubtitleProviders.Count == 0)
            {
                var defaultProvider = CreateDefaultAiSubtitleProvider();
                defaultProvider.ApiKey = Properties.Settings.Default.DeepSeekApiKey ?? string.Empty;
                defaultProvider.BaseUrl = string.IsNullOrWhiteSpace(Properties.Settings.Default.DeepSeekBaseUrl)
                    ? defaultProvider.BaseUrl
                    : Properties.Settings.Default.DeepSeekBaseUrl;
                defaultProvider.Model = string.IsNullOrWhiteSpace(Properties.Settings.Default.DeepSeekModel)
                    ? defaultProvider.Model
                    : Properties.Settings.Default.DeepSeekModel;
                defaultProvider.ResponseFormat = string.IsNullOrWhiteSpace(Properties.Settings.Default.DeepSeekResponseFormat)
                    ? defaultProvider.ResponseFormat
                    : Properties.Settings.Default.DeepSeekResponseFormat;
                _aiSubtitleProviders.Add(defaultProvider);
                SaveAiSubtitleProviders();
            }
        }

        private void SaveAiSubtitleProviders()
        {
            var json = JsonSerializer.Serialize(_aiSubtitleProviders, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            Properties.Settings.Default.AiSubtitleProvidersJson = json;
            Properties.Settings.Default.Save();
        }

        private AiSubtitleProviderProfile CreateDefaultAiSubtitleProvider()
        {
            return new AiSubtitleProviderProfile
            {
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                EndpointPath = "/v1/audio/transcriptions",
                Model = "whisper-1",
                ResponseFormat = "srt",
                Notes = "默认 DeepSeek 提供商"
            };
        }

        private AiSubtitleProviderProfile? PromptAiSubtitleProvider()
        {
            if (_aiSubtitleProviders == null || _aiSubtitleProviders.Count == 0)
            {
                LoadAiSubtitleProviders();
            }

            if (_aiSubtitleProviders.Count == 0)
            {
                Services.ToastNotification.ShowWarning("尚未配置任何字幕 AI 供应商。");
                if (!ShowAiProviderManagerDialog())
                {
                    return null;
                }
            }

            while (true)
            {
                if (_aiSubtitleProviders.Count == 1)
                {
                    var single = _aiSubtitleProviders[0];
                    if (string.IsNullOrWhiteSpace(single.ApiKey))
                    {
                        Services.ToastNotification.ShowWarning($"供应商 {single.DisplayName} 未配置 API Key。");
                        if (!ShowAiProviderManagerDialog())
                        {
                            return null;
                        }
                        continue;
                    }

                    return single;
                }

                var selector = new Views.AiSubtitleProviderSelectionWindow(_aiSubtitleProviders,
                    Properties.Settings.Default.LastAiSubtitleProviderId)
                {
                    Owner = this
                };

                var dialogResult = selector.ShowDialog();
                if (dialogResult == true && selector.SelectedProvider != null)
                {
                    var match = _aiSubtitleProviders.FirstOrDefault(p => p.Id == selector.SelectedProvider.Id)
                                ?? selector.SelectedProvider;

                    if (string.IsNullOrWhiteSpace(match.ApiKey))
                    {
                        Services.ToastNotification.ShowWarning("该供应商未配置 API Key，请先在“管理...”中设置。");
                        continue;
                    }

                    if (selector.RememberChoice)
                    {
                        Properties.Settings.Default.LastAiSubtitleProviderId = match.Id;
                        Properties.Settings.Default.Save();
                    }

                    return match;
                }

                if (selector.ManageRequested)
                {
                    if (!ShowAiProviderManagerDialog())
                    {
                        return null;
                    }

                    continue;
                }

                return null;
            }
        }

        private bool ShowAiProviderManagerDialog()
        {
            var dialog = new Views.AiSubtitleProviderManagerWindow(_aiSubtitleProviders)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _aiSubtitleProviders = dialog.UpdatedProviders.Select(p => p.Clone()).ToList();
                SaveAiSubtitleProviders();
                return true;
            }

            return false;
        }

        private void EnsureExecutionLogPanelVisible()
        {
            if (!_isExecutionLogVisible)
            {
                _isExecutionLogVisible = true;
                ApplyOutputPanelVisibility();
            }

            if (OutputInfoTabs != null && ExecutionLogTabItem != null)
            {
                OutputInfoTabs.SelectedItem = ExecutionLogTabItem;
            }
        }

        private void AppendExecutionLogLine(string message)
        {
            if (LogOutputBox == null)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendExecutionLogLine(message));
                return;
            }

            var sanitized = (message ?? string.Empty).TrimEnd('\r', '\n');
            var indented = sanitized.Replace(Environment.NewLine, Environment.NewLine + "           ");
            var formatted = $"[{DateTime.Now:HH:mm:ss}] {indented}{Environment.NewLine}";

            LogOutputBox.AppendText(formatted);
            LogOutputBox.ScrollToEnd();
        }

        private TaskProgressItem CreateTaskProgress(string type, string name, string detail)
        {
            var task = new TaskProgressItem(type, name)
            {
                Detail = detail
            };

            Dispatcher.Invoke(() =>
            {
                _taskProgressItems.Insert(0, task);
                EnsureTaskMonitorTabVisible();
            });

            return task;
        }

        private void UpdateTaskProgress(TaskProgressItem? task, double progress, string status, string detail)
        {
            if (task == null)
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                task.ProgressValue = Math.Clamp(progress, 0, 100);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    task.Status = status;
                }
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    task.Detail = detail;
                }
            });
        }

        private void CompleteTaskProgress(TaskProgressItem? task, bool success, string status, string? detail = null)
        {
            if (task == null)
            {
                return;
            }

            Dispatcher.Invoke(() => task.Complete(success, status, detail));
        }

        private void EnsureTaskMonitorTabVisible()
        {
            if (OutputInfoTabs != null && TaskMonitorTabItem != null)
            {
                OutputInfoTabs.SelectedItem = TaskMonitorTabItem;
            }
        }

        private void BtnClearFinishedTasks_Click(object sender, RoutedEventArgs e)
        {
            var finished = _taskProgressItems.Where(t => t.IsCompleted).ToList();
            foreach (var item in finished)
            {
                _taskProgressItems.Remove(item);
            }
        }

        private sealed class TaskProgressItem : INotifyPropertyChanged
        {
            private string _status;
            private double _progressValue;
            private string _detail;

            public TaskProgressItem(string taskType, string taskName)
            {
                TaskType = taskType;
                TaskName = taskName;
                _status = "准备中";
                _detail = string.Empty;
                StartTime = DateTime.Now;
            }

            public string TaskType { get; }
            public string TaskName { get; }
            public DateTime StartTime { get; }
            public DateTime? EndTime { get; private set; }
            public bool IsCompleted { get; private set; }
            public bool IsSuccess { get; private set; }

            public string Status
            {
                get => _status;
                set => SetProperty(ref _status, value);
            }

            public double ProgressValue
            {
                get => _progressValue;
                set
                {
                    if (SetProperty(ref _progressValue, value))
                    {
                        OnPropertyChanged(nameof(ProgressText));
                    }
                }
            }

            public string ProgressText => $"{ProgressValue:0}%";

            public string Detail
            {
                get => _detail;
                set => SetProperty(ref _detail, value);
            }

            public string StartTimeText => StartTime.ToString("HH:mm:ss");

            public string DurationText
            {
                get
                {
                    var end = EndTime ?? DateTime.Now;
                    var span = end - StartTime;
                    return $"{(int)span.TotalMinutes:D2}:{span.Seconds:D2}";
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public void Complete(bool success, string finalStatus, string? detail = null)
            {
                if (IsCompleted)
                {
                    return;
                }

                IsCompleted = true;
                IsSuccess = success;
                EndTime = DateTime.Now;
                Status = finalStatus;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    Detail = detail;
                }

                if (success && ProgressValue < 100)
                {
                    ProgressValue = 100;
                }

                OnPropertyChanged(nameof(DurationText));
            }

            private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return false;
                }

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged(string? propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        #endregion

        #region 播放器右键菜单

        // ========== 播放控制 ==========
        
        /// <summary>
        /// 播放/暂停
        /// </summary>
        private void PlayerMenu_PlayPause_Click(object sender, RoutedEventArgs e)
        {
            var isPlaying = _videoPlayerViewModel.IsPlaying;
            PreparePlaybackViewState(isPlayRequested: !isPlaying);

            if (isPlaying)
            {
                _videoPlayerViewModel.Pause();
                Services.DebugLogger.LogInfo("右键菜单: 暂停");
            }
            else
            {
                _videoPlayerViewModel.Play();
                Services.DebugLogger.LogInfo("右键菜单: 播放");
            }
        }

        /// <summary>
        /// 停止
        /// </summary>
        private void PlayerMenu_Stop_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.Stop();
            Services.DebugLogger.LogInfo("右键菜单: 停止");
        }

        /// <summary>
        /// 上一个视频
        /// </summary>
        private void PlayerMenu_Previous_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                var currentIndex = _videoListViewModel.Files.IndexOf(_videoListViewModel.SelectedFile);
                if (currentIndex > 0)
                {
                    var previousFile = _videoListViewModel.Files[currentIndex - 1];
                    _videoListViewModel.SelectedFile = previousFile;
                    _videoPlayerViewModel.LoadVideo(previousFile.FilePath);
                    _videoPlayerViewModel.Play(); // 开始播放
                    Services.DebugLogger.LogInfo($"右键菜单: 上一个 -> {previousFile.FileName}");
                }
                else
                {
                    Services.ToastNotification.ShowWarning("已经是第一个视频");
                }
            }
        }

        /// <summary>
        /// 下一个视频
        /// </summary>
        private void PlayerMenu_Next_Click(object sender, RoutedEventArgs e)
        {
            if (_videoListViewModel.SelectedFile != null)
            {
                var currentIndex = _videoListViewModel.Files.IndexOf(_videoListViewModel.SelectedFile);
                if (currentIndex < _videoListViewModel.Files.Count - 1)
                {
                    var nextFile = _videoListViewModel.Files[currentIndex + 1];
                    _videoListViewModel.SelectedFile = nextFile;
                    _videoPlayerViewModel.LoadVideo(nextFile.FilePath);
                    _videoPlayerViewModel.Play(); // 开始播放
                    Services.DebugLogger.LogInfo($"右键菜单: 下一个 -> {nextFile.FileName}");
                }
                else
                {
                    Services.ToastNotification.ShowWarning("已经是最后一个视频");
                }
            }
        }

        /// <summary>
        /// 处理播放模式菜单点击事件
        /// </summary>
        /// <remarks>
        /// 播放模式说明：
        /// - Single: 单曲播放，播放完后停止
        /// - Sequential: 顺序播放，播放完后自动播放下一首
        /// - RepeatOne: 单曲循环，重复播放当前视频
        /// - RepeatAll: 循环播放，播放完列表后重新开始
        /// - Random: 随机播放，随机选择下一首
        /// </remarks>
        private void PlayerMenu_PlayMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string modeTag)
            {
                PlayMode selectedMode;
                bool isSingleMode = false; // 标记是否为单曲播放模式

                // 根据选择的模式设置对应的枚举值和状态
                switch (modeTag)
                {
                    case "Single":
                        selectedMode = PlayMode.Sequential; // 单曲播放使用Sequential但不自动下一首
                        isSingleMode = true;
                        break;
                    case "RepeatOne":
                        selectedMode = PlayMode.RepeatOne;
                        break;
                    case "Sequential":
                        selectedMode = PlayMode.Sequential;
                        break;
                    case "RepeatAll":
                        selectedMode = PlayMode.RepeatAll;
                        break;
                    case "Random":
                        selectedMode = PlayMode.Random;
                        break;
                    default:
                        return; // 未知模式，直接返回
                }

                // 更新播放器循环状态（只对单曲循环启用）
                _videoPlayerViewModel.IsLoopEnabled = (selectedMode == PlayMode.RepeatOne);

                // 设置单曲播放模式状态
                _videoPlayerViewModel.IsSinglePlayMode = isSingleMode;
                _isSinglePlayMode = isSingleMode;

                // 更新PlayQueueManager的播放模式
                if (_videoListViewModel?.PlayQueueManager != null)
                {
                    _videoListViewModel.PlayQueueManager.CurrentMode = selectedMode;
                }

                // 更新UI显示
                UpdatePlayModeMenuDisplay(modeTag);

                Services.DebugLogger.LogInfo($"播放模式已设置为: {GetPlayModeDisplayName(modeTag)}");
                Services.ToastNotification.ShowSuccess($"播放模式: {GetPlayModeDisplayName(modeTag)}");
            }
        }

        /// <summary>
        /// 更新播放模式菜单显示
        /// </summary>
        private void UpdatePlayModeMenuDisplay(string selectedModeTag)
        {
            // 更新菜单项的选中状态
            var playModeMenu = FindName("PlayMode_Single") as MenuItem;
            if (playModeMenu != null) playModeMenu.Header = selectedModeTag == "Single" ? "● 单曲播放" : "○ 单曲播放";

            playModeMenu = FindName("PlayMode_RepeatOne") as MenuItem;
            if (playModeMenu != null) playModeMenu.Header = selectedModeTag == "RepeatOne" ? "🔁 单曲循环" : "🔂 单曲循环";

            playModeMenu = FindName("PlayMode_Sequential") as MenuItem;
            if (playModeMenu != null) playModeMenu.Header = selectedModeTag == "Sequential" ? "➡️ 顺序播放" : "➡️ 顺序播放";

            playModeMenu = FindName("PlayMode_RepeatAll") as MenuItem;
            if (playModeMenu != null) playModeMenu.Header = selectedModeTag == "RepeatAll" ? "🔄 循环播放" : "🔄 循环播放";

            playModeMenu = FindName("PlayMode_Random") as MenuItem;
            if (playModeMenu != null) playModeMenu.Header = selectedModeTag == "Random" ? "🔀 随机播放" : "🔀 随机播放";
        }

        /// <summary>
        /// 获取播放模式的显示名称
        /// </summary>
        private string GetPlayModeDisplayName(string modeTag)
        {
            return modeTag switch
            {
                "Single" => "单曲播放",
                "RepeatOne" => "单曲循环",
                "Sequential" => "顺序播放",
                "RepeatAll" => "循环播放",
                "Random" => "随机播放",
                _ => "未知模式"
            };
        }

        // ========== 剪辑功能 ==========

        /// <summary>
        /// 标记入点
        /// </summary>
        private void PlayerMenu_MarkIn_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.MarkInPoint();
            Services.DebugLogger.LogSuccess("右键菜单: 标记入点");
            Services.ToastNotification.ShowSuccess("已标记入点");
        }

        /// <summary>
        /// 标记出点
        /// </summary>
        private void PlayerMenu_MarkOut_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.MarkOutPoint();
            Services.DebugLogger.LogSuccess("右键菜单: 标记出点");
            Services.ToastNotification.ShowSuccess("已标记出点");
        }

        /// <summary>
        /// 清除入点
        /// </summary>
        private void PlayerMenu_ClearIn_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.ClearInPoint();
            Services.DebugLogger.LogInfo("右键菜单: 清除入点");
            Services.ToastNotification.ShowInfo("已清除入点");
        }

        /// <summary>
        /// 清除出点
        /// </summary>
        private void PlayerMenu_ClearOut_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.ClearOutPoint();
            Services.DebugLogger.LogInfo("右键菜单: 清除出点");
            Services.ToastNotification.ShowInfo("已清除出点");
        }

        /// <summary>
        /// 预览片段 (从入点播放到出点)
        /// </summary>
        private void PlayerMenu_PreviewClip_Click(object sender, RoutedEventArgs e)
        {
            if (!_videoPlayerViewModel.HasInPoint || !_videoPlayerViewModel.HasOutPoint)
            {
                Services.ToastNotification.ShowWarning("请先标记入点和出点");
                return;
            }

            _videoPlayerViewModel.Seek(_videoPlayerViewModel.InPoint);
            _videoPlayerViewModel.Play();
            Services.DebugLogger.LogInfo("右键菜单: 预览片段");
            Services.ToastNotification.ShowInfo("开始预览片段");
        }

        // ========== 播放速度 ==========

        /// <summary>
        /// 设置播放速度
        /// </summary>
        private void PlayerMenu_Speed_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag != null)
            {
                if (float.TryParse(menuItem.Tag.ToString(), out float speed))
                {
                    _videoPlayerViewModel.SetPlaybackSpeed(speed);
                    Services.DebugLogger.LogInfo($"右键菜单: 设置播放速度 {speed}x");
                    Services.ToastNotification.ShowSuccess($"播放速度: {speed}x");
                }
            }
        }

        // ========== 截图功能 ==========

        /// <summary>
        /// 截取当前帧
        /// </summary>
        private void PlayerMenu_Screenshot_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayerViewModel.TakeScreenshot();
            Services.DebugLogger.LogInfo("右键菜单: 截取当前帧");
        }

        // ========== 视图控制 ==========

        /// <summary>
        /// 全屏
        /// </summary>
        private void PlayerMenu_FullScreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
            Services.DebugLogger.LogInfo("右键菜单: 切换全屏");
        }

        #endregion

        #region 裁剪选取框功能

        // 裁剪框相关字段 - Canvas实现

        // 裁剪选取框状态
        private bool _isCropSelectorDragging = false;
        private bool _isCropHandleDragging = false;
        private Point _cropDragStartPoint;
        private double _cropDragStartLeft;
        private double _cropDragStartTop;
        private double _cropDragStartWidth;
        private double _cropDragStartHeight;
        private string? _cropHandleTag;
        private bool _isUpdatingCropFromCode = false; // 防止循环更新的标志

        // 保存裁剪框状态，用于窗口激活/失活时的状态恢复
        private double _savedCropLeft = 0;
        private double _savedCropTop = 0;
        private double _savedCropWidth = 0;
        private double _savedCropHeight = 0;
        private bool _hasSavedCropState = false;

        /// <summary>
        /// 显示裁剪选取框 - Canvas版本
        /// </summary>
        private void ShowCropSelectorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CropOverlayPopup.IsOpen)
                    {
                    // 当前显示中，点击则隐藏
                    Services.DebugLogger.LogInfo("隐藏裁剪框 - Popup版本");

                    CropOverlayPopup.IsOpen = false;
                    ShowCropSelectorButton.Content = "🖱️ 显示裁剪框";
                    ShowCropSelectorButton.Style = (Style)FindResource("ButtonInfoStyle");

                    Services.DebugLogger.LogInfo("裁剪框已隐藏");
                }
                else
                {
                    // 当前隐藏，点击则显示
                    Services.DebugLogger.LogInfo("显示裁剪框 - Popup版本");

                    ShowPopupCropSelector();
                    ShowCropSelectorButton.Content = "👁️ 隐藏裁剪框";
                    ShowCropSelectorButton.Style = (Style)FindResource("ButtonWarningStyle");

                    // 设置比例锁定状态
                    UpdateCropAspectLockIndicator();

                    Services.DebugLogger.LogInfo("裁剪框已显示");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"切换裁剪框显示状态出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新裁剪选择器按钮状态
        /// </summary>
        private void UpdateCropSelectorButtonState()
        {
            try
        {
                if (CropOverlayPopup.IsOpen)
                {
                    ShowCropSelectorButton.Content = "👁️ 隐藏裁剪框";
                    ShowCropSelectorButton.Style = (Style)FindResource("ButtonWarningStyle");
                }
                else
                {
                    ShowCropSelectorButton.Content = "🖱️ 显示裁剪框";
                    ShowCropSelectorButton.Style = (Style)FindResource("ButtonInfoStyle");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新裁剪选择器按钮状态出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理鼠标移动 - 用于拖拽点调整大小
        /// </summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isCropHandleDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(PopupCropCanvas);
                double deltaX = currentPoint.X - _cropDragStartPoint.X;
                double deltaY = currentPoint.Y - _cropDragStartPoint.Y;

                double newLeft = _cropDragStartLeft;
                double newTop = _cropDragStartTop;
                double newWidth = _cropDragStartWidth;
                double newHeight = _cropDragStartHeight;

                // 根据拖拽点位置调整选取框
                switch (_cropHandleTag)
                {
                    case "TopLeft":
                        newLeft = Math.Max(0, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newTop = Math.Max(0, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "Top":
                        newTop = Math.Max(0, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "TopRight":
                        newTop = Math.Max(0, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, 1920 - _cropDragStartLeft));
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "Left":
                        newLeft = Math.Max(0, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        break;
                    case "Right":
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, 1920 - _cropDragStartLeft));
                        break;
                    case "BottomLeft":
                        newLeft = Math.Max(0, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, 1080 - _cropDragStartTop));
                        break;
                    case "Bottom":
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, 1080 - _cropDragStartTop));
                        break;
                    case "BottomRight":
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, 1920 - _cropDragStartLeft));
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, 1080 - _cropDragStartTop));
                        break;
                }

                // 如果锁定比例
                if (LockAspectRatioCheckBox.IsChecked == true)
                {
                    double aspectRatio = _cropDragStartWidth / _cropDragStartHeight;
                    
                    // 根据拖拽方向决定以宽度还是高度为准
                    if (_cropHandleTag?.Contains("Left") == true || _cropHandleTag?.Contains("Right") == true)
                    {
                        newHeight = newWidth / aspectRatio;
                    }
                    else if (_cropHandleTag == "Top" || _cropHandleTag == "Bottom")
                    {
                        newWidth = newHeight * aspectRatio;
                    }
                    else // 角落拖拽点
                    {
                        // 以变化较大的维度为准
                        if (Math.Abs(deltaX) > Math.Abs(deltaY))
                        {
                            newHeight = newWidth / aspectRatio;
                        }
                        else
                        {
                            newWidth = newHeight * aspectRatio;
                        }
                    }
                }

                Canvas.SetLeft(PopupCropSelector, newLeft);
                Canvas.SetTop(PopupCropSelector, newTop);
                PopupCropSelector.Width = newWidth;
                PopupCropSelector.Height = newHeight;

                UpdateCropMask();
            }
        }

        /// <summary>
        /// 处理鼠标释放 - 用于拖拽点调整大小
        /// </summary>
        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);

            if (_isCropHandleDragging)
            {
                _isCropHandleDragging = false;
                _cropHandleTag = null;
                Mouse.Capture(null);
            }
        }

        /// <summary>
        /// 更新裁剪遮罩路径 (显示选取框外的半透明区域)
        /// </summary>
        private void UpdateCropMask()
        {
            try
            {
                // 不再使用内部Canvas遮罩

                // 保留函数以满足旧调用，但不执行
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新裁剪遮罩失败: {ex.Message}");
            }
        }






        /// <summary>
        /// 计算最大公约数
        /// </summary>
        private static int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        /// <summary>
        /// 16:9横屏预设
        /// </summary>
        private void Preset16_9Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(16.0 / 9.0, "16:9");
            }

        /// <summary>
        /// 9:16竖屏预设
        /// </summary>
        private void Preset9_16Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(9.0 / 16.0, "9:16");
        }

        /// <summary>
        /// 1:1方形预设
        /// </summary>
        private void Preset1_1Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(1.0, "1:1");
        }

        /// <summary>
        /// 4:3经典预设
        /// </summary>
        private void Preset4_3Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(4.0 / 3.0, "4:3");
        }

        /// <summary>
        /// 3:4竖屏预设
        /// </summary>
        private void Preset3_4Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(3.0 / 4.0, "3:4");
        }

        /// <summary>
        /// 21:9电影预设
        /// </summary>
        private void Preset21_9Button_Click(object sender, RoutedEventArgs e)
        {
            ApplyPresetRatio(21.0 / 9.0, "21:9");
        }

        /// <summary>
        /// 应用预设比例（自动启用比例锁定）
        /// </summary>
        private void ApplyPresetRatio(double aspectRatio, string ratioName)
        {
            try
            {
                // 获取应用前的尺寸（用于比较）
                int.TryParse(CropWTextBox.Text, out int oldWidth);
                int.TryParse(CropHTextBox.Text, out int oldHeight);

                // 自动启用比例锁定
                LockAspectRatioCheckBox.IsChecked = true;

                // 设置预设裁剪尺寸
                SetCropPreset(aspectRatio, ratioName);

                // 获取应用后的尺寸和视频尺寸
                int.TryParse(CropWTextBox.Text, out int newWidth);
                int.TryParse(CropHTextBox.Text, out int newHeight);

                // 获取视频尺寸用于计算面积百分比
                var videoWidth = _videoPlayerViewModel.VideoWidth > 0 ? _videoPlayerViewModel.VideoWidth : 1920;
                var videoHeight = _videoPlayerViewModel.VideoHeight > 0 ? _videoPlayerViewModel.VideoHeight : 1080;

                // 计算裁剪区域占视频总面积的百分比
                double cropArea = newWidth * newHeight;
                double videoArea = videoWidth * videoHeight;
                double areaPercentage = (cropArea / videoArea) * 100.0;

                // 显示详细的反馈信息
                string sizeInfo = $"{newWidth}×{newHeight}";
                if (oldWidth > 0 && oldHeight > 0 && (oldWidth != newWidth || oldHeight != newHeight))
                {
                    sizeInfo += $" (之前: {oldWidth}×{oldHeight})";
                }
                sizeInfo += $" ({areaPercentage:F1}% 面积)";

                Services.ToastNotification.ShowSuccess($"已应用 {ratioName} 比例: {sizeInfo}");
                Services.DebugLogger.LogInfo($"应用预设比例: {ratioName} → {sizeInfo}, 自动启用比例锁定");
            }
            catch (Exception ex)
            {
                Services.ToastNotification.ShowError($"应用预设比例失败: {ex.Message}");
                Services.DebugLogger.LogError($"ApplyPresetRatio 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 应用选择按钮点击事件 - 确认裁剪区域选择
        /// </summary>
        private void ApplyCropButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("应用裁剪区域选择...");

                // 验证裁剪框是否可见
                if (!CropOverlayPopup.IsOpen)
            {
                    Services.ToastNotification.ShowWarning("请先显示裁剪框并调整裁剪区域");
                    return;
                }

                // 获取裁剪参数
                var validationResult = ValidateAndParseCropParameters(
                    CropXTextBox.Text, CropYTextBox.Text,
                    CropWTextBox.Text, CropHTextBox.Text);

                if (!validationResult.IsValid)
                {
                    Services.ToastNotification.ShowError($"裁剪参数无效: {validationResult.ErrorMessage}");
                    Services.DebugLogger.LogError($"裁剪参数验证失败: {validationResult.ErrorMessage}");
                    return;
                }

                // 隐藏裁剪框并更新按钮状态
                CropOverlayPopup.IsOpen = false;
                UpdateCropSelectorButtonState();

                Services.ToastNotification.ShowSuccess("裁剪区域选择已应用，请点击\"执行裁剪\"开始处理");
                Services.DebugLogger.LogInfo("裁剪区域选择已应用，裁剪框已隐藏");
            }
            catch (Exception ex)
            {
                Services.ToastNotification.ShowError($"应用选择失败: {ex.Message}");
                Services.DebugLogger.LogError($"ApplyCropButton_Click 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 输出设置数据结构
        /// </summary>
        private class OutputSettings
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public string OutputPath { get; set; } = string.Empty;
            public string FileNamingMode { get; set; } = string.Empty;
            public string CustomPrefix { get; set; } = string.Empty;
            public string CustomSuffix { get; set; } = string.Empty;
            public string OutputFormat { get; set; } = string.Empty;
            public string VideoCodec { get; set; } = string.Empty;
            public string AudioCodec { get; set; } = string.Empty;
            public string AudioBitrate { get; set; } = string.Empty;
            public int Quality { get; set; }
            public string CustomArgs { get; set; } = string.Empty;
        }

        private class ClipCutTask
        {
            public VideoClip Clip { get; set; } = null!;
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string OutputPath { get; set; } = string.Empty;
        }

        public class RecentProjectItem
        {
            public string DisplayName { get; init; } = string.Empty;
            public string FilePath { get; init; } = string.Empty;
            public string Tooltip { get; init; } = string.Empty;
            public string ShortcutHint { get; init; } = string.Empty;
            public bool IsPlaceholder { get; init; }
            public bool IsEnabled => !IsPlaceholder;

            public static RecentProjectItem Create(string filePath)
            {
                var fileName = Path.GetFileName(filePath);
                var folder = Path.GetDirectoryName(filePath) ?? string.Empty;

                return new RecentProjectItem
                {
                    DisplayName = string.IsNullOrWhiteSpace(fileName) ? filePath : fileName,
                    FilePath = filePath,
                    Tooltip = filePath,
                    ShortcutHint = folder,
                    IsPlaceholder = false
                };
            }

            public static RecentProjectItem CreatePlaceholder()
            {
                return new RecentProjectItem
                {
                    DisplayName = "(空)",
                    Tooltip = "尚未打开任何项目",
                    ShortcutHint = string.Empty,
                    IsPlaceholder = true
                };
            }
        }

        private readonly record struct MediaInfoProcessResult(bool Success, string Output, string Error, int ExitCode);

        #region 项目快照收集

        private class ProjectSnapshot
        {
            public DateTime CapturedAt { get; set; } = DateTime.Now;
            public string ApplicationVersion { get; set; } = string.Empty;
            public List<ProjectMediaItemSnapshot> MediaFiles { get; set; } = new();
            public List<ProjectClipSnapshot> ClipTasks { get; set; } = new();
            public List<ProjectMergeItemSnapshot> MergeTasks { get; set; } = new();
            public ProjectOutputSettingsSnapshot OutputSettings { get; set; } = new();
            public CropTaskSnapshot CropTask { get; set; } = new();
            public Models.WatermarkParameters? WatermarkParameters { get; set; }
            public WatermarkRemovalSnapshot RemoveWatermark { get; set; } = new();
            public Models.DeduplicateParameters? DeduplicateParameters { get; set; }
            public Models.AudioParameters? AudioParameters { get; set; }
            public Models.TranscodeParameters? TranscodeParameters { get; set; }
            public Models.SubtitleParameters? SubtitleParameters { get; set; }
            public Models.FilterParameters? FilterParameters { get; set; }
            public Models.FlipParameters? FlipParameters { get; set; }
            public Models.MergeParameters? MergeParameters { get; set; }
            public GifTaskSnapshot GifTask { get; set; } = new();
            public bool UseCommandPromptForOperations { get; set; }
            public PlayerStateSnapshot PlayerState { get; set; } = new();
            public InterfaceStateSnapshot InterfaceState { get; set; } = new();
            public CommandPanelStateSnapshot CommandPanel { get; set; } = new();
            public bool IsCommandPreviewVisible { get; set; }
            public bool IsExecutionLogVisible { get; set; }
            public List<BatchSubtitleTaskSnapshot> BatchSubtitleTasks { get; set; } = new();
        }

        private class ProjectMediaItemSnapshot
        {
            public string FilePath { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public bool IsSelected { get; set; }
            public bool IsPlaying { get; set; }
            public TimeSpan Duration { get; set; }
            public string VideoCodec { get; set; } = string.Empty;
            public string AudioCodec { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
            public bool? IsFormatValidated { get; set; }
            public string FormatValidationMessage { get; set; } = string.Empty;
        }

        private class ProjectClipSnapshot
        {
            public string Name { get; set; } = string.Empty;
            public string CustomTitle { get; set; } = string.Empty;
            public long StartTime { get; set; }
            public long EndTime { get; set; }
            public string SourceFilePath { get; set; } = string.Empty;
            public int Order { get; set; }
            public bool IsSelected { get; set; }
        }

        private class ProjectMergeItemSnapshot
        {
            public string FilePath { get; set; } = string.Empty;
            public int Order { get; set; }
            public TimeSpan Duration { get; set; }
            public string VideoCodec { get; set; } = string.Empty;
            public string AudioCodec { get; set; } = string.Empty;
            public string Resolution { get; set; } = string.Empty;
        }

        private class ProjectOutputSettingsSnapshot
        {
            public string OutputPath { get; set; } = string.Empty;
            public string FileNamingMode { get; set; } = string.Empty;
            public string CustomPrefix { get; set; } = string.Empty;
            public string CustomSuffix { get; set; } = string.Empty;
            public string OutputFormat { get; set; } = string.Empty;
            public string VideoCodec { get; set; } = string.Empty;
            public string AudioCodec { get; set; } = string.Empty;
            public string AudioBitrate { get; set; } = string.Empty;
            public int Quality { get; set; }
            public string CustomArgs { get; set; } = string.Empty;
            public bool IsValid { get; set; }
        }

        private class CropTaskSnapshot
        {
            public string X { get; set; } = string.Empty;
            public string Y { get; set; } = string.Empty;
            public string Width { get; set; } = string.Empty;
            public string Height { get; set; } = string.Empty;
        }

        private class WatermarkRemovalSnapshot
        {
            public string X { get; set; } = string.Empty;
            public string Y { get; set; } = string.Empty;
            public string Width { get; set; } = string.Empty;
            public string Height { get; set; } = string.Empty;
        }

        private class GifTaskSnapshot
        {
            public string StartTime { get; set; } = string.Empty;
            public string EndTime { get; set; } = string.Empty;
            public string FramesPerSecond { get; set; } = string.Empty;
            public string Width { get; set; } = string.Empty;
            public string QualityOption { get; set; } = string.Empty;
        }

        private class PlayerStateSnapshot
        {
            public string CurrentFilePath { get; set; } = string.Empty;
            public string CurrentFileName { get; set; } = string.Empty;
            public long CurrentPosition { get; set; }
            public long Duration { get; set; }
            public long InPoint { get; set; }
            public long OutPoint { get; set; }
            public bool HasInPoint { get; set; }
            public bool HasOutPoint { get; set; }
            public float Volume { get; set; }
            public bool IsMuted { get; set; }
            public float PlaybackRate { get; set; }
            public bool IsLoopEnabled { get; set; }
            public bool IsSinglePlayMode { get; set; }
        }

        private class InterfaceStateSnapshot
        {
            public string InterfaceMode { get; set; } = string.Empty;
            public string Theme { get; set; } = string.Empty;
            public string LayoutPreset { get; set; } = string.Empty;
            public bool IsFullScreen { get; set; }
            public double WindowWidth { get; set; }
            public double WindowHeight { get; set; }
            public GridLengthSnapshot? LeftColumn { get; set; }
            public GridLengthSnapshot? RightColumn { get; set; }
            public GridLengthSnapshot? BottomRow { get; set; }
            public GridLengthSnapshot? BottomStatusRow { get; set; }
            public bool CommandPreviewVisible { get; set; }
            public bool ExecutionLogVisible { get; set; }
        }

        private class GridLengthSnapshot
        {
            public double Value { get; set; }
            public GridUnitType UnitType { get; set; }

            public static GridLengthSnapshot FromGridLength(GridLength gridLength)
            {
                return new GridLengthSnapshot
                {
                    Value = gridLength.Value,
                    UnitType = gridLength.GridUnitType
                };
            }
        }

        private class CommandPanelStateSnapshot
        {
            public string EmbeddedFfmpegPath { get; set; } = string.Empty;
            public string EmbeddedCommand { get; set; } = string.Empty;
            public string EmbeddedConsoleOutput { get; set; } = string.Empty;
            public string ExecutionLog { get; set; } = string.Empty;
            public string CommandPreview { get; set; } = string.Empty;
            public string CommandDescription { get; set; } = string.Empty;
        }

        private class BatchSubtitleTaskSnapshot
        {
            public string Id { get; set; } = string.Empty;
            public string SourceFilePath { get; set; } = string.Empty;
            public string SourceFileName { get; set; } = string.Empty;
            public string? ClipName { get; set; }
            public long? ClipStartTime { get; set; }
            public long? ClipEndTime { get; set; }
            public string Provider { get; set; } = string.Empty; // AsrProvider as string
            public string TaskType { get; set; } = string.Empty; // BatchSubtitleTaskType as string
            public string Status { get; set; } = string.Empty; // BatchSubtitleTaskStatus as string
            public double Progress { get; set; }
            public string? OutputSrtPath { get; set; }
            public string? ErrorMessage { get; set; }
        }

        private ProjectSnapshot CaptureProjectSnapshot()
        {
            var snapshot = new ProjectSnapshot
            {
                CapturedAt = DateTime.Now,
                ApplicationVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "dev",
                UseCommandPromptForOperations = UseCommandPromptForCropCheckBox?.IsChecked ?? false,
                IsCommandPreviewVisible = _isCommandPreviewVisible,
                IsExecutionLogVisible = _isExecutionLogVisible
            };

            if (_videoListViewModel != null)
            {
                snapshot.MediaFiles = _videoListViewModel.Files.Select(file => new ProjectMediaItemSnapshot
                {
                    FilePath = file.FilePath,
                    FileName = file.FileName,
                    IsSelected = file.IsSelected,
                    IsPlaying = file.IsPlaying,
                    Duration = file.Duration,
                    VideoCodec = file.VideoCodec,
                    AudioCodec = file.AudioCodec,
                    Width = file.Width,
                    Height = file.Height,
                    IsFormatValidated = file.IsFormatValidated,
                    FormatValidationMessage = file.FormatValidationMessage
                }).ToList();
            }

            if (_clipManager != null)
            {
                snapshot.ClipTasks = _clipManager.Clips.Select(clip => new ProjectClipSnapshot
                {
                    Name = clip.Name,
                    CustomTitle = clip.CustomTitle,
                    StartTime = clip.StartTime,
                    EndTime = clip.EndTime,
                    SourceFilePath = clip.SourceFilePath,
                    Order = clip.Order,
                    IsSelected = clip.IsSelected
                }).ToList();
            }

            snapshot.MergeTasks = _mergeItems.Select(item => new ProjectMergeItemSnapshot
            {
                FilePath = item.FilePath,
                Order = item.Order,
                Duration = item.Duration,
                VideoCodec = item.VideoCodec,
                AudioCodec = item.AudioCodec,
                Resolution = item.Resolution
            }).ToList();

            var uiOutputSettings = GetOutputSettings(validate: false);
            snapshot.OutputSettings = new ProjectOutputSettingsSnapshot
            {
                OutputPath = uiOutputSettings.OutputPath,
                FileNamingMode = uiOutputSettings.FileNamingMode,
                CustomPrefix = uiOutputSettings.CustomPrefix,
                CustomSuffix = uiOutputSettings.CustomSuffix,
                OutputFormat = uiOutputSettings.OutputFormat,
                VideoCodec = uiOutputSettings.VideoCodec,
                AudioCodec = uiOutputSettings.AudioCodec,
                AudioBitrate = uiOutputSettings.AudioBitrate,
                Quality = uiOutputSettings.Quality,
                CustomArgs = uiOutputSettings.CustomArgs,
                IsValid = uiOutputSettings.IsValid
            };

            snapshot.CropTask = CaptureCropTaskState();
            snapshot.RemoveWatermark = CaptureRemoveWatermarkState();
            snapshot.WatermarkParameters = GetWatermarkParameters(showWarnings: false);
            snapshot.DeduplicateParameters = GetDeduplicateParameters();
            snapshot.AudioParameters = GetAudioParameters();
            snapshot.TranscodeParameters = GetTranscodeParameters();
            snapshot.SubtitleParameters = GetSubtitleParameters(showWarnings: false);
            snapshot.FilterParameters = _currentFilterParameters?.Clone();
            snapshot.FlipParameters = GetFlipParameters();
            snapshot.MergeParameters = GetMergeParameters();
            snapshot.GifTask = CaptureGifTaskState();
            snapshot.PlayerState = CapturePlayerState();
            snapshot.InterfaceState = CaptureInterfaceState();
            snapshot.CommandPanel = CaptureCommandPanelState();
            snapshot.BatchSubtitleTasks = CaptureBatchSubtitleTasks();

            Services.DebugLogger.LogInfo($"项目快照: 媒体 {snapshot.MediaFiles.Count}，片段 {snapshot.ClipTasks.Count}，合并 {snapshot.MergeTasks.Count}，批量字幕任务 {snapshot.BatchSubtitleTasks.Count}");
            return snapshot;
        }

        private CropTaskSnapshot CaptureCropTaskState()
        {
            return new CropTaskSnapshot
            {
                X = CropXTextBox?.Text?.Trim() ?? string.Empty,
                Y = CropYTextBox?.Text?.Trim() ?? string.Empty,
                Width = CropWTextBox?.Text?.Trim() ?? string.Empty,
                Height = CropHTextBox?.Text?.Trim() ?? string.Empty
            };
        }

        private WatermarkRemovalSnapshot CaptureRemoveWatermarkState()
        {
            return new WatermarkRemovalSnapshot
            {
                X = txtRemoveX?.Text?.Trim() ?? string.Empty,
                Y = txtRemoveY?.Text?.Trim() ?? string.Empty,
                Width = txtRemoveW?.Text?.Trim() ?? string.Empty,
                Height = txtRemoveH?.Text?.Trim() ?? string.Empty
            };
        }

        private GifTaskSnapshot CaptureGifTaskState()
        {
            var qualityText = (cboGifQuality?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

            return new GifTaskSnapshot
            {
                StartTime = txtGifStartTime?.Text?.Trim() ?? string.Empty,
                EndTime = txtGifEndTime?.Text?.Trim() ?? string.Empty,
                FramesPerSecond = txtGifFPS?.Text?.Trim() ?? string.Empty,
                Width = txtGifWidth?.Text?.Trim() ?? string.Empty,
                QualityOption = qualityText
            };
        }

        private PlayerStateSnapshot CapturePlayerState()
        {
            if (_videoPlayerViewModel == null)
            {
                return new PlayerStateSnapshot();
            }

            return new PlayerStateSnapshot
            {
                CurrentFilePath = _videoPlayerViewModel.CurrentFilePath,
                CurrentFileName = _videoPlayerViewModel.CurrentFileName,
                CurrentPosition = _videoPlayerViewModel.CurrentPosition,
                Duration = _videoPlayerViewModel.Duration,
                InPoint = _videoPlayerViewModel.InPoint,
                OutPoint = _videoPlayerViewModel.OutPoint,
                HasInPoint = _videoPlayerViewModel.HasInPoint,
                HasOutPoint = _videoPlayerViewModel.HasOutPoint,
                Volume = _videoPlayerViewModel.Volume,
                IsMuted = _videoPlayerViewModel.IsMuted,
                PlaybackRate = _videoPlayerViewModel.PlaybackRate,
                IsLoopEnabled = _videoPlayerViewModel.IsLoopEnabled,
                IsSinglePlayMode = _videoPlayerViewModel.IsSinglePlayMode
            };
        }

        private InterfaceStateSnapshot CaptureInterfaceState()
        {
            var snapshot = new InterfaceStateSnapshot
            {
                InterfaceMode = _interfaceMode.ToString(),
                Theme = _currentTheme.ToString(),
                LayoutPreset = _currentLayoutPreset.ToString(),
                IsFullScreen = _isFullScreen,
                WindowWidth = Width,
                WindowHeight = Height,
                CommandPreviewVisible = _isCommandPreviewVisible,
                ExecutionLogVisible = _isExecutionLogVisible
            };

            if (LeftColumn != null)
            {
                snapshot.LeftColumn = GridLengthSnapshot.FromGridLength(LeftColumn.Width);
            }

            if (RightColumn != null)
            {
                snapshot.RightColumn = GridLengthSnapshot.FromGridLength(RightColumn.Width);
            }

            if (BottomRow != null)
            {
                snapshot.BottomRow = GridLengthSnapshot.FromGridLength(BottomRow.Height);
            }

            if (BottomStatusRow != null)
            {
                snapshot.BottomStatusRow = GridLengthSnapshot.FromGridLength(BottomStatusRow.Height);
            }

            return snapshot;
        }

        private CommandPanelStateSnapshot CaptureCommandPanelState()
        {
            return new CommandPanelStateSnapshot
            {
                EmbeddedFfmpegPath = EmbeddedFFmpegPathTextBox?.Text?.Trim() ?? string.Empty,
                EmbeddedCommand = EmbeddedCommandTextBox?.Text ?? string.Empty,
                EmbeddedConsoleOutput = EmbeddedOutputTextBox?.Text ?? string.Empty,
                ExecutionLog = LogOutputBox?.Text ?? string.Empty,
                CommandPreview = CommandPreviewBox?.Text ?? string.Empty,
                CommandDescription = CommandDescriptionBox?.Text ?? string.Empty
            };
        }

        private List<BatchSubtitleTaskSnapshot> CaptureBatchSubtitleTasks()
        {
            if (_batchSubtitleCoordinator == null || _batchSubtitleCoordinator.Tasks.Count == 0)
            {
                return new List<BatchSubtitleTaskSnapshot>();
            }

            return _batchSubtitleCoordinator.Tasks.Select(task => new BatchSubtitleTaskSnapshot
            {
                Id = task.Id,
                SourceFilePath = task.SourceFilePath,
                SourceFileName = task.SourceFileName,
                ClipName = task.ClipName,
                ClipStartTime = task.ClipStartTime,
                ClipEndTime = task.ClipEndTime,
                Provider = task.Provider.ToString(),
                TaskType = task.TaskType.ToString(),
                Status = task.Status.ToString(),
                Progress = task.Progress,
                OutputSrtPath = task.OutputSrtPath,
                ErrorMessage = task.ErrorMessage
            }).ToList();
        }

        #endregion

        /// <summary>
        /// 获取输出设置
        /// </summary>
        private OutputSettings GetOutputSettings(bool validate = true)
        {
            var settings = new OutputSettings();
            var isValid = true;

            try
            {
                // 输出路径
                settings.OutputPath = OutputPathBox.Text.Trim();
                if (validate)
                {
                if (string.IsNullOrEmpty(settings.OutputPath))
            {
                        isValid = false;
                    settings.ErrorMessage = "输出路径不能为空";
                        settings.IsValid = isValid;
                    return settings;
            }

                if (!Directory.Exists(settings.OutputPath))
            {
                try
                {
                        Directory.CreateDirectory(settings.OutputPath);
                    }
                    catch
                    {
                            isValid = false;
                        settings.ErrorMessage = "输出路径无效或无法创建";
                            settings.IsValid = isValid;
                        return settings;
                    }
                    }
                }
                else if (string.IsNullOrWhiteSpace(settings.OutputPath))
                {
                    isValid = false;
                }

                // 文件命名模式
                var radioButtons = new[] {
                    new { Element = (FrameworkElement)FindName("OriginalNameRadio"), Mode = "原文件名_处理" },
                    new { Element = (FrameworkElement)FindName("CustomPrefixRadio"), Mode = "自定义前缀" },
                    new { Element = (FrameworkElement)FindName("CustomSuffixRadio"), Mode = "自定义后缀" }
                };

                foreach (var radio in radioButtons)
                {
                    if (radio.Element != null)
                    {
                        var radioButton = (RadioButton)radio.Element;
                        if (radioButton.IsChecked == true)
                    {
                            settings.FileNamingMode = radio.Mode;
                            if (radio.Mode == "自定义前缀")
                            {
                                settings.CustomPrefix = CustomPrefixBox.Text.Trim();
                            }
                            else if (radio.Mode == "自定义后缀")
                            {
                                settings.CustomSuffix = CustomSuffixBox.Text.Trim();
                            }
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(settings.FileNamingMode))
                {
                    settings.FileNamingMode = "原文件名_处理"; // 默认值
                }

                // 输出格式
                if (OutputFormatBox.SelectedItem is ComboBoxItem item)
                {
                    settings.OutputFormat = item.Content.ToString() ?? "MP4 (推荐)";
                }
                else
                {
                    settings.OutputFormat = "MP4 (推荐)";
                }

                // 视频编码器
                var videoCodecRadios = new[] {
                    new { Element = (FrameworkElement)FindName("CopyCodecRadio"), Codec = "复制" },
                    new { Element = (FrameworkElement)FindName("H264CodecRadio"), Codec = "H.264" },
                    new { Element = (FrameworkElement)FindName("H265CodecRadio"), Codec = "H.265" }
                        };

                foreach (var radio in videoCodecRadios)
                {
                    if (radio.Element != null)
                    {
                        var radioButton = (RadioButton)radio.Element;
                        if (radioButton.IsChecked == true)
                        {
                            settings.VideoCodec = radio.Codec;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(settings.VideoCodec))
                {
                    settings.VideoCodec = "复制"; // 默认值
                }

                // 质量设置
                settings.Quality = (int)QualitySlider.Value;

                // 音频设置
                if (AudioCodecBox.SelectedItem is ComboBoxItem audioItem)
                        {
                    settings.AudioCodec = audioItem.Content.ToString() ?? "AAC";
                        }
                        else
                        {
                    settings.AudioCodec = "AAC";
                        }

                if (AudioBitrateBox.SelectedItem is ComboBoxItem bitrateItem)
                {
                    settings.AudioBitrate = bitrateItem.Content.ToString() ?? "128 kbps";
                    }
                    else
                    {
                    settings.AudioBitrate = "128 kbps";
                    }

                // 自定义参数
                settings.CustomArgs = CustomArgsBox.Text.Trim();

                settings.IsValid = isValid;
                }
                catch (Exception ex)
                {
                settings.IsValid = false;
                settings.ErrorMessage = $"获取输出设置失败: {ex.Message}";
                Services.DebugLogger.LogError($"GetOutputSettings 失败: {ex.Message}");
                }

            return settings;
        }

        /// <summary>
        /// 生成输出文件名
        /// </summary>
        private string GenerateOutputFileName(string inputPath, CropParameterValidationResult cropParams, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_处理
                    outputFileName = $"{inputFileName}_crop_{cropParams.Width}x{cropParams.Height}";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 获取文件扩展名
        /// </summary>
        private string GetFileExtension(string format)
        {
            return format.ToLower() switch
            {
                "mp4" or "mp4 (推荐)" => ".mp4",
                "mkv" => ".mkv",
                "avi" => ".avi",
                "mov" => ".mov",
                "wmv" => ".wmv",
                "flv" => ".flv",
                "webm" => ".webm",
                "m4v" => ".m4v",
                "mpg" => ".mpg",
                "mpeg" => ".mpeg",
                "ts" => ".ts",
                "m2ts" => ".m2ts",
                _ => ".mp4"
            };
        }

        /// <summary>
        /// 更新状态栏
        /// </summary>
        private void UpdateStatusBar(string message, string icon, string color, string currentTask = null)
        {
            Dispatcher.Invoke(() =>
            {
                StatusBarText.Text = message;
                StatusIcon.Text = icon;
                StatusIcon.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

                // 更新当前任务状态（如果提供了参数）
                if (currentTask != null && StatusCurrentTask != null)
        {
                    StatusCurrentTask.Text = currentTask;
                }
            });
        }

        /// <summary>
        /// 获取FFmpeg视频编码器名称
        /// </summary>
        private string GetVideoCodecForFFmpeg(string uiVideoCodec)
        {
            return uiVideoCodec switch
            {
                "复制" => "copy",
                "H.264" => "libx264",
                "H.265" => "libx265",
                _ => "libx264"
            };
        }

        /// <summary>
        /// 获取FFmpeg音频编码器名称
        /// </summary>
        private string GetAudioCodecForFFmpeg(string uiAudioCodec)
        {
            return uiAudioCodec switch
            {
                "复制" => "copy",
                "AAC" => "aac",
                "MP3" => "libmp3lame",
                _ => "aac"
            };
        }

        /// <summary>
        /// 更新编码器警告显示
        /// </summary>
        private void UpdateCodecWarning(OutputSettings settings)
        {
            Dispatcher.Invoke(() =>
            {
                if (settings.VideoCodec == "复制")
        {
                    CropCodecWarningText.Text = "⚠️ 当前选择复制编码器，不支持裁剪操作。请切换到 H.264 或 H.265 编码器。";
                    CropCodecWarningText.Visibility = Visibility.Visible;
                }
                else
                {
                    CropCodecWarningText.Text = "";
                    CropCodecWarningText.Visibility = Visibility.Collapsed;
                }
            });
        }

        /// <summary>
        /// 更新命令预览
        /// </summary>
        private void UpdateCommandPreview(VideoFile sampleFile, CropParameterValidationResult cropParams, OutputSettings settings)
        {
            try
            {
                var inputPath = sampleFile.FilePath;
                var outputFileName = GenerateOutputFileName(inputPath, cropParams, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                Dispatcher.Invoke(() =>
                {
                    // 检查是否可以进行裁剪
                    if (settings.VideoCodec == "复制")
                    {
                        CommandPreviewBox.Text = "⚠️ 无法预览命令：复制编码器不能与裁剪操作同时使用\r\n\r\n请在输出设置中选择H.264或H.265编码器来启用裁剪功能。";
                        CommandDescriptionBox.Text = "• 状态: 不支持的配置\r\n" +
                                                    "• 原因: 复制编码器 + 裁剪过滤器冲突\r\n" +
                                                    "• 解决方案: 选择H.264或H.265编码器";
                        return;
                    }

                    // 构建FFmpeg命令参数
                    var args = new List<string>();

                    // 输入文件
                    args.Add($"-i \"{inputPath}\"");

                    // 裁剪参数
                    args.Add($"-filter:v \"crop={cropParams.Width}:{cropParams.Height}:{cropParams.X}:{cropParams.Y}\"");

                    // 视频编码
                    switch (settings.VideoCodec)
                    {
                        case "H.264":
                            args.Add("-c:v libx264");
                            args.Add($"-crf {settings.Quality}");
                            args.Add("-preset faster");
                            args.Add("-tune zerolatency");
                            break;
                        case "H.265":
                            args.Add("-c:v libx265");
                            args.Add($"-crf {settings.Quality}");
                            args.Add("-preset faster");
                            break;
                    }

                    // 音频编码
                    switch (settings.AudioCodec)
                    {
                        case "AAC":
                            args.Add("-c:a aac");
                            args.Add($"-b:a {settings.AudioBitrate.Replace(" kbps", "k")}");
                            break;
                        case "MP3":
                            args.Add("-c:a libmp3lame");
                            args.Add($"-b:a {settings.AudioBitrate.Replace(" kbps", "k")}");
                            break;
                        case "复制":
                            args.Add("-c:a copy");
                            break;
                    }

                    // 自定义参数
                    if (!string.IsNullOrEmpty(settings.CustomArgs))
                    {
                        args.Add(settings.CustomArgs);
                    }

                    // 通用参数
                    args.Add("-movflags +faststart");
                    args.Add("-y");

                    // 输出文件
                    args.Add($"\"{outputPath}\"");

                    var command = $"ffmpeg {string.Join(" ", args)}";

                    CommandPreviewBox.Text = command;
                    CommandDescriptionBox.Text = $"• 输入文件: {Path.GetFileName(inputPath)}\r\n" +
                                                $"• 输出文件: {outputFileName}\r\n" +
                                                $"• 裁剪参数: {cropParams.Width}x{cropParams.Height} @ ({cropParams.X},{cropParams.Y})\r\n" +
                                                $"• 视频编码: {settings.VideoCodec}\r\n" +
                                                $"• 音频编码: {settings.AudioCodec} ({settings.AudioBitrate})\r\n" +
                                                $"• 输出格式: {settings.OutputFormat}";
                });
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateCommandPreview 失败: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    CommandPreviewBox.Text = $"⚠️ 命令预览生成失败: {ex.Message}";
                    CommandDescriptionBox.Text = "• 状态: 预览失败\r\n• 请检查参数设置";
                });
            }
        }

        /// <summary>
        /// 执行裁剪按钮点击事件 - 执行批量裁剪操作
        /// </summary>
        private async void ExecuteCropButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始执行批量视频裁剪...");

                // 检查是否通过命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    await ExecuteCropViaCommandPrompt();
                    return;
                }

                // 获取被选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    var message = "⚠️ 未选择文件\r\n\r\n请在左侧文件列表中勾选需要裁剪的视频文件。\r\n\r\n" +
                                 "提示：点击文件名左侧的复选框来选择文件。";

                    var msgResult = MessageBox.Show(message, "选择文件提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

                    Services.DebugLogger.LogInfo("用户尝试执行裁剪但未选择文件");
                    return;
                }

                // 获取裁剪参数
                var validationResult = ValidateAndParseCropParameters(
                    CropXTextBox.Text, CropYTextBox.Text,
                    CropWTextBox.Text, CropHTextBox.Text);

                if (!validationResult.IsValid)
                {
                    Services.ToastNotification.ShowError($"裁剪参数无效: {validationResult.ErrorMessage}");
                    Services.DebugLogger.LogError($"裁剪参数验证失败: {validationResult.ErrorMessage}");
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    Services.ToastNotification.ShowError($"输出设置无效: {outputSettings.ErrorMessage}");
                    return;
                }

                // 检查是否可以进行裁剪：复制编码器不能与裁剪同时使用
                if (outputSettings.VideoCodec == "复制")
                {
                    // 在UI线程中显示对话框询问用户是否要自动切换编码器
                    var msgResult = MessageBox.Show(
                        "⚠️ 复制编码器不支持裁剪操作\r\n\r\n" +
                        "裁剪需要重新编码视频才能应用过滤器。\r\n\r\n" +
                        "是否要自动切换到推荐的 H.264 编码器？\r\n\r\n" +
                        "H.264 提供最佳的质量与速度平衡。",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        // 用户选择自动切换，修改RadioButton选择
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });

                        Services.ToastNotification.ShowInfo("已自动切换到 H.264 编码器，请重新点击'执行裁剪'按钮");
                        Services.DebugLogger.LogInfo("用户选择自动切换到H.264编码器进行裁剪操作");
                    }
                    else
                    {
                        Services.ToastNotification.ShowWarning("请在'输出设置'中手动选择 H.264 或 H.265 编码器，然后重新执行裁剪");
                        Services.DebugLogger.LogInfo("用户拒绝自动切换编码器");
                    }

                    return;
                }

                // 取消之前的裁剪操作（如果存在）
                _cropCancellationTokenSource?.Cancel();
                _cropCancellationTokenSource = new CancellationTokenSource();

                var cancellationToken = _cropCancellationTokenSource.Token;

                // 生成命令预览和编码器警告
                UpdateCommandPreview(selectedFiles.First(), validationResult, outputSettings);
                UpdateCodecWarning(outputSettings);

                // 准备批量任务
                    var cropParameters = new Models.CropParameters
                    {
                        X = validationResult.X,
                        Y = validationResult.Y,
                        Width = validationResult.Width,
                        Height = validationResult.Height
                    };

                var batchTasks = selectedFiles.Select(inputFile =>
                {
                    var outputFileName = GenerateOutputFileName(inputFile.FilePath, validationResult, outputSettings);
                    var outputPath = Path.Combine(outputSettings.OutputPath, outputFileName);

                    return new Services.FfmpegBatchProcessor.BatchTask
                    {
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        Description = $"处理文件: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                        ExecuteTask = async (input, output, ct) =>
                        {
                            var fileStartTime = DateTime.Now;

                    // 创建裁剪历史记录
                    var historyRecord = new Models.CropHistory
                    {
                                OriginalVideoPath = input,
                                OutputVideoPath = output,
                        Parameters = cropParameters,
                        Status = Models.CropStatus.Processing
                    };
                    _cropHistoryService.AddCropRecord(historyRecord);

                            Services.DebugLogger.LogInfo($"开始裁剪文件: {input} -> {output}");

                    try
                    {
                                var result = await _videoProcessingService.CropVideoAsync(
                                    input, output, cropParameters,
                        GetVideoCodecForFFmpeg(outputSettings.VideoCodec),
                        outputSettings.Quality,
                        GetAudioCodecForFFmpeg(outputSettings.AudioCodec),
                        outputSettings.AudioBitrate,
                        outputSettings.CustomArgs,
                                    null, // 进度由批量处理器统一处理
                                    ct);

                    var fileProcessingTime = (long)(DateTime.Now - fileStartTime).TotalMilliseconds;
                    long outputFileSize = 0;
                                if (result.Success && File.Exists(output))
                    {
                                    outputFileSize = new FileInfo(output).Length;
                    }

                                // 更新历史记录
                    _cropHistoryService.UpdateCropStatus(
                        historyRecord.Id,
                        result.Success ? Models.CropStatus.Completed : Models.CropStatus.Failed,
                        result.Success ? null : result.ErrorMessage,
                        fileProcessingTime,
                        outputFileSize);

                                return result;
                            }
                            catch (OperationCanceledException)
                            {
                                _cropHistoryService.UpdateCropStatus(historyRecord.Id, Models.CropStatus.Cancelled, "用户取消", 0, 0);
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _cropHistoryService.UpdateCropStatus(historyRecord.Id, Models.CropStatus.Failed, ex.Message, 0, 0);
                                throw;
                            }
                        },
                        EstimatedDuration = null // 由VideoProcessingService内部估算
                    };
                }).ToList();

                var config = new Services.FfmpegBatchProcessor.BatchConfig
                {
                    OperationName = "批量视频裁剪",
                    OperationIcon = "⚙️",
                    OperationColor = "#2196F3",
                    LogHeaderLines = new List<string>
                    {
                        $"📐 裁剪参数: {validationResult.Width}x{validationResult.Height} @ ({validationResult.X},{validationResult.Y})",
                        $"📁 输出路径: {outputSettings.OutputPath}",
                        $"📝 文件命名: {outputSettings.FileNamingMode}",
                        $"🎬 输出格式: {outputSettings.OutputFormat}",
                        $"🎵 音频设置: {outputSettings.AudioCodec} @ {outputSettings.AudioBitrate}"
                    },
                    UpdateStatusBar = UpdateStatusBar,
                    UpdateProgress = (progress, text) =>
                {
                        ExecutionProgressBar.Value = progress;
                        ProgressInfoText.Text = text;
                    },
                    AppendLog = (text) => LogOutputBox.Text += text,
                    SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0, // 执行日志现在是第1个标签页（索引0）
                    InitializeLog = (text) => LogOutputBox.Text = text
                };

                var batchResult = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

                // 显示结果提示
                if (batchResult.SuccessCount == batchResult.TotalTasks)
                {
                    Services.ToastNotification.ShowSuccess($"批量裁剪全部完成!\n成功处理 {batchResult.SuccessCount} 个文件");
                }
                else if (batchResult.SuccessCount > 0)
                {
                    Services.ToastNotification.ShowWarning($"批量裁剪部分完成\n成功: {batchResult.SuccessCount}, 失败: {batchResult.FailCount}");
                }
                else
                {
                    Services.ToastNotification.ShowError($"批量裁剪全部失败\n请检查日志了解详情");
                }

                Services.DebugLogger.LogInfo($"批量裁剪完成: {batchResult.SuccessCount}/{batchResult.TotalTasks}成功, {batchResult.FailCount}失败");
            }
            catch (Exception ex)
            {
                Services.ToastNotification.ShowError($"执行批量裁剪失败: {ex.Message}");
                Services.DebugLogger.LogError($"ExecuteCropButton_Click 失败: {ex.Message}");

                // 更新状态栏显示错误
                UpdateStatusBar("处理失败", "❌", "#F44336", "空闲");
            }
        }

        /// <summary>
        /// 通过命令提示符执行裁剪
        /// </summary>
        private async Task ExecuteCropViaCommandPrompt()
        {
            try
            {
                Services.DebugLogger.LogInfo("通过命令提示符执行裁剪...");

                // 获取被选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    var message = "⚠️ 未选择文件\r\n\r\n请在左侧文件列表中勾选需要裁剪的视频文件。\r\n\r\n" +
                                 "提示：点击文件名左侧的复选框来选择文件。";

                    var msgResult2 = MessageBox.Show(message, "选择文件提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

                    Services.DebugLogger.LogInfo("用户尝试执行裁剪但未选择文件");
                    return;
                }

                // 获取裁剪参数
                var validationResult = ValidateAndParseCropParameters(
                    CropXTextBox.Text, CropYTextBox.Text,
                    CropWTextBox.Text, CropHTextBox.Text);

                if (!validationResult.IsValid)
                {
                    Services.ToastNotification.ShowError($"裁剪参数无效: {validationResult.ErrorMessage}");
                    Services.DebugLogger.LogError($"裁剪参数验证失败: {validationResult.ErrorMessage}");
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    Services.ToastNotification.ShowError($"输出设置无效: {outputSettings.ErrorMessage}");
                    return;
                }

                // 检查是否可以进行裁剪：复制编码器不能与裁剪同时使用
                if (outputSettings.VideoCodec == "复制")
                {
                    var msgResult3 = MessageBox.Show(
                        "⚠️ 复制编码器不支持裁剪操作\r\n\r\n" +
                        "裁剪需要重新编码视频才能应用过滤器。\r\n\r\n" +
                        "是否要自动切换到推荐的 H.264 编码器？\r\n\r\n" +
                        "H.264 提供最佳的质量与速度平衡。",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult3 == MessageBoxResult.Yes)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });
                        Services.ToastNotification.ShowInfo("已自动切换到 H.264 编码器，请重新点击'执行裁剪'按钮");
                        return;
                    }
                    else
                    {
                        Services.ToastNotification.ShowWarning("请在'输出设置'中手动选择 H.264 或 H.265 编码器，然后重新执行裁剪");
                        return;
                    }
                }

                // 准备命令列表
                var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();
                var cropParameters = new Models.CropParameters
                {
                    X = validationResult.X,
                    Y = validationResult.Y,
                    Width = validationResult.Width,
                    Height = validationResult.Height
                };

                for (int i = 0; i < selectedFiles.Count; i++)
                {
                    var inputFile = selectedFiles[i];
                    var outputFileName = GenerateOutputFileName(inputFile.FilePath, validationResult, outputSettings);
                    var outputPath = Path.Combine(outputSettings.OutputPath, outputFileName);

                    try
                    {
                        var ffmpegArgs = GenerateCropFFmpegCommand(
                            inputFile.FilePath, outputPath,
                            cropParameters,
                            GetVideoCodecForFFmpeg(outputSettings.VideoCodec),
                            outputSettings.Quality,
                            GetAudioCodecForFFmpeg(outputSettings.AudioCodec),
                            outputSettings.AudioBitrate,
                            outputSettings.CustomArgs);

                        commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                        {
                            Index = i + 1,
                            Total = selectedFiles.Count,
                            TaskId = Path.GetFileName(inputFile.FilePath),
                            InputPath = inputFile.FilePath,
                            OutputPath = outputPath,
                            CommandArguments = ffmpegArgs
                        });
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogError($"生成裁剪命令失败: {ex.Message}");
                        // 继续处理其他文件
                    }
                }

                if (commands.Count > 0)
                {
                    var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                    {
                        OperationName = "FFmpeg 裁剪命令生成器",
                        OperationIcon = "🎬",
                        SummaryLines = new List<string>
                        {
                            $"📊 待处理文件数: {selectedFiles.Count}",
                            $"📐 裁剪参数: {validationResult.Width}x{validationResult.Height} @ ({validationResult.X},{validationResult.Y})",
                            $"🎬 输出格式: {outputSettings.VideoCodec} + {outputSettings.AudioCodec}"
                        },
                        AppendOutput = (text) => EmbeddedAppendOutput(text),
                AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1, // 命令预览现在是第2个标签页（索引1）
                        SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                    };

                    _ffmpegCommandPreviewService.ShowCommands(commands, config);
                Services.ToastNotification.ShowInfo("FFmpeg命令已生成，请在命令提示符中执行");
                }

                Services.DebugLogger.LogInfo($"为 {selectedFiles.Count} 个文件生成了FFmpeg裁剪命令");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ExecuteCropViaCommandPrompt 失败: {ex.Message}");
                Services.ToastNotification.ShowError($"生成裁剪命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成裁剪FFmpeg命令
        /// </summary>
        private string GenerateCropFFmpegCommand(string inputPath, string outputPath,
            Models.CropParameters cropParameters, string videoCodec = "libx264",
            int quality = 20, string audioCodec = "aac", string audioBitrate = "128k",
            string customArgs = "")
        {
            // 检查是否可以进行裁剪：复制编码器不能与过滤器同时使用
            if (videoCodec.ToLower() == "copy")
            {
                throw new InvalidOperationException("无法使用复制编码器进行裁剪操作。裁剪需要重新编码视频，请选择H.264或H.265编码器。");
            }

            // 基本裁剪参数: -filter:v "crop=w:h:x:y"
            var cropFilter = $"crop={cropParameters.Width}:{cropParameters.Height}:{cropParameters.X}:{cropParameters.Y}";

            // 构建基础参数
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\"",
                "-filter:v", $"\"{cropFilter}\""
            };

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                // 简单的参数解析，将空格分隔的参数添加到列表中
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 添加通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 锁定比例复选框选中事件
        /// </summary>
        private void LockAspectRatioCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("裁剪比例锁定已启用");
                Services.ToastNotification.ShowInfo("比例锁定已启用");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"LockAspectRatioCheckBox_Checked 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 锁定比例复选框取消选中事件
        /// </summary>
        private void LockAspectRatioCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("裁剪比例锁定已禁用");
                Services.ToastNotification.ShowInfo("比例锁定已禁用");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"LockAspectRatioCheckBox_Unchecked 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 裁剪参数输入框文本变化 - 反向更新Canvas裁剪框
        /// </summary>
        private void CropParameter_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingCropFromCode) return; // 避免循环更新

            try
            {
                // 解析和验证参数（现在参数是实际视频坐标）
                var validationResult = ValidateAndParseCropParameters(
                    CropXTextBox.Text, CropYTextBox.Text,
                    CropWTextBox.Text, CropHTextBox.Text);

                if (!validationResult.IsValid)
                {
                    Services.DebugLogger.LogWarning($"裁剪参数无效: {validationResult.ErrorMessage}");
                    return;
                }

                // 用户输入的是实际视频坐标
                int actualX = validationResult.X;
                int actualY = validationResult.Y;
                int actualW = validationResult.Width;
                int actualH = validationResult.Height;

                // 获取当前视频的实际分辨率
                var videoWidth = _videoPlayerViewModel.VideoWidth > 0 ? _videoPlayerViewModel.VideoWidth : 1920;
                var videoHeight = _videoPlayerViewModel.VideoHeight > 0 ? _videoPlayerViewModel.VideoHeight : 1080;

                // 获取视频显示区域边界
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                // 将实际视频坐标转换为相对于视频显示区域的相对坐标
                double relativeX = (actualX / (double)videoWidth) * videoDisplayRect.Width;
                double relativeY = (actualY / (double)videoHeight) * videoDisplayRect.Height;
                double relativeW = (actualW / (double)videoWidth) * videoDisplayRect.Width;
                double relativeH = (actualH / (double)videoHeight) * videoDisplayRect.Height;

                // 转换为Canvas的逻辑坐标
                double logicX = videoDisplayRect.X + relativeX;
                double logicY = videoDisplayRect.Y + relativeY;
                double logicW = relativeW;
                double logicH = relativeH;

                // 确保逻辑坐标在视频显示区域内
                logicX = Math.Max(videoDisplayRect.X, Math.Min(logicX, videoDisplayRect.X + videoDisplayRect.Width - logicW));
                logicY = Math.Max(videoDisplayRect.Y, Math.Min(logicY, videoDisplayRect.Y + videoDisplayRect.Height - logicH));
                logicW = Math.Min(logicW, videoDisplayRect.Width);
                logicH = Math.Min(logicH, videoDisplayRect.Height);

                // 更新Popup裁剪框（使用逻辑坐标）
                if (PopupCropCanvas != null && PopupCropSelector != null)
                {
                    Canvas.SetLeft(PopupCropSelector, logicX);
                    Canvas.SetTop(PopupCropSelector, logicY);
                    PopupCropSelector.Width = logicW;
                    PopupCropSelector.Height = logicH;

                    UpdatePopupCropMask();
                    UpdatePopupCropDisplay();

                    Services.DebugLogger.LogInfo($"从参数框更新Popup裁剪框: 实际坐标 X={actualX}, Y={actualY}, W={actualW}, H={actualH} → 相对坐标: X={relativeX:F1}, Y={relativeY:F1}, W={relativeW:F1}, H={relativeH:F1} → 逻辑坐标: X={logicX:F1}, Y={logicY:F1}, W={logicW:F1}, H={logicH:F1}");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"CropParameter_TextChanged 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重置裁剪按钮点击事件
        /// </summary>
        private void ResetCropButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Canvas版本：直接重置裁剪框
                InitializePopupCropSelector();
                Services.ToastNotification.ShowInfo("裁剪框已重置到中心位置");
            }
            catch (Exception ex)
            {
                Services.ToastNotification.ShowError($"重置裁剪失败: {ex.Message}");
                Services.DebugLogger.LogError($"ResetCropButton_Click 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 取消选择操作 - 取消裁剪区域选择或正在进行的裁剪操作
        /// </summary>
        private void CancelCropButton_Click(object sender, RoutedEventArgs e)
                {
            try
            {
                // 优先检查是否有正在进行的裁剪操作需要取消
                if (_cropCancellationTokenSource != null && !_cropCancellationTokenSource.IsCancellationRequested)
                {
                    _cropCancellationTokenSource.Cancel();
                    Services.DebugLogger.LogInfo("用户主动取消裁剪操作");
                    Services.ToastNotification.ShowInfo("正在取消裁剪操作...");
                    return;
                }

                // 如果没有正在进行的操作，则隐藏裁剪框（取消选择）
                if (CropOverlayPopup.IsOpen)
                {
                    CropOverlayPopup.IsOpen = false;
                    UpdateCropSelectorButtonState();
                    Services.DebugLogger.LogInfo("裁剪区域选择已取消");
                    Services.ToastNotification.ShowInfo("裁剪区域选择已取消");
                }
                else
                {
                    Services.ToastNotification.ShowInfo("当前没有正在进行的裁剪操作，也没有显示的裁剪框");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"CancelCropButton_Click 失败: {ex.Message}");
                Services.ToastNotification.ShowError($"取消选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置裁剪预设
        /// </summary>
        private void SetCropPreset(double aspectRatio, string presetName)
        {
            try
            {
                var videoWidth = _videoPlayerViewModel.VideoWidth;
                var videoHeight = _videoPlayerViewModel.VideoHeight;

                if (videoWidth <= 0 || videoHeight <= 0)
                {
                    Services.ToastNotification.ShowWarning("请先加载视频文件");
                    return;
                }

                int cropWidth, cropHeight, cropX, cropY;

                // 智能裁剪算法：根据目标比例和视频比例的关系选择最佳策略
                var videoRatio = (double)videoWidth / videoHeight;

                if (aspectRatio > videoRatio)
                {
                    // 目标比例更宽：保持宽度，裁剪高度（适合超宽内容如电影）
                    cropWidth = videoWidth;
                    cropHeight = (int)(cropWidth / aspectRatio);

                    // 确保高度不超过视频高度
                    if (cropHeight > videoHeight)
                {
                    cropHeight = videoHeight;
                        cropWidth = (int)(cropHeight * aspectRatio);
                    }
                }
                else if (aspectRatio < videoRatio)
                {
                    // 目标比例更窄：保持高度，裁剪宽度（适合竖屏内容）
                    cropHeight = videoHeight;
                    cropWidth = (int)(cropHeight * aspectRatio);

                    // 确保宽度不超过视频宽度
                    if (cropWidth > videoWidth)
                    {
                        cropWidth = videoWidth;
                        cropHeight = (int)(cropWidth / aspectRatio);
                    }
                }
                else
                {
                    // 比例相同：使用全屏
                    cropWidth = videoWidth;
                        cropHeight = videoHeight;
                }

                // 计算居中位置
                cropX = (videoWidth - cropWidth) / 2;
                cropY = (videoHeight - cropHeight) / 2;

                // 设置参数框
                CropXTextBox.Text = cropX.ToString();
                CropYTextBox.Text = cropY.ToString();
                CropWTextBox.Text = cropWidth.ToString();
                CropHTextBox.Text = cropHeight.ToString();

                // 直接设置居中的可视化裁剪框（确保预设总是居中）
                // Popup版本：设置Popup裁剪框
                if (PopupCropCanvas != null && PopupCropSelector != null)
                {
                    // 获取视频在逻辑坐标系中的显示区域（考虑黑边）
                    var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                    // 将视频显示区域从逻辑坐标系转换为Canvas坐标系
                    double canvasScaleX = PopupCropCanvas.Width / 1920.0;
                    double canvasScaleY = PopupCropCanvas.Height / 1080.0;

                    double overlayVideoX = videoDisplayRect.X * canvasScaleX;
                    double overlayVideoY = videoDisplayRect.Y * canvasScaleY;
                    double overlayVideoWidth = videoDisplayRect.Width * canvasScaleX;
                    double overlayVideoHeight = videoDisplayRect.Height * canvasScaleY;

                    // 计算裁剪尺寸在Canvas坐标系中的大小
                    double scaleX = overlayVideoWidth / videoWidth;
                    double scaleY = overlayVideoHeight / videoHeight;
                    double canvasWidth = cropWidth * scaleX;
                    double canvasHeight = cropHeight * scaleY;

                    // 在视频显示区域内居中
                    double canvasX = overlayVideoX + (overlayVideoWidth - canvasWidth) / 2.0;
                    double canvasY = overlayVideoY + (overlayVideoHeight - canvasHeight) / 2.0;

                    // 确保裁剪框在Canvas范围内
                    canvasX = Math.Max(0, Math.Min(canvasX, PopupCropCanvas.Width - canvasWidth));
                    canvasY = Math.Max(0, Math.Min(canvasY, PopupCropCanvas.Height - canvasHeight));
                    canvasWidth = Math.Min(canvasWidth, PopupCropCanvas.Width);
                    canvasHeight = Math.Min(canvasHeight, PopupCropCanvas.Height);

                    // 直接设置Popup裁剪框位置和尺寸
                    Canvas.SetLeft(PopupCropSelector, canvasX);
                    Canvas.SetTop(PopupCropSelector, canvasY);
                    PopupCropSelector.Width = canvasWidth;
                    PopupCropSelector.Height = canvasHeight;

                    // 更新遮罩和显示
                    UpdatePopupCropMask();
                    UpdatePopupCropDisplay();

                    Services.DebugLogger.LogInfo($"Popup裁剪框已设置为预设: 位置=({canvasX:F1}, {canvasY:F1}), 大小=({canvasWidth:F1}x{canvasHeight:F1})");
                }

                Services.ToastNotification.ShowSuccess($"已应用 {presetName} 预设");
                Services.DebugLogger.LogInfo($"应用裁剪预设: {presetName}, 参数: X={cropX}, Y={cropY}, W={cropWidth}, H={cropHeight}");
            }
            catch (Exception ex)
            {
                Services.ToastNotification.ShowError($"应用预设失败: {ex.Message}");
                Services.DebugLogger.LogError($"SetCropPreset 失败: {ex.Message}");
            }
        }

        #endregion

        #region 裁剪参数验证

        /// <summary>
        /// 裁剪参数验证结果
        /// </summary>
        private class CropParameterValidationResult
        {
            public bool IsValid { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
        }

        /// <summary>
        /// 验证和解析裁剪参数
        /// </summary>
        private CropParameterValidationResult ValidateAndParseCropParameters(
            string xText, string yText, string wText, string hText)
        {
            var result = new CropParameterValidationResult();

            try
            {
                // 解析参数 - 增强的类型验证
                if (string.IsNullOrWhiteSpace(xText) || string.IsNullOrWhiteSpace(yText) ||
                    string.IsNullOrWhiteSpace(wText) || string.IsNullOrWhiteSpace(hText))
                {
                    result.ErrorMessage = "所有裁剪参数都不能为空";
                    return result;
                }

                if (!int.TryParse(xText.Trim(), out int x) ||
                    !int.TryParse(yText.Trim(), out int y) ||
                    !int.TryParse(wText.Trim(), out int w) ||
                    !int.TryParse(hText.Trim(), out int h))
                {
                    result.ErrorMessage = "参数必须是有效的整数（不能包含字母或特殊字符）";
                    return result;
                }

                // 获取视频尺寸
                var videoWidth = _videoPlayerViewModel.VideoWidth > 0 ? _videoPlayerViewModel.VideoWidth : 1920;
                var videoHeight = _videoPlayerViewModel.VideoHeight > 0 ? _videoPlayerViewModel.VideoHeight : 1080;

                // 增强的坐标验证
                if (x < 0)
                {
                    result.ErrorMessage = $"X坐标不能为负数 (当前: {x})";
                    return result;
                }

                if (y < 0)
                {
                    result.ErrorMessage = $"Y坐标不能为负数 (当前: {y})";
                    return result;
                }

                // 增强的尺寸验证
                if (w <= 0)
                {
                    result.ErrorMessage = $"宽度必须大于0 (当前: {w})";
                    return result;
                }

                if (h <= 0)
                {
                    result.ErrorMessage = $"高度必须大于0 (当前: {h})";
                    return result;
                }

                // 合理的最大值限制（防止输入过大的数值）
                const int MAX_COORDINATE = 100000;
                const int MAX_DIMENSION = 100000;

                if (x > MAX_COORDINATE || y > MAX_COORDINATE)
                {
                    result.ErrorMessage = $"坐标值过大 (最大允许: {MAX_COORDINATE})";
                    return result;
                }

                if (w > MAX_DIMENSION || h > MAX_DIMENSION)
                {
                    result.ErrorMessage = $"尺寸值过大 (最大允许: {MAX_DIMENSION})";
                    return result;
                }

                // 边界检查 - 增强的错误信息
                if (x + w > videoWidth)
                {
                    var maxAllowedX = Math.Max(0, videoWidth - w);
                    result.ErrorMessage = $"裁剪区域超出视频右侧边界。建议X坐标不超过 {maxAllowedX} (当前X+W={x + w}, 视频宽度={videoWidth})";
                    return result;
                }

                if (y + h > videoHeight)
                {
                    var maxAllowedY = Math.Max(0, videoHeight - h);
                    result.ErrorMessage = $"裁剪区域超出视频底部边界。建议Y坐标不超过 {maxAllowedY} (当前Y+H={y + h}, 视频高度={videoHeight})";
                    return result;
                }

                // 增强的最小尺寸验证
                const int MIN_CROP_SIZE = 8; // 降低最小尺寸以支持特殊需求
                if (w < MIN_CROP_SIZE || h < MIN_CROP_SIZE)
                {
                    result.ErrorMessage = $"裁剪区域尺寸过小 (最小{MIN_CROP_SIZE}x{MIN_CROP_SIZE}像素，当前{w}x{h})";
                    return result;
                }

                // 最大尺寸验证 - 增强检查
                if (w > videoWidth)
                {
                    result.ErrorMessage = $"裁剪宽度不能超过视频宽度 (当前: {w}, 视频宽度: {videoWidth})";
                    return result;
                }

                if (h > videoHeight)
                {
                    result.ErrorMessage = $"裁剪高度不能超过视频高度 (当前: {h}, 视频高度: {videoHeight})";
                    return result;
                }

                // 比例合理性检查
                var aspectRatio = (double)w / h;
                if (aspectRatio < 0.1 || aspectRatio > 10.0)
                {
                    result.ErrorMessage = $"裁剪区域宽高比不合理 (当前比例: {aspectRatio:F2}, 建议范围: 0.1-10.0)";
                    return result;
                }

                // 面积合理性检查（防止裁剪区域过小）
                var area = (long)w * h;
                var videoArea = (long)videoWidth * videoHeight;
                if (area < videoArea * 0.0001) // 小于视频面积的0.01%
                {
                    result.ErrorMessage = $"裁剪区域面积过小 (建议裁剪区域至少占视频面积的0.01%)";
                    return result;
                }

                // 验证通过 - 设置结果
                result.IsValid = true;
                result.X = x;
                result.Y = y;
                result.Width = w;
                result.Height = h;

                return result;
            }
            catch (OverflowException)
            {
                result.ErrorMessage = "参数数值过大或过小，请输入合理范围内的整数";
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"参数验证失败: {ex.Message}";
                return result;
            }
        }

        #endregion

        #region 裁剪预设测试方法

        /// <summary>
        /// 测试所有预设比例在不同视频尺寸下的表现（调试用）
        /// </summary>
        private void TestCropPresets()
        {
            var testVideos = new[]
            {
                (Width: 1920, Height: 1080, Name: "16:9 全高清"),
                (Width: 3840, Height: 2160, Name: "16:9 4K"),
                (Width: 1280, Height: 720, Name: "16:9 标清"),
                (Width: 1080, Height: 1920, Name: "9:16 竖屏"),
                (Width: 720, Height: 1280, Name: "9:16 小屏"),
                (Width: 2560, Height: 1080, Name: "21:9 超宽")
            };

            var presets = new[]
            {
                (Ratio: 16.0 / 9.0, Name: "16:9"),
                (Ratio: 9.0 / 16.0, Name: "9:16"),
                (Ratio: 1.0, Name: "1:1"),
                (Ratio: 4.0 / 3.0, Name: "4:3"),
                (Ratio: 3.0 / 4.0, Name: "3:4"),
                (Ratio: 21.0 / 9.0, Name: "21:9")
            };

            Services.DebugLogger.LogInfo("=== 裁剪预设测试开始 ===");

            foreach (var video in testVideos)
            {
                Services.DebugLogger.LogInfo($"测试视频: {video.Name} ({video.Width}×{video.Height})");

                foreach (var preset in presets)
                {
                    // 临时设置视频尺寸
                    var originalWidth = _videoPlayerViewModel.VideoWidth;
                    var originalHeight = _videoPlayerViewModel.VideoHeight;

                    try
                    {
                        // 模拟设置视频尺寸
                        var videoField = _videoPlayerViewModel.GetType().GetProperty("VideoWidth");
                        videoField?.SetValue(_videoPlayerViewModel, video.Width);

                        videoField = _videoPlayerViewModel.GetType().GetProperty("VideoHeight");
                        videoField?.SetValue(_videoPlayerViewModel, video.Height);

                        // 计算裁剪参数
                        var videoRatio = (double)video.Width / video.Height;
                        int cropWidth, cropHeight, cropX, cropY;

                        if (preset.Ratio > videoRatio)
                        {
                            cropWidth = video.Width;
                            cropHeight = (int)(cropWidth / preset.Ratio);
                            if (cropHeight > video.Height)
                            {
                                cropHeight = video.Height;
                                cropWidth = (int)(cropHeight * preset.Ratio);
                            }
                        }
                        else if (preset.Ratio < videoRatio)
                        {
                            cropHeight = video.Height;
                            cropWidth = (int)(cropHeight * preset.Ratio);
                            if (cropWidth > video.Width)
                            {
                                cropWidth = video.Width;
                                cropHeight = (int)(cropWidth / preset.Ratio);
                            }
                        }
                        else
                        {
                            cropWidth = video.Width;
                            cropHeight = video.Height;
                        }

                        cropX = (video.Width - cropWidth) / 2;
                        cropY = (video.Height - cropHeight) / 2;

                        double areaPercentage = ((double)cropWidth * cropHeight / (video.Width * video.Height)) * 100.0;

                        Services.DebugLogger.LogInfo($"  {preset.Name} → {cropWidth}×{cropHeight} ({areaPercentage:F1}%)");
                    }
                    finally
                    {
                        // 恢复原始尺寸
                        var videoField = _videoPlayerViewModel.GetType().GetProperty("VideoWidth");
                        videoField?.SetValue(_videoPlayerViewModel, originalWidth);

                        videoField = _videoPlayerViewModel.GetType().GetProperty("VideoHeight");
                        videoField?.SetValue(_videoPlayerViewModel, originalHeight);
                    }
                }

                Services.DebugLogger.LogInfo("");
            }

            Services.DebugLogger.LogInfo("=== 裁剪预设测试结束 ===");
        }

        #endregion

        #region ViewModel事件处理

        /// <summary>
        /// VideoListViewModel的PropertyChanged事件处理
        /// </summary>
        private void VideoListViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (_isLoadingProject)
                {
                    return;
                }

                if (e.PropertyName == nameof(_videoListViewModel.FileCount))
                {
                    // 更新文件数量显示
                    UpdateStatusBarFileCount();
                }
                else if (e.PropertyName == nameof(_videoListViewModel.SelectedFileCount))
                {
                    // 更新选中文件数量（如果需要的话）
                    UpdateStatusBarFileCount();
                }
                else if (e.PropertyName == nameof(_videoListViewModel.StatusMessage))
                {
                    // 更新状态消息
                    UpdateStatusBarMessage(_videoListViewModel.StatusMessage);
                }
                else if (e.PropertyName == nameof(_videoListViewModel.SelectedFile))
                {
                    // 当选中文件改变时，检查是否为图片文件
                    var selectedFile = _videoListViewModel.SelectedFile;
                    if (selectedFile != null)
                    {
                        var extension = Path.GetExtension(selectedFile.FilePath).ToLower();
                        if (VideoListViewModel.SupportedImageExtensions.Contains(extension))
                        {
                            // 如果是图片文件，切换到图片模式并显示图片
                            LoadImagePreview(selectedFile.FilePath);
                            if (_viewMode != 1)
                            {
                                SetViewMode(1);
                            }
                        }
                        else if (VideoListViewModel.SupportedVideoExtensions.Contains(extension))
                        {
                            // 如果是视频文件，切换到播放器模式
                            if (_viewMode != 0)
                            {
                                SetViewMode(0);
                            }
                            
                            // 自动匹配同目录下的字幕文件
                            TryAutoLoadSubtitleFile(selectedFile.FilePath);
                        }
                    }
                    else
                    {
                        // 没有选中文件，清除图片预览
                        if (ImagePreview != null)
                        {
                            ImagePreview.Source = null;
                        }
                        if (ImagePlaceholderText != null)
                        {
                            ImagePlaceholderText.Visibility = Visibility.Visible;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"VideoListViewModel_PropertyChanged 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载图片预览
        /// </summary>
        private void LoadImagePreview(string imagePath)
        {
            try
            {
                if (ImagePreview == null) return;

                if (File.Exists(imagePath))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze(); // 冻结以提高性能

                    ImagePreview.Source = bitmap;
                    
                    // 隐藏占位符文本
                    if (ImagePlaceholderText != null)
                    {
                        ImagePlaceholderText.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    ImagePreview.Source = null;
                    
                    // 显示占位符文本
                    if (ImagePlaceholderText != null)
                    {
                        ImagePlaceholderText.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"加载图片预览失败: {ex.Message}");
                if (ImagePreview != null)
                {
                    ImagePreview.Source = null;
                }
                if (ImagePlaceholderText != null)
                {
                    ImagePlaceholderText.Visibility = Visibility.Visible;
                }
            }
        }

        /// <summary>
        /// VideoPlayerViewModel的PropertyChanged事件处理
        /// </summary>
        private void VideoPlayerViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(_videoPlayerViewModel.FormattedDuration))
                {
                    // 更新总时长显示
                    UpdateStatusBarDuration();
                }
                // CurrentPosition 的更新由定时器处理，这里不需要额外处理
                // 视频尺寸变化时，更新字幕预览布局
                else if ((e.PropertyName == nameof(_videoPlayerViewModel.VideoWidth) || 
                          e.PropertyName == nameof(_videoPlayerViewModel.VideoHeight)) &&
                         _isSubtitlePreviewEnabled && 
                         SubtitlePreviewPopup != null && 
                         SubtitlePreviewPopup.IsOpen)
                {
                    var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                    UpdateSubtitlePreviewLayout(videoDisplayRect);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"VideoPlayerViewModel_PropertyChanged 错误: {ex.Message}");
            }
        }

        #endregion

        #region 裁剪框窗口激活状态控制

        /// <summary>
        /// 控制裁剪框显示状态 - Canvas版本
        /// 显示条件：复选框勾选（主要基于用户意图）
        /// </summary>

        /// <summary>
        /// 恢复Popup裁剪框（使用之前保存的状态）
        /// </summary>
        private void RestorePopupCropSelector()
        {
            try
            {
                // 获取视频播放器容器的屏幕坐标
                var videoContainer = VideoPlayerContainer;
                var topLeft = videoContainer.PointToScreen(new Point(0, 0));
                var bottomRight = videoContainer.PointToScreen(new Point(videoContainer.ActualWidth, videoContainer.ActualHeight));
                var containerBounds = new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

                // 设置Popup位置和大小 - 使用容器的逻辑尺寸1920x1080
                CropOverlayPopup.HorizontalOffset = containerBounds.X;
                CropOverlayPopup.VerticalOffset = containerBounds.Y;
                PopupCropCanvas.Width = 1920;  // 使用逻辑尺寸
                PopupCropCanvas.Height = 1080; // 使用逻辑尺寸

                // 恢复之前保存的裁剪框状态
                if (PopupCropSelector != null)
                {
                    Canvas.SetLeft(PopupCropSelector, _savedCropLeft);
                    Canvas.SetTop(PopupCropSelector, _savedCropTop);
                    PopupCropSelector.Width = _savedCropWidth;
                    PopupCropSelector.Height = _savedCropHeight;

                    // 更新遮罩和显示
                    UpdatePopupCropMask();
                    UpdatePopupCropDisplay();

                    Services.DebugLogger.LogInfo($"恢复裁剪框状态: 位置({_savedCropLeft:F1},{_savedCropTop:F1}), 大小({_savedCropWidth:F1}x{_savedCropHeight:F1})");
                }

                // 显示Popup
                CropOverlayPopup.IsOpen = true;

                Services.DebugLogger.LogInfo($"Popup裁剪框状态已恢复: 屏幕位置=({containerBounds.X:F0}, {containerBounds.Y:F0}), 容器渲染大小=({containerBounds.Width:F0}x{containerBounds.Height:F0})");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"RestorePopupCropSelector 错误: {ex.Message}");
                // 如果恢复失败，回退到默认初始化
                _hasSavedCropState = false;
                ShowPopupCropSelector();
            }
        }

        /// <summary>
        /// 显示Popup裁剪框
        /// </summary>
        private void ShowPopupCropSelector()
        {
            try
            {
                // 获取视频播放器容器的屏幕坐标
                var videoContainer = VideoPlayerContainer;
                var topLeft = videoContainer.PointToScreen(new Point(0, 0));
                var bottomRight = videoContainer.PointToScreen(new Point(videoContainer.ActualWidth, videoContainer.ActualHeight));
                var containerBounds = new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

                // 设置Popup位置和大小 - 使用容器的逻辑尺寸1920x1080
                CropOverlayPopup.HorizontalOffset = containerBounds.X;
                CropOverlayPopup.VerticalOffset = containerBounds.Y;
                PopupCropCanvas.Width = 1920;  // 使用逻辑尺寸
                PopupCropCanvas.Height = 1080; // 使用逻辑尺寸

                // 初始化裁剪框到中心位置
                InitializePopupCropSelector();

                // 显示Popup
                CropOverlayPopup.IsOpen = true;

                Services.DebugLogger.LogInfo($"Popup裁剪框已显示: 屏幕位置=({containerBounds.X:F0}, {containerBounds.Y:F0}), 容器渲染大小=({containerBounds.Width:F0}x{containerBounds.Height:F0}), Canvas逻辑大小=(1920x1080)");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ShowPopupCropSelector 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新Popup裁剪框遮罩
        /// </summary>
        private void UpdatePopupCropMask()
        {
            try
            {
                if (PopupCropSelector != null && PopupCropMask != null && PopupCropCanvas != null)
                {
                    double selectorLeft = Canvas.GetLeft(PopupCropSelector);
                    double selectorTop = Canvas.GetTop(PopupCropSelector);
                    double selectorWidth = PopupCropSelector.Width;
                    double selectorHeight = PopupCropSelector.Height;

                    // 创建镂空路径：整个Canvas减去裁剪框区域
                    var geometry = new CombinedGeometry(
                        GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, PopupCropCanvas.Width, PopupCropCanvas.Height)),
                        new RectangleGeometry(new Rect(selectorLeft, selectorTop, selectorWidth, selectorHeight))
                    );

                    PopupCropMask.Data = geometry;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdatePopupCropMask 出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新Popup裁剪框显示信息
        /// </summary>
        private void UpdatePopupCropDisplay()
        {
            try
            {
                if (PopupCropSelector != null && PopupCropSizeText != null)
                {
                    double logicWidth = PopupCropSelector.Width;
                    double logicHeight = PopupCropSelector.Height;
                    double ratio = logicWidth / logicHeight;

                    // 获取视频显示区域（考虑黑边）
                    var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                    // 获取当前视频的实际分辨率
                    int videoWidth = _videoPlayerViewModel.VideoWidth;
                    int videoHeight = _videoPlayerViewModel.VideoHeight;

                    // 如果没有视频信息，使用默认值
                    if (videoWidth <= 0 || videoHeight <= 0)
                    {
                        videoWidth = 1920;
                        videoHeight = 1080;
                    }

                    // 将逻辑坐标转换为实际视频坐标（基于视频显示区域的比例）
                    double widthRatio = logicWidth / videoDisplayRect.Width;
                    double heightRatio = logicHeight / videoDisplayRect.Height;
                    double actualWidth = widthRatio * videoWidth;
                    double actualHeight = heightRatio * videoHeight;

                    // 更新尺寸显示（显示实际视频坐标）
                    PopupCropSizeText.Text = $"{actualWidth:F0} × {actualHeight:F0} ({ratio:F2}:1)";

                    // 更新参数框（如果不是代码更新导致的）
                    if (!_isUpdatingCropFromCode)
                    {
                        _isUpdatingCropFromCode = true;
                        try
                        {
                            // 计算相对于Canvas的逻辑坐标
                            double logicLeft = Canvas.GetLeft(PopupCropSelector);
                            double logicTop = Canvas.GetTop(PopupCropSelector);

                            // 计算裁剪框相对于视频显示区域的逻辑坐标
                            double relativeLogicLeft = logicLeft - videoDisplayRect.X;
                            double relativeLogicTop = logicTop - videoDisplayRect.Y;

                            // 转换为实际视频坐标（相对于视频内容）
                            double actualLeft = (relativeLogicLeft / videoDisplayRect.Width) * videoWidth;
                            double actualTop = (relativeLogicTop / videoDisplayRect.Height) * videoHeight;

                            // 确保坐标不为负数（处理边界情况）
                            actualLeft = Math.Max(0, actualLeft);
                            actualTop = Math.Max(0, actualTop);

                            // 更新参数框（显示实际视频坐标）
                            CropXTextBox.Text = ((int)actualLeft).ToString();
                            CropYTextBox.Text = ((int)actualTop).ToString();
                            CropWTextBox.Text = ((int)actualWidth).ToString();
                            CropHTextBox.Text = ((int)actualHeight).ToString();
                        }
                        finally
                        {
                            _isUpdatingCropFromCode = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdatePopupCropDisplay 出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新裁剪比例锁定指示器
        /// </summary>
        private void UpdateCropAspectLockIndicator()
        {
            try
            {
                bool isLocked = LockAspectRatioCheckBox?.IsChecked == true;
                // Canvas版本暂时不显示比例锁定指示器，保持简洁
                // 如需要可以添加相应的UI元素
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateCropAspectLockIndicator 出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化Popup裁剪框到视频区域中心
        /// </summary>
        private void InitializePopupCropSelector()
        {
            try
            {
                // 获取视频在逻辑坐标系(1920x1080)中的显示区域
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                // 计算视频显示区域的中心点（在逻辑坐标系中）
                double videoCenterX = videoDisplayRect.X + videoDisplayRect.Width / 2;
                double videoCenterY = videoDisplayRect.Y + videoDisplayRect.Height / 2;

                // 计算默认裁剪框尺寸（视频显示区域的75%）
                double defaultWidth = Math.Min(videoDisplayRect.Width * 0.75, videoDisplayRect.Width - 40); // 留40px边距
                double defaultHeight = Math.Min(videoDisplayRect.Height * 0.75, videoDisplayRect.Height - 40);

                // 确保裁剪框不超出视频显示区域
                defaultWidth = Math.Max(100, Math.Min(defaultWidth, videoDisplayRect.Width - 20));
                defaultHeight = Math.Max(100, Math.Min(defaultHeight, videoDisplayRect.Height - 20));

                // 计算裁剪框位置，使其在视频显示区域内居中
                double cropLeft = videoCenterX - defaultWidth / 2;
                double cropTop = videoCenterY - defaultHeight / 2;

                // 确保裁剪框完全在视频显示区域内
                cropLeft = Math.Max(videoDisplayRect.X + 10, Math.Min(cropLeft, videoDisplayRect.X + videoDisplayRect.Width - defaultWidth - 10));
                cropTop = Math.Max(videoDisplayRect.Y + 10, Math.Min(cropTop, videoDisplayRect.Y + videoDisplayRect.Height - defaultHeight - 10));

                // 设置裁剪框位置和大小（在逻辑坐标系中）
                Canvas.SetLeft(PopupCropSelector, cropLeft);
                Canvas.SetTop(PopupCropSelector, cropTop);
                PopupCropSelector.Width = defaultWidth;
                PopupCropSelector.Height = defaultHeight;

                // 更新遮罩和显示
                UpdatePopupCropMask();
                UpdatePopupCropDisplay();

                Services.DebugLogger.LogInfo($"Popup裁剪框已初始化到视频中心: 视频显示区域=({videoDisplayRect.X:F0},{videoDisplayRect.Y:F0},{videoDisplayRect.Width:F0}x{videoDisplayRect.Height:F0}), 裁剪框中心=({videoCenterX:F0},{videoCenterY:F0}), 裁剪框=({cropLeft:F0},{cropTop:F0},{defaultWidth:F0}x{defaultHeight:F0})");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"InitializePopupCropSelector 错误: {ex.Message}");

                // 出错时使用简单居中方式作为fallback
                try
                {
                    double centerX = PopupCropCanvas.Width / 2;
                    double centerY = PopupCropCanvas.Height / 2;
                    double defaultWidth = Math.Min(800, PopupCropCanvas.Width * 0.6);
                    double defaultHeight = Math.Min(450, PopupCropCanvas.Height * 0.6);

                    Canvas.SetLeft(PopupCropSelector, centerX - defaultWidth / 2);
                    Canvas.SetTop(PopupCropSelector, centerY - defaultHeight / 2);
                    PopupCropSelector.Width = defaultWidth;
                    PopupCropSelector.Height = defaultHeight;

                    UpdatePopupCropMask();
                    UpdatePopupCropDisplay();
                }
                catch
                {
                    // 如果fallback也失败，忽略错误
                }
            }
        }

        /// <summary>
        /// 主窗口激活事件处理 - Popup版本
        /// 重新显示Popup裁剪框
        /// </summary>
        private void MainWindow_Activated(object sender, EventArgs e)
        {
            Services.DebugLogger.LogInfo("主窗口激活 - Popup版本");

            // 更新按钮状态以反映当前裁剪框状态
            UpdateCropSelectorButtonState();

            // 当主窗口重新获得焦点时，如果裁剪功能已启用，则显示Popup
            if (!CropOverlayPopup.IsOpen)
            {
                if (_hasSavedCropState)
                {
                    // 恢复之前保存的状态
                    Services.DebugLogger.LogInfo("主窗口重新获得焦点，恢复Popup裁剪框状态");
                    RestorePopupCropSelector();
                    UpdateCropSelectorButtonState();
                }
                else
                {
                    // 没有保存的状态，不自动显示
                    Services.DebugLogger.LogInfo("主窗口重新获得焦点，裁剪框保持隐藏状态");
                }
            }
            
            // 恢复字幕预览Popup（如果之前是显示状态）
            if (_hasSavedSubtitleState && chkShowSubtitlePreview != null && chkShowSubtitlePreview.IsChecked == true)
            {
                Services.DebugLogger.LogInfo("主窗口重新获得焦点，恢复字幕预览Popup");
                ShowSubtitlePreviewPopup();
            }
        }

        /// <summary>
        /// 主窗口失活事件处理 - Popup版本
        /// Popup是Topmost的，会阻挡其他应用，需要隐藏
        /// </summary>
        private void MainWindow_Deactivated(object sender, EventArgs e)
        {
            Services.DebugLogger.LogInfo("主窗口失活 - Popup版本隐藏裁剪框");

            // 当主窗口失去焦点时，隐藏Popup以避免阻挡其他应用程序
            if (CropOverlayPopup.IsOpen)
            {
                // 在隐藏前保存当前的裁剪框状态
                if (PopupCropSelector != null)
                {
                    _savedCropLeft = Canvas.GetLeft(PopupCropSelector);
                    _savedCropTop = Canvas.GetTop(PopupCropSelector);
                    _savedCropWidth = PopupCropSelector.Width;
                    _savedCropHeight = PopupCropSelector.Height;
                    _hasSavedCropState = true;
                    Services.DebugLogger.LogInfo($"保存裁剪框状态: 位置({_savedCropLeft:F1},{_savedCropTop:F1}), 大小({_savedCropWidth:F1}x{_savedCropHeight:F1})");
                }

                Services.DebugLogger.LogInfo("隐藏Popup裁剪框，避免阻挡其他应用");
                CropOverlayPopup.IsOpen = false;

                // 更新按钮状态
                UpdateCropSelectorButtonState();
            }
            
            // 隐藏字幕预览Popup（如果正在显示）
            if (SubtitlePreviewPopup != null && SubtitlePreviewPopup.IsOpen)
            {
                _hasSavedSubtitleState = true;
                Services.DebugLogger.LogInfo("隐藏字幕预览Popup，避免阻挡其他应用");
                SubtitlePreviewPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// 主窗口位置变化事件处理 - Popup版本
        /// 当主窗口移动时，需要更新Popup的位置
        /// </summary>
        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            // 只有在Popup打开时才需要更新位置
            if (CropOverlayPopup.IsOpen)
            {
                try
                {
                    // 获取视频播放器容器的屏幕坐标
                    var videoContainer = VideoPlayerContainer;
                    if (videoContainer != null && videoContainer.IsVisible)
                    {
                        var topLeft = videoContainer.PointToScreen(new Point(0, 0));

                        // 更新Popup位置
                        CropOverlayPopup.HorizontalOffset = topLeft.X;
                        CropOverlayPopup.VerticalOffset = topLeft.Y;

                        Services.DebugLogger.LogInfo($"Popup位置已更新: ({topLeft.X:F0}, {topLeft.Y:F0})");
                    }
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"更新Popup位置失败: {ex.Message}");
                }
            }
            
            // 同时更新字幕预览Popup位置
            UpdateSubtitlePreviewPopupPosition();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded || _isFullScreen || _isRestoringPlayerModeSize || _interfaceMode != InterfaceDisplayMode.PlayerOnly)
            {
                Services.DebugLogger.LogInfo($"[PlayerModeSize] SizeChanged ignored: isLoaded={IsLoaded}, isFullScreen={_isFullScreen}, isRestoring={_isRestoringPlayerModeSize}, mode={_interfaceMode}");
                return;
            }

            if (_isApplyingPlayerModeWindowSize || WindowState != WindowState.Normal)
            {
                Services.DebugLogger.LogInfo($"[PlayerModeSize] SizeChanged ignored: applying={_isApplyingPlayerModeWindowSize}, windowState={WindowState}");
                return;
            }

            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            _playerModeLastWindowWidth = Math.Max(Width, PlayerModeWindowWidth);
            _playerModeLastWindowHeight = Math.Max(Height, PlayerModeWindowHeight);
            Services.DebugLogger.LogInfo($"[PlayerModeSize] SizeChanged stored new values: width={Width:F0}, height={Height:F0} -> stored=({_playerModeLastWindowWidth:F0}, {_playerModeLastWindowHeight:F0})");
        }

        #region Canvas裁剪框事件处理

        /// <summary>
        /// 裁剪框确认菜单项点击 - Canvas版本
        /// </summary>
        private void CropApplyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("用户确认Canvas裁剪区域");

                // 获取当前裁剪框的矩形
                var cropRect = new Rect(
                    Canvas.GetLeft(PopupCropSelector),
                    Canvas.GetTop(PopupCropSelector),
                    PopupCropSelector.Width,
                    PopupCropSelector.Height
                );

                // 应用裁剪参数到界面

                // 隐藏裁剪框
                CropOverlayPopup.IsOpen = false;

                // 更新按钮状态
                UpdateCropSelectorButtonState();

                Services.ToastNotification.ShowSuccess("裁剪区域已确认");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"应用Canvas裁剪出错: {ex.Message}");
                Services.ToastNotification.ShowError($"应用裁剪失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 裁剪框重置菜单项点击
        /// </summary>
        private void CropResetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("用户重置Canvas裁剪框到中心");
                InitializePopupCropSelector();
                Services.ToastNotification.ShowInfo("裁剪框已重置到中心位置");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"重置Canvas裁剪框出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 裁剪框取消菜单项点击 - Canvas版本
        /// </summary>
        private void CropCancelMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("用户取消Canvas裁剪");

                // 隐藏裁剪框
                CropOverlayPopup.IsOpen = false;

                // 更新按钮状态
                UpdateCropSelectorButtonState();

                Services.ToastNotification.ShowInfo("裁剪已取消");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"取消Canvas裁剪出错: {ex.Message}");
            }
        }

        #endregion


        /// <summary>
        /// Popup裁剪框鼠标按下 - 开始拖拽
        /// </summary>
        private void CropSelector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isCropSelectorDragging = true;
            _cropDragStartPoint = e.GetPosition(PopupCropCanvas);
            _cropDragStartLeft = Canvas.GetLeft(PopupCropSelector);
            _cropDragStartTop = Canvas.GetTop(PopupCropSelector);
            PopupCropSelector.CaptureMouse();
            e.Handled = true;
        }


        /// <summary>
        /// Popup裁剪框鼠标释放 - 结束拖拽
        /// </summary>
        private void CropSelector_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isCropSelectorDragging)
            {
                _isCropSelectorDragging = false;
                PopupCropSelector.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Canvas裁剪框鼠标进入 - 显示操作提示
        /// </summary>
        private void CropSelector_MouseEnter(object sender, MouseEventArgs e)
        {
            // Canvas版本暂时不显示操作提示，保持简洁
            // 如需要可以添加相应的UI元素
        }

        /// <summary>
        /// Canvas裁剪框鼠标离开 - 隐藏操作提示
        /// </summary>
        private void CropSelector_MouseLeave(object sender, MouseEventArgs e)
        {
            // Canvas版本暂时不显示操作提示，保持简洁
        }

        /// <summary>
        /// Popup裁剪框控制点鼠标按下
        /// </summary>
        private void CropHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement handle)
            {
                _isCropHandleDragging = true;
                _cropHandleTag = handle.Tag as string;
                _cropDragStartPoint = e.GetPosition(PopupCropCanvas);
                _cropDragStartWidth = PopupCropSelector.Width;
                _cropDragStartHeight = PopupCropSelector.Height;
                _cropDragStartLeft = Canvas.GetLeft(PopupCropSelector);
                _cropDragStartTop = Canvas.GetTop(PopupCropSelector);
                handle.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Popup裁剪框鼠标移动 - 处理整体拖拽和控制点拖拽
        /// </summary>
        private void CropSelector_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isCropSelectorDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(PopupCropCanvas);
                double offsetX = currentPoint.X - _cropDragStartPoint.X;
                double offsetY = currentPoint.Y - _cropDragStartPoint.Y;

                double newLeft = _cropDragStartLeft + offsetX;
                double newTop = _cropDragStartTop + offsetY;

                // 获取视频显示区域边界
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                // 限制在视频显示区域范围内
                double maxLeft = videoDisplayRect.X + videoDisplayRect.Width - PopupCropSelector.Width;
                double maxTop = videoDisplayRect.Y + videoDisplayRect.Height - PopupCropSelector.Height;

                newLeft = Math.Max(videoDisplayRect.X, Math.Min(newLeft, maxLeft));
                newTop = Math.Max(videoDisplayRect.Y, Math.Min(newTop, maxTop));

                Canvas.SetLeft(PopupCropSelector, newLeft);
                Canvas.SetTop(PopupCropSelector, newTop);

                // 标记状态已修改
                _hasSavedCropState = false;

                UpdatePopupCropDisplay();
                UpdatePopupCropMask();
                e.Handled = true;
            }
            else if (_isCropHandleDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(PopupCropCanvas);
                double deltaX = currentPoint.X - _cropDragStartPoint.X;
                double deltaY = currentPoint.Y - _cropDragStartPoint.Y;

                double newLeft = _cropDragStartLeft;
                double newTop = _cropDragStartTop;
                double newWidth = _cropDragStartWidth;
                double newHeight = _cropDragStartHeight;

                // 获取视频显示区域边界
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();

                // 根据拖拽点位置调整选取框，限制在视频显示区域内
                switch (_cropHandleTag)
                {
                    case "TopLeft":
                        newLeft = Math.Max(videoDisplayRect.X, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newTop = Math.Max(videoDisplayRect.Y, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "Top":
                        newTop = Math.Max(videoDisplayRect.Y, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "TopRight":
                        newTop = Math.Max(videoDisplayRect.Y, Math.Min(_cropDragStartTop + deltaY, _cropDragStartTop + _cropDragStartHeight - 50));
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, videoDisplayRect.X + videoDisplayRect.Width - _cropDragStartLeft));
                        newHeight = _cropDragStartHeight - (newTop - _cropDragStartTop);
                        break;
                    case "Left":
                        newLeft = Math.Max(videoDisplayRect.X, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        break;
                    case "Right":
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, videoDisplayRect.X + videoDisplayRect.Width - _cropDragStartLeft));
                        break;
                    case "BottomLeft":
                        newLeft = Math.Max(videoDisplayRect.X, Math.Min(_cropDragStartLeft + deltaX, _cropDragStartLeft + _cropDragStartWidth - 50));
                        newWidth = _cropDragStartWidth - (newLeft - _cropDragStartLeft);
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, videoDisplayRect.Y + videoDisplayRect.Height - _cropDragStartTop));
                        break;
                    case "Bottom":
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, videoDisplayRect.Y + videoDisplayRect.Height - _cropDragStartTop));
                        break;
                    case "BottomRight":
                        newWidth = Math.Max(50, Math.Min(_cropDragStartWidth + deltaX, videoDisplayRect.X + videoDisplayRect.Width - _cropDragStartLeft));
                        newHeight = Math.Max(50, Math.Min(_cropDragStartHeight + deltaY, videoDisplayRect.Y + videoDisplayRect.Height - _cropDragStartTop));
                        break;
                }

                // 如果锁定比例
                if (LockAspectRatioCheckBox.IsChecked == true)
                {
                    double aspectRatio = _cropDragStartWidth / _cropDragStartHeight;

                    // 根据拖拽方向决定以宽度还是高度为准
                    if (_cropHandleTag?.Contains("Left") == true || _cropHandleTag?.Contains("Right") == true)
                    {
                        newHeight = newWidth / aspectRatio;
                    }
                    else if (_cropHandleTag == "Top" || _cropHandleTag == "Bottom")
                    {
                        newWidth = newHeight * aspectRatio;
                    }
                    else // 角落拖拽点
                    {
                        // 以变化较大的维度为准
                        if (Math.Abs(deltaX) > Math.Abs(deltaY))
                        {
                            newHeight = newWidth / aspectRatio;
                        }
                        else
                        {
                            newWidth = newHeight * aspectRatio;
                        }
                    }

                    // 比例锁定后，确保裁剪框仍在视频显示区域内
                    if (newLeft + newWidth > videoDisplayRect.X + videoDisplayRect.Width)
                    {
                        newWidth = videoDisplayRect.X + videoDisplayRect.Width - newLeft;
                        newHeight = newWidth / aspectRatio;
                    }
                    if (newTop + newHeight > videoDisplayRect.Y + videoDisplayRect.Height)
                    {
                        newHeight = videoDisplayRect.Y + videoDisplayRect.Height - newTop;
                        newWidth = newHeight * aspectRatio;
                    }
                    // 确保最小尺寸
                    newWidth = Math.Max(50, newWidth);
                    newHeight = Math.Max(50, newHeight);
                }

                Canvas.SetLeft(PopupCropSelector, newLeft);
                Canvas.SetTop(PopupCropSelector, newTop);
                PopupCropSelector.Width = newWidth;
                PopupCropSelector.Height = newHeight;

                // 标记状态已修改
                _hasSavedCropState = false;

                UpdatePopupCropDisplay();
                UpdatePopupCropMask();
                e.Handled = true;
            }
        }


        #endregion


        #region 状态栏更新方法

        /// <summary>
        /// 更新状态栏文件数量显示
        /// </summary>
        private void UpdateStatusBarFileCount()
        {
            try
            {
                if (StatusFileCount != null)
                {
                    var fileCount = _videoListViewModel.FileCount;
                    var selectedCount = _videoListViewModel.SelectedFileCount;
                    StatusFileCount.Text = $"文件: {fileCount}";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateStatusBarFileCount 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新状态栏消息
        /// </summary>
        private void UpdateStatusBarMessage(string message)
        {
            try
            {
                if (StatusBarText != null)
                {
                    StatusBarText.Text = message;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateStatusBarMessage 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新状态栏时长显示
        /// </summary>
        private void UpdateStatusBarDuration()
        {
            try
            {
                if (StatusDuration != null)
                {
                    StatusDuration.Text = $"时长: {_videoPlayerViewModel.FormattedDuration}";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateStatusBarDuration 错误: {ex.Message}");
            }
        }

        #endregion

        #region 系统监控功能

        /// <summary>
        /// 初始化系统监控
        /// </summary>
        private void InitializeSystemMonitoring()
        {
            try
            {
                // 启动定时器更新系统信息
                var systemMonitorTimer = new System.Timers.Timer(2000); // 每2秒更新一次
                systemMonitorTimer.Elapsed += SystemMonitorTimer_Elapsed;
                systemMonitorTimer.Start();

                // 初始更新一次
                UpdateSystemStatus();

                // 延迟检查FFmpeg版本（不阻塞启动，后台执行）
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // 延迟2秒，不阻塞启动
                    try
                    {
                        CheckFFmpegVersion();
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogError($"延迟检查FFmpeg版本失败: {ex.Message}");
                    }
                });

                Services.DebugLogger.LogInfo("系统监控已初始化（FFmpeg版本检查已延迟）");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"InitializeSystemMonitoring 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 系统监控定时器事件
        /// </summary>
        private void SystemMonitorTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // 在UI线程中更新
                Dispatcher.Invoke(() =>
                {
                    UpdateSystemStatus();
                });
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"SystemMonitorTimer_Elapsed 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新系统状态信息
        /// </summary>
        private void UpdateSystemStatus()
        {
            try
            {
                // 更新CPU使用率
                UpdateCPUUsage();

                // 更新内存使用情况
                UpdateMemoryUsage();

                // 更新磁盘空间
                UpdateDiskSpace();

                // 更新FFmpeg状态
                UpdateFFmpegStatus();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateSystemStatus 错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新CPU使用率
        /// </summary>
        private void UpdateCPUUsage()
        {
            try
            {
                if (StatusCPU != null)
                {
                    var cpuUsage = GetCurrentCpuUsage();
                    StatusCPU.Text = $"CPU: {cpuUsage:F1}%";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateCPUUsage 错误: {ex.Message}");
                if (StatusCPU != null) StatusCPU.Text = "CPU: 未知";
            }
        }

        /// <summary>
        /// 更新内存使用情况
        /// </summary>
        private void UpdateMemoryUsage()
        {
            try
            {
                if (StatusMemory != null)
                {
                    var memoryUsage = GetCurrentMemoryUsage();
                    StatusMemory.Text = $"内存: {memoryUsage} MB";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateMemoryUsage 错误: {ex.Message}");
                if (StatusMemory != null) StatusMemory.Text = "内存: 未知";
            }
        }

        /// <summary>
        /// 更新磁盘空间
        /// </summary>
        private void UpdateDiskSpace()
        {
            try
            {
                if (StatusDiskSpace != null)
                {
                    var availableSpace = GetAvailableDiskSpace();
                    StatusDiskSpace.Text = $"可用: {availableSpace}";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateDiskSpace 错误: {ex.Message}");
                if (StatusDiskSpace != null) StatusDiskSpace.Text = "可用: 未知";
            }
        }

        /// <summary>
        /// 更新FFmpeg状态
        /// </summary>
        private void UpdateFFmpegStatus()
        {
            try
            {
                if (StatusFFmpegVersion != null)
                {
                    // 这里可以添加FFmpeg运行状态检测
                    // 目前只是简单显示版本信息
                    var version = GetFFmpegVersion();
                    StatusFFmpegVersion.Text = $"FFmpeg: {version}";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateFFmpegStatus 错误: {ex.Message}");
                if (StatusFFmpegVersion != null) StatusFFmpegVersion.Text = "FFmpeg: 未知";
            }
        }

        /// <summary>
        /// 获取当前CPU使用率
        /// </summary>
        private float GetCurrentCpuUsage()
        {
            try
            {
                using (var cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total"))
                {
                    cpuCounter.NextValue(); // 第一次调用返回0
                    System.Threading.Thread.Sleep(100); // 短暂等待
                    return cpuCounter.NextValue();
                }
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// 获取当前内存使用情况（MB）
        /// </summary>
        private long GetCurrentMemoryUsage()
        {
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.WorkingSet64 / 1024 / 1024; // 转换为MB
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 获取可用磁盘空间
        /// </summary>
        private string GetAvailableDiskSpace()
        {
            try
            {
                var driveInfo = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.CurrentDirectory));
                var availableSpace = driveInfo.AvailableFreeSpace / 1024 / 1024 / 1024; // 转换为GB
                return $"{availableSpace} GB";
            }
            catch
            {
                return "未知";
            }
        }

        /// <summary>
        /// 检查FFmpeg版本
        /// </summary>
        private void CheckFFmpegVersion()
        {
            try
            {
                var ffprobePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe");

                if (System.IO.File.Exists(ffprobePath))
                {
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ffprobePath,
                            Arguments = "-version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (output.Contains("ffprobe version"))
                    {
                        var versionLine = output.Split('\n').FirstOrDefault(line => line.Contains("ffprobe version"));
                        if (versionLine != null)
                        {
                            var version = versionLine.Split(' ').Skip(2).FirstOrDefault();
                            if (StatusFFmpegVersion != null)
                                StatusFFmpegVersion.Text = $"FFmpeg: {version}";
                        }
                    }
                }
                else
                {
                    if (StatusFFmpegVersion != null)
                        StatusFFmpegVersion.Text = "FFmpeg: 未检测到";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"CheckFFmpegVersion 错误: {ex.Message}");
                if (StatusFFmpegVersion != null)
                    StatusFFmpegVersion.Text = "FFmpeg: 未知";
            }
        }

        /// <summary>
        /// 获取FFmpeg版本（简化版）
        /// </summary>
        private string GetFFmpegVersion()
        {
            // 这里可以实现更复杂的版本检测逻辑
            // 目前返回静态版本
            return "已检测";
        }

        #endregion

        #region 嵌入式命令提示符功能

        private Process? _embeddedCurrentProcess;
        private CancellationTokenSource? _embeddedCancellationTokenSource;
        private readonly List<string> _embeddedCommandHistory = new List<string>();
        private int _embeddedHistoryIndex = -1;
        private string _embeddedCurrentInput = "";

        /// <summary>
        /// 嵌入式清空按钮点击事件
        /// </summary>
        private void EmbeddedClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (EmbeddedOutputTextBox != null)
            {
                EmbeddedOutputTextBox.Clear();
                EmbeddedOutputTextBox.AppendText("FFmpeg 命令提示符已就绪...\n");
                EmbeddedOutputTextBox.AppendText("输入 FFmpeg 命令并按 Enter 执行\n");
                EmbeddedOutputTextBox.AppendText("输入 'help' 查看可用命令\n");
                EmbeddedOutputTextBox.AppendText("输入 'clear' 清空输出\n");
                EmbeddedOutputTextBox.AppendText("输入 'exit' 返回播放器\n");
                EmbeddedOutputTextBox.AppendText("\n> ");
            }
        }

        /// <summary>
        /// 嵌入式停止按钮点击事件
        /// </summary>
        private void EmbeddedStopButton_Click(object sender, RoutedEventArgs e)
        {
            EmbeddedStopCurrentProcess();
        }

        /// <summary>
        /// 嵌入式浏览FFmpeg按钮点击事件
        /// </summary>
        private void EmbeddedBrowseFFmpegButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "选择FFmpeg可执行文件",
                    Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    FileName = "ffmpeg.exe"
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (EmbeddedFFmpegPathTextBox != null)
                    {
                        EmbeddedFFmpegPathTextBox.Text = dialog.FileName;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"浏览FFmpeg文件失败: {ex.Message}");
                MessageBox.Show($"浏览FFmpeg文件失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 嵌入式命令文本框按键事件
        /// </summary>
        private void EmbeddedCommandTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    var command = EmbeddedCommandTextBox?.Text.Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        EmbeddedExecuteCommand(command);
                        if (EmbeddedCommandTextBox != null)
                        {
                            EmbeddedCommandTextBox.Text = "";
                        }
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (_embeddedCommandHistory.Count > 0)
                    {
                        if (_embeddedHistoryIndex == -1)
                        {
                            _embeddedCurrentInput = EmbeddedCommandTextBox?.Text ?? "";
                            _embeddedHistoryIndex = _embeddedCommandHistory.Count - 1;
                        }
                        else if (_embeddedHistoryIndex > 0)
                        {
                            _embeddedHistoryIndex--;
                        }

                        if (_embeddedHistoryIndex >= 0 && _embeddedHistoryIndex < _embeddedCommandHistory.Count)
                        {
                            if (EmbeddedCommandTextBox != null)
                            {
                                EmbeddedCommandTextBox.Text = _embeddedCommandHistory[_embeddedHistoryIndex];
                                EmbeddedCommandTextBox.Select(EmbeddedCommandTextBox.Text.Length, 0);
                            }
                        }
                    }
                    e.Handled = true;
                    break;

                case Key.Down:
                    if (_embeddedHistoryIndex >= 0)
                    {
                        _embeddedHistoryIndex++;
                        if (_embeddedHistoryIndex >= _embeddedCommandHistory.Count)
                        {
                            if (EmbeddedCommandTextBox != null)
                            {
                                EmbeddedCommandTextBox.Text = _embeddedCurrentInput;
                            }
                            _embeddedHistoryIndex = -1;
                        }
                        else
                        {
                            if (EmbeddedCommandTextBox != null)
                            {
                                EmbeddedCommandTextBox.Text = _embeddedCommandHistory[_embeddedHistoryIndex];
                            }
                        }
                        if (EmbeddedCommandTextBox != null)
                        {
                            EmbeddedCommandTextBox.Select(EmbeddedCommandTextBox.Text.Length, 0);
                        }
                    }
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// 嵌入式命令文本框文本改变事件
        /// </summary>
        private void EmbeddedCommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _embeddedHistoryIndex = -1;
        }

        /// <summary>
        /// 嵌入式执行按钮点击事件
        /// </summary>
        private void EmbeddedExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            var command = EmbeddedCommandTextBox?.Text.Trim();
            if (!string.IsNullOrEmpty(command))
            {
                EmbeddedExecuteCommand(command);
                if (EmbeddedCommandTextBox != null)
                {
                    EmbeddedCommandTextBox.Text = "";
                }
            }
        }

        /// <summary>
        /// 执行嵌入式命令
        /// </summary>
        private Brush ResolveBrush(string resourceKey, Brush fallback)
        {
            if (TryFindResource(resourceKey) is Brush brush)
            {
                return brush;
            }

            if (Application.Current?.TryFindResource(resourceKey) is Brush appBrush)
            {
                return appBrush;
            }

            return fallback;
        }

        private async void EmbeddedExecuteCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            // 添加到历史记录
            if (!_embeddedCommandHistory.Contains(command))
            {
                _embeddedCommandHistory.Add(command);
            }
            _embeddedHistoryIndex = -1;

            // 显示执行的命令
            EmbeddedAppendOutput($"> {command}", ResolveBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));

            // 处理内置命令
            if (EmbeddedHandleBuiltInCommand(command))
            {
                return;
            }

            // 执行FFmpeg命令
            await EmbeddedExecuteFFmpegCommand(command);
        }

        /// <summary>
        /// 处理嵌入式内置命令
        /// </summary>
        private bool EmbeddedHandleBuiltInCommand(string command)
        {
            var cmd = command.Trim().ToLower();

            switch (cmd)
            {
                case "clear":
                    Dispatcher.Invoke(() =>
                    {
                        if (EmbeddedOutputTextBox != null)
                        {
                            EmbeddedOutputTextBox.Clear();
                            EmbeddedOutputTextBox.AppendText("FFmpeg 命令提示符已就绪...\n");
                            EmbeddedOutputTextBox.AppendText("输入 FFmpeg 命令并按 Enter 执行\n");
                            EmbeddedOutputTextBox.AppendText("输入 'help' 查看可用命令\n");
                            EmbeddedOutputTextBox.AppendText("输入 'clear' 清空输出\n");
                            EmbeddedOutputTextBox.AppendText("输入 'exit' 返回播放器\n");
                            EmbeddedOutputTextBox.AppendText("\n> ");
                        }
                    });
                    return true;

                case "help":
                    EmbeddedShowHelp();
                    return true;
            }

            if (cmd.StartsWith("help "))
            {
                var keyword = cmd.Substring(5).Trim();
                EmbeddedShowHelpSearch(keyword);
                return true;
            }

            if (cmd.StartsWith("list "))
            {
                var categoryName = cmd.Substring(5).Trim();
                EmbeddedShowCategoryCommands(categoryName);
                return true;
            }

            switch (cmd)
            {

                case "history":
                    EmbeddedAppendOutput("命令历史:", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
                    for (int i = 0; i < _embeddedCommandHistory.Count; i++)
                    {
                        EmbeddedAppendOutput($"  {i + 1}: {_embeddedCommandHistory[i]}");
                    }
                    EmbeddedAppendOutput("");
                    return true;

                case "exit":
                    // 返回播放器模式
                    Dispatcher.Invoke(() =>
                    {
                        SetViewMode(0);
                    });
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 执行嵌入式FFmpeg命令
        /// </summary>
        private async Task EmbeddedExecuteFFmpegCommand(string arguments)
        {
            if (_embeddedCurrentProcess != null && !_embeddedCurrentProcess.HasExited)
            {
                EmbeddedAppendOutput("错误: 已有命令正在执行，请等待完成或点击停止按钮", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            var ffmpegPath = EmbeddedFFmpegPathTextBox?.Text.Trim() ?? "ffmpeg.exe";
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                EmbeddedAppendOutput("错误: 未设置FFmpeg路径", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            if (!File.Exists(ffmpegPath) && ffmpegPath != "ffmpeg.exe")
            {
                EmbeddedAppendOutput($"错误: FFmpeg可执行文件不存在: {ffmpegPath}", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            // 解析命令参数：如果用户输入了完整的"ffmpeg ..."命令，提取参数部分
            var trimmedArgs = arguments.Trim();
            if (trimmedArgs.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                var ffmpegPrefix = "ffmpeg";
                if (trimmedArgs.Length > ffmpegPrefix.Length &&
                    (trimmedArgs[ffmpegPrefix.Length] == ' ' || trimmedArgs[ffmpegPrefix.Length] == '\t'))
                {
                    arguments = trimmedArgs.Substring(ffmpegPrefix.Length).Trim();
                    EmbeddedAppendOutput($"提取的参数: {arguments}", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                else if (trimmedArgs.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    arguments = "";
                    EmbeddedAppendOutput("执行FFmpeg (无参数)", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
            }

            try
            {
                _embeddedCancellationTokenSource = new CancellationTokenSource();

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Directory.GetCurrentDirectory()
                    },
                    EnableRaisingEvents = true
                };

                _embeddedCurrentProcess = process;

                // 设置进度条
                Dispatcher.Invoke(() =>
                {
                    if (EmbeddedProgressBar != null)
                    {
                        EmbeddedProgressBar.Value = 0;
                    }
                    if (EmbeddedProgressTextBlock != null)
                    {
                        EmbeddedProgressTextBlock.Text = "执行中...";
                    }
                    if (EmbeddedStopButton != null)
                    {
                        EmbeddedStopButton.IsEnabled = true;
                    }
                });

                // 启动进程
                process.Start();

                // 创建任务来读取输出
                var standardOutputBrush = ResolveBrush("Brush.CommandPromptTextOutput", Brushes.White);
                var errorOutputBrush = ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow);
                var outputTask = Task.Run(() => EmbeddedReadOutput(process.StandardOutput, standardOutputBrush));
                var errorTask = Task.Run(() => EmbeddedReadError(process.StandardError, errorOutputBrush));

                // 等待进程完成
                await process.WaitForExitAsync(_embeddedCancellationTokenSource.Token);

                // 等待输出读取完成
                await Task.WhenAll(outputTask, errorTask);

                // 更新进度条
                Dispatcher.Invoke(() =>
                {
                    if (EmbeddedProgressBar != null)
                    {
                        EmbeddedProgressBar.Value = 100;
                    }
                    if (EmbeddedProgressTextBlock != null)
                    {
                        EmbeddedProgressTextBlock.Text = $"完成 (退出码: {process.ExitCode})";
                    }
                    if (EmbeddedStopButton != null)
                    {
                        EmbeddedStopButton.IsEnabled = false;
                    }
                });

                if (process.ExitCode == 0)
                {
                    EmbeddedAppendOutput("命令执行成功", ResolveBrush("Brush.CommandPromptTextSuccess", Brushes.Green));
                }
                else
                {
                    EmbeddedAppendOutput($"命令执行失败 (退出码: {process.ExitCode})", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                }

                EmbeddedAppendOutput("");
                EmbeddedAppendOutput("> ");
            }
            catch (OperationCanceledException)
            {
                EmbeddedAppendOutput("命令已取消", ResolveBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                EmbeddedAppendOutput("> ");
            }
            catch (Exception ex)
            {
                EmbeddedAppendOutput($"执行命令时出错: {ex.Message}", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                EmbeddedAppendOutput("> ");
            }
            finally
            {
                _embeddedCurrentProcess = null;
                _embeddedCancellationTokenSource?.Dispose();
                _embeddedCancellationTokenSource = null;

                Dispatcher.Invoke(() =>
                {
                    if (EmbeddedStopButton != null)
                    {
                        EmbeddedStopButton.IsEnabled = false;
                    }
                });
            }
        }

        /// <summary>
        /// 读取嵌入式进程标准输出
        /// </summary>
        private async Task EmbeddedReadOutput(StreamReader reader, Brush color)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    EmbeddedAppendOutput(line, color);
                    EmbeddedParseProgress(line);
                }
            }
            catch (Exception ex)
            {
                EmbeddedAppendOutput($"读取输出时出错: {ex.Message}", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        /// <summary>
        /// 读取嵌入式进程错误输出
        /// </summary>
        private async Task EmbeddedReadError(StreamReader reader, Brush color)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    EmbeddedAppendOutput(line, color);
                    EmbeddedParseProgress(line);
                }
            }
            catch (Exception ex)
            {
                EmbeddedAppendOutput($"读取错误输出时出错: {ex.Message}", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        /// <summary>
        /// 显示嵌入式帮助信息
        /// </summary>
        private void EmbeddedShowHelp()
        {
            EmbeddedAppendOutput("可用命令:", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            EmbeddedAppendOutput("  help              - 显示此帮助信息");
            EmbeddedAppendOutput("  help <关键词>      - 搜索命令示例");
            EmbeddedAppendOutput("  list <类别>       - 列出指定类别的所有命令");
            EmbeddedAppendOutput("  list categories   - 列出所有命令类别");
            EmbeddedAppendOutput("  clear             - 清空输出窗口");
            EmbeddedAppendOutput("  exit              - 返回播放器");
            EmbeddedAppendOutput("  history           - 显示命令历史");
            EmbeddedAppendOutput("");
            EmbeddedAppendOutput("命令类别:", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            var categories = _ffmpegCommandHelpService.GetAllCategories();
            foreach (var category in categories)
            {
                EmbeddedAppendOutput($"  • {category.Name} - {category.Description}");
            }
            EmbeddedAppendOutput("");
            EmbeddedAppendOutput("示例:", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
            EmbeddedAppendOutput("  help 裁剪          - 搜索包含'裁剪'的命令");
            EmbeddedAppendOutput("  list 视频剪切      - 列出'视频剪切'类别的所有命令");
            EmbeddedAppendOutput("");
        }

        /// <summary>
        /// 搜索嵌入式命令
        /// </summary>
        private void EmbeddedShowHelpSearch(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                EmbeddedAppendOutput("请输入搜索关键词，例如: help 裁剪", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            var results = _ffmpegCommandHelpService.SearchCommands(keyword);
            if (results.Count == 0)
            {
                EmbeddedAppendOutput($"未找到包含 '{keyword}' 的命令", ResolveBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                EmbeddedAppendOutput("提示: 使用 'list categories' 查看所有类别", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            EmbeddedAppendOutput($"找到 {results.Count} 个匹配的命令:", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            EmbeddedAppendOutput("");
            foreach (var result in results)
            {
                EmbeddedAppendOutput($"【{result.Name}】", ResolveBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));
                EmbeddedAppendOutput($"  说明: {result.Description}");
                EmbeddedAppendOutput($"  命令: {result.Command}", ResolveBrush("Brush.CommandPromptTextOutput", Brushes.White));
                if (!string.IsNullOrWhiteSpace(result.Parameters))
                {
                    EmbeddedAppendOutput($"  参数: {result.Parameters.Replace("\n", "\n        ")}", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                EmbeddedAppendOutput("");
            }
        }

        /// <summary>
        /// 显示嵌入式类别命令
        /// </summary>
        private void EmbeddedShowCategoryCommands(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                EmbeddedAppendOutput("请输入类别名称，例如: list 视频剪切", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            if (categoryName.Equals("categories", StringComparison.OrdinalIgnoreCase))
            {
                EmbeddedAppendOutput("所有命令类别:", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
                var allCategories = _ffmpegCommandHelpService.GetAllCategories();
                foreach (var cat in allCategories)
                {
                    EmbeddedAppendOutput($"  • {cat.Name} - {cat.Description} ({cat.Examples.Count} 个示例)");
                }
                EmbeddedAppendOutput("");
                EmbeddedAppendOutput("使用 'list <类别名>' 查看该类别的详细命令", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            var foundCategory = _ffmpegCommandHelpService.GetCategoryByName(categoryName);
            if (foundCategory == null)
            {
                EmbeddedAppendOutput($"未找到类别 '{categoryName}'", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
                EmbeddedAppendOutput("使用 'list categories' 查看所有类别", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            EmbeddedAppendOutput($"【{foundCategory.Name}】", ResolveBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            EmbeddedAppendOutput($"{foundCategory.Description}");
            EmbeddedAppendOutput("");
            EmbeddedAppendOutput($"共 {foundCategory.Examples.Count} 个命令示例:", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
            EmbeddedAppendOutput("");

            foreach (var example in foundCategory.Examples)
            {
                EmbeddedAppendOutput($"【{example.Name}】", ResolveBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));
                EmbeddedAppendOutput($"  说明: {example.Description}");
                EmbeddedAppendOutput($"  命令: {example.Command}", ResolveBrush("Brush.CommandPromptTextOutput", Brushes.White));
                if (!string.IsNullOrWhiteSpace(example.Parameters))
                {
                    EmbeddedAppendOutput($"  参数: {example.Parameters.Replace("\n", "\n        ")}", ResolveBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                EmbeddedAppendOutput("");
            }
        }

        /// <summary>
        /// 解析嵌入式进度信息
        /// </summary>
        private void EmbeddedParseProgress(string line)
        {
            try
            {
                var match = Regex.Match(line, @"frame=\s*(\d+)\s+fps=\s*(\d+(?:\.\d+)?)\s+.*time=(\d{2}:\d{2}:\d{2}(?:\.\d{2})?).*bitrate=\s*(\d+(?:\.\d+)?)kbits/s");

                if (match.Success)
                {
                    var frame = match.Groups[1].Value;
                    var fps = match.Groups[2].Value;
                    var time = match.Groups[3].Value;
                    var bitrate = match.Groups[4].Value;

                    Dispatcher.Invoke(() =>
                    {
                        if (EmbeddedProgressTextBlock != null)
                        {
                            EmbeddedProgressTextBlock.Text = $"帧:{frame} FPS:{fps} 时间:{time} 码率:{bitrate}kbps";
                        }
                    });
                }
                else
                {
                    var durationMatch = Regex.Match(line, @"Duration:\s*(\d{2}:\d{2}:\d{2}(?:\.\d{2})?)");
                    if (durationMatch.Success)
                    {
                        var duration = durationMatch.Groups[1].Value;
                        Dispatcher.Invoke(() =>
                        {
                            if (EmbeddedProgressTextBlock != null)
                            {
                                EmbeddedProgressTextBlock.Text = $"时长: {duration}";
                            }
                        });
                    }
                }
            }
            catch
            {
                // 忽略解析错误
            }
        }

        /// <summary>
        /// 添加嵌入式输出文本
        /// </summary>
        private void EmbeddedAppendOutput(string text, Brush? color = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (EmbeddedOutputTextBox != null)
                {
                    if (color == null)
                    {
                        color = ResolveBrush("Brush.CommandPromptTextDefault", Brushes.LightGray);
                    }

                    EmbeddedOutputTextBox.AppendText(text + Environment.NewLine);
                    EmbeddedOutputTextBox.ScrollToEnd();
                }

                if (EmbeddedOutputScrollViewer != null)
                {
                    EmbeddedOutputScrollViewer.ScrollToBottom();
                }
            });
        }

        /// <summary>
        /// 停止嵌入式当前进程
        /// </summary>
        private void EmbeddedStopCurrentProcess()
        {
            try
            {
                _embeddedCancellationTokenSource?.Cancel();

                if (_embeddedCurrentProcess != null && !_embeddedCurrentProcess.HasExited)
                {
                    _embeddedCurrentProcess.Kill();
                    EmbeddedAppendOutput("命令已停止", ResolveBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                    EmbeddedAppendOutput("> ");
                }
            }
            catch (Exception ex)
            {
                EmbeddedAppendOutput($"停止命令时出错: {ex.Message}", ResolveBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        #endregion

        /// <summary>
        /// 自动搜索并设置FFmpeg.exe路径
        /// </summary>
        private void AutoFindFFmpegPath()
        {
            try
            {
                string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                Services.DebugLogger.LogInfo($"程序运行目录: {appDirectory}");

                string ffmpegPath = FindFFmpegExecutable();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    // 设置嵌入式FFmpeg路径输入框
                    if (EmbeddedFFmpegPathTextBox != null)
                    {
                        EmbeddedFFmpegPathTextBox.Text = ffmpegPath;
                        Services.DebugLogger.LogInfo($"自动设置FFmpeg路径: {ffmpegPath}");
                    }

                    // 设置VideoProcessingService的FFmpeg路径
                    _videoProcessingService.SetFFmpegPath(ffmpegPath);

                    // 设置VideoInformationService的FFprobe路径（通常与ffmpeg在同一目录）
                    string ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
                    if (File.Exists(ffprobePath))
                    {
                        _videoInformationService.SetFFprobePath(ffprobePath);
                        Services.DebugLogger.LogInfo($"自动设置FFprobe路径: {ffprobePath}");

                        // 同时设置VideoProcessingService中VideoInformationService的FFprobe路径
                        // 通过反射或其他方式设置内部的VideoInformationService
                        var videoProcessingServiceType = _videoProcessingService.GetType();
                        var videoInfoServiceField = videoProcessingServiceType.GetField("_videoInformationService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (videoInfoServiceField != null)
                        {
                            var internalVideoInfoService = videoInfoServiceField.GetValue(_videoProcessingService) as Services.VideoInformationService;
                            internalVideoInfoService?.SetFFprobePath(ffprobePath);
                            Services.DebugLogger.LogInfo("已同步设置VideoProcessingService中的FFprobe路径");
                        }
                    }
                    else
                    {
                        Services.DebugLogger.LogWarning($"未找到FFprobe文件: {ffprobePath}");
                    }

                    // 更新BatchAiSubtitleCoordinator的FFmpeg路径
                    if (_batchSubtitleCoordinator != null)
                    {
                        _batchSubtitleCoordinator.SetFFmpegPath(ffmpegPath);
                        Services.DebugLogger.LogInfo("已更新BatchAiSubtitleCoordinator的FFmpeg路径");
                    }
                }
                else
                {
                    Services.DebugLogger.LogWarning($"未找到FFmpeg.exe文件，搜索范围：程序目录({appDirectory})及其子目录、项目根目录、常见安装目录、PATH环境变量");
                    MessageBox.Show("未找到FFmpeg.exe文件。\n\n搜索范围包括：\n• 程序目录及其子目录\n• 项目根目录的tools/ffmpeg文件夹\n• 系统常见安装目录\n• PATH环境变量中的目录\n\n请确保FFmpeg已正确安装，或手动设置FFmpeg路径。",
                        "FFmpeg未找到", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"自动搜索FFmpeg路径失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 搜索FFmpeg.exe可执行文件（优化版：优先检查已知位置）
        /// </summary>
        /// <returns>FFmpeg.exe的完整路径，如果未找到则返回null</returns>
        private string FindFFmpegExecutable()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Services.DebugLogger.LogInfo($"开始搜索FFmpeg.exe，程序目录: {appDirectory}");

            // 1. 优先检查程序目录根目录（最常见位置，最快）
            Services.DebugLogger.LogInfo("步骤1: 检查程序目录根目录");
            string rootPath = Path.Combine(appDirectory, "ffmpeg.exe");
            if (File.Exists(rootPath))
            {
                Services.DebugLogger.LogInfo($"在程序目录根目录找到FFmpeg: {rootPath}");
                return rootPath;
            }

            // 2. 优先检查 tools/ffmpeg 目录（项目结构，快速）
            Services.DebugLogger.LogInfo("步骤2: 检查 tools/ffmpeg 目录");
            string toolsPath = Path.Combine(appDirectory, "tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(toolsPath))
            {
                Services.DebugLogger.LogInfo($"在 tools/ffmpeg 目录找到FFmpeg: {toolsPath}");
                return toolsPath;
            }

            // 3. 检查程序目录的直接子目录（限制深度，避免全目录搜索）
            Services.DebugLogger.LogInfo("步骤3: 检查程序目录的直接子目录（限制深度）");
            string ffmpegPath = SearchFFmpegInDirectory(appDirectory, maxDepth: 2);
            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                Services.DebugLogger.LogInfo($"在程序目录子目录找到FFmpeg: {ffmpegPath}");
                return ffmpegPath;
            }

            // 4. 向上查找项目根目录（通常包含tools文件夹）
            Services.DebugLogger.LogInfo("步骤4: 向上查找项目根目录");
            string currentDir = appDirectory;
            for (int i = 0; i < 6; i++) // 最多向上查找6层
            {
                currentDir = Directory.GetParent(currentDir)?.FullName;
                if (currentDir == null) break;

                Services.DebugLogger.LogInfo($"检查上级目录: {currentDir}");

                // 检查上级目录下的tools/ffmpeg/ffmpeg.exe
                string parentToolsPath = Path.Combine(currentDir, "tools", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(parentToolsPath))
                {
                    Services.DebugLogger.LogInfo($"在项目根目录找到FFmpeg: {parentToolsPath}");
                    return parentToolsPath;
                }

                // 检查上级目录下的tools/ffmpeg子目录（只检查根目录，不递归）
                string toolsDir = Path.Combine(currentDir, "tools", "ffmpeg");
                if (Directory.Exists(toolsDir))
                {
                    string toolsFfmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
                    if (File.Exists(toolsFfmpegPath))
                    {
                        Services.DebugLogger.LogInfo($"在项目根目录的tools文件夹找到FFmpeg: {toolsFfmpegPath}");
                        return toolsFfmpegPath;
                    }
                }
            }

            // 5. 搜索常见的安装目录（系统级安装）
            Services.DebugLogger.LogInfo("步骤5: 搜索常见安装目录");
            string[] commonPaths = new string[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ffmpeg\bin\ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"ffmpeg\bin\ffmpeg.exe")
            };

            foreach (string path in commonPaths)
            {
                if (File.Exists(path))
                {
                    Services.DebugLogger.LogInfo($"在常见安装目录找到FFmpeg: {path}");
                    return path;
                }
            }

            // 6. 最后才搜索PATH环境变量（作为最后的后备选项）
            Services.DebugLogger.LogInfo("步骤6: 搜索PATH环境变量中的目录（最后的后备选项）");
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                string[] paths = pathEnv.Split(Path.PathSeparator);
                foreach (string path in paths)
                {
                    if (Directory.Exists(path))
                    {
                        string potentialPath = Path.Combine(path, "ffmpeg.exe");
                        if (File.Exists(potentialPath))
                        {
                            Services.DebugLogger.LogInfo($"在PATH中找到FFmpeg: {potentialPath}");
                            return potentialPath;
                        }
                    }
                }
            }

            Services.DebugLogger.LogWarning("未在任何位置找到FFmpeg.exe");
            return null;
        }

        /// <summary>
        /// 在指定目录及其子目录中搜索ffmpeg.exe（优化版：限制搜索深度）
        /// </summary>
        /// <param name="directory">要搜索的目录</param>
        /// <param name="maxDepth">最大搜索深度（默认2层，避免全目录搜索）</param>
        /// <returns>找到的ffmpeg.exe完整路径，如果未找到则返回null</returns>
        private string SearchFFmpegInDirectory(string directory, int maxDepth = 2)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return null;

                // 首先检查当前目录
                string ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                    return ffmpegPath;

                // 如果 maxDepth <= 0，只检查当前目录，不递归
                if (maxDepth <= 0)
                    return null;

                // 限制搜索深度，避免全目录搜索（性能优化）
                // 使用递归搜索，但限制深度
                return SearchFFmpegRecursive(directory, 0, maxDepth);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 递归搜索ffmpeg.exe文件，遍历所有子目录
        /// </summary>
        /// <param name="directory">当前搜索目录</param>
        /// <param name="currentDepth">当前递归深度</param>
        /// <param name="maxDepth">最大递归深度</param>
        /// <returns>找到的ffmpeg.exe完整路径，如果未找到则返回null</returns>
        private string SearchFFmpegRecursive(string directory, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth)
                return null;

            try
            {
                // 检查当前目录
                string ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                    return ffmpegPath;

                // 搜索所有子目录（不再限制特定目录名称）
                string[] subDirectories = Directory.GetDirectories(directory);
                foreach (string subDir in subDirectories)
                {
                    string result = SearchFFmpegRecursive(subDir, currentDepth + 1, maxDepth);
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }
            catch
            {
                // 忽略访问权限等异常
            }

            return null;
        }

        /// <summary>
        /// 查找父级元素
        /// </summary>
        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null)
                return null;

            T parent = parentObject as T;
            if (parent != null)
                return parent;
            else
                return FindParent<T>(parentObject);
        }

        /// <summary>
        /// 递归查找具有指定名称的视觉子元素
        /// </summary>
        private static T FindVisualChild<T>(DependencyObject parent, string childName) where T : FrameworkElement
        {
            if (parent == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && typedChild.Name == childName)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child, childName);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private async void StartCutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请先在播放列表中勾选一个需要剪切的文件。", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (selectedFiles.Count > 1)
                {
                    MessageBox.Show("一次仅支持针对单个源文件剪切片段，请只勾选一个文件。", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var selectedFile = selectedFiles[0];
                if (string.IsNullOrWhiteSpace(selectedFile.FilePath) || !File.Exists(selectedFile.FilePath))
                {
                    MessageBox.Show("选中的源文件路径无效，请重新选择。", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var selectedClips = _clipManager.GetSelectedClips().OrderBy(c => c.Order).ToArray();
                if (selectedClips.Length == 0)
                {
                    MessageBox.Show("请在片段列表中勾选至少一个片段。", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (HasDuplicateClipTitles(selectedClips, out var duplicateNames))
                {
                    MessageBox.Show($"存在同名片段：{duplicateNames}\r\n请先修改片段标题后再尝试剪切。", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!TryPrepareClipCutTasks(selectedFile, selectedClips, outputSettings, out var tasks, out var preparationError))
                {
                    MessageBox.Show(preparationError, "剪切片段", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowClipCutCommands(selectedFile, tasks, outputSettings.CustomArgs);
                    return;
                }

                _clipCutCancellationTokenSource?.Cancel();
                _clipCutCancellationTokenSource = new CancellationTokenSource();

                await ExecuteClipCutBatchAsync(selectedFile, tasks, outputSettings, _clipCutCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"StartCutButton_Click 失败: {ex.Message}");
                MessageBox.Show($"剪切片段时发生错误：{ex.Message}", "剪切片段", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 水印功能事件处理

        /// <summary>
        /// 浏览水印图片按钮
        /// </summary>
        private void BrowseWatermarkImageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Forms.OpenFileDialog
                {
                    Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                    Title = "选择水印图片"
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    txtWatermarkImagePath.Text = dialog.FileName;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"浏览水印图片失败: {ex.Message}");
                MessageBox.Show($"选择水印图片时发生错误：{ex.Message}", "选择图片", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 选择文字颜色按钮
        /// </summary>
        private void SelectTextColorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new System.Windows.Forms.ColorDialog();
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var color = dialog.Color;
                    // 转换为FFmpeg颜色格式（支持颜色名称或十六进制）
                    // 这里使用简单的颜色名称映射，也可以使用十六进制格式
                    var colorName = GetColorName(color);
                    txtTextColor.Text = colorName;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"选择颜色失败: {ex.Message}");
                MessageBox.Show($"选择颜色时发生错误：{ex.Message}", "选择颜色", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取颜色名称（转换为FFmpeg支持的颜色格式）
        /// </summary>
        private string GetColorName(System.Drawing.Color color)
        {
            // 常见颜色映射
            if (color.R == 255 && color.G == 255 && color.B == 255) return "white";
            if (color.R == 0 && color.G == 0 && color.B == 0) return "black";
            if (color.R == 255 && color.G == 0 && color.B == 0) return "red";
            if (color.R == 0 && color.G == 255 && color.B == 0) return "green";
            if (color.R == 0 && color.G == 0 && color.B == 255) return "blue";
            if (color.R == 255 && color.G == 255 && color.B == 0) return "yellow";
            if (color.R == 255 && color.G == 0 && color.B == 255) return "magenta";
            if (color.R == 0 && color.G == 255 && color.B == 255) return "cyan";

            // 其他颜色使用十六进制格式
            return $"0x{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// 水印位置按钮点击事件（快速定位）
        /// </summary>
        private void WatermarkPositionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button button) return;

                // 获取视频信息以计算位置
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请先选择一个视频文件", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 这里假设使用第一个选中的视频，实际可能需要根据当前播放的视频来确定
                // 为了简化，我们使用固定的预设值
                var (x, y) = GetPositionFromButton(button.Name);
                txtWatermarkX.Text = x.ToString();
                txtWatermarkY.Text = y.ToString();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"设置水印位置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据按钮名称获取位置坐标（预设值，实际应该根据视频尺寸计算）
        /// </summary>
        private (int x, int y) GetPositionFromButton(string buttonName)
        {
            // 这些是预设的固定位置值，实际应用中应该根据视频尺寸动态计算
            return buttonName switch
            {
                "PositionTopLeftButton" => (10, 10),
                "PositionTopCenterButton" => (640, 10),      // 假设1920宽度，居中
                "PositionTopRightButton" => (1910, 10),
                "PositionMiddleLeftButton" => (10, 360),      // 假设1080高度，居中
                "PositionCenterButton" => (960, 540),         // 假设1920x1080，中心
                "PositionMiddleRightButton" => (1910, 360),
                "PositionBottomLeftButton" => (10, 1070),
                "PositionBottomCenterButton" => (640, 1070),
                "PositionBottomRightButton" => (1910, 1070),
                _ => (10, 10)
            };
        }

        /// <summary>
        /// 添加水印按钮点击事件
        /// </summary>
        private async void AddWatermarkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取水印参数
                var watermarkParams = GetWatermarkParameters();
                if (watermarkParams == null)
                {
                    return; // 错误已在GetWatermarkParameters中提示
                }

                // 检查是否可以进行水印处理：复制编码器不能与水印同时使用
                if (outputSettings.VideoCodec == "复制")
                {
                    var msgResult = MessageBox.Show(
                        "⚠️ 复制编码器不支持添加水印操作\r\n\r\n" +
                        "添加水印需要重新编码视频才能应用过滤器。\r\n\r\n" +
                        "是否要自动切换到推荐的 H.264 编码器？\r\n\r\n" +
                        "H.264 提供最佳的质量与速度平衡。",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });

                        MessageBox.Show("已自动切换到 H.264 编码器，请重新点击'添加水印'按钮", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        Services.DebugLogger.LogInfo("用户选择自动切换到H.264编码器进行水印操作");
                    }
                    else
                    {
                        MessageBox.Show("请在'输出设置'中手动选择 H.264 或 H.265 编码器，然后重新执行添加水印", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Services.DebugLogger.LogInfo("用户拒绝自动切换编码器");
                    }

                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowWatermarkCommands(selectedFiles, watermarkParams, outputSettings);
                    return;
                }

                // 执行批量添加水印
                _watermarkCancellationTokenSource?.Cancel();
                _watermarkCancellationTokenSource = new CancellationTokenSource();

                await ExecuteWatermarkBatchAsync(selectedFiles, watermarkParams, outputSettings, _watermarkCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"AddWatermarkButton_Click 失败: {ex.Message}");
                MessageBox.Show($"添加水印时发生错误：{ex.Message}", "添加水印", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 移除水印按钮点击事件
        /// </summary>
        private async void RemoveWatermarkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "移除水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取移除水印参数
                if (!int.TryParse(txtRemoveX.Text, out var x) || x < 0)
                {
                    MessageBox.Show("请输入有效的X坐标", "移除水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(txtRemoveY.Text, out var y) || y < 0)
                {
                    MessageBox.Show("请输入有效的Y坐标", "移除水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(txtRemoveW.Text, out var w) || w <= 0)
                {
                    MessageBox.Show("请输入有效的宽度", "移除水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(txtRemoveH.Text, out var h) || h <= 0)
                {
                    MessageBox.Show("请输入有效的高度", "移除水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var removeParams = new Models.RemoveWatermarkParameters
                {
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h
                };

                // 检查是否可以进行移除水印处理：复制编码器不能与移除水印同时使用
                if (outputSettings.VideoCodec == "复制")
                {
                    var msgResult = MessageBox.Show(
                        "⚠️ 复制编码器不支持移除水印操作\r\n\r\n" +
                        "移除水印需要重新编码视频才能应用过滤器。\r\n\r\n" +
                        "是否要自动切换到推荐的 H.264 编码器？\r\n\r\n" +
                        "H.264 提供最佳的质量与速度平衡。",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });

                        MessageBox.Show("已自动切换到 H.264 编码器，请重新点击'移除水印'按钮", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        Services.DebugLogger.LogInfo("用户选择自动切换到H.264编码器进行移除水印操作");
                    }
                    else
                    {
                        MessageBox.Show("请在'输出设置'中手动选择 H.264 或 H.265 编码器，然后重新执行移除水印", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Services.DebugLogger.LogInfo("用户拒绝自动切换编码器");
                    }

                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowRemoveWatermarkCommands(selectedFiles, removeParams, outputSettings);
                    return;
                }

                // 执行批量移除水印
                _watermarkCancellationTokenSource?.Cancel();
                _watermarkCancellationTokenSource = new CancellationTokenSource();

                await ExecuteRemoveWatermarkBatchAsync(selectedFiles, removeParams, outputSettings, _watermarkCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"RemoveWatermarkButton_Click 失败: {ex.Message}");
                MessageBox.Show($"移除水印时发生错误：{ex.Message}", "移除水印", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取水印参数
        /// </summary>
        private Models.WatermarkParameters? GetWatermarkParameters(bool showWarnings = true)
        {
            var parameters = new Models.WatermarkParameters();
            var hasValidType = true;

            // 确定水印类型
            if (RadioImageWatermark?.IsChecked == true)
            {
                parameters.Type = Models.WatermarkType.Image;
                parameters.ImagePath = txtWatermarkImagePath?.Text?.Trim() ?? string.Empty;
                parameters.ImageOpacity = sliderImageOpacity?.Value ?? 80;

                if (string.IsNullOrWhiteSpace(parameters.ImagePath))
                {
                    if (showWarnings)
                {
                    MessageBox.Show("请输入水印图片路径", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(parameters.ImagePath) && !File.Exists(parameters.ImagePath))
                {
                    if (showWarnings)
                {
                    MessageBox.Show("水印图片文件不存在", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                    }
                }
            }
            else if (RadioTextWatermark?.IsChecked == true)
            {
                parameters.Type = Models.WatermarkType.Text;
                parameters.Text = txtWatermarkText?.Text?.Trim() ?? string.Empty;
                parameters.FontSize = int.TryParse(txtFontSize?.Text, out var fontSize) ? fontSize : 24;
                parameters.TextColor = txtTextColor?.Text?.Trim() ?? "white";
                parameters.TextOpacity = sliderTextOpacity?.Value ?? 80;

                if (string.IsNullOrWhiteSpace(parameters.Text))
                {
                    if (showWarnings)
                {
                    MessageBox.Show("请输入水印文字", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                    }
                }
            }
            else
            {
                hasValidType = false;
                parameters.Type = Models.WatermarkType.None;
            }

            if (!hasValidType && showWarnings)
            {
                MessageBox.Show("请选择水印类型（图片或文字）", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            // 位置参数
            if (!int.TryParse(txtWatermarkX?.Text, out var x) || x < 0)
            {
                if (showWarnings)
            {
                MessageBox.Show("请输入有效的X坐标", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
                }
                x = Math.Max(0, x);
            }

            if (!int.TryParse(txtWatermarkY?.Text, out var y) || y < 0)
            {
                if (showWarnings)
            {
                MessageBox.Show("请输入有效的Y坐标", "添加水印", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
                }
                y = Math.Max(0, y);
            }

            parameters.X = x;
            parameters.Y = y;

            return parameters;
        }

        /// <summary>
        /// 批量执行添加水印
        /// </summary>
        private async Task ExecuteWatermarkBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.WatermarkParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateWatermarkOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"添加水印: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.AddWatermarkAsync(
                            input, output, parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            GetAudioCodecForFFmpeg(settings.AudioCodec),
                            settings.AudioBitrate.Replace(" kbps", "k"),
                            settings.CustomArgs,
                            null, // Progress handled by batch processor
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量添加水印",
                OperationIcon = "🎨",
                OperationColor = "#9C27B0",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🎵 音频设置: {settings.AudioCodec} @ {settings.AudioBitrate}",
                    parameters.Type == Models.WatermarkType.Image
                        ? $"🖼️ 水印图片: {Path.GetFileName(parameters.ImagePath)}"
                        : $"📝 水印文字: {parameters.Text}",
                    $"📍 水印位置: X={parameters.X}, Y={parameters.Y}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            // 显示结果
            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 批量执行移除水印
        /// </summary>
        private async Task ExecuteRemoveWatermarkBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.RemoveWatermarkParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateRemoveWatermarkOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"移除水印: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.RemoveWatermarkAsync(
                            input, output, parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            GetAudioCodecForFFmpeg(settings.AudioCodec),
                            settings.AudioBitrate.Replace(" kbps", "k"),
                            settings.CustomArgs,
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量移除水印",
                OperationIcon = "🗑️",
                OperationColor = "#F44336",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🎵 音频设置: {settings.AudioCodec} @ {settings.AudioBitrate}",
                    $"📍 移除区域: X={parameters.X}, Y={parameters.Y}, W={parameters.Width}, H={parameters.Height}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            // 显示结果
            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 生成水印输出文件名
        /// </summary>
        private string GenerateWatermarkOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_处理
                    outputFileName = $"{inputFileName}_水印";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 生成移除水印输出文件名
        /// </summary>
        private string GenerateRemoveWatermarkOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_处理
                    outputFileName = $"{inputFileName}_移除水印";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 显示水印命令（命令提示符模式）
        /// </summary>
        private void ShowWatermarkCommands(
            List<Models.VideoFile> selectedFiles,
            Models.WatermarkParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateWatermarkOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildWatermarkArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.AudioBitrate.Replace(" kbps", "k"),
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成水印命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 添加水印命令生成器",
                    OperationIcon = "🎨",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        parameters.Type == Models.WatermarkType.Image
                            ? $"🖼️ 水印图片: {Path.GetFileName(parameters.ImagePath)}"
                            : $"📝 水印文字: {parameters.Text}",
                        $"📍 位置: X={parameters.X}, Y={parameters.Y}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 显示移除水印命令（命令提示符模式）
        /// </summary>
        private void ShowRemoveWatermarkCommands(
            List<Models.VideoFile> selectedFiles,
            Models.RemoveWatermarkParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateRemoveWatermarkOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildRemoveWatermarkArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.AudioBitrate.Replace(" kbps", "k"),
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成移除水印命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 移除水印命令生成器",
                    OperationIcon = "🗑️",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"📍 移除区域: X={parameters.X}, Y={parameters.Y}, W={parameters.Width}, H={parameters.Height}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        #endregion

        #region 去重功能事件处理

        /// <summary>
        /// 应用去重设置按钮点击事件
        /// </summary>
        private async void ApplyDeduplicateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始执行去重处理...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "去重处理", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Services.DebugLogger.LogWarning("去重处理：未选择任何视频文件");
                    return;
                }

                Services.DebugLogger.LogInfo($"去重处理：已选择 {selectedFiles.Count} 个文件");

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取去重参数
                var deduplicateParams = GetDeduplicateParameters();
                if (deduplicateParams == null)
                {
                    return; // 错误已在GetDeduplicateParameters中提示
                }

                // 检查去重模式是否关闭
                if (deduplicateParams.Mode == Models.DeduplicateMode.Off)
                {
                    MessageBox.Show("去重模式已关闭，无需处理", "去重处理", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 检查是否可以进行去重处理：复制编码器不能与去重同时使用
                if (outputSettings.VideoCodec == "复制")
                {
                    var msgResult = MessageBox.Show(
                        "⚠️ 复制编码器不支持去重处理操作\r\n\r\n" +
                        "去重处理需要重新编码视频才能应用过滤器。\r\n\r\n" +
                        "是否要自动切换到推荐的 H.264 编码器？\r\n\r\n" +
                        "H.264 提供最佳的质量与速度平衡。",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });

                        MessageBox.Show("已自动切换到 H.264 编码器，请重新点击'应用去重设置'按钮", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        Services.DebugLogger.LogInfo("用户选择自动切换到H.264编码器进行去重操作");
                    }
                    else
                    {
                        MessageBox.Show("请在'输出设置'中手动选择 H.264 或 H.265 编码器，然后重新执行去重处理", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        Services.DebugLogger.LogInfo("用户拒绝自动切换编码器");
                    }

                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowDeduplicateCommands(selectedFiles, deduplicateParams, outputSettings);
                    return;
                }

                // 执行批量去重处理
                _deduplicateCancellationTokenSource?.Cancel();
                _deduplicateCancellationTokenSource = new CancellationTokenSource();

                await ExecuteDeduplicateBatchAsync(selectedFiles, deduplicateParams, outputSettings, _deduplicateCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ApplyDeduplicateButton_Click 失败: {ex.Message}");
                MessageBox.Show($"去重处理时发生错误：{ex.Message}", "去重处理", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取去重参数
        /// </summary>
        private Models.DeduplicateParameters? GetDeduplicateParameters()
        {
            try
            {
                var parameters = new Models.DeduplicateParameters();

                // 使用FindName查找控件（因为控件在TabItem内部）
                var radioOff = FindName("RadioOffMode") as RadioButton;
                var radioLight = FindName("RadioLightMode") as RadioButton;
                var radioMedium = FindName("RadioMediumMode") as RadioButton;
                var radioHeavy = FindName("RadioHeavyMode") as RadioButton;

                // 获取去重模式
                if (radioOff?.IsChecked == true)
                {
                    parameters.Mode = Models.DeduplicateMode.Off;
                }
                else if (radioLight?.IsChecked == true)
                {
                    parameters.Mode = Models.DeduplicateMode.Light;
                }
                else if (radioMedium?.IsChecked == true)
                {
                    parameters.Mode = Models.DeduplicateMode.Medium;
                }
                else if (radioHeavy?.IsChecked == true)
                {
                    parameters.Mode = Models.DeduplicateMode.Heavy;
                }
                else
                {
                    parameters.Mode = Models.DeduplicateMode.Off;
                }

                // 如果选择了预设模式，应用预设参数
                if (parameters.Mode != Models.DeduplicateMode.Off)
                {
                    parameters.ApplyMode();
                }

                // 获取手动调整的参数（覆盖预设值）
                var sliderBrightness = FindName("sliderBrightness") as Slider;
                var sliderContrast = FindName("sliderContrast") as Slider;
                var sliderSaturation = FindName("sliderSaturation") as Slider;
                var sliderNoise = FindName("sliderNoise") as Slider;
                var sliderBlur = FindName("sliderBlur") as Slider;
                var sliderCropEdge = FindName("sliderCropEdge") as Slider;

                if (sliderBrightness != null)
                {
                    parameters.Brightness = sliderBrightness.Value;
                }
                if (sliderContrast != null)
                {
                    parameters.Contrast = sliderContrast.Value;
                }
                if (sliderSaturation != null)
                {
                    parameters.Saturation = sliderSaturation.Value;
                }
                if (sliderNoise != null)
                {
                    parameters.Noise = sliderNoise.Value;
                }
                if (sliderBlur != null)
                {
                    parameters.Blur = sliderBlur.Value;
                }
                if (sliderCropEdge != null)
                {
                    parameters.CropEdge = sliderCropEdge.Value;
                }

                Services.DebugLogger.LogInfo($"去重参数获取成功: 模式={parameters.Mode}, 亮度={parameters.Brightness}, 对比度={parameters.Contrast}, 饱和度={parameters.Saturation}, 噪点={parameters.Noise}, 模糊={parameters.Blur}, 边缘裁剪={parameters.CropEdge}");

                return parameters;
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"获取去重参数失败: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"获取去重参数时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// 批量执行去重处理
        /// </summary>
        private async Task ExecuteDeduplicateBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.DeduplicateParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateDeduplicateOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"去重处理: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ApplyDeduplicateAsync(
                            input, output, parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            GetAudioCodecForFFmpeg(settings.AudioCodec),
                            settings.AudioBitrate.Replace(" kbps", "k"),
                            settings.CustomArgs,
                            null, // Progress handled by batch processor
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量去重处理",
                OperationIcon = "🎯",
                OperationColor = "#FF9800",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🎵 音频设置: {settings.AudioCodec} @ {settings.AudioBitrate}",
                    $"🎯 去重模式: {GetModeName(parameters.Mode)}",
                    $"🎨 色彩调整: 亮度={parameters.Brightness:F0}%, 对比度={parameters.Contrast:F0}%, 饱和度={parameters.Saturation:F0}%",
                    $"⚙️ 高级效果: 噪点={parameters.Noise:F0}, 模糊={parameters.Blur:F0}, 边缘裁剪={parameters.CropEdge:F0}px"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            // 显示结果
            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 生成去重输出文件名
        /// </summary>
        private string GenerateDeduplicateOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_处理
                    outputFileName = $"{inputFileName}_去重";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 获取模式名称
        /// </summary>
        private string GetModeName(Models.DeduplicateMode mode)
        {
            return mode switch
            {
                Models.DeduplicateMode.Off => "关闭",
                Models.DeduplicateMode.Light => "轻度",
                Models.DeduplicateMode.Medium => "中度",
                Models.DeduplicateMode.Heavy => "重度",
                _ => "未知"
            };
        }

        /// <summary>
        /// 显示去重命令（命令提示符模式）
        /// </summary>
        private void ShowDeduplicateCommands(
            List<Models.VideoFile> selectedFiles,
            Models.DeduplicateParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateDeduplicateOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildDeduplicateArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.AudioBitrate.Replace(" kbps", "k"),
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成去重命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 去重处理命令生成器",
                    OperationIcon = "🎯",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🎯 去重模式: {GetModeName(parameters.Mode)}",
                        $"🎨 色彩调整: 亮度={parameters.Brightness:F0}%, 对比度={parameters.Contrast:F0}%, 饱和度={parameters.Saturation:F0}%",
                        $"⚙️ 高级效果: 噪点={parameters.Noise:F0}, 模糊={parameters.Blur:F0}, 边缘裁剪={parameters.CropEdge:F0}px"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        #endregion

        #region 音频功能事件处理

        private CancellationTokenSource? _audioCancellationTokenSource;

        /// <summary>
        /// 音量预设按钮：静音
        /// </summary>
        private void BtnVolumeMute_Click(object sender, RoutedEventArgs e)
        {
            var slider = FindName("sliderVolume") as Slider;
            if (slider != null)
            {
                slider.Value = 0;
            }
        }

        /// <summary>
        /// 音量预设按钮：50%
        /// </summary>
        private void BtnVolume50_Click(object sender, RoutedEventArgs e)
        {
            var slider = FindName("sliderVolume") as Slider;
            if (slider != null)
            {
                slider.Value = 50;
            }
        }

        /// <summary>
        /// 音量预设按钮：100%
        /// </summary>
        private void BtnVolume100_Click(object sender, RoutedEventArgs e)
        {
            var slider = FindName("sliderVolume") as Slider;
            if (slider != null)
            {
                slider.Value = 100;
            }
        }

        /// <summary>
        /// 音量预设按钮：150%
        /// </summary>
        private void BtnVolume150_Click(object sender, RoutedEventArgs e)
        {
            var slider = FindName("sliderVolume") as Slider;
            if (slider != null)
            {
                slider.Value = 150;
            }
        }

        /// <summary>
        /// 浏览音频文件按钮
        /// </summary>
        private void BtnBrowseAudioFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Forms.OpenFileDialog
                {
                    Filter = "音频文件|*.mp3;*.wav;*.aac;*.flac;*.m4a;*.ogg;*.wma|所有文件|*.*",
                    Title = "选择音频文件"
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    var txtBox = FindName("txtAudioFilePath") as TextBox;
                    if (txtBox != null)
                    {
                        txtBox.Text = dialog.FileName;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"浏览音频文件失败: {ex.Message}");
                MessageBox.Show($"浏览音频文件时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 应用所有音频设置按钮
        /// </summary>
        private async void BtnApplyAudioSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始应用音频设置...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "应用音频设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取音频参数
                var audioParams = GetAudioParameters();
                if (audioParams == null)
                {
                    return; // 错误已在GetAudioParameters中提示
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowAudioSettingsCommands(selectedFiles, audioParams, outputSettings);
                    return;
                }

                // 执行批量应用音频设置
                _audioCancellationTokenSource?.Cancel();
                _audioCancellationTokenSource = new CancellationTokenSource();

                await ExecuteAudioSettingsBatchAsync(selectedFiles, audioParams, outputSettings, _audioCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnApplyAudioSettings_Click 失败: {ex.Message}");
                MessageBox.Show($"应用音频设置时发生错误：{ex.Message}", "应用音频设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 提取音频按钮
        /// </summary>
        private async void BtnExtractAudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始提取音频...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "提取音频", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置（用于输出路径）
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取音频格式和比特率
                var audioFormat = GetSelectedAudioFormat();
                var bitrate = GetSelectedBitrate();

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowExtractAudioCommands(selectedFiles, audioFormat, bitrate, outputSettings);
                    return;
                }

                // 执行批量提取音频
                _audioCancellationTokenSource?.Cancel();
                _audioCancellationTokenSource = new CancellationTokenSource();

                await ExecuteExtractAudioBatchAsync(selectedFiles, audioFormat, bitrate, outputSettings, _audioCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnExtractAudio_Click 失败: {ex.Message}");
                MessageBox.Show($"提取音频时发生错误：{ex.Message}", "提取音频", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 替换音频按钮
        /// </summary>
        private async void BtnReplaceAudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始替换音频...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "替换音频", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取音频文件路径
                var txtBox = FindName("txtAudioFilePath") as TextBox;
                var audioFilePath = txtBox?.Text?.Trim();
                if (string.IsNullOrEmpty(audioFilePath) || audioFilePath == "选择音频文件..." || !File.Exists(audioFilePath))
                {
                    MessageBox.Show("请先选择要替换的音频文件", "替换音频", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowReplaceAudioCommands(selectedFiles, audioFilePath, outputSettings);
                    return;
                }

                // 执行批量替换音频
                _audioCancellationTokenSource?.Cancel();
                _audioCancellationTokenSource = new CancellationTokenSource();

                await ExecuteReplaceAudioBatchAsync(selectedFiles, audioFilePath, outputSettings, _audioCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnReplaceAudio_Click 失败: {ex.Message}");
                MessageBox.Show($"替换音频时发生错误：{ex.Message}", "替换音频", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 删除音频按钮
        /// </summary>
        private async void BtnRemoveAudio_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始删除音频...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "删除音频", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowRemoveAudioCommands(selectedFiles, outputSettings);
                    return;
                }

                // 执行批量删除音频
                _audioCancellationTokenSource?.Cancel();
                _audioCancellationTokenSource = new CancellationTokenSource();

                await ExecuteRemoveAudioBatchAsync(selectedFiles, outputSettings, _audioCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnRemoveAudio_Click 失败: {ex.Message}");
                MessageBox.Show($"删除音频时发生错误：{ex.Message}", "删除音频", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取音频参数
        /// </summary>
        private Models.AudioParameters? GetAudioParameters()
        {
            try
            {
                var parameters = new Models.AudioParameters();

                // 获取音量
                var sliderVolume = FindName("sliderVolume") as Slider;
                if (sliderVolume != null)
                {
                    parameters.Volume = sliderVolume.Value;
                }

                // 获取淡入淡出
                var txtFadeIn = FindName("txtFadeIn") as TextBox;
                if (txtFadeIn != null && double.TryParse(txtFadeIn.Text, out var fadeIn))
                {
                    parameters.FadeIn = Math.Max(0, fadeIn);
                }

                var txtFadeOut = FindName("txtFadeOut") as TextBox;
                if (txtFadeOut != null && double.TryParse(txtFadeOut.Text, out var fadeOut))
                {
                    parameters.FadeOut = Math.Max(0, fadeOut);
                }

                // 获取音频格式
                parameters.Format = GetSelectedAudioFormat();

                // 获取比特率
                parameters.Bitrate = GetSelectedBitrate();

                Services.DebugLogger.LogInfo($"音频参数获取成功: 音量={parameters.Volume}%, 淡入={parameters.FadeIn}s, 淡出={parameters.FadeOut}s, 格式={parameters.Format}, 比特率={parameters.Bitrate}");

                return parameters;
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"获取音频参数失败: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"获取音频参数时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        /// <summary>
        /// 获取选中的音频格式
        /// </summary>
        private string GetSelectedAudioFormat()
        {
            var cbo = FindName("cboAudioFormat") as ComboBox;
            if (cbo?.SelectedItem is ComboBoxItem item)
            {
                var content = item.Content?.ToString() ?? "";
                // 移除括号中的内容
                return content.Split('(')[0].Trim();
            }
            return "AAC";
        }

        /// <summary>
        /// 获取选中的比特率
        /// </summary>
        private string GetSelectedBitrate()
        {
            var cbo = FindName("cboBitrate") as ComboBox;
            if (cbo?.SelectedItem is ComboBoxItem item)
            {
                var content = item.Content?.ToString() ?? "";
                // 提取比特率值，如 "192 kbps (推荐)" -> "192 kbps"
                var parts = content.Split('(');
                return parts[0].Trim();
            }
            return "192 kbps";
        }

        /// <summary>
        /// 批量执行应用音频设置
        /// </summary>
        private async Task ExecuteAudioSettingsBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.AudioParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "音频设置");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"应用音频设置: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ApplyAudioSettingsAsync(
                            input, output, parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            settings.CustomArgs,
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量应用音频设置",
                OperationIcon = "🔊",
                OperationColor = "#2196F3",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🔊 音量: {parameters.Volume:F0}%",
                    $"🎵 淡入: {parameters.FadeIn:F1}秒, 淡出: {parameters.FadeOut:F1}秒",
                    $"🎼 音频格式: {parameters.Format}, 比特率: {parameters.Bitrate}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 批量执行提取音频
        /// </summary>
        private async Task ExecuteExtractAudioBatchAsync(
            List<Models.VideoFile> selectedFiles,
            string audioFormat,
            string bitrate,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                // 提取音频的输出文件名：原文件名.扩展名（根据格式）
                var inputFileName = Path.GetFileNameWithoutExtension(inputFile.FilePath);
                var extension = GetAudioFileExtension(audioFormat);
                var outputFileName = $"{inputFileName}{extension}";
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"提取音频: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ExtractAudioAsync(
                            input, output, audioFormat, bitrate,
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量提取音频",
                OperationIcon = "📤",
                OperationColor = "#2196F3",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"🎼 音频格式: {audioFormat}",
                    $"📊 比特率: {bitrate}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功提取 {result.SuccessCount} 个音频文件"
                : "提取失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 批量执行替换音频
        /// </summary>
        private async Task ExecuteReplaceAudioBatchAsync(
            List<Models.VideoFile> selectedFiles,
            string audioFilePath,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "替换音频");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"替换音频: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ReplaceAudioAsync(
                            input, audioFilePath, output,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量替换音频",
                OperationIcon = "🔄",
                OperationColor = "#FF9800",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🎵 音频文件: {Path.GetFileName(audioFilePath)}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功替换 {result.SuccessCount} 个文件的音频"
                : "替换失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 批量执行删除音频
        /// </summary>
        private async Task ExecuteRemoveAudioBatchAsync(
            List<Models.VideoFile> selectedFiles,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "无音频");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"删除音频: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.RemoveAudioAsync(
                            input, output,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量删除音频",
                OperationIcon = "🗑️",
                OperationColor = "#F44336",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功删除 {result.SuccessCount} 个文件的音频"
                : "删除失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        /// <summary>
        /// 生成音频处理输出文件名
        /// </summary>
        private string GenerateAudioOutputFileName(string inputPath, OutputSettings settings, string suffix)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_处理
                    outputFileName = $"{inputFileName}_{suffix}";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 获取音频文件扩展名
        /// </summary>
        private string GetAudioFileExtension(string format)
        {
            return format.ToUpper() switch
            {
                "AAC" => ".aac",
                "MP3" => ".mp3",
                "WAV" => ".wav",
                "FLAC" => ".flac",
                _ => ".aac"
            };
        }

        #region 转码功能

        /// <summary>
        /// 开始转码按钮点击事件
        /// </summary>
        private async void BtnStartTranscode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请先选择要转码的视频文件", "转码", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取转码参数
                var transcodeParams = GetTranscodeParameters();
                if (transcodeParams == null)
                {
                    MessageBox.Show("获取转码参数失败", "转码", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null)
                {
                    MessageBox.Show("获取输出设置失败", "转码", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 验证输出路径
                if (string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "转码", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowTranscodeCommands(selectedFiles, transcodeParams, outputSettings);
                    return;
                }

                // 执行批量转码
                _transcodeCancellationTokenSource?.Cancel();
                _transcodeCancellationTokenSource = new CancellationTokenSource();

                await ExecuteTranscodeBatchAsync(selectedFiles, transcodeParams, outputSettings, _transcodeCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnStartTranscode_Click 失败: {ex.Message}");
                MessageBox.Show($"转码时发生错误：{ex.Message}", "转码", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 获取转码参数
        /// </summary>
        private Models.TranscodeParameters? GetTranscodeParameters()
        {
            try
            {
                var parameters = new Models.TranscodeParameters();

                // 获取转码模式
                var radioFast = FindName("RadioFastTranscode") as RadioButton;
                var radioStandard = FindName("RadioStandardTranscode") as RadioButton;
                var radioHighQuality = FindName("RadioHighQualityTranscode") as RadioButton;
                var radioCompress = FindName("RadioCompressTranscode") as RadioButton;

                if (radioFast?.IsChecked == true)
                {
                    parameters.Mode = Models.TranscodeMode.Fast;
                }
                else if (radioStandard?.IsChecked == true)
                {
                    parameters.Mode = Models.TranscodeMode.Standard;
                }
                else if (radioHighQuality?.IsChecked == true)
                {
                    parameters.Mode = Models.TranscodeMode.HighQuality;
                }
                else if (radioCompress?.IsChecked == true)
                {
                    parameters.Mode = Models.TranscodeMode.Compress;
                }
                else
                {
                    parameters.Mode = Models.TranscodeMode.Standard;
                }

                // 获取输出格式
                var cboFormat = FindName("cboTranscodeFormat") as ComboBox;
                if (cboFormat?.SelectedItem is ComboBoxItem selectedFormat)
                {
                    parameters.OutputFormat = selectedFormat.Content.ToString()?.Replace(" (推荐)", "") ?? "MP4";
                }

                // 获取视频编码器
                var cboVideoCodec = FindName("cboVideoCodec") as ComboBox;
                if (cboVideoCodec?.SelectedItem is ComboBoxItem selectedVideoCodec)
                {
                    parameters.VideoCodec = selectedVideoCodec.Content.ToString() ?? "H.264 / AVC";
                }

                // 获取音频编码器
                var cboAudioCodec = FindName("cboAudioCodec") as ComboBox;
                if (cboAudioCodec?.SelectedItem is ComboBoxItem selectedAudioCodec)
                {
                    parameters.AudioCodec = selectedAudioCodec.Content.ToString() ?? "AAC (推荐)";
                }

                // 获取CRF值
                var sliderCRF = FindName("sliderTranscodeCRF") as Slider;
                if (sliderCRF != null)
                {
                    parameters.CRF = (int)sliderCRF.Value;
                }

                // 获取音频比特率
                var cboBitrate = FindName("cboTranscodeBitrate") as ComboBox;
                if (cboBitrate?.SelectedItem is ComboBoxItem selectedBitrate)
                {
                    parameters.AudioBitrate = selectedBitrate.Content.ToString() ?? "256 kbps (推荐)";
                }

                // 获取高级选项
                var checkDualPass = FindName("CheckBoxDualPassTranscode") as CheckBox;
                var checkHardwareAccel = FindName("CheckBoxHardwareAccelTranscode") as CheckBox;
                var checkKeepMetadata = FindName("CheckBoxKeepMetadataTranscode") as CheckBox;

                parameters.DualPass = checkDualPass?.IsChecked ?? false;
                parameters.HardwareAcceleration = checkHardwareAccel?.IsChecked ?? false;
                parameters.KeepMetadata = checkKeepMetadata?.IsChecked ?? true;

                Services.DebugLogger.LogInfo($"转码参数获取成功: 模式={parameters.Mode}, 格式={parameters.OutputFormat}, 视频编码={parameters.VideoCodec}, 音频编码={parameters.AudioCodec}, CRF={parameters.CRF}, 音频比特率={parameters.AudioBitrate}, 双通道={parameters.DualPass}, 硬件加速={parameters.HardwareAcceleration}, 保留元数据={parameters.KeepMetadata}");

                return parameters;
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"获取转码参数失败: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 批量执行转码
        /// </summary>
        private async Task ExecuteTranscodeBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.TranscodeParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateTranscodeOutputFileName(inputFile.FilePath, parameters, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"转码: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.TranscodeAsync(
                            input, output, parameters,
                            settings.CustomArgs,
                            null, // Progress handled by batch processor
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量转码",
                OperationIcon = "🔄",
                OperationColor = "#FF5722",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 转码模式: {GetTranscodeModeName(parameters.Mode)}",
                    $"📦 输出格式: {parameters.OutputFormat}",
                    $"🎥 视频编码: {parameters.VideoCodec}",
                    $"🎵 音频编码: {parameters.AudioCodec}",
                    $"🎯 CRF值: {parameters.CRF}",
                    $"📊 音频比特率: {parameters.AudioBitrate}",
                    parameters.DualPass ? "⚙️ 双通道编码: 启用" : "⚙️ 双通道编码: 禁用",
                    parameters.HardwareAcceleration ? "⚡ 硬件加速: 启用" : "⚡ 硬件加速: 禁用",
                    parameters.KeepMetadata ? "📋 保留元数据: 启用" : "📋 保留元数据: 禁用"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功转码 {result.SuccessCount} 个文件"
                : "转码失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        private void ShowTranscodeCommands(
            List<Models.VideoFile> selectedFiles,
            Models.TranscodeParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateTranscodeOutputFileName(inputFile.FilePath, parameters, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var args = _videoProcessingService.BuildTranscodeArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = args
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成转码命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 转码命令生成器",
                    OperationIcon = "⚙️",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🎬 模式: {GetTranscodeModeName(parameters.Mode)}",
                        $"🎞️ 输出格式: {parameters.OutputFormat}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 生成转码输出文件名
        /// </summary>
        private string GenerateTranscodeOutputFileName(string inputPath, Models.TranscodeParameters parameters, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetTranscodeFileExtension(parameters.OutputFormat);

            string outputFileName;

            switch (settings.FileNamingMode)
            {
                case "自定义前缀":
                    outputFileName = $"{settings.CustomPrefix}{inputFileName}";
                    break;
                case "自定义后缀":
                    outputFileName = $"{inputFileName}{settings.CustomSuffix}";
                    break;
                default: // 原文件名_转码
                    outputFileName = $"{inputFileName}_转码";
                    break;
            }

            return $"{outputFileName}{extension}";
        }

        /// <summary>
        /// 获取转码文件扩展名
        /// </summary>
        private string GetTranscodeFileExtension(string format)
        {
            return format.ToUpper() switch
            {
                "MP4" or "MP4 (推荐)" => ".mp4",
                "MKV" => ".mkv",
                "AVI" => ".avi",
                "MOV" => ".mov",
                "WMV" => ".wmv",
                "FLV" => ".flv",
                "WEBM" => ".webm",
                "M4V" => ".m4v",
                "MPG" => ".mpg",
                "MPEG" => ".mpeg",
                "TS" => ".ts",
                "M2TS" => ".m2ts",
                _ => ".mp4"
            };
        }

        /// <summary>
        /// 获取转码模式名称
        /// </summary>
        private string GetTranscodeModeName(Models.TranscodeMode mode)
        {
            return mode switch
            {
                Models.TranscodeMode.Fast => "快速",
                Models.TranscodeMode.Standard => "标准",
                Models.TranscodeMode.HighQuality => "高质量",
                Models.TranscodeMode.Compress => "压缩",
                _ => "标准"
            };
        }

        #endregion

        #region GIF功能

        /// <summary>
        /// 使用当前时间按钮点击事件（提取帧）
        /// </summary>
        private void BtnUseCurrentTimeForExtract_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_videoPlayerViewModel != null && _videoPlayerViewModel.FormattedCurrentTime != null)
                {
                    txtExtractTime.Text = _videoPlayerViewModel.FormattedCurrentTime;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnUseCurrentTimeForExtract_Click 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用入点时间按钮点击事件（GIF）
        /// </summary>
        private void BtnUseInPointForGif_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_videoPlayerViewModel != null && _videoPlayerViewModel.HasInPoint)
                {
                    txtGifStartTime.Text = _videoPlayerViewModel.FormattedInPoint;
                }
                else
                {
                    MessageBox.Show("请先标记入点", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnUseInPointForGif_Click 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用出点时间按钮点击事件（GIF）
        /// </summary>
        private void BtnUseOutPointForGif_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_videoPlayerViewModel != null && _videoPlayerViewModel.HasOutPoint)
                {
                    txtGifEndTime.Text = _videoPlayerViewModel.FormattedOutPoint;
                }
                else
                {
                    MessageBox.Show("请先标记出点", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnUseOutPointForGif_Click 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 提取当前帧按钮点击事件
        /// </summary>
        private async void BtnExtractFrame_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的文件
                var selectedFile = _videoListViewModel.Files.FirstOrDefault(f => f.IsSelected);
                if (selectedFile == null)
                {
                    MessageBox.Show("请先选择要提取帧的视频文件", "提取帧", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null)
                {
                    MessageBox.Show("获取输出设置失败", "提取帧", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 验证输出路径
                if (string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "提取帧", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 解析时间
                if (!TimeSpan.TryParse(txtExtractTime.Text, out var extractTime))
                {
                    MessageBox.Show("时间格式不正确，请使用格式：HH:mm:ss.fff", "提取帧", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取图片格式
                var format = "png";
                if (cboPictureFormat.SelectedItem is ComboBoxItem selectedFormat)
                {
                    var formatText = selectedFormat.Content.ToString() ?? "PNG (无损)";
                    format = formatText.ToLower().Contains("png") ? "png" :
                             formatText.ToLower().Contains("jpg") || formatText.ToLower().Contains("jpeg") ? "jpg" :
                             formatText.ToLower().Contains("bmp") ? "bmp" :
                             formatText.ToLower().Contains("webp") ? "webp" : "png";
                }

                // 生成输出文件名
                var inputFileName = Path.GetFileNameWithoutExtension(selectedFile.FilePath);
                var extension = format == "jpg" ? ".jpg" : format == "bmp" ? ".bmp" : format == "webp" ? ".webp" : ".png";
                var timeStr = $"{(int)extractTime.TotalHours:D2}{(int)extractTime.Minutes:D2}{(int)extractTime.Seconds:D2}{(int)extractTime.Milliseconds:D3}";
                var outputFileName = $"{inputFileName}_frame_{timeStr}{extension}";
                var outputPath = Path.Combine(outputSettings.OutputPath, outputFileName);

                // 执行提取帧
                var result = await _videoProcessingService.ExtractFrameAsync(
                    selectedFile.FilePath,
                    outputPath,
                    extractTime,
                    format,
                    null,
                    CancellationToken.None);

                if (result.Success)
                {
                    UpdateStatusBar($"成功提取帧: {Path.GetFileName(outputPath)}", "✅", "#4CAF50");
                    Services.ToastNotification.ShowSuccess($"成功提取帧: {Path.GetFileName(outputPath)}");
                }
                else
                {
                    UpdateStatusBar($"提取帧失败: {result.ErrorMessage}", "❌", "#F44336");
                    MessageBox.Show($"提取帧失败：{result.ErrorMessage}", "提取帧", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnExtractFrame_Click 失败: {ex.Message}");
                MessageBox.Show($"提取帧时发生错误：{ex.Message}", "提取帧", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 制作GIF按钮点击事件
        /// </summary>
        private async void BtnMakeGif_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的文件
                var selectedFile = _videoListViewModel.Files.FirstOrDefault(f => f.IsSelected);
                if (selectedFile == null)
                {
                    MessageBox.Show("请先选择要制作GIF的视频文件", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null)
                {
                    MessageBox.Show("获取输出设置失败", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 验证输出路径
                if (string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 解析时间
                if (!TimeSpan.TryParse(txtGifStartTime.Text, out var startTime))
                {
                    MessageBox.Show("开始时间格式不正确，请使用格式：HH:mm:ss.fff", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!TimeSpan.TryParse(txtGifEndTime.Text, out var endTime))
                {
                    MessageBox.Show("结束时间格式不正确，请使用格式：HH:mm:ss.fff", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (startTime >= endTime)
                {
                    MessageBox.Show("开始时间必须小于结束时间", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取FPS和宽度
                if (!int.TryParse(txtGifFPS.Text, out var fps) || fps <= 0 || fps > 60)
                {
                    MessageBox.Show("FPS必须是1-60之间的整数", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(txtGifWidth.Text, out var width) || width <= 0 || width > 1920)
                {
                    MessageBox.Show("宽度必须是1-1920之间的整数", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取质量模式
                var qualityMode = Models.GifQualityMode.Medium;
                if (cboGifQuality.SelectedItem is ComboBoxItem selectedQuality)
                {
                    var qualityText = selectedQuality.Content.ToString() ?? "中等质量（推荐）";
                    qualityMode = qualityText.Contains("低质量") ? Models.GifQualityMode.Low :
                                  qualityText.Contains("高质量") ? Models.GifQualityMode.High :
                                  Models.GifQualityMode.Medium;
                }

                // 创建GIF参数
                var gifParams = new Models.GifParameters
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    FPS = fps,
                    Width = width,
                    QualityMode = qualityMode
                };

                // 生成输出文件名
                var inputFileName = Path.GetFileNameWithoutExtension(selectedFile.FilePath);
                var startTimeStr = $"{(int)startTime.TotalHours:D2}{(int)startTime.Minutes:D2}{(int)startTime.Seconds:D2}{(int)startTime.Milliseconds:D3}";
                var endTimeStr = $"{(int)endTime.TotalHours:D2}{(int)endTime.Minutes:D2}{(int)endTime.Seconds:D2}{(int)endTime.Milliseconds:D3}";
                var outputFileName = $"{inputFileName}_gif_{startTimeStr}_{endTimeStr}.gif";
                var outputPath = Path.Combine(outputSettings.OutputPath, outputFileName);

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowGifCommands(selectedFile, gifParams, outputSettings, outputPath, outputFileName);
                    return;
                }

                // 执行制作GIF
                _gifStartTime = DateTime.Now;
                UpdateStatusBar("正在制作GIF...", "🎞️", "#9C27B0", "制作GIF中");
                OutputInfoTabs.SelectedIndex = 0; // 切换到日志标签页
                LogOutputBox.Text = $"🎞️ 开始制作GIF\r\n";
                LogOutputBox.Text += $"📂 输入文件: {Path.GetFileName(selectedFile.FilePath)}\r\n";
                LogOutputBox.Text += $"📁 输出文件: {outputFileName}\r\n";
                LogOutputBox.Text += $"⏱️ 时间范围: {startTime:hh\\:mm\\:ss\\.fff} - {endTime:hh\\:mm\\:ss\\.fff}\r\n";
                LogOutputBox.Text += $"🎬 FPS: {fps}, 宽度: {width}px\r\n";
                LogOutputBox.Text += $"🎯 质量模式: {GetGifQualityModeName(qualityMode)}\r\n\r\n";

                ExecutionProgressBar.Value = 0;
                ProgressInfoText.Text = "0% | 0.0s | 制作GIF中";

                var result = await _videoProcessingService.CreateGifAsync(
                    selectedFile.FilePath,
                    outputPath,
                    gifParams,
                    (progress) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ExecutionProgressBar.Value = progress;
                            var elapsed = DateTime.Now - _gifStartTime;
                            ProgressInfoText.Text = $"{progress:F1}% | {elapsed.TotalSeconds:F1}s | 制作GIF中";
                        });
                    },
                    CancellationToken.None);

                if (result.Success)
                {
                    ExecutionProgressBar.Value = 100;
                    ProgressInfoText.Text = "100% | 完成";
                    UpdateStatusBar($"成功制作GIF: {Path.GetFileName(outputPath)}", "✅", "#4CAF50");
                    LogOutputBox.Text += $"\r\n✅ GIF制作成功: {Path.GetFileName(outputPath)}\r\n";
                    Services.ToastNotification.ShowSuccess($"成功制作GIF: {Path.GetFileName(outputPath)}");
                }
                else
                {
                    UpdateStatusBar($"制作GIF失败: {result.ErrorMessage}", "❌", "#F44336");
                    LogOutputBox.Text += $"\r\n❌ GIF制作失败: {result.ErrorMessage}\r\n";
                    MessageBox.Show($"制作GIF失败：{result.ErrorMessage}", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnMakeGif_Click 失败: {ex.Message}");
                MessageBox.Show($"制作GIF时发生错误：{ex.Message}", "制作GIF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowGifCommands(
            Models.VideoFile inputFile,
            Models.GifParameters parameters,
            OutputSettings settings,
            string outputPath,
            string outputFileName)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();
            var paletteFileName = $"{Path.GetFileNameWithoutExtension(outputFileName)}_palette.png";
            var palettePath = Path.Combine(settings.OutputPath, paletteFileName);

            try
            {
                var paletteArgs = _videoProcessingService.BuildGifPaletteArguments(
                    inputFile.FilePath,
                    palettePath,
                    parameters);

                commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                {
                    Index = 1,
                    Total = 2,
                    TaskId = "生成调色板",
                    InputPath = inputFile.FilePath,
                    OutputPath = palettePath,
                    CommandArguments = paletteArgs
                });
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"生成GIF调色板命令失败: {ex.Message}");
            }

            try
            {
                var gifArgs = _videoProcessingService.BuildGifArguments(
                    inputFile.FilePath,
                    outputPath,
                    palettePath,
                    parameters);

                commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                {
                    Index = 2,
                    Total = 2,
                    TaskId = "生成GIF",
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    CommandArguments = gifArgs
                });
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"生成GIF命令失败: {ex.Message}");
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg GIF 命令生成器",
                    OperationIcon = "🎞️",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"⏱️ 时间范围: {parameters.StartTime:hh\\:mm\\:ss\\.fff} - {parameters.EndTime:hh\\:mm\\:ss\\.fff}",
                        $"🎬 FPS: {parameters.FPS}, 宽度: {parameters.Width}px",
                        $"🎯 质量模式: {GetGifQualityModeName(parameters.QualityMode)}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };

                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        private DateTime _gifStartTime;

        /// <summary>
        /// 获取GIF质量模式名称
        /// </summary>
        private string GetGifQualityModeName(Models.GifQualityMode mode)
        {
            return mode switch
            {
                Models.GifQualityMode.Low => "低质量",
                Models.GifQualityMode.Medium => "中等质量",
                Models.GifQualityMode.High => "高质量",
                _ => "中等质量"
            };
        }

        /// <summary>
        /// 水平拼接按钮点击事件
        /// </summary>
        private async void BtnHorizontalConcat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的图片文件
                var selectedFiles = _videoListViewModel.Files
                    .Where(f => f.IsSelected && VideoListViewModel.SupportedImageExtensions.Contains(Path.GetExtension(f.FilePath).ToLower()))
                    .ToList();

                if (selectedFiles.Count < 2)
                {
                    MessageBox.Show("请至少选择2张图片进行拼接", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null || string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 执行水平拼接
                await ExecuteImageConcatAsync(selectedFiles, true, outputSettings);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnHorizontalConcat_Click 失败: {ex.Message}");
                MessageBox.Show($"水平拼接时发生错误：{ex.Message}", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 垂直拼接按钮点击事件
        /// </summary>
        private async void BtnVerticalConcat_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取选中的图片文件
                var selectedFiles = _videoListViewModel.Files
                    .Where(f => f.IsSelected && VideoListViewModel.SupportedImageExtensions.Contains(Path.GetExtension(f.FilePath).ToLower()))
                    .ToList();

                if (selectedFiles.Count < 2)
                {
                    MessageBox.Show("请至少选择2张图片进行拼接", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null || string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 执行垂直拼接
                await ExecuteImageConcatAsync(selectedFiles, false, outputSettings);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnVerticalConcat_Click 失败: {ex.Message}");
                MessageBox.Show($"垂直拼接时发生错误：{ex.Message}", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 执行图片拼接
        /// </summary>
        private async Task ExecuteImageConcatAsync(List<Models.VideoFile> imageFiles, bool horizontal, OutputSettings settings)
        {
            try
            {
                UpdateStatusBar($"正在拼接图片...", "🎨", "#9C27B0", "图片拼接中");
                OutputInfoTabs.SelectedIndex = 0;
                LogOutputBox.Text = $"🎨 开始图片拼接\r\n";
                LogOutputBox.Text += $"📂 图片数量: {imageFiles.Count}\r\n";
                LogOutputBox.Text += $"📐 拼接方向: {(horizontal ? "水平" : "垂直")}\r\n";
                LogOutputBox.Text += $"📁 输出路径: {settings.OutputPath}\r\n\r\n";

                ExecutionProgressBar.Value = 0;
                ProgressInfoText.Text = "0% | 0.0s | 图片拼接中";

                var startTime = DateTime.Now;

                // 生成输出文件名
                var outputFileName = $"concat_{DateTime.Now:yyyyMMddHHmmss}.png";
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                // 使用FFmpeg拼接图片
                var result = await _videoProcessingService.ConcatImagesAsync(
                    imageFiles.Select(f => f.FilePath).ToList(),
                    outputPath,
                    horizontal,
                    (progress) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ExecutionProgressBar.Value = progress;
                            var elapsed = DateTime.Now - startTime;
                            ProgressInfoText.Text = $"{progress:F1}% | {elapsed.TotalSeconds:F1}s | 图片拼接中";
                        });
                    },
                    CancellationToken.None);

                if (result.Success)
                {
                    ExecutionProgressBar.Value = 100;
                    ProgressInfoText.Text = "100% | 完成";
                    UpdateStatusBar($"成功拼接图片: {Path.GetFileName(outputPath)}", "✅", "#4CAF50");
                    LogOutputBox.Text += $"\r\n✅ 图片拼接成功: {Path.GetFileName(outputPath)}\r\n";
                    Services.ToastNotification.ShowSuccess($"成功拼接图片: {Path.GetFileName(outputPath)}");
                }
                else
                {
                    UpdateStatusBar($"图片拼接失败: {result.ErrorMessage}", "❌", "#F44336");
                    LogOutputBox.Text += $"\r\n❌ 图片拼接失败: {result.ErrorMessage}\r\n";
                    MessageBox.Show($"图片拼接失败：{result.ErrorMessage}", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ExecuteImageConcatAsync 失败: {ex.Message}");
                MessageBox.Show($"图片拼接时发生错误：{ex.Message}", "图片拼接", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 字幕功能

        private CancellationTokenSource? _subtitleCancellationTokenSource;
        private List<Services.SubtitleItem>? _subtitleItems;
        private bool _isSubtitlePreviewEnabled = false;
        private DispatcherTimer? _subtitlePreviewTimer;
        private bool _hasSavedSubtitleState = false; // 是否保存了字幕预览状态

        /// <summary>
        /// 浏览字幕文件按钮点击事件
        /// </summary>
        private void BtnBrowseSubtitle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Forms.OpenFileDialog
                {
                    Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt|所有文件|*.*",
                    Title = "选择字幕文件"
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    SubtitlePathBox.Text = dialog.FileName;
                    // 加载字幕文件用于预览
                    LoadSubtitleFile(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnBrowseSubtitle_Click 失败: {ex.Message}");
                MessageBox.Show($"选择字幕文件时发生错误：{ex.Message}", "字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 尝试自动加载同目录下的字幕文件
        /// </summary>
        private void TryAutoLoadSubtitleFile(string videoFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(videoFilePath) || !File.Exists(videoFilePath))
                    return;

                var directory = Path.GetDirectoryName(videoFilePath);
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(videoFilePath);
                
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileNameWithoutExtension))
                    return;

                // 尝试匹配的字幕文件扩展名
                var subtitleExtensions = new[] { ".srt", ".ass", ".ssa", ".vtt" };
                
                foreach (var ext in subtitleExtensions)
                {
                    var subtitlePath = Path.Combine(directory, fileNameWithoutExtension + ext);
                    if (File.Exists(subtitlePath))
                    {
                        // 找到匹配的字幕文件，自动加载
                        Services.DebugLogger.LogInfo($"自动匹配字幕文件: {subtitlePath}");
                        
                        // 更新字幕路径文本框（如果存在）
                        if (SubtitlePathBox != null)
                        {
                            SubtitlePathBox.Text = subtitlePath;
                        }
                        
                        // 加载字幕文件
                        LoadSubtitleFile(subtitlePath);
                        
                        // 字幕文件加载后会自动启用预览（在LoadSubtitleFile中处理）
                        
                        return; // 找到第一个匹配的就返回
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"TryAutoLoadSubtitleFile 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载字幕文件
        /// </summary>
        private void LoadSubtitleFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    _subtitleItems = null;
                    // 隐藏预览层
                    HideSubtitlePreviewPopup();
                    StopSubtitlePreviewTimer();
                    return;
                }

                // 使用新的统一解析方法，支持SRT/ASS/SSA/VTT格式
                _subtitleItems = Services.SubtitleParser.ParseSubtitleFile(filePath);
                
                if (_subtitleItems != null && _subtitleItems.Count > 0)
                {
                    Services.DebugLogger.LogInfo($"已加载字幕文件: {filePath}, 共 {_subtitleItems.Count} 条字幕");
                    
                    // 自动启用预览
                    _isSubtitlePreviewEnabled = true;
                    Services.DebugLogger.LogInfo($"字幕预览已启用，共 {_subtitleItems.Count} 条字幕");
                    
                    // 确保在UI线程上更新
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        // 如果显示开关已启用，则显示字幕预览
                        if (chkShowSubtitlePreview != null && chkShowSubtitlePreview.IsChecked == true)
                        {
                            ShowSubtitlePreviewPopup();
                        }
                    });
                    
                    StartSubtitlePreviewTimer();
                    UpdateSubtitlePreview();
                }
                else
                {
                    Services.DebugLogger.LogWarning($"字幕文件加载失败或字幕列表为空: {filePath}");
                    // 隐藏预览层
                    HideSubtitlePreviewPopup();
                    StopSubtitlePreviewTimer();
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"加载字幕文件失败: {ex.Message}");
                _subtitleItems = null;
                // 隐藏预览层
                HideSubtitlePreviewPopup();
                StopSubtitlePreviewTimer();
            }
        }

        /// <summary>
        /// 显示字幕预览开关Checked事件
        /// </summary>
        private void ChkShowSubtitlePreview_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isSubtitlePreviewEnabled && _subtitleItems != null && _subtitleItems.Count > 0)
                {
                    ShowSubtitlePreviewPopup();
                }
                else
                {
                    MessageBox.Show("请先选择并加载字幕文件", "字幕预览", MessageBoxButton.OK, MessageBoxImage.Information);
                    if (chkShowSubtitlePreview != null)
                    {
                        chkShowSubtitlePreview.IsChecked = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ChkShowSubtitlePreview_Checked 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示字幕预览开关Unchecked事件
        /// </summary>
        private void ChkShowSubtitlePreview_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                HideSubtitlePreviewPopup();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ChkShowSubtitlePreview_Unchecked 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 字幕颜色选择器按钮点击事件
        /// </summary>
        private void BtnSubtitleColorPicker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new System.Windows.Forms.ColorDialog
                {
                    Color = System.Drawing.Color.White,
                    AllowFullOpen = true,
                    FullOpen = true
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var color = dialog.Color;
                    // 转换为十六进制格式
                    var hexColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    txtSubtitleColor.Text = hexColor;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnSubtitleColorPicker_Click 失败: {ex.Message}");
                MessageBox.Show($"选择颜色时发生错误：{ex.Message}", "字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 应用字幕设置按钮点击事件
        /// </summary>
        private async void BtnApplySubtitle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证字幕文件
                if (string.IsNullOrWhiteSpace(SubtitlePathBox.Text) || !File.Exists(SubtitlePathBox.Text))
                {
                    MessageBox.Show("请先选择字幕文件", "字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files
                    .Where(f => f.IsSelected && VideoListViewModel.SupportedVideoExtensions.Contains(Path.GetExtension(f.FilePath).ToLower()))
                    .ToList();

                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (outputSettings == null || string.IsNullOrWhiteSpace(outputSettings.OutputPath))
                {
                    MessageBox.Show("请先设置输出路径", "字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取字幕参数
                var subtitleParams = GetSubtitleParameters();

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowSubtitleCommands(selectedFiles, subtitleParams, outputSettings);
                    return;
                }

                // 执行批量处理
                _subtitleCancellationTokenSource = new CancellationTokenSource();
                await ExecuteSubtitleBatchAsync(selectedFiles, subtitleParams, outputSettings, _subtitleCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnApplySubtitle_Click 失败: {ex.Message}");
                MessageBox.Show($"应用字幕时发生错误：{ex.Message}", "字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region 批量AI字幕

        /// <summary>
        /// 批量字幕进度更新事件
        /// </summary>
        private void BatchSubtitleCoordinator_ProgressUpdated(object? sender, Services.AiSubtitle.BatchSubtitleProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(BatchSubtitleCoordinator));
            });
        }

        /// <summary>
        /// 批量字幕完成事件
        /// </summary>
        private void BatchSubtitleCoordinator_BatchCompleted(object? sender, Services.AiSubtitle.BatchSubtitleCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(BatchSubtitleCoordinator));
                Services.ToastNotification.ShowSuccess(
                    $"批量字幕生成完成：成功 {e.CompletedCount}，失败 {e.FailedCount}，总计 {e.TotalCount}");
            });
        }

        /// <summary>
        /// 从播放列表添加批量字幕任务
        /// </summary>
        private void BtnAddBatchSubtitleFromPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_batchSubtitleCoordinator == null)
            {
                MessageBox.Show("批量字幕协调器未初始化", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
            if (selectedFiles.Count == 0)
            {
                MessageBox.Show("请先在播放列表中选择要生成字幕的文件", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 显示ASR提供商选择对话框
            var provider = PromptBatchAsrProvider();
            if (provider == null)
            {
                return;
            }

            _batchSubtitleCoordinator.AddSelectedFilesFromPlaylist(provider.Value);
            OnPropertyChanged(nameof(BatchSubtitleCoordinator));
            Services.ToastNotification.ShowInfo($"已添加 {selectedFiles.Count} 个文件到批量字幕队列");
        }

        /// <summary>
        /// 从剪辑区域添加批量字幕任务
        /// </summary>
        private void BtnAddBatchSubtitleFromClips_Click(object sender, RoutedEventArgs e)
        {
            if (_batchSubtitleCoordinator == null)
            {
                MessageBox.Show("批量字幕协调器未初始化", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedClips = _clipManager.Clips.Where(c => c.IsSelected).ToList();
            if (selectedClips.Count == 0)
            {
                MessageBox.Show("请先在剪辑区域中选择要生成字幕的片段", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 显示ASR提供商选择对话框
            var provider = PromptBatchAsrProvider();
            if (provider == null)
            {
                return;
            }

            _batchSubtitleCoordinator.AddSelectedClipsFromClipArea(provider.Value);
            OnPropertyChanged(nameof(BatchSubtitleCoordinator));
            Services.ToastNotification.ShowInfo($"已添加 {selectedClips.Count} 个片段到批量字幕队列");
        }

        /// <summary>
        /// 提示用户选择ASR提供商
        /// </summary>
        private Services.AiSubtitle.AsrProvider? PromptBatchAsrProvider()
        {
            var dialog = new Window
            {
                Title = "选择ASR提供商",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var stackPanel = new StackPanel { Margin = new Thickness(20) };
            
            var titleText = new TextBlock
            {
                Text = "请选择用于生成字幕的ASR提供商：",
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 20)
            };
            stackPanel.Children.Add(titleText);

            Services.AiSubtitle.AsrProvider? selectedProvider = null;

            var providers = new[]
            {
                (Services.AiSubtitle.AsrProvider.Bcut, "🅱 B接口 (BcutASR)"),
                (Services.AiSubtitle.AsrProvider.JianYing, "🅹 J接口 (JianYingASR)"),
                (Services.AiSubtitle.AsrProvider.FasterWhisperCpu, "⚡ Faster Whisper (CPU)"),
                (Services.AiSubtitle.AsrProvider.FasterWhisperGpu, "⚡ Faster Whisper (GPU)")
            };

            foreach (var (provider, displayName) in providers)
            {
                var button = new Button
                {
                    Content = displayName,
                    Height = 40,
                    Margin = new Thickness(0, 0, 0, 10),
                    FontSize = 13
                };
                button.Click += (s, e) =>
                {
                    selectedProvider = provider;
                    dialog.DialogResult = true;
                    dialog.Close();
                };
                stackPanel.Children.Add(button);
            }

            var cancelButton = new Button
            {
                Content = "取消",
                Height = 35,
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 13
            };
            cancelButton.Click += (s, e) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };
            stackPanel.Children.Add(cancelButton);

            dialog.Content = stackPanel;
            if (dialog.ShowDialog() == true)
            {
                return selectedProvider;
            }
            return null;
        }

        /// <summary>
        /// 开始批量字幕生成
        /// </summary>
        private async void BtnStartBatchSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (_batchSubtitleCoordinator == null)
            {
                MessageBox.Show("批量字幕协调器未初始化", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_batchSubtitleCoordinator.Tasks.Count == 0)
            {
                MessageBox.Show("队列中没有待处理的任务", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await _batchSubtitleCoordinator.StartBatchProcessingAsync();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"批量字幕生成失败: {ex.Message}");
                MessageBox.Show($"批量字幕生成失败：{ex.Message}", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 取消批量字幕生成
        /// </summary>
        private void BtnCancelBatchSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (_batchSubtitleCoordinator == null) return;
            _batchSubtitleCoordinator.CancelBatchProcessing();
            Services.ToastNotification.ShowInfo("已取消批量字幕生成");
        }

        /// <summary>
        /// 清空批量字幕队列
        /// </summary>
        private void BtnClearBatchSubtitleTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_batchSubtitleCoordinator == null) return;
            
            if (_batchSubtitleCoordinator.Tasks.Count == 0)
            {
                MessageBox.Show("队列已为空", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要清空队列中的所有 {_batchSubtitleCoordinator.Tasks.Count} 个任务吗？",
                "清空队列",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    _batchSubtitleCoordinator.ClearAllTasks();
                    OnPropertyChanged(nameof(BatchSubtitleCoordinator));
                    Services.ToastNotification.ShowInfo("已清空批量字幕队列");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"清空队列失败：{ex.Message}", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 移除单个批量字幕任务
        /// </summary>
        private void RemoveBatchSubtitleTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Services.AiSubtitle.BatchSubtitleTask task)
            {
                return;
            }

            if (_batchSubtitleCoordinator == null) return;

            try
            {
                _batchSubtitleCoordinator.RemoveTask(task);
                OnPropertyChanged(nameof(BatchSubtitleCoordinator));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移除任务失败：{ex.Message}", "批量AI字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        /// <summary>
        /// 获取字幕参数
        /// </summary>
        private Models.SubtitleParameters GetSubtitleParameters(bool showWarnings = true)
        {
            var parameters = new Models.SubtitleParameters
            {
                SubtitleFilePath = SubtitlePathBox.Text
            };

            // 字体
            if (cboSubtitleFont.SelectedItem is ComboBoxItem fontItem)
            {
                parameters.FontFamily = fontItem.Content.ToString() ?? "微软雅黑";
            }

            // 字号（验证范围：1-200）
            if (int.TryParse(txtSubtitleSize.Text, out var fontSize))
            {
                if (fontSize < 1)
                {
                    if (showWarnings)
                {
                    MessageBox.Show("字号不能小于1，已自动设置为1", "参数验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtSubtitleSize.Text = "1";
                    }
                    parameters.FontSize = 1;
                }
                else if (fontSize > 200)
                {
                    if (showWarnings)
                {
                    MessageBox.Show("字号不能大于200，已自动设置为200", "参数验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtSubtitleSize.Text = "200";
                    }
                    parameters.FontSize = 200;
                }
                else
                {
                    parameters.FontSize = fontSize;
                }
            }
            else
            {
                if (showWarnings)
            {
                MessageBox.Show("字号格式无效，已使用默认值24", "参数验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtSubtitleSize.Text = "24";
                }
                parameters.FontSize = 24;
            }

            // 颜色（验证格式）
            var colorText = txtSubtitleColor.Text.Trim();
            if (string.IsNullOrWhiteSpace(colorText))
            {
                colorText = "white";
                if (showWarnings)
                {
                txtSubtitleColor.Text = "white";
                }
            }
            
            // 验证颜色格式：支持颜色名称（white, black等）或十六进制（#FFFFFF）
            var isValidColor = false;
            if (colorText.StartsWith("#") && colorText.Length == 7)
            {
                // 验证十六进制格式
                isValidColor = System.Text.RegularExpressions.Regex.IsMatch(colorText, @"^#[0-9A-Fa-f]{6}$");
            }
            else
            {
                // 验证颜色名称（常见颜色）
                var validColorNames = new[] { "white", "black", "red", "green", "blue", "yellow", "cyan", "magenta" };
                isValidColor = Array.Exists(validColorNames, name => string.Equals(name, colorText, StringComparison.OrdinalIgnoreCase));
            }
            
            if (!isValidColor)
            {
                if (showWarnings)
            {
                MessageBox.Show($"颜色格式无效（支持颜色名称如white/black或十六进制如#FFFFFF），已使用默认值white", "参数验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtSubtitleColor.Text = "white";
                }
                colorText = "white";
            }
            
            parameters.FontColor = colorText;

            // 位置
            if (cboSubtitlePosition.SelectedIndex >= 0)
            {
                parameters.Position = cboSubtitlePosition.SelectedIndex switch
                {
                    0 => Models.SubtitlePosition.Top,
                    1 => Models.SubtitlePosition.Center,
                    2 => Models.SubtitlePosition.Bottom,
                    _ => Models.SubtitlePosition.Bottom
                };
            }

            // 描边宽度
            parameters.OutlineWidth = sliderSubtitleOutline.Value;

            // 阴影
            parameters.EnableShadow = chkSubtitleShadow.IsChecked ?? true;

            // 时间偏移
            parameters.TimeOffset = sliderSubtitleOffset.Value;

            return parameters;
        }

        /// <summary>
        /// 执行字幕批量处理
        /// </summary>
        private async Task ExecuteSubtitleBatchAsync(
            List<Models.VideoFile> videoFiles,
            Models.SubtitleParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            try
            {
                UpdateStatusBar($"正在应用字幕...", "🔤", "#4CAF50", "字幕处理中");
                OutputInfoTabs.SelectedIndex = 0;
                LogOutputBox.Text = $"🔤 开始应用字幕\r\n";
                LogOutputBox.Text += $"📂 视频数量: {videoFiles.Count}\r\n";
                LogOutputBox.Text += $"📄 字幕文件: {Path.GetFileName(parameters.SubtitleFilePath)}\r\n";
                LogOutputBox.Text += $"🎨 字体: {parameters.FontFamily}, 字号: {parameters.FontSize}, 颜色: {parameters.FontColor}\r\n";
                LogOutputBox.Text += $"📍 位置: {parameters.Position}, 描边: {parameters.OutlineWidth}px, 阴影: {(parameters.EnableShadow ? "是" : "否")}\r\n";
                if (Math.Abs(parameters.TimeOffset) > 0.01)
                {
                    LogOutputBox.Text += $"⏱️ 时间偏移: {parameters.TimeOffset:F1}秒\r\n";
                }
                LogOutputBox.Text += $"📁 输出路径: {settings.OutputPath}\r\n\r\n";

                ExecutionProgressBar.Value = 0;
                ProgressInfoText.Text = "0% | 0.0s | 字幕处理中";

                var startTime = DateTime.Now;
                var successCount = 0;
                var failedCount = 0;
                var failedFiles = new List<(string file, string error)>();

                // 逐个处理视频文件
                for (int index = 0; index < videoFiles.Count; index++)
                {
                    var file = videoFiles[index];
                    try
                    {
                        var outputFileName = GenerateSubtitleOutputFileName(file.FilePath, settings);
                        var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                        var result = await _videoProcessingService.ApplySubtitleAsync(
                            file.FilePath,
                            outputPath,
                            parameters,
                            (progress) =>
                            {
                                var totalProgress = (index * 100.0 + progress) / videoFiles.Count;
                                Dispatcher.Invoke(() =>
                                {
                                    ExecutionProgressBar.Value = totalProgress;
                                    var elapsed = DateTime.Now - startTime;
                                    ProgressInfoText.Text = $"{totalProgress:F1}% | {elapsed.TotalSeconds:F1}s | 处理: {Path.GetFileName(file.FilePath)}";
                                });
                            },
                            cancellationToken);

                        if (result.Success)
                        {
                            successCount++;
                            LogOutputBox.Text += $"✅ {Path.GetFileName(file.FilePath)} -> {Path.GetFileName(outputPath)}\r\n";
                        }
                        else
                        {
                            failedCount++;
                            failedFiles.Add((file.FilePath, result.ErrorMessage));
                            LogOutputBox.Text += $"❌ {Path.GetFileName(file.FilePath)}: {result.ErrorMessage}\r\n";
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        failedFiles.Add((file.FilePath, ex.Message));
                        LogOutputBox.Text += $"❌ {Path.GetFileName(file.FilePath)}: {ex.Message}\r\n";
                    }
                }

                // 显示结果
                ExecutionProgressBar.Value = 100;
                ProgressInfoText.Text = "100% | 完成";

                if (successCount > 0)
                {
                    UpdateStatusBar($"成功处理 {successCount} 个视频", "✅", "#4CAF50");
                    LogOutputBox.Text += $"\r\n✅ 成功处理 {successCount} 个视频\r\n";
                    Services.ToastNotification.ShowSuccess($"成功处理 {successCount} 个视频");
                }

                if (failedCount > 0)
                {
                    LogOutputBox.Text += $"\r\n❌ 失败 {failedCount} 个视频:\r\n";
                    foreach (var (file, error) in failedFiles)
                    {
                        LogOutputBox.Text += $"  - {Path.GetFileName(file)}: {error}\r\n";
                    }
                    MessageBox.Show($"有 {failedCount} 个视频处理失败，请查看日志", "字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ExecuteSubtitleBatchAsync 失败: {ex.Message}");
                MessageBox.Show($"应用字幕时发生错误：{ex.Message}", "字幕", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowSubtitleCommands(
            List<Models.VideoFile> selectedFiles,
            Models.SubtitleParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var file = selectedFiles[i];
                var outputFileName = GenerateSubtitleOutputFileName(file.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);
                var width = file.Width > 0 ? file.Width : 1920;
                var height = file.Height > 0 ? file.Height : 1080;

                try
                {
                    var args = _videoProcessingService.BuildSubtitleArguments(
                        file.FilePath,
                        outputPath,
                        parameters,
                        out var _,
                        width,
                        height);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(file.FilePath),
                        InputPath = file.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = args
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成字幕命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 字幕命令生成器",
                    OperationIcon = "🔤",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🖋️ 字体: {parameters.FontFamily} ({parameters.FontSize}px)",
                        $"🎨 颜色: {parameters.FontColor}, 描边: {parameters.OutlineWidth}px",
                        $"📍 位置: {parameters.Position}, 阴影: {(parameters.EnableShadow ? "开启" : "关闭")}",
                        $"⏱️ 时间偏移: {parameters.TimeOffset:F2}s"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };

                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 生成字幕输出文件名
        /// </summary>
        private string GenerateSubtitleOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var outputExtension = GetFileExtension(settings.OutputFormat);
            return $"{inputFileName}_subtitle{outputExtension}";
        }


        /// <summary>
        /// 更新字幕预览显示
        /// </summary>
        private void UpdateSubtitlePreview()
        {
            try
            {
                // 确保在UI线程上执行
                if (Application.Current?.Dispatcher?.CheckAccess() == false)
                {
                    Application.Current.Dispatcher.Invoke(() => UpdateSubtitlePreview());
                    return;
                }
                
                if (!_isSubtitlePreviewEnabled)
                {
                    if (SubtitlePreviewText != null)
                    {
                        SubtitlePreviewText.Text = "";
                    }
                    return;
                }
                
                if (SubtitlePreviewText == null)
                {
                    Services.DebugLogger.LogError($"SubtitlePreviewText 为 null");
                    return;
                }
                
                if (_subtitleItems == null || _subtitleItems.Count == 0)
                {
                    SubtitlePreviewText.Text = "";
                    return;
                }

                // 获取当前播放时间
                var currentTimeMs = _videoPlayerViewModel.CurrentPosition;
                var currentTime = TimeSpan.FromMilliseconds(currentTimeMs);
                
                // 查找当前时间对应的字幕
                var currentSubtitle = Services.SubtitleParser.GetSubtitleAtTime(_subtitleItems, currentTime);
                
                if (currentSubtitle != null)
                {
                    // 更新字幕文本
                    SubtitlePreviewText.Text = currentSubtitle.Text;
                }
                else
                {
                    // 没有字幕时隐藏文本
                    SubtitlePreviewText.Text = "";
                }
                
                // 无论是否有字幕，都要应用样式（确保样式实时更新）
                ApplySubtitleStyleToPreview();
                
                // 更新字幕布局以匹配视频显示区域
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                UpdateSubtitlePreviewLayout(videoDisplayRect);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateSubtitlePreview 失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 应用字幕样式到预览层
        /// </summary>
        private void ApplySubtitleStyleToPreview()
        {
            try
            {
                if (SubtitlePreviewText == null) return;

                // 字体
                if (cboSubtitleFont.SelectedItem is ComboBoxItem fontItem)
                {
                    var fontName = fontItem.Content.ToString() ?? "微软雅黑";
                    SubtitlePreviewText.FontFamily = new System.Windows.Media.FontFamily(fontName);
                }

                // 字号 - 根据视频显示区域动态调整（在UpdateSubtitlePreviewLayout中处理）
                // 这里只保存基础字号，实际字号会根据视频尺寸在UpdateSubtitlePreviewLayout中计算

                // 颜色
                try
                {
                    var colorText = txtSubtitleColor.Text.Trim();
                    if (colorText.StartsWith("#"))
                    {
                        SubtitlePreviewText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText));
                    }
                    else
                    {
                        // 颜色名称映射
                        var colorMap = new Dictionary<string, System.Windows.Media.Color>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "white", System.Windows.Media.Colors.White },
                            { "black", System.Windows.Media.Colors.Black },
                            { "red", System.Windows.Media.Colors.Red },
                            { "green", System.Windows.Media.Colors.Green },
                            { "blue", System.Windows.Media.Colors.Blue },
                            { "yellow", System.Windows.Media.Colors.Yellow },
                            { "cyan", System.Windows.Media.Colors.Cyan },
                            { "magenta", System.Windows.Media.Colors.Magenta }
                        };
                        
                        if (colorMap.TryGetValue(colorText, out var color))
                        {
                            SubtitlePreviewText.Foreground = new SolidColorBrush(color);
                        }
                        else
                        {
                            SubtitlePreviewText.Foreground = new SolidColorBrush(System.Windows.Media.Colors.White);
                        }
                    }
                }
                catch
                {
                    SubtitlePreviewText.Foreground = new SolidColorBrush(System.Windows.Media.Colors.White);
                }

                // 位置
                if (cboSubtitlePosition.SelectedIndex >= 0)
                {
                    SubtitlePreviewText.VerticalAlignment = cboSubtitlePosition.SelectedIndex switch
                    {
                        0 => VerticalAlignment.Top,      // 顶部
                        1 => VerticalAlignment.Center,   // 居中
                        2 => VerticalAlignment.Bottom,   // 底部
                        _ => VerticalAlignment.Bottom
                    };
                }

                // 描边（使用DropShadowEffect模拟描边效果）
                // 描边宽度也需要根据视频分辨率和显示区域缩放（与字体大小保持一致）
                var baseOutlineWidth = sliderSubtitleOutline.Value;
                var scaledOutlineWidth = baseOutlineWidth;
                
                // 主窗体显示缩放因子（与字体大小保持一致）
                const double displayScaleFactor = 2.125;
                
                // 获取视频信息以计算缩放
                var videoHeight = _videoPlayerViewModel.VideoHeight;
                var videoWidth = _videoPlayerViewModel.VideoWidth;
                if (videoHeight > 0 && videoWidth > 0)
                {
                    var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                    var baseVideoHeight = 1080.0;
                    
                    // 第一步：按视频原始高度缩放（与FFmpeg保持一致）
                    var ffmpegOutlineWidth = baseOutlineWidth * (videoHeight / baseVideoHeight);
                    
                    // 第二步：按显示区域高度缩放（预览时视频被缩放显示）
                    var displayScaleY = videoDisplayRect.Height / videoHeight;
                    
                    // 第三步：应用主窗体显示缩放因子（2.125倍）
                    scaledOutlineWidth = ffmpegOutlineWidth * displayScaleY * displayScaleFactor;
                    
                    // 限制描边宽度范围（最小0.5，最大21.25，因为乘以2.125后可能较大）
                    scaledOutlineWidth = Math.Max(0.5, Math.Min(21.25, scaledOutlineWidth));
                }
                else
                {
                    // 没有视频信息，使用基础描边宽度 * 显示缩放因子
                    scaledOutlineWidth = baseOutlineWidth * displayScaleFactor;
                }
                
                // 描边和阴影效果处理
                // 描边和阴影都使用 DropShadowEffect，但需要根据复选框状态调整效果
                var hasOutline = scaledOutlineWidth > 0;
                var hasShadow = chkSubtitleShadow.IsChecked == true;
                
                if (hasOutline || hasShadow)
                {
                    // 创建或更新 DropShadowEffect
                    DropShadowEffect? shadowEffect = null;
                    
                    if (SubtitlePreviewText.Effect is DropShadowEffect existingShadow)
                    {
                        shadowEffect = existingShadow;
                    }
                    else
                    {
                        shadowEffect = new DropShadowEffect
                        {
                            Color = System.Windows.Media.Colors.Black,
                            Direction = 315,
                            Opacity = 0.8
                        };
                        SubtitlePreviewText.Effect = shadowEffect;
                    }
                    
                    if (shadowEffect != null)
                    {
                        if (hasOutline && hasShadow)
                        {
                            // 既有描边又有阴影：使用较大的偏移和模糊来同时显示描边和阴影
                            shadowEffect.ShadowDepth = scaledOutlineWidth * 1.5; // 阴影偏移稍大
                            shadowEffect.BlurRadius = Math.Max(2, scaledOutlineWidth * 3); // 模糊半径更大以显示阴影
                        }
                        else if (hasOutline)
                        {
                            // 只有描边：使用较小的偏移和较大的模糊来模拟描边效果
                            shadowEffect.ShadowDepth = scaledOutlineWidth * 0.5; // 较小的偏移
                            shadowEffect.BlurRadius = Math.Max(2, scaledOutlineWidth * 2); // 模糊半径用于描边
                        }
                        else if (hasShadow)
                        {
                            // 只有阴影：使用较大的偏移和模糊来显示阴影效果
                            shadowEffect.ShadowDepth = 3; // 默认阴影偏移
                            shadowEffect.BlurRadius = 5; // 默认阴影模糊
                        }
                    }
                }
                else
                {
                    // 既没有描边也没有阴影，移除效果
                    SubtitlePreviewText.Effect = null;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ApplySubtitleStyleToPreview 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示字幕预览Popup
        /// </summary>
        private void ShowSubtitlePreviewPopup()
        {
            try
            {
                if (SubtitlePreviewPopup == null || SubtitlePreviewLayer == null)
                {
                    Services.DebugLogger.LogError($"字幕预览Popup或Layer为null");
                    return;
                }

                // 检查开关状态
                if (chkShowSubtitlePreview == null || chkShowSubtitlePreview.IsChecked != true)
                {
                    return;
                }

                // 获取视频播放器容器的屏幕坐标
                var videoContainer = VideoPlayerContainer;
                if (videoContainer == null || !videoContainer.IsVisible)
                {
                    Services.DebugLogger.LogWarning($"VideoPlayerContainer不可见，无法显示字幕预览");
                    return;
                }

                var containerTopLeft = videoContainer.PointToScreen(new Point(0, 0));
                
                // 获取视频在容器中的实际显示区域（考虑黑边）
                var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                
                // 计算视频显示区域的屏幕坐标
                // 注意：videoDisplayRect 是相对于容器(1920x1080)的逻辑坐标
                // 需要转换为屏幕坐标
                var containerActualWidth = videoContainer.ActualWidth;
                var containerActualHeight = videoContainer.ActualHeight;
                var scaleX = containerActualWidth / 1920.0;
                var scaleY = containerActualHeight / 1080.0;
                
                // 视频显示区域的屏幕坐标
                var videoScreenX = containerTopLeft.X + videoDisplayRect.X * scaleX;
                var videoScreenY = containerTopLeft.Y + videoDisplayRect.Y * scaleY;
                var videoScreenWidth = videoDisplayRect.Width * scaleX;
                var videoScreenHeight = videoDisplayRect.Height * scaleY;
                
                // 设置Popup位置和大小 - 使用容器的完整尺寸（1920x1080逻辑尺寸）
                SubtitlePreviewPopup.HorizontalOffset = containerTopLeft.X;
                SubtitlePreviewPopup.VerticalOffset = containerTopLeft.Y;
                SubtitlePreviewLayer.Width = 1920;  // 使用逻辑尺寸
                SubtitlePreviewLayer.Height = 1080; // 使用逻辑尺寸
                
                // 更新字幕文本的位置和大小，使其相对于视频显示区域
                UpdateSubtitlePreviewLayout(videoDisplayRect);

                // 显示Popup
                SubtitlePreviewPopup.IsOpen = true;
                _hasSavedSubtitleState = true;

                Services.DebugLogger.LogInfo($"字幕预览Popup已显示: 容器位置=({containerTopLeft.X:F0}, {containerTopLeft.Y:F0}), 视频显示区域=({videoDisplayRect.X:F0}, {videoDisplayRect.Y:F0}, {videoDisplayRect.Width:F0}x{videoDisplayRect.Height:F0})");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"ShowSubtitlePreviewPopup 失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 更新字幕预览的布局，使其匹配视频显示区域
        /// </summary>
        private void UpdateSubtitlePreviewLayout(Rect videoDisplayRect)
        {
            try
            {
                if (SubtitlePreviewText == null) return;
                
                // 获取视频原始尺寸
                var videoWidth = _videoPlayerViewModel.VideoWidth;
                var videoHeight = _videoPlayerViewModel.VideoHeight;
                
                if (videoWidth <= 0 || videoHeight <= 0)
                {
                    // 没有视频信息，使用默认布局
                    SubtitlePreviewText.HorizontalAlignment = HorizontalAlignment.Center;
                    SubtitlePreviewText.VerticalAlignment = VerticalAlignment.Bottom;
                    SubtitlePreviewText.Margin = new Thickness(0, 0, 0, 80);
                    SubtitlePreviewText.MaxWidth = 1600;
                    return;
                }
                
                // 计算视频显示区域的缩放比例（相对于原始视频尺寸）
                var scaleX = videoDisplayRect.Width / videoWidth;
                var scaleY = videoDisplayRect.Height / videoHeight;
                var scale = Math.Min(scaleX, scaleY); // 使用较小的缩放比例，确保字幕不会超出
                
                // 根据视频显示区域调整字幕位置
                // 字幕应该显示在视频显示区域的底部，而不是容器底部
                var videoBottom = videoDisplayRect.Y + videoDisplayRect.Height;
                var marginBottom = 1080 - videoBottom + 80; // 从视频底部向上80像素
                
                SubtitlePreviewText.Margin = new Thickness(
                    videoDisplayRect.X, // 左边距：视频显示区域的X偏移
                    0,
                    videoDisplayRect.X, // 右边距：保持对称
                    marginBottom
                );
                
                // 根据视频显示区域调整字幕最大宽度
                SubtitlePreviewText.MaxWidth = videoDisplayRect.Width - 40; // 留40像素边距
                
                // 根据视频原始分辨率和显示区域调整字体大小
                // 预览字体大小需要考虑三个缩放因子：
                // 1. 视频原始高度相对于1080p的缩放（与FFmpeg保持一致）
                // 2. 显示区域高度相对于1080p的缩放（预览时视频被缩放显示）
                // 3. 主窗体显示缩放因子（由于主窗体字体较小，需要放大2.125倍才能看清）
                // 
                // FFmpeg: 字体大小 = 基础字体 * (视频原始高度 / 1080)
                // 预览: 字体大小 = FFmpeg字体大小 * (显示区域高度 / 视频原始高度) * 显示缩放因子
                //      = 基础字体 * (显示区域高度 / 1080) * 显示缩放因子
                if (int.TryParse(txtSubtitleSize?.Text, out var baseFontSize) && baseFontSize > 0)
                {
                    // 获取视频原始分辨率
                    var originalVideoHeight = _videoPlayerViewModel.VideoHeight;
                    var originalVideoWidth = _videoPlayerViewModel.VideoWidth;
                    
                    // 主窗体显示缩放因子（由于主窗体字体较小，需要放大才能看清）
                    const double displayScaleFactor = 2.125;
                    
                    if (originalVideoHeight > 0 && originalVideoWidth > 0)
                    {
                        // 基准：1920x1080的视频，字体大小使用用户设置的值
                        var baseVideoHeight = 1080.0;
                        
                        // 第一步：按视频原始高度缩放（与FFmpeg保持一致）
                        var ffmpegFontSize = baseFontSize * (originalVideoHeight / baseVideoHeight);
                        
                        // 第二步：按显示区域高度缩放（预览时视频被缩放显示）
                        // 显示区域高度相对于原始视频高度的缩放比例
                        var displayScaleY = videoDisplayRect.Height / originalVideoHeight;
                        
                        // 第三步：应用主窗体显示缩放因子（2.125倍）
                        // 预览字体大小 = FFmpeg字体大小 * 显示缩放比例 * 显示缩放因子
                        // 这样预览中的字体视觉大小会与FFmpeg生成视频中的字体视觉大小一致
                        var previewFontSize = ffmpegFontSize * displayScaleY * displayScaleFactor;
                        
                        // 限制字体大小范围（最小12，最大153，因为乘以2.125后可能较大）
                        previewFontSize = Math.Max(12, Math.Min(153, previewFontSize));
                        SubtitlePreviewText.FontSize = previewFontSize;
                        
                        // Services.DebugLogger.LogInfo($"字幕预览字体大小计算: 基础={baseFontSize}, 原始高度={originalVideoHeight}, FFmpeg字体={ffmpegFontSize:F1}, 显示缩放={displayScaleY:F3}, 显示缩放因子={displayScaleFactor}, 预览字体={previewFontSize:F1}");
                    }
                    else
                    {
                        // 没有视频信息，使用基础字体大小 * 显示缩放因子
                        SubtitlePreviewText.FontSize = baseFontSize * displayScaleFactor;
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateSubtitlePreviewLayout 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏字幕预览Popup
        /// </summary>
        private void HideSubtitlePreviewPopup()
        {
            try
            {
                if (SubtitlePreviewPopup != null)
                {
                    SubtitlePreviewPopup.IsOpen = false;
                    _hasSavedSubtitleState = false;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"HideSubtitlePreviewPopup 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新字幕预览Popup位置（当窗口大小或位置改变时调用）
        /// </summary>
        private void UpdateSubtitlePreviewPopupPosition()
        {
            try
            {
                if (SubtitlePreviewPopup != null && SubtitlePreviewPopup.IsOpen)
                {
                    var videoContainer = VideoPlayerContainer;
                    if (videoContainer != null && videoContainer.IsVisible)
                    {
                        var topLeft = videoContainer.PointToScreen(new Point(0, 0));
                        SubtitlePreviewPopup.HorizontalOffset = topLeft.X;
                        SubtitlePreviewPopup.VerticalOffset = topLeft.Y;
                        
                        // 同时更新字幕布局以匹配视频显示区域
                        var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                        UpdateSubtitlePreviewLayout(videoDisplayRect);
                    }
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"UpdateSubtitlePreviewPopupPosition 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动字幕预览定时器
        /// </summary>
        private void StartSubtitlePreviewTimer()
        {
            try
            {
                StopSubtitlePreviewTimer();
                
                _subtitlePreviewTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100) // 每100ms更新一次，确保字幕实时显示
                };
                _subtitlePreviewTimer.Tick += (s, e) => UpdateSubtitlePreview();
                _subtitlePreviewTimer.Start();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"StartSubtitlePreviewTimer 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止字幕预览定时器
        /// </summary>
        private void StopSubtitlePreviewTimer()
        {
            try
            {
                if (_subtitlePreviewTimer != null)
                {
                    _subtitlePreviewTimer.Stop();
                    _subtitlePreviewTimer = null;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"StopSubtitlePreviewTimer 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 监听字幕样式参数变化，实时更新预览
        /// </summary>
        private void SetupSubtitleStyleListeners()
        {
            try
            {
                // 字体变化
                if (cboSubtitleFont != null)
                {
                    cboSubtitleFont.SelectionChanged += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }

                // 字号变化
                if (txtSubtitleSize != null)
                {
                    txtSubtitleSize.TextChanged += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 更新布局以匹配视频显示区域（字号变化会影响布局）
                            var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                            UpdateSubtitlePreviewLayout(videoDisplayRect);
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }

                // 颜色变化
                if (txtSubtitleColor != null)
                {
                    txtSubtitleColor.TextChanged += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }

                // 位置变化
                if (cboSubtitlePosition != null)
                {
                    cboSubtitlePosition.SelectionChanged += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }

                // 描边变化
                if (sliderSubtitleOutline != null)
                {
                    sliderSubtitleOutline.ValueChanged += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 更新布局以匹配视频显示区域
                            var videoDisplayRect = _videoPlayerViewModel.GetVideoDisplayRect();
                            UpdateSubtitlePreviewLayout(videoDisplayRect);
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }

                // 阴影变化
                if (chkSubtitleShadow != null)
                {
                    chkSubtitleShadow.Checked += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                    chkSubtitleShadow.Unchecked += (s, e) => 
                    {
                        if (_isSubtitlePreviewEnabled)
                        {
                            ApplySubtitleStyleToPreview();
                            // 如果当前有字幕显示，确保文本也更新
                            UpdateSubtitlePreview();
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"SetupSubtitleStyleListeners 失败: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// 显示应用音频设置命令（命令提示符模式）
        /// </summary>
        private void ShowAudioSettingsCommands(
            List<Models.VideoFile> selectedFiles,
            Models.AudioParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "音频设置");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildAudioSettingsArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成音频设置命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 音频设置命令生成器",
                    OperationIcon = "🔊",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🔊 音量: {parameters.Volume:F0}%",
                        $"🎵 淡入: {parameters.FadeIn:F1}秒, 淡出: {parameters.FadeOut:F1}秒",
                        $"🎼 音频格式: {parameters.Format}, 比特率: {parameters.Bitrate}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 显示提取音频命令（命令提示符模式）
        /// </summary>
        private void ShowExtractAudioCommands(
            List<Models.VideoFile> selectedFiles,
            string audioFormat,
            string bitrate,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var inputFileName = Path.GetFileNameWithoutExtension(inputFile.FilePath);
                var extension = GetAudioFileExtension(audioFormat);
                var outputFileName = $"{inputFileName}{extension}";
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildExtractAudioArguments(
                        inputFile.FilePath,
                        outputPath,
                        audioFormat,
                        bitrate);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成提取音频命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 提取音频命令生成器",
                    OperationIcon = "📤",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🎼 音频格式: {audioFormat}",
                        $"📊 比特率: {bitrate}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 显示替换音频命令（命令提示符模式）
        /// </summary>
        private void ShowReplaceAudioCommands(
            List<Models.VideoFile> selectedFiles,
            string audioFilePath,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "替换音频");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildReplaceAudioArguments(
                        inputFile.FilePath,
                        audioFilePath,
                        outputPath,
                        GetVideoCodecForFFmpeg(settings.VideoCodec));

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成替换音频命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 替换音频命令生成器",
                    OperationIcon = "🔄",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🎵 音频文件: {Path.GetFileName(audioFilePath)}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 显示删除音频命令（命令提示符模式）
        /// </summary>
        private void ShowRemoveAudioCommands(
            List<Models.VideoFile> selectedFiles,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateAudioOutputFileName(inputFile.FilePath, settings, "无音频");
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildRemoveAudioArguments(
                        inputFile.FilePath,
                        outputPath,
                        GetVideoCodecForFFmpeg(settings.VideoCodec));

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成删除音频命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 删除音频命令生成器",
                    OperationIcon = "🗑️",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        #endregion

        #region 时码功能事件处理

        private CancellationTokenSource? _timecodeCancellationTokenSource;
        private List<Models.TimecodeSegment>? _parsedTimecodeSegments;

        /// <summary>
        /// 加载时间码示例按钮
        /// </summary>
        private void BtnLoadTimecodeExample_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exampleText = @"时间戳: 00:01:25,510 - 00:02:48,430

时间戳: 00:02:48,430 - 00:04:42,045

时间戳: 00:07:49,422 - 00:09:22,220

时间戳: 00:09:22,220 - 00:12:05,817

时间戳: 00:12:39,870 - 00:13:28,782

时间戳: 00:14:00,620 - 00:15:46,662

时间戳: 00:19:11,110 --> 00:20:34,020

时间戳: 00:21:29,280 --> 00:22:01,507";

                var txtInput = FindName("txtTimecodeInput") as TextBox;
                if (txtInput != null)
                {
                    txtInput.Text = exampleText;
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"加载时间码示例失败: {ex.Message}");
                MessageBox.Show($"加载示例时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清空时间码输入按钮
        /// </summary>
        private void BtnClearTimecodeInput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var txtInput = FindName("txtTimecodeInput") as TextBox;
                var txtOutput = FindName("txtTimecodeOutput") as TextBox;
                var txtCount = FindName("txtTimecodeCount") as TextBlock;

                if (txtInput != null)
                {
                    txtInput.Text = string.Empty;
                }
                if (txtOutput != null)
                {
                    txtOutput.Text = string.Empty;
                }
                if (txtCount != null)
                {
                    txtCount.Text = "解析结果（共 0 个片段）";
                }

                _parsedTimecodeSegments = null;
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"清空时间码输入失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析时间码按钮
        /// </summary>
        private void BtnParseTimecode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var txtInput = FindName("txtTimecodeInput") as TextBox;
                var txtOutput = FindName("txtTimecodeOutput") as TextBox;
                var txtCount = FindName("txtTimecodeCount") as TextBlock;

                if (txtInput == null || txtOutput == null || txtCount == null)
                {
                    MessageBox.Show("无法找到时间码输入/输出控件", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var inputText = txtInput.Text;
                if (string.IsNullOrWhiteSpace(inputText))
                {
                    MessageBox.Show("请输入时间码文本", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 解析时间码
                _parsedTimecodeSegments = Services.TimecodeParser.ParseTimecodes(inputText);

                if (_parsedTimecodeSegments.Count == 0)
                {
                    txtOutput.Text = "未能解析出有效的时间码片段，请检查格式是否正确。";
                    txtCount.Text = "解析结果（共 0 个片段）";
                    MessageBox.Show("未能解析出有效的时间码片段，请检查格式是否正确。", "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 格式化输出
                var outputText = Services.TimecodeParser.FormatTimecodesForOutput(_parsedTimecodeSegments);
                txtOutput.Text = outputText;
                txtCount.Text = $"解析结果（共 {_parsedTimecodeSegments.Count} 个片段）";

                Services.DebugLogger.LogInfo($"成功解析 {_parsedTimecodeSegments.Count} 个时间码片段");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"解析时间码失败: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"解析时间码时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 开始批量分割按钮
        /// </summary>
        private async void BtnSplitByTimecode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始批量分割...");

                // 检查时间码是否已解析
                if (_parsedTimecodeSegments == null || _parsedTimecodeSegments.Count == 0)
                {
                    MessageBox.Show("请先解析时间码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取选中的视频文件（只能选择一个）
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "批量分割", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (selectedFiles.Count > 1)
                {
                    MessageBox.Show("批量分割功能仅支持选择一个视频文件", "批量分割", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var inputFile = selectedFiles[0];

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowTimecodeSplitCommands(inputFile, _parsedTimecodeSegments, outputSettings, false);
                    return;
                }

                // 执行批量分割
                _timecodeCancellationTokenSource?.Cancel();
                _timecodeCancellationTokenSource = new CancellationTokenSource();

                await ExecuteTimecodeSplitAsync(inputFile, _parsedTimecodeSegments, outputSettings, false, _timecodeCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnSplitByTimecode_Click 失败: {ex.Message}");
                MessageBox.Show($"批量分割时发生错误：{ex.Message}", "批量分割", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 分割并合并按钮
        /// </summary>
        private async void BtnSplitAndMerge_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始分割并合并...");

                // 检查时间码是否已解析
                if (_parsedTimecodeSegments == null || _parsedTimecodeSegments.Count == 0)
                {
                    MessageBox.Show("请先解析时间码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取选中的视频文件（只能选择一个）
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "分割并合并", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (selectedFiles.Count > 1)
                {
                    MessageBox.Show("分割并合并功能仅支持选择一个视频文件", "分割并合并", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var inputFile = selectedFiles[0];

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查是否使用命令提示符执行
                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowTimecodeSplitCommands(inputFile, _parsedTimecodeSegments, outputSettings, true);
                    return;
                }

                // 执行分割并合并
                _timecodeCancellationTokenSource?.Cancel();
                _timecodeCancellationTokenSource = new CancellationTokenSource();

                await ExecuteTimecodeSplitAsync(inputFile, _parsedTimecodeSegments, outputSettings, true, _timecodeCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnSplitAndMerge_Click 失败: {ex.Message}");
                MessageBox.Show($"分割并合并时发生错误：{ex.Message}", "分割并合并", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 执行时间码分割
        /// </summary>
        private async Task ExecuteTimecodeSplitAsync(
            Models.VideoFile inputFile,
            List<Models.TimecodeSegment> segments,
            OutputSettings settings,
            bool mergeAfterSplit,
            CancellationToken cancellationToken)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputFile.FilePath);
            var extension = GetFileExtension(settings.OutputFormat);
            var keepCodec = chkKeepOriginalCodec?.IsChecked == true;

            // 第一步：分割视频
            var splitFiles = new List<string>();
            var batchTasks = segments.Select((segment, index) =>
            {
                var outputFileName = $"{inputFileName}_片段{segment.Index:D3}{extension}";
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);
                splitFiles.Add(outputPath);

                var startTime = TimeSpan.FromMilliseconds(segment.StartTime);
                var endTime = TimeSpan.FromMilliseconds(segment.EndTime);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = $"片段{segment.Index}",
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"分割片段 {segment.Index}: {segment.ToDisplayString()}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var customArgs = keepCodec ? "" : $"-c:v {GetVideoCodecForFFmpeg(settings.VideoCodec)} -c:a {GetAudioCodecForFFmpeg(settings.AudioCodec)}";
                        var startMs = (long)startTime.TotalMilliseconds;
                        var endMs = (long)endTime.TotalMilliseconds;
                        var result = await _videoProcessingService.CutClipAsync(input, output, startMs, endMs, customArgs, null, ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var splitConfig = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量分割视频",
                OperationIcon = "✂️",
                OperationColor = "#FF9800",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 输入文件: {Path.GetFileName(inputFile.FilePath)}",
                    $"📊 片段数量: {segments.Count}",
                    $"🎬 编码方式: {(keepCodec ? "复制（不重新编码）" : $"{settings.VideoCodec} + {settings.AudioCodec}")}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var splitResult = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, splitConfig, cancellationToken);

            if (splitResult.SuccessCount == 0)
            {
                UpdateStatusBar("分割失败", "❌", "#F44336");
                return;
            }

            // 第二步：如果需要合并
            if (mergeAfterSplit)
            {
                // 只合并成功分割的文件
                var successfulFiles = splitFiles.Where(f => File.Exists(f)).ToList();
                if (successfulFiles.Count == 0)
                {
                    UpdateStatusBar("没有成功分割的文件可合并", "❌", "#F44336");
                    return;
                }

                var mergeOutputFileName = $"{inputFileName}_合并{extension}";
                var mergeOutputPath = Path.Combine(settings.OutputPath, mergeOutputFileName);

                // 创建concat列表文件
                var concatListFile = Services.VideoProcessingService.CreateConcatListFile(settings.OutputPath, successfulFiles);

                try
                {
                    var concatArgs = Services.VideoProcessingService.BuildConcatArguments(
                        concatListFile,
                        mergeOutputPath,
                        keepCodec ? "copy" : GetVideoCodecForFFmpeg(settings.VideoCodec),
                        keepCodec ? "copy" : GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.CustomArgs);

                    LogOutputBox.Text += $"\r\n\r\n🔗 开始合并 {successfulFiles.Count} 个片段...\r\n";
                    LogOutputBox.Text += $"📁 输出文件: {mergeOutputFileName}\r\n";

                    // 执行合并命令（使用FFmpeg直接执行）
                    var mergeResult = await ExecuteFFmpegCommandForConcatAsync(concatArgs, mergeOutputPath, cancellationToken);

                    if (mergeResult.Success)
                    {
                        LogOutputBox.Text += $"✅ 合并完成: {mergeOutputFileName}\r\n";
                        UpdateStatusBar($"成功分割并合并 {splitResult.SuccessCount} 个片段", "✅", "#4CAF50");
                    }
                    else
                    {
                        LogOutputBox.Text += $"❌ 合并失败: {mergeResult.ErrorMessage}\r\n";
                        UpdateStatusBar("合并失败", "❌", "#F44336");
                    }
                }
                finally
                {
                    // 清理concat列表文件
                    try
                    {
                        if (File.Exists(concatListFile))
                        {
                            File.Delete(concatListFile);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                UpdateStatusBar($"成功分割 {splitResult.SuccessCount} 个片段", "✅", "#4CAF50");
            }
        }

        /// <summary>
        /// 显示时间码分割命令（命令提示符模式）
        /// </summary>
        private void ShowTimecodeSplitCommands(
            Models.VideoFile inputFile,
            List<Models.TimecodeSegment> segments,
            OutputSettings settings,
            bool includeMerge)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();
            var inputFileName = Path.GetFileNameWithoutExtension(inputFile.FilePath);
            var extension = GetFileExtension(settings.OutputFormat);
            var keepCodec = chkKeepOriginalCodec?.IsChecked == true;

            // 分割命令
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                var outputFileName = $"{inputFileName}_片段{segment.Index:D3}{extension}";
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var startTime = TimeSpan.FromMilliseconds(segment.StartTime);
                    var endTime = TimeSpan.FromMilliseconds(segment.EndTime);
                    var customArgs = keepCodec ? "" : $"-c:v {GetVideoCodecForFFmpeg(settings.VideoCodec)} -c:a {GetAudioCodecForFFmpeg(settings.AudioCodec)}";
                    var ffmpegArgs = Services.VideoProcessingService.BuildClipCutArguments(inputFile.FilePath, outputPath, startTime, endTime, customArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = segments.Count,
                        TaskId = $"片段{segment.Index}",
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成分割命令失败: {ex.Message}");
                }
            }

            // 如果需要合并，添加合并命令
            if (includeMerge && commands.Count > 0)
            {
                var mergeOutputFileName = $"{inputFileName}_合并{extension}";
                var mergeOutputPath = Path.Combine(settings.OutputPath, mergeOutputFileName);
                var splitFiles = commands.Select(c => c.OutputPath).ToList();
                var concatListFile = Services.VideoProcessingService.CreateConcatListFile(settings.OutputPath, splitFiles);

                try
                {
                    var concatArgs = Services.VideoProcessingService.BuildConcatArguments(
                        concatListFile,
                        mergeOutputPath,
                        keepCodec ? "copy" : GetVideoCodecForFFmpeg(settings.VideoCodec),
                        keepCodec ? "copy" : GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = commands.Count + 1,
                        Total = commands.Count + 1,
                        TaskId = "合并",
                        InputPath = concatListFile,
                        OutputPath = mergeOutputPath,
                        CommandArguments = concatArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成合并命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = includeMerge ? "FFmpeg 时间码分割并合并命令生成器" : "FFmpeg 时间码分割命令生成器",
                    OperationIcon = includeMerge ? "🔗" : "✂️",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"📝 输入文件: {Path.GetFileName(inputFile.FilePath)}",
                        $"📊 片段数量: {segments.Count}",
                        $"🎬 编码方式: {(keepCodec ? "复制（不重新编码）" : $"{settings.VideoCodec} + {settings.AudioCodec}")}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 执行FFmpeg合并命令
        /// </summary>
        private async Task<VideoProcessingResult> ExecuteFFmpegCommandForConcatAsync(string arguments, string outputPath, CancellationToken cancellationToken)
        {
            var result = new VideoProcessingResult();
            try
            {
                // 获取视频信息以估算时长
                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(outputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;

                // 使用VideoProcessingService的内部方法执行命令
                // 由于ExecuteFFmpegAsync是私有的，我们需要通过反射或创建一个公共方法
                // 这里我们直接使用Process来执行
                var ffmpegPath = _videoProcessingService.GetFFmpegPath();
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        Dispatcher.Invoke(() => LogOutputBox.Text += e.Data + "\r\n");
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        Dispatcher.Invoke(() => LogOutputBox.Text += e.Data + "\r\n");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken);

                result.Success = process.ExitCode == 0;
                result.ErrorMessage = result.Success ? string.Empty : errorBuilder.ToString();
                result.OutputPath = outputPath;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"执行合并命令时发生错误: {ex.Message}";
            }

            return result;
        }

        #endregion

        #region 滤镜功能

        private void InitializeFilterControls()
        {
            try
            {
                if (sliderFilterBrightness == null)
                {
                    return;
                }

                ApplyFilterParametersToSliders(_currentFilterParameters);
                UpdateFilterCommandPreview();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"初始化滤镜控件失败: {ex.Message}");
            }
        }

        private void ApplyFilterParametersToSliders(Models.FilterParameters parameters)
        {
            if (sliderFilterBrightness == null)
            {
                return;
            }

            _isFilterInitializing = true;
            try
            {
                sliderFilterBrightness.Value = parameters.Brightness;
                sliderFilterContrast.Value = parameters.Contrast;
                sliderFilterSaturation.Value = parameters.Saturation;
                sliderFilterTemperature.Value = parameters.Temperature;
                sliderFilterBlur.Value = parameters.Blur;
                sliderFilterSharpen.Value = parameters.Sharpen;
                sliderFilterVignette.Value = parameters.Vignette;
            }
            finally
            {
                _isFilterInitializing = false;
            }
        }

        private async void FilterPresetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button button || button.Tag == null)
                {
                    return;
                }

                if (!Enum.TryParse(button.Tag.ToString(), out Models.FilterPreset preset))
                {
                    return;
                }

                _currentFilterParameters = GetPresetParameters(preset);
                ApplyFilterParametersToSliders(_currentFilterParameters);
                UpdateFilterCommandPreview();

                await RefreshFilterPreviewAsync(allowSessionStartup: true);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"应用滤镜预设失败: {ex.Message}");
            }
        }

        private async void FilterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isFilterInitializing)
            {
                return;
            }

            UpdateFilterParametersFromSliders();
            _currentFilterParameters.Preset = Models.FilterPreset.Custom;
            UpdateFilterCommandPreview();

            if (sender is Slider slider && slider.IsMouseCaptureWithin)
            {
                _filterPreviewPending = true;
                return;
            }

            _filterPreviewPending = false;
            await RefreshFilterPreviewAsync(allowSessionStartup: true);
        }

        private async void FilterSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_filterPreviewPending)
            {
                return;
            }

            _filterPreviewPending = false;
            await RefreshFilterPreviewAsync(allowSessionStartup: true);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var isPlaying = _videoPlayerViewModel?.IsPlaying ?? false;
                PreparePlaybackViewState(isPlayRequested: !isPlaying);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"播放按钮预处理失败: {ex.Message}");
            }
        }

        private void PreparePlaybackViewState(bool isPlayRequested)
        {
            try
            {
                if (_viewMode != 0)
                {
                    SetViewMode(0);
                }
                else if (isPlayRequested && _isFlipPreviewActive)
                {
                    ResetFlipPreviewState();
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"切换播放视图模式失败: {ex.Message}");
            }
        }

        private void UpdateFilterParametersFromSliders()
        {
            if (sliderFilterBrightness == null)
            {
                return;
            }

            _currentFilterParameters.Brightness = sliderFilterBrightness.Value;
            _currentFilterParameters.Contrast = sliderFilterContrast.Value;
            _currentFilterParameters.Saturation = sliderFilterSaturation.Value;
            _currentFilterParameters.Temperature = sliderFilterTemperature.Value;
            _currentFilterParameters.Blur = sliderFilterBlur.Value;
            _currentFilterParameters.Sharpen = sliderFilterSharpen.Value;
            _currentFilterParameters.Vignette = sliderFilterVignette.Value;
        }

        private Models.FilterParameters GetPresetParameters(Models.FilterPreset preset)
        {
            var parameters = Models.FilterParameters.CreateDefault();
            parameters.Preset = preset;

            switch (preset)
            {
                case Models.FilterPreset.Retro:
                    parameters.Brightness = 5;
                    parameters.Contrast = 12;
                    parameters.Saturation = -20;
                    parameters.Temperature = 25;
                    parameters.Vignette = 30;
                    break;
                case Models.FilterPreset.Monochrome:
                    parameters.Brightness = 10;
                    parameters.Contrast = 20;
                    parameters.Saturation = -100;
                    break;
                case Models.FilterPreset.Soft:
                    parameters.Brightness = 8;
                    parameters.Contrast = -10;
                    parameters.Saturation = -5;
                    parameters.Blur = 4;
                    parameters.Vignette = 15;
                    break;
                case Models.FilterPreset.Vibrant:
                    parameters.Contrast = 10;
                    parameters.Saturation = 35;
                    parameters.Sharpen = 3;
                    break;
                case Models.FilterPreset.Cool:
                    parameters.Temperature = -40;
                    parameters.Saturation = -10;
                    parameters.Brightness = 5;
                    break;
                case Models.FilterPreset.Warm:
                    parameters.Temperature = 40;
                    parameters.Saturation = 5;
                    parameters.Vignette = 10;
                    break;
                case Models.FilterPreset.Cinema:
                    parameters.Brightness = -5;
                    parameters.Contrast = 25;
                    parameters.Saturation = -10;
                    parameters.Temperature = 15;
                    parameters.Vignette = 40;
                    break;
                case Models.FilterPreset.Film:
                    parameters.Brightness = 3;
                    parameters.Contrast = 15;
                    parameters.Saturation = -5;
                    parameters.Temperature = 10;
                    parameters.Vignette = 25;
                    break;
                case Models.FilterPreset.Rainbow:
                    parameters.Saturation = 45;
                    parameters.Temperature = 20;
                    break;
                case Models.FilterPreset.Mist:
                    parameters.Brightness = 12;
                    parameters.Saturation = -15;
                    parameters.Blur = 6;
                    parameters.Vignette = 10;
                    break;
                case Models.FilterPreset.Sharp:
                    parameters.Contrast = 10;
                    parameters.Sharpen = 6;
                    parameters.Saturation = 5;
                    break;
                case Models.FilterPreset.None:
                case Models.FilterPreset.Custom:
                default:
                    parameters = Models.FilterParameters.CreateDefault();
                    parameters.Preset = preset;
                    break;
            }

            return parameters;
        }

        private void UpdateFilterCommandPreview()
        {
            try
            {
                if (TxtFilterCommandPreview == null)
                {
                    return;
                }

                var filters = Services.VideoProcessingService.BuildFilterFilterChain(_currentFilterParameters);
                if (filters.Count > 0)
                {
                    TxtFilterCommandPreview.Text = $"-vf \"{string.Join(",", filters)}\"";
                }
                else
                {
                    TxtFilterCommandPreview.Text = "无滤镜";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新滤镜命令预览失败: {ex.Message}");
                if (TxtFilterCommandPreview != null)
                {
                    TxtFilterCommandPreview.Text = "无滤镜";
                }
            }
        }

        private async Task RefreshFilterPreviewAsync(bool allowSessionStartup = false)
        {
            if (_isFlipPreviewBusy)
            {
                return;
            }

            if (!_isFlipPreviewActive ||
                string.IsNullOrEmpty(_flipPreviewBaseImagePath) ||
                !File.Exists(_flipPreviewBaseImagePath))
            {
                if (allowSessionStartup)
                {
                    await ShowFilterPreviewAsync(forceCapture: true, allowEmptyFilters: true);
                }
                return;
            }

            try
            {
                await ApplyFilterPreviewAsync(allowEmptyFilters: true);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"刷新滤镜预览失败: {ex.Message}");
            }
        }

        private Models.FilterParameters GetFilterParameters()
        {
            return _currentFilterParameters.Clone();
        }

        private async void BtnApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "应用滤镜设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var filterParams = GetFilterParameters();
                if (!filterParams.HasAdjustments())
                {
                    MessageBox.Show("请先选择一个滤镜预设或调整参数", "应用滤镜设置", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (outputSettings.VideoCodec == "复制")
                {
                    var prompt = MessageBox.Show(
                        "⚠️ 复制编码器不支持滤镜处理操作。\r\n\r\n应用滤镜需要重新编码视频才能生效。\r\n\r\n是否要自动切换到推荐的 H.264 编码器？",
                        "编码器选择提示",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (prompt == MessageBoxResult.Yes)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var h264Radio = FindName("H264CodecRadio") as RadioButton;
                            if (h264Radio != null)
                            {
                                h264Radio.IsChecked = true;
                            }
                        });

                        MessageBox.Show("已自动切换到 H.264 编码器，请重新点击“应用滤镜设置”按钮。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("请在输出设置中手动选择 H.264 或 H.265 编码器，然后重新执行滤镜处理。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    return;
                }

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowFilterCommands(selectedFiles, filterParams, outputSettings);
                    return;
                }

                _filterCancellationTokenSource?.Cancel();
                _filterCancellationTokenSource = new CancellationTokenSource();

                await ExecuteFilterBatchAsync(selectedFiles, filterParams, outputSettings, _filterCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"应用滤镜设置失败: {ex.Message}");
                MessageBox.Show($"应用滤镜设置时发生错误：{ex.Message}", "应用滤镜设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ShowFilterPreviewAsync(bool forceCapture = false, bool allowEmptyFilters = false)
        {
            if (_videoPlayerViewModel == null ||
                !_videoPlayerViewModel.HasVideo ||
                string.IsNullOrWhiteSpace(_videoPlayerViewModel.CurrentFilePath))
            {
                MessageBox.Show("请先加载一个视频文件", "滤镜预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isFlipPreviewBusy)
            {
                Services.DebugLogger.LogInfo("图片预览正在生成，请稍候...");
                return;
            }

            try
            {
                _isFlipPreviewBusy = true;

                if (_videoPlayerViewModel.IsPlaying)
                {
                    _videoPlayerViewModel.Pause();
                }

                if (!await EnsureFlipPreviewSessionAsync(forceCapture))
                {
                    return;
                }

                await ApplyFilterPreviewAsync(allowEmptyFilters);
            }
            finally
            {
                _isFlipPreviewBusy = false;
            }
        }

        private async Task ApplyFilterPreviewAsync(bool allowEmptyFilters)
        {
            if (!_isFlipPreviewActive ||
                string.IsNullOrEmpty(_flipPreviewBaseImagePath) ||
                !File.Exists(_flipPreviewBaseImagePath))
            {
                return;
            }

            var parameters = GetFilterParameters();
            if (!parameters.HasAdjustments() && !allowEmptyFilters)
            {
                MessageBox.Show("请先选择一个滤镜预设或调整参数", "滤镜预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var filters = parameters.HasAdjustments()
                ? Services.VideoProcessingService.BuildFilterFilterChain(parameters)
                : new List<string>();

            if (filters.Count == 0)
            {
                LoadImagePreview(_flipPreviewBaseImagePath);
                return;
            }

            try
            {
                Directory.CreateDirectory(_flipPreviewTempDir);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"创建滤镜预览临时目录失败: {ex.Message}");
                return;
            }

            var processedPath = Path.Combine(_flipPreviewTempDir, $"filter_preview_{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
            var args = new List<string>
            {
                "-i", $"\"{_flipPreviewBaseImagePath}\"",
                "-vf", $"\"{string.Join(",", filters)}\"",
                "-y",
                $"\"{processedPath}\""
            };

            if (!await RunFfmpegCommandAsync(args, "生成滤镜预览", "滤镜预览"))
            {
                return;
            }

            DeleteFileIfExists(_flipPreviewProcessedImagePath);
            _flipPreviewProcessedImagePath = processedPath;

            LoadImagePreview(processedPath);
        }

        private async Task ExecuteFilterBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.FilterParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateFilterOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"应用滤镜: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ApplyFiltersAsync(
                            input,
                            output,
                            parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            GetAudioCodecForFFmpeg(settings.AudioCodec),
                            settings.AudioBitrate.Replace(" kbps", "k"),
                            settings.CustomArgs,
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var filters = Services.VideoProcessingService.BuildFilterFilterChain(parameters);

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量应用滤镜",
                OperationIcon = "🎨",
                OperationColor = "#673AB7",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🎛️ 预设: {GetFilterPresetName(parameters.Preset)}",
                    $"🔗 滤镜链: {(filters.Count > 0 ? string.Join(",", filters) : "无")}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        private void ShowFilterCommands(
            List<Models.VideoFile> selectedFiles,
            Models.FilterParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateFilterOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var ffmpegArgs = Services.VideoProcessingService.BuildFilterArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.AudioBitrate.Replace(" kbps", "k"),
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = ffmpegArgs
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成滤镜命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var filters = Services.VideoProcessingService.BuildFilterFilterChain(parameters);
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 滤镜命令生成器",
                    OperationIcon = "🎨",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🎛️ 预设: {GetFilterPresetName(parameters.Preset)}",
                        $"🔗 滤镜链: {(filters.Count > 0 ? string.Join(",", filters) : "无")}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };
                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        private string GenerateFilterOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            string suffix = settings.FileNamingMode switch
            {
                "原文件名_时间戳" => $"_{DateTime.Now:yyyyMMdd_HHmmss}",
                "原文件名_序号" => "_滤镜_001",
                "自定义前缀" => string.Empty,
                "自定义后缀" => settings.CustomSuffix,
                _ => "_滤镜"
            };

            if (settings.FileNamingMode == "自定义前缀")
            {
                return $"{settings.CustomPrefix}{inputFileName}{extension}";
            }

            return $"{inputFileName}{suffix}{extension}";
        }

        private string GetFilterPresetName(Models.FilterPreset preset)
        {
            return preset switch
            {
                Models.FilterPreset.Retro => "复古",
                Models.FilterPreset.Monochrome => "黑白",
                Models.FilterPreset.Soft => "柔和",
                Models.FilterPreset.Vibrant => "鲜艳",
                Models.FilterPreset.Cool => "冷色",
                Models.FilterPreset.Warm => "暖色",
                Models.FilterPreset.Cinema => "电影",
                Models.FilterPreset.Film => "胶片",
                Models.FilterPreset.Rainbow => "彩虹",
                Models.FilterPreset.Mist => "朦胧",
                Models.FilterPreset.Sharp => "锐利",
                Models.FilterPreset.Custom => "自定义",
                _ => "无"
            };
        }

        #endregion

        #region 翻转功能

        // 翻转按钮事件处理
        private async void BtnFlipHorizontal_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.FlipType = Models.FlipType.Horizontal;
                _currentFlipParameters.RotateType = Models.RotateType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnFlipVertical_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.FlipType = Models.FlipType.Vertical;
                _currentFlipParameters.RotateType = Models.RotateType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnFlipBoth_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.FlipType = Models.FlipType.Both;
                _currentFlipParameters.RotateType = Models.RotateType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnFlipNone_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.RotateType = Models.RotateType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        // 旋转按钮事件处理
        private async void BtnRotate90_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.RotateType = Models.RotateType.Rotate90;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnRotate180_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.RotateType = Models.RotateType.Rotate180;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnRotate270_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.RotateType = Models.RotateType.Rotate270;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnRotateAuto_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.RotateType = Models.RotateType.Auto;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.TransposeType = Models.TransposeType.None;
            });
        }

        private async void BtnApplyCustomRotate_Click(object sender, RoutedEventArgs e)
        {
            var slider = FindName("sliderRotateAngle") as Slider;
            if (slider != null)
            {
                await HandleFlipButtonAsync(() =>
                {
                    _currentFlipParameters.RotateType = Models.RotateType.Custom;
                    _currentFlipParameters.CustomRotateAngle = slider.Value;
                    _currentFlipParameters.FlipType = Models.FlipType.None;
                    _currentFlipParameters.TransposeType = Models.TransposeType.None;
                });
            }
        }

        // 转置按钮事件处理
        private async void BtnTranspose0_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.TransposeType = Models.TransposeType.Transpose0;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.RotateType = Models.RotateType.None;
            });
        }

        private async void BtnTranspose1_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.TransposeType = Models.TransposeType.Transpose1;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.RotateType = Models.RotateType.None;
            });
        }

        private async void BtnTranspose2_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.TransposeType = Models.TransposeType.Transpose2;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.RotateType = Models.RotateType.None;
            });
        }

        private async void BtnTranspose3_Click(object sender, RoutedEventArgs e)
        {
            await HandleFlipButtonAsync(() =>
            {
                _currentFlipParameters.TransposeType = Models.TransposeType.Transpose3;
                _currentFlipParameters.FlipType = Models.FlipType.None;
                _currentFlipParameters.RotateType = Models.RotateType.None;
            });
        }

        private async Task HandleFlipButtonAsync(Action parameterUpdater)
        {
            try
            {
                parameterUpdater?.Invoke();
                UpdateFlipCommandPreview();
                await ShowFlipPreviewAsync();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"翻转按钮处理失败: {ex.Message}");
            }
        }

        private async Task ShowFlipPreviewAsync(bool forceCapture = false)
        {
            if (_videoPlayerViewModel == null ||
                !_videoPlayerViewModel.HasVideo ||
                string.IsNullOrWhiteSpace(_videoPlayerViewModel.CurrentFilePath))
            {
                MessageBox.Show("请先加载一个视频文件", "翻转预览", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isFlipPreviewBusy)
            {
                Services.DebugLogger.LogInfo("翻转预览正在生成，请稍候...");
                return;
            }

            try
            {
                _isFlipPreviewBusy = true;

                if (_videoPlayerViewModel.IsPlaying)
                {
                    _videoPlayerViewModel.Pause();
                }

                if (!await EnsureFlipPreviewSessionAsync(forceCapture))
                {
                    return;
                }

                await ApplyFlipPreviewAsync();
            }
            finally
            {
                _isFlipPreviewBusy = false;
            }
        }

        private async Task<bool> EnsureFlipPreviewSessionAsync(bool forceCapture = false)
        {
            if (!forceCapture &&
                _isFlipPreviewActive &&
                !string.IsNullOrEmpty(_flipPreviewBaseImagePath) &&
                File.Exists(_flipPreviewBaseImagePath))
            {
                if (_viewMode != 1)
                {
                    SetViewMode(1);
                }
                return true;
            }

            return await CaptureFlipPreviewFrameAsync();
        }

        private bool TryGetCurrentVideoPath(out string videoPath)
        {
            videoPath = _videoPlayerViewModel?.CurrentFilePath ?? string.Empty;
            return !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath);
        }

        private async Task<bool> CaptureFlipPreviewFrameAsync()
        {
            if (!TryGetCurrentVideoPath(out var videoPath))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(_flipPreviewTempDir);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"创建翻转预览临时目录失败: {ex.Message}");
                return false;
            }

            DeleteFileIfExists(_flipPreviewBaseImagePath);
            DeleteFileIfExists(_flipPreviewProcessedImagePath);
            _flipPreviewBaseImagePath = null;
            _flipPreviewProcessedImagePath = null;
            _isFlipPreviewActive = false;

            var baseImagePath = Path.Combine(_flipPreviewTempDir, $"flip_base_{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
            var positionSeconds = Math.Max(0, (_videoPlayerViewModel?.CurrentPosition ?? 0) / 1000.0);
            var formattedPosition = positionSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            var args = new List<string>
            {
                "-ss", formattedPosition,
                "-i", $"\"{videoPath}\"",
                "-frames:v", "1",
                "-q:v", "2",
                "-y",
                $"\"{baseImagePath}\""
            };

            if (!await RunFfmpegCommandAsync(args, "截取预览帧", "翻转预览"))
            {
                return false;
            }

            _flipPreviewBaseImagePath = baseImagePath;
            _flipPreviewProcessedImagePath = null;
            _isFlipPreviewActive = true;

            LoadImagePreview(baseImagePath);
            if (_viewMode != 1)
            {
                SetViewMode(1);
            }

            return true;
        }

        private async Task ApplyFlipPreviewAsync()
        {
            if (!_isFlipPreviewActive ||
                string.IsNullOrEmpty(_flipPreviewBaseImagePath) ||
                !File.Exists(_flipPreviewBaseImagePath))
            {
                return;
            }

            var filters = BuildFlipFilterChain(_currentFlipParameters);
            if (filters.Count == 0)
            {
                LoadImagePreview(_flipPreviewBaseImagePath);
                return;
            }

            try
            {
                Directory.CreateDirectory(_flipPreviewTempDir);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"创建翻转预览临时目录失败: {ex.Message}");
                return;
            }

            var processedPath = Path.Combine(_flipPreviewTempDir, $"flip_preview_{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
            var args = new List<string>
            {
                "-i", $"\"{_flipPreviewBaseImagePath}\"",
                "-vf", $"\"{string.Join(",", filters)}\"",
                "-y",
                $"\"{processedPath}\""
            };

            if (!await RunFfmpegCommandAsync(args, "生成翻转预览", "翻转预览"))
            {
                return;
            }

            DeleteFileIfExists(_flipPreviewProcessedImagePath);
            _flipPreviewProcessedImagePath = processedPath;

            LoadImagePreview(processedPath);
        }

        private async Task<bool> RunFfmpegCommandAsync(List<string> arguments, string operationName, string messageBoxTitle)
        {
            var ffmpegPath = _videoProcessingService?.GetFFmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg 工具，请先在设置中配置。", messageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            var errorBuilder = new StringBuilder();
            using var process = new Process { StartInfo = startInfo };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorMessage = errorBuilder.ToString();
                Services.DebugLogger.LogError($"FFmpeg {operationName}失败: {errorMessage}");
                MessageBox.Show(
                    $"FFmpeg {operationName}失败：{(string.IsNullOrWhiteSpace(errorMessage) ? "请检查日志输出" : errorMessage)}",
                    messageBoxTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private void DeleteFileIfExists(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogWarning($"删除临时文件失败: {ex.Message}");
            }
        }

        private void ResetFlipPreviewState()
        {
            if (!_isFlipPreviewActive)
            {
                return;
            }

            DeleteFileIfExists(_flipPreviewBaseImagePath);
            DeleteFileIfExists(_flipPreviewProcessedImagePath);
            _flipPreviewBaseImagePath = null;
            _flipPreviewProcessedImagePath = null;
            _isFlipPreviewActive = false;

            if (ImagePreview != null)
            {
                ImagePreview.Source = null;
            }
            if (ImagePlaceholderText != null)
            {
                ImagePlaceholderText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 更新翻转命令预览
        /// </summary>
        private void UpdateFlipCommandPreview()
        {
            try
            {
                var parameters = _currentFlipParameters;
                var filters = BuildFlipFilterChain(parameters);

                if (filters.Count > 0)
                {
                    var filterString = string.Join(",", filters);
                    var commandText = $"-vf \"{filterString}\"";
                    TxtFlipCommandPreview.Text = commandText;
                }
                else
                {
                    TxtFlipCommandPreview.Text = "无变换";
                }

                // 更新预览描述
                UpdateFlipPreviewDescription();
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新翻转命令预览失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新翻转预览描述文本
        /// </summary>
        private void UpdateFlipPreviewDescription()
        {
            try
            {
                var parameters = _currentFlipParameters;
                var descriptions = new List<string>();

                if (parameters.TransposeType != Models.TransposeType.None)
                {
                    descriptions.Add(GetTransposeTypeDescription(parameters.TransposeType));
                }
                else
                {
                    if (parameters.FlipType != Models.FlipType.None)
                    {
                        descriptions.Add(GetFlipTypeDescription(parameters.FlipType));
                    }
                    if (parameters.RotateType != Models.RotateType.None)
                    {
                        descriptions.Add(GetRotateTypeDescription(parameters.RotateType, parameters.CustomRotateAngle));
                    }
                }

                if (descriptions.Count > 0)
                {
                    TxtFlipPreviewDescription.Text = $"当前: {string.Join(" + ", descriptions)}";
                }
                else
                {
                    TxtFlipPreviewDescription.Text = "当前: 无变换";
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新翻转预览描述失败: {ex.Message}");
            }
        }

        private List<string> BuildFlipFilterChain(Models.FlipParameters parameters)
        {
            var filters = new List<string>();

            if (parameters.TransposeType != Models.TransposeType.None)
            {
                var transposeValue = parameters.TransposeType switch
                {
                    Models.TransposeType.Transpose0 => "0",
                    Models.TransposeType.Transpose1 => "1",
                    Models.TransposeType.Transpose2 => "2",
                    Models.TransposeType.Transpose3 => "3",
                    _ => "1"
                };
                filters.Add($"transpose={transposeValue}");
            }
            else
            {
                if (parameters.FlipType == Models.FlipType.Horizontal)
                {
                    filters.Add("hflip");
                }
                else if (parameters.FlipType == Models.FlipType.Vertical)
                {
                    filters.Add("vflip");
                }
                else if (parameters.FlipType == Models.FlipType.Both)
                {
                    filters.Add("hflip");
                    filters.Add("vflip");
                }

                if (parameters.RotateType == Models.RotateType.Rotate90)
                {
                    filters.Add("transpose=2");
                }
                else if (parameters.RotateType == Models.RotateType.Rotate180)
                {
                    filters.Add("transpose=2");
                    filters.Add("transpose=2");
                }
                else if (parameters.RotateType == Models.RotateType.Rotate270)
                {
                    filters.Add("transpose=1");
                }
                else if (parameters.RotateType == Models.RotateType.Custom && Math.Abs(parameters.CustomRotateAngle) > 0.01)
                {
                    var normalizedAngle = ((int)Math.Round(parameters.CustomRotateAngle / 90.0) * 90) % 360;
                    if (normalizedAngle < 0) normalizedAngle += 360;

                    if (normalizedAngle == 90)
                    {
                        filters.Add("transpose=2");
                    }
                    else if (normalizedAngle == 180)
                    {
                        filters.Add("transpose=2");
                        filters.Add("transpose=2");
                    }
                    else if (normalizedAngle == 270)
                    {
                        filters.Add("transpose=1");
                    }
                }
                else if (parameters.RotateType == Models.RotateType.Auto)
                {
                    filters.Add("transpose=1");
                }
            }

            return filters;
        }

        /// <summary>
        /// 获取翻转类型描述
        /// </summary>
        private string GetFlipTypeDescription(Models.FlipType flipType)
        {
            return flipType switch
            {
                Models.FlipType.Horizontal => "水平翻转",
                Models.FlipType.Vertical => "垂直翻转",
                Models.FlipType.Both => "水平+垂直翻转",
                _ => "无"
            };
        }

        /// <summary>
        /// 获取旋转类型描述
        /// </summary>
        private string GetRotateTypeDescription(Models.RotateType rotateType, double customAngle)
        {
            return rotateType switch
            {
                Models.RotateType.Rotate90 => "顺时针90°",
                Models.RotateType.Rotate180 => "180°",
                Models.RotateType.Rotate270 => "逆时针90°",
                Models.RotateType.Custom => $"{customAngle:F0}°",
                Models.RotateType.Auto => "自动检测",
                _ => "无"
            };
        }

        /// <summary>
        /// 获取转置类型描述
        /// </summary>
        private string GetTransposeTypeDescription(Models.TransposeType transposeType)
        {
            return transposeType switch
            {
                Models.TransposeType.Transpose0 => "Transpose 0 (顺时针90°+垂直翻转)",
                Models.TransposeType.Transpose1 => "Transpose 1 (逆时针90°)",
                Models.TransposeType.Transpose2 => "Transpose 2 (顺时针90°)",
                Models.TransposeType.Transpose3 => "Transpose 3 (逆时针90°+垂直翻转)",
                _ => "无"
            };
        }

        /// <summary>
        /// 获取翻转参数
        /// </summary>
        private Models.FlipParameters GetFlipParameters()
        {
            return _currentFlipParameters;
        }

        /// <summary>
        /// 生成翻转输出文件名
        /// </summary>
        private string GenerateFlipOutputFileName(string inputPath, OutputSettings settings)
        {
            var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            switch (settings.FileNamingMode)
            {
                case "原文件名_处理":
                    return $"{inputFileName}_翻转{extension}";
                case "原文件名_时间戳":
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    return $"{inputFileName}_{timestamp}{extension}";
                case "原文件名_序号":
                    return $"{inputFileName}_翻转_001{extension}";
                default:
                    return $"{inputFileName}_翻转{extension}";
            }
        }

        /// <summary>
        /// 应用翻转按钮点击事件
        /// </summary>
        private async void BtnApplyFlip_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("开始应用翻转设置...");

                // 获取选中的视频文件
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    MessageBox.Show("请至少选择一个视频文件", "应用翻转设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取输出设置
                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 获取翻转参数
                var flipParams = GetFlipParameters();
                
                // 检查是否有任何变换操作
                if (flipParams.FlipType == Models.FlipType.None &&
                    flipParams.RotateType == Models.RotateType.None &&
                    flipParams.TransposeType == Models.TransposeType.None)
                {
                    MessageBox.Show("请至少选择一种翻转或旋转操作", "应用翻转设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowFlipCommands(selectedFiles, flipParams, outputSettings);
                    return;
                }

                // 执行批量应用翻转设置
                _flipCancellationTokenSource?.Cancel();
                _flipCancellationTokenSource = new CancellationTokenSource();

                await ExecuteFlipBatchAsync(selectedFiles, flipParams, outputSettings, _flipCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"BtnApplyFlip_Click 失败: {ex.Message}");
                MessageBox.Show($"应用翻转设置时发生错误：{ex.Message}", "应用翻转设置", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 批量执行翻转处理
        /// </summary>
        private async Task ExecuteFlipBatchAsync(
            List<Models.VideoFile> selectedFiles,
            Models.FlipParameters parameters,
            OutputSettings settings,
            CancellationToken cancellationToken)
        {
            var batchTasks = selectedFiles.Select(inputFile =>
            {
                var outputFileName = GenerateFlipOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                return new Services.FfmpegBatchProcessor.BatchTask
                {
                    TaskId = Path.GetFileName(inputFile.FilePath),
                    InputPath = inputFile.FilePath,
                    OutputPath = outputPath,
                    Description = $"应用翻转设置: {Path.GetFileName(inputFile.FilePath)}\r\n📁 输出文件: {outputFileName}",
                    ExecuteTask = async (input, output, ct) =>
                    {
                        var result = await _videoProcessingService.ApplyFlipAsync(
                            input, output, parameters,
                            GetVideoCodecForFFmpeg(settings.VideoCodec),
                            settings.Quality,
                            GetAudioCodecForFFmpeg(settings.AudioCodec),
                            settings.AudioBitrate,
                            settings.CustomArgs,
                            null,
                            ct);
                        return result;
                    },
                    EstimatedDuration = null
                };
            }).ToList();

            var config = new Services.FfmpegBatchProcessor.BatchConfig
            {
                OperationName = "批量应用翻转设置",
                OperationIcon = "🔄",
                OperationColor = "#FF9800",
                LogHeaderLines = new List<string>
                {
                    $"📁 输出路径: {settings.OutputPath}",
                    $"📝 文件命名: {settings.FileNamingMode}",
                    $"🎬 输出格式: {settings.OutputFormat}",
                    $"🔄 翻转: {GetFlipTypeDescription(parameters.FlipType)}",
                    $"🔁 旋转: {GetRotateTypeDescription(parameters.RotateType, parameters.CustomRotateAngle)}",
                    $"📐 转置: {GetTransposeTypeDescription(parameters.TransposeType)}"
                },
                UpdateStatusBar = UpdateStatusBar,
                UpdateProgress = (progress, text) =>
                {
                    ExecutionProgressBar.Value = progress;
                    ProgressInfoText.Text = text;
                },
                AppendLog = (text) => LogOutputBox.Text += text,
                SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0,
                InitializeLog = (text) => LogOutputBox.Text = text
            };

            var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

            var message = result.SuccessCount > 0
                ? $"成功处理 {result.SuccessCount} 个文件"
                : "处理失败";
            UpdateStatusBar(message, result.SuccessCount > 0 ? "✅" : "❌", result.SuccessCount > 0 ? "#4CAF50" : "#F44336");
        }

        private void ShowFlipCommands(
            List<Models.VideoFile> selectedFiles,
            Models.FlipParameters parameters,
            OutputSettings settings)
        {
            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>();

            for (int i = 0; i < selectedFiles.Count; i++)
            {
                var inputFile = selectedFiles[i];
                var outputFileName = GenerateFlipOutputFileName(inputFile.FilePath, settings);
                var outputPath = Path.Combine(settings.OutputPath, outputFileName);

                try
                {
                    var args = _videoProcessingService.BuildFlipArguments(
                        inputFile.FilePath,
                        outputPath,
                        parameters,
                        GetVideoCodecForFFmpeg(settings.VideoCodec),
                        settings.Quality,
                        GetAudioCodecForFFmpeg(settings.AudioCodec),
                        settings.AudioBitrate,
                        settings.CustomArgs);

                    commands.Add(new Services.FfmpegCommandPreviewService.CommandItem
                    {
                        Index = i + 1,
                        Total = selectedFiles.Count,
                        TaskId = Path.GetFileName(inputFile.FilePath),
                        InputPath = inputFile.FilePath,
                        OutputPath = outputPath,
                        CommandArguments = args
                    });
                }
                catch (Exception ex)
                {
                    Services.DebugLogger.LogError($"生成翻转命令失败: {ex.Message}");
                }
            }

            if (commands.Count > 0)
            {
                var config = new Services.FfmpegCommandPreviewService.PreviewConfig
                {
                    OperationName = "FFmpeg 翻转命令生成器",
                    OperationIcon = "🔄",
                    SummaryLines = new List<string>
                    {
                        $"📁 输出目录: {settings.OutputPath}",
                        $"🔄 翻转: {GetFlipTypeDescription(parameters.FlipType)}",
                        $"🔁 旋转: {GetRotateTypeDescription(parameters.RotateType, parameters.CustomRotateAngle)}",
                        $"📐 转置: {GetTransposeTypeDescription(parameters.TransposeType)}"
                    },
                    AppendOutput = (text) => EmbeddedAppendOutput(text),
                    AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                    UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                    SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                    SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
                };

                _ffmpegCommandPreviewService.ShowCommands(commands, config);
            }
        }

        /// <summary>
        /// 更新预览图片按钮点击事件
        /// </summary>
        private async void BtnUpdatePreview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnUpdatePreview.IsEnabled = false;
                BtnUpdatePreview.Content = "⏳ 重新抓取...";
                await ShowFlipPreviewAsync(forceCapture: true);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"更新预览图片失败: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"更新预览图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnUpdatePreview.IsEnabled = true;
                BtnUpdatePreview.Content = "🔄 重新抓取当前帧";
            }
        }

        #endregion

        #region 合并功能

        private async void BtnMergeAddSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                if (selectedFiles.Count == 0)
                {
                    Services.ToastNotification.ShowInfo("请在左侧文件列表中勾选需要合并的文件");
                    return;
                }

                await AddFilesToMergeListAsync(selectedFiles);
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"添加合并文件失败: {ex.Message}");
                Services.ToastNotification.ShowError($"添加文件失败: {ex.Message}");
            }
        }

        private void BtnMergeRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (MergeListView?.SelectedItem is MergeItem mergeItem)
            {
                _mergeItems.Remove(mergeItem);
                RefreshMergeOrder();
                UpdateMergeSummary();
                UpdateMergeCommandPreview();
            }
            else
            {
                Services.ToastNotification.ShowInfo("请选择需要移除的文件");
            }
        }

        private void BtnMergeMoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (MergeListView?.SelectedItem is MergeItem mergeItem)
            {
                var index = _mergeItems.IndexOf(mergeItem);
                if (index > 0)
                {
                    _mergeItems.Move(index, index - 1);
                    MergeListView.SelectedIndex = index - 1;
                    RefreshMergeOrder();
                    UpdateMergeCommandPreview();
                }
            }
        }

        private void BtnMergeMoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (MergeListView?.SelectedItem is MergeItem mergeItem)
            {
                var index = _mergeItems.IndexOf(mergeItem);
                if (index >= 0 && index < _mergeItems.Count - 1)
                {
                    _mergeItems.Move(index, index + 1);
                    MergeListView.SelectedIndex = index + 1;
                    RefreshMergeOrder();
                    UpdateMergeCommandPreview();
                }
            }
        }

        private void BtnMergeClear_Click(object sender, RoutedEventArgs e)
        {
            if (_mergeItems.Count == 0)
            {
                return;
            }

            var result = MessageBox.Show("确定要清空合并队列吗？", "清空合并队列", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _mergeItems.Clear();
                UpdateMergeSummary();
                UpdateMergeCommandPreview();
            }
        }

        private void MergeOptionRadio_Checked(object sender, RoutedEventArgs e)
        {
            UpdateMergeCommandPreview();
        }

        private async void BtnMergeStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mergeItems.Count < 2)
                {
                    Services.ToastNotification.ShowWarning("请至少添加两个文件到合并队列");
                    return;
                }

                var outputSettings = GetOutputSettings();
                if (!outputSettings.IsValid)
                {
                    MessageBox.Show(outputSettings.ErrorMessage, "输出设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var mergeParameters = GetMergeParameters();
                var mergeFiles = _mergeItems.Select(item => item.FilePath).ToList();
                var outputFileName = GenerateMergeOutputFileName(mergeFiles.First(), outputSettings);
                var outputPath = Path.Combine(outputSettings.OutputPath, outputFileName);

                if (UseCommandPromptForCropCheckBox?.IsChecked == true)
                {
                    ShowMergeCommands(mergeFiles, outputSettings, mergeParameters, outputPath);
                    return;
                }

                _mergeCancellationTokenSource?.Cancel();
                _mergeCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = _mergeCancellationTokenSource.Token;

                var videoCodec = mergeParameters.UseFastConcat ? "copy" : GetVideoCodecForFFmpeg(outputSettings.VideoCodec);
                var audioCodec = mergeParameters.UseFastConcat ? "copy" : GetAudioCodecForFFmpeg(outputSettings.AudioCodec);

                var batchTasks = new List<Services.FfmpegBatchProcessor.BatchTask>
                {
                    new Services.FfmpegBatchProcessor.BatchTask
                    {
                        TaskId = outputFileName,
                        InputPath = mergeFiles.First(),
                        OutputPath = outputPath,
                        Description = $"合并 {mergeFiles.Count} 个文件\r\n📁 输出: {outputFileName}",
                        ExecuteTask = async (_, output, ct) =>
                        {
                            return await _videoProcessingService.MergeVideosAsync(
                                mergeFiles,
                                output,
                                videoCodec,
                                audioCodec,
                                outputSettings.CustomArgs,
                                null,
                                ct);
                        }
                    }
                };

                var config = new Services.FfmpegBatchProcessor.BatchConfig
                {
                    OperationName = "视频合并",
                    OperationIcon = "🔗",
                    OperationColor = "#4CAF50",
                    LogHeaderLines = new List<string>
                    {
                        $"📁 输出目录: {outputSettings.OutputPath}",
                        $"📄 文件数量: {mergeFiles.Count}",
                        $"🎬 合并模式: {(mergeParameters.UseFastConcat ? "无损快速合并" : "重新编码")}",
                        $"🎥 输出文件: {outputFileName}"
                    },
                    UpdateStatusBar = UpdateStatusBar,
                    UpdateProgress = (progress, text) =>
                    {
                        ExecutionProgressBar.Value = progress;
                        ProgressInfoText.Text = text;
                    },
                    AppendLog = (text) => LogOutputBox.Text += text,
                    InitializeLog = (text) => LogOutputBox.Text = text,
                    SwitchToLogTab = () => OutputInfoTabs.SelectedIndex = 0
                };

                var result = await _ffmpegBatchProcessor.ExecuteBatchAsync(batchTasks, config, cancellationToken);

                if (result.FailCount == 0)
                {
                    Services.ToastNotification.ShowSuccess("合并完成，输出文件已生成");
                }
                else
                {
                    Services.ToastNotification.ShowWarning($"部分文件合并失败，成功 {result.SuccessCount} 个，失败 {result.FailCount} 个");
                }
            }
            catch (OperationCanceledException)
            {
                Services.ToastNotification.ShowWarning("合并操作已取消");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"执行合并失败: {ex.Message}");
                Services.ToastNotification.ShowError($"执行合并失败: {ex.Message}");
            }
        }

        private async Task AddFilesToMergeListAsync(IEnumerable<VideoFile> files)
        {
            foreach (var file in files)
            {
                if (_mergeItems.Any(item => string.Equals(item.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var mergeItem = new MergeItem(file.FilePath)
                {
                    Duration = file.Duration,
                    VideoCodec = file.VideoCodec,
                    AudioCodec = file.AudioCodec,
                    Resolution = file.Width > 0 && file.Height > 0 ? $"{file.Width}x{file.Height}" : string.Empty
                };

                if (mergeItem.Duration == TimeSpan.Zero || string.IsNullOrWhiteSpace(mergeItem.VideoCodec))
                {
                    try
                    {
                        var info = await _videoInformationService.GetVideoInformationAsync(file.FilePath);
                        if (info != null)
                        {
                            if (info.Duration > TimeSpan.Zero)
                            {
                                mergeItem.Duration = info.Duration;
                            }
                            if (!string.IsNullOrWhiteSpace(info.VideoCodec))
                            {
                                mergeItem.VideoCodec = info.VideoCodec;
                            }
                            if (!string.IsNullOrWhiteSpace(info.AudioCodec))
                            {
                                mergeItem.AudioCodec = info.AudioCodec;
                            }
                            if (info.Width > 0 && info.Height > 0)
                            {
                                mergeItem.Resolution = $"{info.Width}x{info.Height}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.DebugLogger.LogError($"读取视频信息失败: {ex.Message}");
                    }
                }

                _mergeItems.Add(mergeItem);
            }

            RefreshMergeOrder();
            UpdateMergeSummary();
            UpdateMergeCommandPreview();
        }

        private void RefreshMergeOrder()
        {
            for (int i = 0; i < _mergeItems.Count; i++)
            {
                _mergeItems[i].Order = i + 1;
            }
        }

        private void UpdateMergeSummary()
        {
            if (TxtMergeSummary == null)
            {
                return;
            }

            if (_mergeItems.Count == 0)
            {
                TxtMergeSummary.Text = "尚未添加任何文件";
                return;
            }

            var totalDuration = TimeSpan.Zero;
            foreach (var item in _mergeItems)
            {
                if (item.Duration > TimeSpan.Zero)
                {
                    totalDuration += item.Duration;
                }
            }

            TxtMergeSummary.Text = $"已添加 {_mergeItems.Count} 个文件，合计约 {FormatDurationText(totalDuration)}";
        }

        private void UpdateMergeCommandPreview()
        {
            if (TxtMergeCommandPreview == null)
            {
                return;
            }

            if (_mergeItems.Count < 2)
            {
                TxtMergeCommandPreview.Text = "请选择至少两个文件后，将显示命令示例";
                return;
            }

            var settings = GetOutputSettings();
            if (!settings.IsValid)
            {
                TxtMergeCommandPreview.Text = $"⚠️ 输出设置无效: {settings.ErrorMessage}";
                return;
            }

            var parameters = GetMergeParameters();
            var sampleListPath = Path.Combine(settings.OutputPath, "concat_list_preview.txt");
            var outputFileName = GenerateMergeOutputFileName(_mergeItems.First().FilePath, settings);
            var outputPath = Path.Combine(settings.OutputPath, outputFileName);
            var videoCodec = parameters.UseFastConcat ? "copy" : GetVideoCodecForFFmpeg(settings.VideoCodec);
            var audioCodec = parameters.UseFastConcat ? "copy" : GetAudioCodecForFFmpeg(settings.AudioCodec);
            var commandArgs = Services.VideoProcessingService.BuildConcatArguments(
                sampleListPath,
                outputPath,
                videoCodec,
                audioCodec,
                settings.CustomArgs);

            TxtMergeCommandPreview.Text = $"ffmpeg {commandArgs}";
        }

        private MergeParameters GetMergeParameters()
        {
            return new MergeParameters
            {
                UseFastConcat = MergeFastModeRadio?.IsChecked == true
            };
        }

        private string GenerateMergeOutputFileName(string firstInputPath, OutputSettings settings)
        {
            var baseName = Path.GetFileNameWithoutExtension(firstInputPath);
            var extension = GetFileExtension(settings.OutputFormat);

            return settings.FileNamingMode switch
            {
                "自定义前缀" => $"{settings.CustomPrefix}{baseName}{extension}",
                "自定义后缀" => $"{baseName}{settings.CustomSuffix}{extension}",
                "原文件名_时间戳" => $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}_merge{extension}",
                "原文件名_序号" => $"{baseName}_merge_001{extension}",
                _ => $"{baseName}_merge{extension}"
            };
        }

        private void ShowMergeCommands(List<string> mergeFiles, OutputSettings settings, MergeParameters parameters, string outputPath)
        {
            var concatListFile = Services.VideoProcessingService.CreateConcatListFile(settings.OutputPath, mergeFiles);
            var videoCodec = parameters.UseFastConcat ? "copy" : GetVideoCodecForFFmpeg(settings.VideoCodec);
            var audioCodec = parameters.UseFastConcat ? "copy" : GetAudioCodecForFFmpeg(settings.AudioCodec);
            var commandArgs = Services.VideoProcessingService.BuildConcatArguments(
                concatListFile,
                outputPath,
                videoCodec,
                audioCodec,
                settings.CustomArgs);

            var commands = new List<Services.FfmpegCommandPreviewService.CommandItem>
            {
                new Services.FfmpegCommandPreviewService.CommandItem
                {
                    Index = 1,
                    Total = 1,
                    TaskId = "合并",
                    InputPath = concatListFile,
                    OutputPath = outputPath,
                    CommandArguments = commandArgs
                }
            };

            var config = new Services.FfmpegCommandPreviewService.PreviewConfig
            {
                OperationName = "FFmpeg 合并命令生成器",
                OperationIcon = "🔗",
                SummaryLines = new List<string>
                {
                    $"📁 输出目录: {settings.OutputPath}",
                    $"📄 待合并文件: {mergeFiles.Count}",
                    $"🎬 合并模式: {(parameters.UseFastConcat ? "无损快速合并" : "重新编码")}",
                    $"📝 列表文件: {Path.GetFileName(concatListFile)}"
                },
                AppendOutput = (text) => EmbeddedAppendOutput(text),
                AppendToPreviewBox = (text) => Dispatcher.Invoke(() => { if (CommandPreviewBox != null) CommandPreviewBox.Text = text; }),
                UpdateDescription = (text) => Dispatcher.Invoke(() => { if (CommandDescriptionBox != null) CommandDescriptionBox.Text = text; }),
                SwitchToCommandTab = () => OutputInfoTabs.SelectedIndex = 1,
                SetPlayerMode = (mode) => SetViewMode(mode ? 0 : 2)
            };

            _ffmpegCommandPreviewService.ShowCommands(commands, config);
            Services.ToastNotification.ShowInfo("已生成FFmpeg命令和 concat 列表文件，可在命令提示符中执行。");
        }

        private static string FormatDurationText(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? duration.ToString(@"hh\:mm\:ss")
                : duration.ToString(@"mm\:ss");
        }

        #endregion

        #region 命令预览标签页功能

        /// <summary>
        /// 复制命令按钮点击事件
        /// </summary>
        private void BtnCopyCommand_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CommandPreviewBox == null || string.IsNullOrWhiteSpace(CommandPreviewBox.Text))
                {
                    Services.ToastNotification.ShowWarning("没有可复制的命令");
                    return;
                }

                // 提取纯命令文本（去除说明文字）
                var commandText = CommandPreviewBox.Text;
                
                // 提取所有FFmpeg命令
                var lines = commandText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var commands = new List<string>();
                var currentCommand = new List<string>();

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    
                    // 如果包含"💻 命令:"，提取命令部分
                    if (trimmedLine.Contains("💻 命令:"))
                    {
                        var cmdPart = trimmedLine.Substring(trimmedLine.IndexOf("💻 命令:") + "💻 命令:".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(cmdPart))
                        {
                            if (currentCommand.Count > 0)
                            {
                                commands.Add(string.Join(" ", currentCommand));
                                currentCommand.Clear();
                            }
                            currentCommand.Add(cmdPart);
                        }
                    }
                    // 如果以"ffmpeg"开头，开始新命令
                    else if (trimmedLine.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentCommand.Count > 0)
                        {
                            commands.Add(string.Join(" ", currentCommand));
                            currentCommand.Clear();
                        }
                        currentCommand.Add(trimmedLine);
                    }
                    // 如果是命令参数（以-开头或包含引号），添加到当前命令
                    else if (currentCommand.Count > 0 && (trimmedLine.StartsWith("-") || trimmedLine.StartsWith("\"") || trimmedLine.Contains("=")))
                    {
                        currentCommand.Add(trimmedLine);
                    }
                    // 如果遇到其他内容（说明文字），结束当前命令
                    else if (currentCommand.Count > 0 && (trimmedLine.Contains("📂") || trimmedLine.Contains("📁") || trimmedLine.Contains("[") || trimmedLine.Contains("=")))
                    {
                        commands.Add(string.Join(" ", currentCommand));
                        currentCommand.Clear();
                    }
                }

                // 添加最后一个命令
                if (currentCommand.Count > 0)
                {
                    commands.Add(string.Join(" ", currentCommand));
                }

                // 如果没有提取到命令，尝试提取所有包含"ffmpeg"的行
                if (commands.Count == 0)
                {
                    foreach (var line in lines)
                    {
                        var trimmedLine = line.Trim();
                        if (trimmedLine.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            commands.Add(trimmedLine);
                        }
                    }
                }

                // 构建要复制的文本
                var textToCopy = commands.Count > 0 
                    ? string.Join("\r\n\r\n", commands)
                    : commandText;

                Clipboard.SetText(textToCopy);
                Services.ToastNotification.ShowSuccess("命令已复制到剪贴板");
                Services.DebugLogger.LogInfo("命令已复制到剪贴板");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"复制命令失败: {ex.Message}");
                Services.ToastNotification.ShowError($"复制命令失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 编辑命令按钮点击事件
        /// </summary>
        private void BtnEditCommand_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CommandPreviewBox == null)
                {
                    return;
                }

                if (CommandPreviewBox.IsReadOnly)
                {
                    // 切换到编辑模式
                    CommandPreviewBox.IsReadOnly = false;
                    CommandPreviewBox.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)); // 硬编码暗色背景
                    CommandPreviewBox.BorderThickness = new Thickness(1);
                    CommandPreviewBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204)); // #007ACC
                    BtnEditCommand.Content = "💾 保存命令";
                    Services.ToastNotification.ShowInfo("命令编辑模式已启用，修改后点击\"保存命令\"保存");
                }
                else
                {
                    // 切换到只读模式
                    CommandPreviewBox.IsReadOnly = true;
                    CommandPreviewBox.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)); // 硬编码暗色背景，保持暗色
                    CommandPreviewBox.BorderThickness = new Thickness(0);
                    CommandPreviewBox.BorderBrush = null;
                    BtnEditCommand.Content = "✏️ 编辑命令";
                    Services.ToastNotification.ShowSuccess("命令已保存");
                    Services.DebugLogger.LogInfo("用户编辑并保存了命令");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"切换编辑模式失败: {ex.Message}");
                Services.ToastNotification.ShowError($"切换编辑模式失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷新预览按钮点击事件
        /// </summary>
        private void BtnRefreshPreview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 根据当前选中的标签页，重新生成命令预览
                var selectedTab = EditTabControl?.SelectedItem as TabItem;
                if (selectedTab == null)
                {
                    Services.ToastNotification.ShowWarning("请先选择一个功能标签页");
                    return;
                }

                var tabHeader = selectedTab.Header?.ToString() ?? "";
                Services.DebugLogger.LogInfo($"刷新命令预览，当前标签页: {tabHeader}");

                // 根据标签页类型调用相应的刷新方法
                if (tabHeader.Contains("切片"))
                {
                    // 切片标签页：需要选中文件和片段
                    var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                    if (selectedFiles.Count == 0)
                    {
                        Services.ToastNotification.ShowWarning("请先选择视频文件");
                        return;
                    }
                    if (selectedFiles.Count > 1)
                    {
                        Services.ToastNotification.ShowWarning("一次仅支持针对单个源文件");
                        return;
                    }
                    var selectedClips = _clipManager.GetSelectedClips().OrderBy(c => c.Order).ToArray();
                    if (selectedClips.Length == 0)
                    {
                        Services.ToastNotification.ShowWarning("请先选择至少一个片段");
                        return;
                    }
                    var outputSettings = GetOutputSettings();
                    if (!outputSettings.IsValid)
                    {
                        Services.ToastNotification.ShowError($"输出设置无效: {outputSettings.ErrorMessage}");
                        return;
                    }
                    if (!TryPrepareClipCutTasks(selectedFiles[0], selectedClips, outputSettings, out var tasks, out var error))
                    {
                        Services.ToastNotification.ShowError(error);
                        return;
                    }
                    ShowClipCutCommands(selectedFiles[0], tasks, outputSettings.CustomArgs);
                }
                else if (tabHeader.Contains("裁剪"))
                {
                    // 裁剪标签页：需要选中文件和裁剪参数
                    var selectedFiles = _videoListViewModel.Files.Where(f => f.IsSelected).ToList();
                    if (selectedFiles.Count == 0)
                    {
                        Services.ToastNotification.ShowWarning("请先选择视频文件");
                        return;
                    }
                    var validationResult = ValidateAndParseCropParameters(
                        CropXTextBox.Text, CropYTextBox.Text,
                        CropWTextBox.Text, CropHTextBox.Text);
                    if (!validationResult.IsValid)
                    {
                        Services.ToastNotification.ShowError($"裁剪参数无效: {validationResult.ErrorMessage}");
                        return;
                    }
                    var outputSettings = GetOutputSettings();
                    if (!outputSettings.IsValid)
                    {
                        Services.ToastNotification.ShowError($"输出设置无效: {outputSettings.ErrorMessage}");
                        return;
                    }
                    // 调用裁剪命令生成方法
                    _ = ExecuteCropViaCommandPrompt();
                }
                else
                {
                    Services.ToastNotification.ShowInfo("当前标签页不支持刷新预览，请使用\"通过命令提示符执行\"功能生成命令");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"刷新命令预览失败: {ex.Message}");
                Services.ToastNotification.ShowError($"刷新命令预览失败: {ex.Message}");
            }
        }

        #endregion

        #region 执行日志标签页功能

        /// <summary>
        /// 保存日志按钮点击事件
        /// </summary>
        private void BtnSaveLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LogOutputBox == null || string.IsNullOrWhiteSpace(LogOutputBox.Text))
                {
                    Services.ToastNotification.ShowWarning("没有可保存的日志内容");
                    return;
                }

                var dialog = new Forms.SaveFileDialog
                {
                    Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    Title = "保存执行日志",
                    FileName = $"执行日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DefaultExt = "txt"
                };

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, LogOutputBox.Text, Encoding.UTF8);
                    Services.ToastNotification.ShowSuccess($"日志已保存到: {Path.GetFileName(dialog.FileName)}");
                    Services.DebugLogger.LogInfo($"执行日志已保存到: {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"保存日志失败: {ex.Message}");
                Services.ToastNotification.ShowError($"保存日志失败: {ex.Message}");
                MessageBox.Show($"保存日志时发生错误：{ex.Message}", "保存日志", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清空日志按钮点击事件
        /// </summary>
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LogOutputBox == null)
                {
                    return;
                }

                // 确认对话框
                var result = MessageBox.Show(
                    "确定要清空执行日志吗？此操作不可撤销。",
                    "清空日志",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    LogOutputBox.Text = "$ 等待执行命令...\r\n\r\n提示: 点击上方'开始处理'按钮开始执行";
                    
                    // 滚动到顶部
                    if (LogScrollViewer != null)
                    {
                        LogScrollViewer.ScrollToTop();
                    }

                    // 重置进度条和状态
                    if (ExecutionProgressBar != null)
                    {
                        ExecutionProgressBar.Value = 0;
                    }
                    if (ProgressInfoText != null)
                    {
                        ProgressInfoText.Text = "0% | 0.0x | 00:00";
                    }
                    if (StatusIndicator != null)
                    {
                        StatusIndicator.Background = ResolveBrush("Brush.StatusIndicator", new SolidColorBrush(Color.FromRgb(153, 153, 153)));
                    }
                    if (StatusText != null)
                    {
                        StatusText.Text = "就绪";
                    }

                    Services.ToastNotification.ShowSuccess("日志已清空");
                    Services.DebugLogger.LogInfo("执行日志已清空");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"清空日志失败: {ex.Message}");
                Services.ToastNotification.ShowError($"清空日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止执行按钮点击事件
        /// </summary>
        private void StopExecutionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Services.DebugLogger.LogInfo("用户点击停止执行按钮");

                // 确认对话框
                var result = MessageBox.Show(
                    "确定要停止当前执行的任务吗？",
                    "停止执行",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                // 取消所有活动的CancellationTokenSource
                _clipCutCancellationTokenSource?.Cancel();
                _cropCancellationTokenSource?.Cancel();
                _watermarkCancellationTokenSource?.Cancel();
                _deduplicateCancellationTokenSource?.Cancel();
                _transcodeCancellationTokenSource?.Cancel();
                _flipCancellationTokenSource?.Cancel();
                _filterCancellationTokenSource?.Cancel();
                _mergeCancellationTokenSource?.Cancel();
                _audioCancellationTokenSource?.Cancel();
                _subtitleCancellationTokenSource?.Cancel();
                _timecodeCancellationTokenSource?.Cancel();
                _embeddedCancellationTokenSource?.Cancel();

                // 更新日志
                if (LogOutputBox != null)
                {
                    LogOutputBox.Text += "\r\n⚠️ 用户已取消执行\r\n";
                }

                // 更新状态
                if (StatusIndicator != null)
                {
                    StatusIndicator.Background = ResolveBrush("Brush.StatusWarning", new SolidColorBrush(Color.FromRgb(255, 152, 0)));
                }
                if (StatusText != null)
                {
                    StatusText.Text = "已取消";
                }
                if (StopExecutionButton != null)
                {
                    StopExecutionButton.IsEnabled = false;
                }

                Services.ToastNotification.ShowInfo("已发送停止信号，任务将尽快停止");
                Services.DebugLogger.LogInfo("已取消所有活动的任务");
            }
            catch (Exception ex)
            {
                Services.DebugLogger.LogError($"停止执行失败: {ex.Message}");
                Services.ToastNotification.ShowError($"停止执行失败: {ex.Message}");
            }
        }

        #endregion

    }
}