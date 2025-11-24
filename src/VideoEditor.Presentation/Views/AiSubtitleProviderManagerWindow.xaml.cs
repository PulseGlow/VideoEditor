using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Views
{
    public partial class AiSubtitleProviderManagerWindow : Window
    {
        private readonly ObservableCollection<AiSubtitleProviderProfile> _providers;
        private AiSubtitleProviderProfile? _selectedProvider;

        public IReadOnlyList<AiSubtitleProviderProfile> UpdatedProviders => _providers.Select(p => p.Clone()).ToList();

        public AiSubtitleProviderManagerWindow(IEnumerable<AiSubtitleProviderProfile> providers)
        {
            InitializeComponent();
            _providers = new ObservableCollection<AiSubtitleProviderProfile>(providers.Select(p => p.Clone()));
            ProviderListBox.ItemsSource = _providers;

            if (_providers.Count == 0)
            {
                var defaultProfile = CreateDefaultProfile();
                _providers.Add(defaultProfile);
            }

            ProviderListBox.SelectedIndex = 0;
        }

        private static AiSubtitleProviderProfile CreateDefaultProfile()
        {
            return new AiSubtitleProviderProfile
            {
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                EndpointPath = "/v1/audio/transcriptions",
                Model = "whisper-1",
                ResponseFormat = "srt",
                Notes = "默认 DeepSeek V3.2 接口"
            };
        }

        private void ProviderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedProvider != null)
            {
                ApplyFormToProvider(_selectedProvider);
            }

            _selectedProvider = ProviderListBox.SelectedItem as AiSubtitleProviderProfile;
            RefreshForm();
        }

        private void RefreshForm()
        {
            if (_selectedProvider == null)
            {
                DisplayNameBox.Text = string.Empty;
                BaseUrlBox.Text = string.Empty;
                EndpointBox.Text = string.Empty;
                ApiKeyBox.Password = string.Empty;
                ModelBox.Text = string.Empty;
                ResponseFormatBox.SelectedIndex = 0;
                NotesBox.Text = string.Empty;
                return;
            }

            DisplayNameBox.Text = _selectedProvider.DisplayName;
            BaseUrlBox.Text = _selectedProvider.BaseUrl;
            EndpointBox.Text = _selectedProvider.EndpointPath;
            ApiKeyBox.Password = _selectedProvider.ApiKey;
            ModelBox.Text = _selectedProvider.Model;
            SelectResponseFormat(_selectedProvider.ResponseFormat);
            NotesBox.Text = _selectedProvider.Notes;
        }

        private void SelectResponseFormat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ResponseFormatBox.SelectedIndex = 0;
                return;
            }

            foreach (var item in ResponseFormatBox.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    ResponseFormatBox.SelectedItem = cbi;
                    return;
                }
            }

            ResponseFormatBox.SelectedIndex = 0;
        }

        private void ApplyFormToProvider(AiSubtitleProviderProfile profile)
        {
            profile.DisplayName = DisplayNameBox.Text.Trim();
            profile.BaseUrl = BaseUrlBox.Text.Trim();
            profile.EndpointPath = EndpointBox.Text.Trim();
            profile.ApiKey = ApiKeyBox.Password.Trim();
            profile.Model = ModelBox.Text.Trim();
            profile.ResponseFormat = (ResponseFormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "srt";
            profile.Notes = NotesBox.Text.Trim();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = new AiSubtitleProviderProfile
            {
                DisplayName = "New Provider",
                BaseUrl = "https://",
                EndpointPath = "/v1/audio/transcriptions",
                Model = "whisper-1",
                ResponseFormat = "srt"
            };

            _providers.Add(profile);
            ProviderListBox.SelectedItem = profile;
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedProvider == null)
            {
                return;
            }

            ApplyFormToProvider(_selectedProvider);
            var clone = _selectedProvider.Clone();
            clone.Id = Guid.NewGuid().ToString("N");
            clone.DisplayName += " (Copy)";
            _providers.Add(clone);
            ProviderListBox.SelectedItem = clone;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedProvider == null)
            {
                return;
            }

            if (_providers.Count == 1)
            {
                MessageBox.Show("至少保留一个供应商配置。", "删除失败", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var toRemove = _selectedProvider;
            var nextIndex = Math.Max(0, ProviderListBox.SelectedIndex - 1);
            _providers.Remove(toRemove);
            ProviderListBox.SelectedIndex = nextIndex;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedProvider != null)
            {
                ApplyFormToProvider(_selectedProvider);
            }

            if (_providers.Any(p => string.IsNullOrWhiteSpace(p.DisplayName) || string.IsNullOrWhiteSpace(p.BaseUrl)))
            {
                MessageBox.Show("请填写每个供应商的显示名称和 Base URL。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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

