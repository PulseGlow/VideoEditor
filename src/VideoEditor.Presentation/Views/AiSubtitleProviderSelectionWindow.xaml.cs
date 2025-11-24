using System.Collections.Generic;
using System.Linq;
using System.Windows;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Views
{
    public partial class AiSubtitleProviderSelectionWindow : Window
    {
        private readonly List<AiSubtitleProviderProfile> _providers;

        public AiSubtitleProviderProfile? SelectedProvider => ProviderListBox.SelectedItem as AiSubtitleProviderProfile;
        public bool RememberChoice => RememberCheckBox.IsChecked == true;
        public bool ManageRequested { get; private set; }

        public AiSubtitleProviderSelectionWindow(IEnumerable<AiSubtitleProviderProfile> providers, string? defaultId = null)
        {
            InitializeComponent();
            _providers = providers.Select(p => p.Clone()).ToList();
            ProviderListBox.ItemsSource = _providers;

            if (!string.IsNullOrWhiteSpace(defaultId))
            {
                var match = _providers.FirstOrDefault(p => p.Id == defaultId);
                if (match != null)
                {
                    ProviderListBox.SelectedItem = match;
                }
            }

            if (ProviderListBox.SelectedItem == null && _providers.Count > 0)
            {
                ProviderListBox.SelectedIndex = 0;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProvider == null)
            {
                MessageBox.Show("请先选择一个供应商。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void ProviderListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedProvider != null)
            {
                DialogResult = true;
                Close();
            }
        }

        private void ManageButton_Click(object sender, RoutedEventArgs e)
        {
            ManageRequested = true;
            DialogResult = false;
            Close();
        }
    }
}

