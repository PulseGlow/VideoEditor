using System.Windows;

namespace VideoEditor.Presentation.Views
{
    public partial class DeepSeekConfigWindow : Window
    {
        public DeepSeekConfigWindow()
        {
            InitializeComponent();
        }

        public string ApiKey
        {
            get => ApiKeyBox.Password;
            set => ApiKeyBox.Password = value ?? string.Empty;
        }

        public string BaseUrl
        {
            get => BaseUrlBox.Text.Trim();
            set => BaseUrlBox.Text = value ?? string.Empty;
        }

        public string ModelName
        {
            get => ModelBox.Text.Trim();
            set => ModelBox.Text = value ?? string.Empty;
        }

        public string ResponseFormat
        {
            get => (ResponseFormatBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "srt";
            set
            {
                foreach (var item in ResponseFormatBox.Items)
                {
                    if (item is System.Windows.Controls.ComboBoxItem cbi &&
                        string.Equals(cbi.Content?.ToString(), value, System.StringComparison.OrdinalIgnoreCase))
                    {
                        ResponseFormatBox.SelectedItem = cbi;
                        return;
                    }
                }

                ResponseFormatBox.SelectedIndex = 0;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                MessageBox.Show("API Key 不能为空。", "配置 AI 模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(BaseUrlBox.Text))
            {
                MessageBox.Show("Base URL 不能为空。", "配置 AI 模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ModelBox.Text))
            {
                MessageBox.Show("模型名称不能为空。", "配置 AI 模型", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

