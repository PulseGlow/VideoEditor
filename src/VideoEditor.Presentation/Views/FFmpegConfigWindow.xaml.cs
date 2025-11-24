using System;
using System.IO;
using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;

namespace VideoEditor.Presentation.Views
{
    /// <summary>
    /// FFmpeg 配置窗口
    /// </summary>
    public partial class FFmpegConfigWindow : Window
    {
        public string? FFmpegPath { get; private set; }
        public string? FFprobePath { get; private set; }

        public FFmpegConfigWindow()
        {
            InitializeComponent();

            // 加载当前配置
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // 设置由MainWindow在ShowDialog之前设置
            // 这里保持为空即可
        }

        private void BrowseFFmpegButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Forms.OpenFileDialog
            {
                Title = "选择 FFmpeg 可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "ffmpeg.exe"
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                FFmpegPathTextBox.Text = dialog.FileName;
            }
        }

        private void BrowseFFprobeButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Forms.OpenFileDialog
            {
                Title = "选择 FFprobe 可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "ffprobe.exe"
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                FFprobePathTextBox.Text = dialog.FileName;
            }
        }

        private void AutoSearchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 搜索程序目录下的 tools/ffmpeg
                var programDir = AppDomain.CurrentDomain.BaseDirectory;
                var toolsDir = Path.Combine(programDir, "tools", "ffmpeg");

                var ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");
                var ffprobePath = Path.Combine(toolsDir, "ffprobe.exe");

                if (File.Exists(ffmpegPath))
                {
                    FFmpegPathTextBox.Text = ffmpegPath;
                }
                else
                {
                    // 搜索子目录
                    if (Directory.Exists(toolsDir))
                    {
                        var ffmpegFiles = Directory.GetFiles(toolsDir, "ffmpeg.exe", SearchOption.AllDirectories);
                        if (ffmpegFiles.Length > 0)
                        {
                            FFmpegPathTextBox.Text = ffmpegFiles[0];
                        }
                    }
                }

                if (File.Exists(ffprobePath))
                {
                    FFprobePathTextBox.Text = ffprobePath;
                }
                else
                {
                    // 搜索子目录
                    if (Directory.Exists(toolsDir))
                    {
                        var ffprobeFiles = Directory.GetFiles(toolsDir, "ffprobe.exe", SearchOption.AllDirectories);
                        if (ffprobeFiles.Length > 0)
                        {
                            FFprobePathTextBox.Text = ffprobeFiles[0];
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(FFmpegPathTextBox.Text))
                {
                    System.Windows.MessageBox.Show("未找到 FFmpeg 可执行文件。\n请手动指定路径。", 
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("已找到 FFmpeg 路径。", 
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"自动搜索失败: {ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var ffmpegPath = FFmpegPathTextBox.Text.Trim();
            var ffprobePath = FFprobePathTextBox.Text.Trim();

            // 验证FFmpeg路径
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                System.Windows.MessageBox.Show("请指定 FFmpeg 可执行文件路径。", 
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(ffmpegPath))
            {
                System.Windows.MessageBox.Show("FFmpeg 可执行文件不存在。", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 验证FFprobe路径（如果指定了）
            if (!string.IsNullOrWhiteSpace(ffprobePath) && !File.Exists(ffprobePath))
            {
                System.Windows.MessageBox.Show("FFprobe 可执行文件不存在。", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            FFmpegPath = ffmpegPath;
            FFprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? null : ffprobePath;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

