using System;
using System.Diagnostics;
using System.Windows;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Views
{
    public partial class UpdateDialog : Window
    {
        private UpdateInfo? _updateInfo;
        private string? _downloadUrl;

        public UpdateDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示加载状态
        /// </summary>
        public void ShowLoading()
        {
            LoadingPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            UpdateInfoPanel.Visibility = Visibility.Collapsed;
            UpToDatePanel.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            TitleTextBlock.Text = "🔍 检查更新";
            SubtitleTextBlock.Text = "正在检查更新...";
        }

        /// <summary>
        /// 显示错误信息
        /// </summary>
        public void ShowError(string errorMessage)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            UpdateInfoPanel.Visibility = Visibility.Collapsed;
            UpToDatePanel.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            ErrorMessageTextBlock.Text = errorMessage;
            TitleTextBlock.Text = "❌ 检查更新失败";
            SubtitleTextBlock.Text = "无法连接到更新服务器";
        }

        /// <summary>
        /// 显示更新信息
        /// </summary>
        public void ShowUpdateInfo(UpdateInfo updateInfo)
        {
            _updateInfo = updateInfo;
            _downloadUrl = updateInfo.DownloadUrl;

            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            UpdateInfoPanel.Visibility = Visibility.Visible;
            UpToDatePanel.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Visible;

            TitleTextBlock.Text = "🎉 发现新版本";
            SubtitleTextBlock.Text = $"最新版本：{updateInfo.Version}";

            CurrentVersionTextBlock.Text = updateInfo.CurrentVersion;
            LatestVersionTextBlock.Text = updateInfo.Version;
            
            if (updateInfo.ReleaseDate.HasValue)
            {
                ReleaseDateTextBlock.Text = updateInfo.ReleaseDate.Value.ToString("yyyy-MM-dd");
            }
            else
            {
                ReleaseDateTextBlock.Text = "未知";
            }

            // 显示更新说明
            if (!string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes))
            {
                ReleaseNotesTextBlock.Text = updateInfo.ReleaseNotes;
            }
            else
            {
                ReleaseNotesTextBlock.Text = "无更新说明";
            }
        }

        /// <summary>
        /// 显示已是最新版本
        /// </summary>
        public void ShowUpToDate(UpdateInfo updateInfo)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            UpdateInfoPanel.Visibility = Visibility.Collapsed;
            UpToDatePanel.Visibility = Visibility.Visible;
            DownloadButton.Visibility = Visibility.Collapsed;

            TitleTextBlock.Text = "✅ 已是最新版本";
            SubtitleTextBlock.Text = $"当前版本：{updateInfo.CurrentVersion}";
            CurrentVersionDisplayTextBlock.Text = $"当前版本：{updateInfo.CurrentVersion}";
        }

        /// <summary>
        /// 前往下载按钮点击
        /// </summary>
        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_downloadUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _downloadUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"无法打开下载链接：{ex.Message}",
                        "错误",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

