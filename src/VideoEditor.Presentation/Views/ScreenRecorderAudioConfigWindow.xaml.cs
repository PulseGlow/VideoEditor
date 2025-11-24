using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VideoEditor.Presentation.Views
{
    public partial class ScreenRecorderAudioConfigWindow : Window
    {
        private readonly ObservableCollection<AudioDeviceInfo> _audioDevices = new();
        private string? _ffmpegPath;

        public ScreenRecorderAudioConfigWindow()
        {
            InitializeComponent();
            SystemAudioComboBox.ItemsSource = _audioDevices;
            MicrophoneComboBox.ItemsSource = _audioDevices;
            this.Loaded += ScreenRecorderAudioConfigWindow_Loaded;
        }

        private async void ScreenRecorderAudioConfigWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _ffmpegPath = FindFFmpegExecutable();
            if (!string.IsNullOrEmpty(_ffmpegPath))
            {
                await RefreshAudioDevicesAsync(null);
            }
            else
            {
                MessageBox.Show("未找到 FFmpeg 可执行文件，无法自动检测音频设备。\n请手动输入设备名称。", 
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public string? SystemAudioDevice
        {
            get
            {
                if (SystemAudioComboBox.SelectedValue != null)
                {
                    return SystemAudioComboBox.SelectedValue.ToString();
                }
                return SystemAudioComboBox.Text?.Trim();
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    SystemAudioComboBox.SelectedIndex = -1;
                    SystemAudioComboBox.Text = string.Empty;
                }
                else
                {
                    var device = _audioDevices.FirstOrDefault(d => d.DeviceName == value);
                    if (device != null)
                    {
                        SystemAudioComboBox.SelectedItem = device;
                    }
                    else
                    {
                        SystemAudioComboBox.Text = value;
                    }
                }
            }
        }

        public string? MicrophoneDevice
        {
            get
            {
                if (MicrophoneComboBox.SelectedValue != null)
                {
                    return MicrophoneComboBox.SelectedValue.ToString();
                }
                return MicrophoneComboBox.Text?.Trim();
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    MicrophoneComboBox.SelectedIndex = -1;
                    MicrophoneComboBox.Text = string.Empty;
                }
                else
                {
                    var device = _audioDevices.FirstOrDefault(d => d.DeviceName == value);
                    if (device != null)
                    {
                        MicrophoneComboBox.SelectedItem = device;
                    }
                    else
                    {
                        MicrophoneComboBox.Text = value;
                    }
                }
            }
        }

        private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            var refreshButton = sender as Button;
            await RefreshAudioDevicesAsync(refreshButton);
        }

        private async Task RefreshAudioDevicesAsync(Button? refreshButton = null)
        {
            if (string.IsNullOrEmpty(_ffmpegPath) || !File.Exists(_ffmpegPath))
            {
                MessageBox.Show("FFmpeg 可执行文件不存在，无法刷新设备列表。", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (refreshButton != null)
            {
                refreshButton.IsEnabled = false;
                refreshButton.Content = "🔄 检测中...";
            }

            try
            {
                var devices = await GetDshowAudioDevicesAsync(_ffmpegPath);
                _audioDevices.Clear();
                foreach (var device in devices)
                {
                    _audioDevices.Add(device);
                }

                if (devices.Count == 0)
                {
                    MessageBox.Show("未检测到任何音频设备。\n请确保系统已正确安装音频驱动。", 
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"获取音频设备列表失败：{ex.Message}", 
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (refreshButton != null)
                {
                    refreshButton.IsEnabled = true;
                    refreshButton.Content = "🔄 刷新设备列表";
                }
            }
        }

        private async Task<List<AudioDeviceInfo>> GetDshowAudioDevicesAsync(string ffmpegPath)
        {
            var devices = new List<AudioDeviceInfo>();

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-list_devices true -f dshow -i dummy",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                // FFmpeg 将设备列表输出到 stderr
                var output = errorBuilder.ToString();

                // 解析音频设备
                // 格式: [dshow @ ...] "设备名称" (audio)
                var pattern = @"\[dshow @ [^\]]+\] ""([^""]+)""\s*\(audio\)";
                var matches = Regex.Matches(output, pattern, RegexOptions.Multiline);

                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var deviceName = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(deviceName))
                        {
                            devices.Add(new AudioDeviceInfo
                            {
                                DeviceName = deviceName,
                                DisplayName = deviceName
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"执行 FFmpeg 命令失败: {ex.Message}", ex);
            }

            return devices;
        }

        private string? FindFFmpegExecutable()
        {
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // 1. 搜索当前程序目录及其子目录
            string ffmpegPath = SearchFFmpegInDirectory(appDirectory);
            if (!string.IsNullOrEmpty(ffmpegPath))
            {
                return ffmpegPath;
            }

            // 2. 向上查找项目根目录
            string currentDir = appDirectory;
            for (int i = 0; i < 6; i++)
            {
                currentDir = Directory.GetParent(currentDir)?.FullName;
                if (currentDir == null) break;

                string toolsPath = Path.Combine(currentDir, "tools", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(toolsPath))
                {
                    return toolsPath;
                }

                string toolsDir = Path.Combine(currentDir, "tools", "ffmpeg");
                if (Directory.Exists(toolsDir))
                {
                    ffmpegPath = SearchFFmpegInDirectory(toolsDir);
                    if (!string.IsNullOrEmpty(ffmpegPath))
                    {
                        return ffmpegPath;
                    }
                }
            }

            // 3. 搜索常见安装目录
            string[] commonPaths = new string[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // 4. 尝试系统 PATH
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "where",
                        Arguments = "ffmpeg",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    var firstLine = output.Split('\n')[0].Trim();
                    if (File.Exists(firstLine))
                    {
                        return firstLine;
                    }
                }
            }
            catch
            {
                // 忽略错误
            }

            return null;
        }

        private string? SearchFFmpegInDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                // 直接查找
                string ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    return ffmpegPath;
                }

                // 递归搜索子目录
                var subDirs = Directory.GetDirectories(directory);
                foreach (var subDir in subDirs)
                {
                    var result = SearchFFmpegInDirectory(subDir);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }
            }
            catch
            {
                // 忽略错误
            }

            return null;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class AudioDeviceInfo
    {
        public string DeviceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}

