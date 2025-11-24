using System.Windows;

namespace VideoEditor.Presentation.Views
{
    public partial class AiSubtitleOptionsWindow : Window
    {
        public AiSubtitleOptionsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        public bool EnableOptimization
        {
            get => EnableOptimizationCheckBox.IsChecked ?? false;
            set => EnableOptimizationCheckBox.IsChecked = value;
        }

        public string? OptimizationPrompt
        {
            get => OptimizationPromptTextBox.Text?.Trim();
            set => OptimizationPromptTextBox.Text = value ?? string.Empty;
        }

        public bool EnableChunking
        {
            get => EnableChunkingCheckBox.IsChecked ?? true;
            set => EnableChunkingCheckBox.IsChecked = value;
        }

        public bool EnableCache
        {
            get => EnableCacheCheckBox.IsChecked ?? true;
            set => EnableCacheCheckBox.IsChecked = value;
        }

        private void LoadSettings()
        {
            EnableOptimization = Properties.Settings.Default.AiSubtitleEnableOptimization;
            OptimizationPrompt = Properties.Settings.Default.AiSubtitleOptimizationPrompt;
            EnableChunking = true; // 默认启用
            EnableCache = true; // 默认启用
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // 保存设置
            Properties.Settings.Default.AiSubtitleEnableOptimization = EnableOptimization;
            Properties.Settings.Default.AiSubtitleOptimizationPrompt = OptimizationPrompt ?? string.Empty;
            Properties.Settings.Default.Save();

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

