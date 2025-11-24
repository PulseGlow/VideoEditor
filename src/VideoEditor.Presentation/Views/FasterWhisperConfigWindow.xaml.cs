using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoEditor.Presentation.Views
{
    public partial class FasterWhisperConfigWindow : Window
    {
        private readonly ObservableCollection<string> _modelCandidates = new();

        public FasterWhisperConfigWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        public string ProgramPath
        {
            get => ProgramPathTextBox.Text.Trim();
            set => ProgramPathTextBox.Text = value ?? string.Empty;
        }

        public string ModelsRootDir
        {
            get => ModelDirTextBox.Text.Trim();
            set => ModelDirTextBox.Text = value ?? string.Empty;
        }

        private string? SelectedModel
        {
            get => ModelComboBox.SelectedItem as string;
            set => ModelComboBox.SelectedItem = value;
        }

        private string SelectedDevice
        {
            get => (DeviceComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "cpu";
            set
            {
                foreach (ComboBoxItem item in DeviceComboBox.Items)
                {
                    if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                    {
                        DeviceComboBox.SelectedItem = item;
                        return;
                    }
                }
                DeviceComboBox.SelectedIndex = 0;
            }
        }

        private void LoadSettings()
        {
            ProgramPath = Properties.Settings.Default.FasterWhisperProgramPath ?? string.Empty;
            var rootDir = Properties.Settings.Default.FasterWhisperModelsRootDir;

            if (string.IsNullOrWhiteSpace(rootDir))
            {
                // 兼容老版本，旧配置保存的是具体模型目录
                rootDir = Properties.Settings.Default.FasterWhisperModelDir;
            }

            ModelsRootDir = rootDir ?? string.Empty;

            SelectedDevice = Properties.Settings.Default.FasterWhisperDevice ?? "cpu";

            LoadModelList();

            var savedModel = Properties.Settings.Default.FasterWhisperSelectedModel;

            if (string.IsNullOrWhiteSpace(savedModel) && Directory.Exists(Properties.Settings.Default.FasterWhisperModelDir))
            {
                savedModel = new DirectoryInfo(Properties.Settings.Default.FasterWhisperModelDir).Name;
            }

            if (!string.IsNullOrWhiteSpace(savedModel))
            {
                SelectedModel = _modelCandidates.FirstOrDefault(m =>
                    string.Equals(m, savedModel, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedModel == null && _modelCandidates.Count > 0)
            {
                SelectedModel = _modelCandidates[0];
            }
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.FasterWhisperProgramPath = ProgramPath;
            Properties.Settings.Default.FasterWhisperModelsRootDir = ModelsRootDir;

            var selectedModelName = SelectedModel ?? string.Empty;
            Properties.Settings.Default.FasterWhisperSelectedModel = selectedModelName;

            var modelDir = GetSelectedModelDirectory();
            Properties.Settings.Default.FasterWhisperModelDir = modelDir;

            Properties.Settings.Default.FasterWhisperDevice = SelectedDevice;

            Properties.Settings.Default.Save();
        }

        private string GetSelectedModelDirectory()
        {
            if (string.IsNullOrWhiteSpace(ModelsRootDir))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(SelectedModel))
            {
                return ModelsRootDir;
            }

            return Path.Combine(ModelsRootDir, SelectedModel);
        }

        private void LoadModelList()
        {
            _modelCandidates.Clear();
            ModelComboBox.ItemsSource = null;

            try
            {
                var root = ModelsRootDir;
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        var modelBin = Path.Combine(dir, "model.bin");
                        var tokenizer = Path.Combine(dir, "tokenizer.json");
                        if (File.Exists(modelBin) && File.Exists(tokenizer))
                        {
                            _modelCandidates.Add(Path.GetFileName(dir));
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ignore discovery errors
            }

            ModelComboBox.ItemsSource = _modelCandidates;
        }

        private void BrowseProgramButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Faster Whisper 可执行程序",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                ProgramPath = dialog.FileName;
            }
        }

        private void BrowseModelDirButton_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 Faster Whisper 模型库根目录（包含多个模型子文件夹）",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ModelsRootDir = dialog.SelectedPath;
                LoadModelList();
            }
        }

        private void RefreshModelListButton_Click(object sender, RoutedEventArgs e)
        {
            var previous = SelectedModel;
            LoadModelList();

            if (!string.IsNullOrWhiteSpace(previous))
            {
                SelectedModel = _modelCandidates.FirstOrDefault(m =>
                    string.Equals(m, previous, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedModel == null && _modelCandidates.Count > 0)
            {
                SelectedModel = _modelCandidates[0];
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ProgramPath) || !File.Exists(ProgramPath))
            {
                MessageBox.Show("请先选择有效的 Faster Whisper 程序路径。", "Faster Whisper 配置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ModelsRootDir) || !Directory.Exists(ModelsRootDir))
            {
                MessageBox.Show("请先选择有效的模型库根目录。", "Faster Whisper 配置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var modelDir = GetSelectedModelDirectory();
            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            {
                MessageBox.Show("请选择一个已下载的 Faster Whisper 模型。", "Faster Whisper 配置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(Path.Combine(modelDir, "model.bin")))
            {
                MessageBox.Show("所选模型目录中没有 model.bin，请确认目录正确。", "Faster Whisper 配置",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveSettings();
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
