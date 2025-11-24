using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.Views
{
    /// <summary>
    /// 执行日志窗口
    /// </summary>
    public partial class LogWindow : Window
    {
        private readonly DispatcherTimer _refreshTimer;
        private long _lastFileSize = 0;

        public LogWindow()
        {
            InitializeComponent();
            
            // 设置窗口图标
            try
            {
                var iconUri = new Uri("pack://application:,,,/VideoEditor.Presentation;component/Resources/app.ico");
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch
            {
                // 忽略图标加载错误
            }

            // 创建定时器，每500ms刷新一次日志
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            // 初始加载日志
            Loaded += (s, e) => RefreshLogs();
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            RefreshLogs();
        }

        private void RefreshLogs()
        {
            try
            {
                var logFilePath = DebugLogger.GetLogFilePath();
                if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
                {
                    StatusTextBlock.Text = "日志文件不存在";
                    return;
                }

                var fileInfo = new FileInfo(logFilePath);
                if (fileInfo.Length == _lastFileSize)
                {
                    // 文件大小未变化，无需刷新
                    return;
                }

                _lastFileSize = fileInfo.Length;

                // 读取日志文件内容
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream, Encoding.UTF8))
                {
                    var content = reader.ReadToEnd();
                    LogTextBox.Text = content;
                    
                    // 自动滚动到底部
                    LogTextBox.ScrollToEnd();
                }

                StatusTextBlock.Text = $"日志文件: {logFilePath} | 大小: {FormatFileSize(_lastFileSize)} | 最后更新: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"读取日志失败: {ex.Message}";
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshLogs();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(LogTextBox.Text))
                {
                    Clipboard.SetText(LogTextBox.Text);
                    StatusTextBlock.Text = "已复制到剪贴板";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要清空日志显示吗？\n（不会删除日志文件）", 
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                LogTextBox.Clear();
                StatusTextBlock.Text = "日志显示已清空";
            }
        }

        private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logFilePath = DebugLogger.GetLogFilePath();
                if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
                {
                    MessageBox.Show("日志文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 在文件管理器中打开并选中文件
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{logFilePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开日志文件失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer?.Stop();
            base.OnClosed(e);
        }
    }
}

