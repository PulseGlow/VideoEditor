using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.Views
{
    public partial class AboutWindow : Window
    {
        private const string GitHubRepositoryUrl = "https://github.com/PulseGlow/VideoEditor";

        public AboutWindow()
        {
            InitializeComponent();
            LoadVersionInfo();
        }

        /// <summary>
        /// 加载版本信息
        /// </summary>
        private void LoadVersionInfo()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                
                string versionText;
                if (version != null)
                {
                    versionText = $"版本 {version.Major}.{version.Minor}.{version.Build}";
                }
                else
                {
                    // 尝试从 AssemblyInformationalVersion 获取
                    var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    if (informationalVersion != null && !string.IsNullOrEmpty(informationalVersion.InformationalVersion))
                    {
                        versionText = $"版本 {informationalVersion.InformationalVersion}";
                    }
                    else
                    {
                        versionText = "版本 1.0.0";
                    }
                }

                VersionTextBlock.Text = versionText;
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"加载版本信息失败: {ex.Message}");
                VersionTextBlock.Text = "版本 1.0.0";
            }
        }

        /// <summary>
        /// GitHub 链接点击
        /// </summary>
        private void GitHubLink_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = GitHubRepositoryUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法打开链接：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                DebugLogger.LogError($"打开 GitHub 链接失败: {ex.Message}");
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

