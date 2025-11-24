using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoEditor.Presentation.Services;
using Forms = System.Windows.Forms;

namespace VideoEditor.Presentation.Views
{
    /// <summary>
    /// FFmpeg 命令提示符窗口
    /// </summary>
    public partial class CommandPromptWindow : Window
    {
        private Process? _currentProcess;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly List<string> _commandHistory = new List<string>();
        private int _historyIndex = -1;
        private string _currentInput = "";
        private readonly Services.FFmpegCommandHelpService _helpService = new Services.FFmpegCommandHelpService();
        private readonly List<string> _ffmpegCommands = new List<string>
        {
            "ffmpeg", "-version", "-formats", "-codecs", "-encoders", "-decoders",
            "-i", "-c:v", "-c:a", "-b:v", "-b:a", "-r", "-s", "-t", "-ss", "-to",
            "-f", "-y", "-vf", "-af", "-filter_complex", "-map", "-metadata"
        };

        public CommandPromptWindow()
        {
            InitializeComponent();

            // 设置窗口图标
            try
            {
                var iconUri = new Uri("pack://application:,,,/VideoEditor.Presentation;component/Resources/ffmpeg.ico");
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch
            {
                // 忽略图标加载错误
            }

            // 初始化FFmpeg路径
            InitializeFFmpegPath();

            // 设置焦点到命令输入框
            Loaded += (s, e) => CommandTextBox.Focus();

            // 添加关闭事件处理
            Closed += CommandPromptWindow_Closed;
        }

        private void InitializeFFmpegPath()
        {
            try
            {
                // 尝试从PATH环境变量或常用位置找到ffmpeg.exe
                var ffmpegPath = FindFFmpegPath();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    FFmpegPathTextBox.Text = ffmpegPath;
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"警告: 无法自动检测FFmpeg路径 - {ex.Message}", ResolveCommandPromptBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
            }
        }

        private string FindFFmpegPath()
        {
            // 检查PATH环境变量
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? Array.Empty<string>();
            foreach (var path in paths)
            {
                var ffmpegPath = Path.Combine(path, "ffmpeg.exe");
                if (File.Exists(ffmpegPath))
                {
                    return ffmpegPath;
                }
            }

            // 检查常用安装位置
            var commonPaths = new[]
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

            return "ffmpeg.exe"; // 默认值
        }

        private Brush ResolveCommandPromptBrush(string resourceKey, Brush fallback)
        {
            if (TryFindResource(resourceKey) is Brush brush)
            {
                return brush;
            }

            if (Application.Current?.TryFindResource(resourceKey) is Brush appBrush)
            {
                return appBrush;
            }

            return fallback;
        }

        private void AppendOutput(string text, Brush? color = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (color == null)
                {
                    color = ResolveCommandPromptBrush("Brush.CommandPromptTextDefault", Brushes.LightGray);
                }

                OutputTextBox.AppendText(text + Environment.NewLine);
                OutputTextBox.ScrollToEnd();
                OutputScrollViewer.ScrollToBottom();
            });
        }

        private async void ExecuteCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            // 添加到历史记录
            if (!_commandHistory.Contains(command))
            {
                _commandHistory.Add(command);
            }
            _historyIndex = -1;

            // 显示执行的命令
            AppendOutput($"> {command}", ResolveCommandPromptBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));

            // 处理内置命令
            if (HandleBuiltInCommand(command))
            {
                return;
            }

            // 执行FFmpeg命令
            await ExecuteFFmpegCommand(command);
        }

        private bool HandleBuiltInCommand(string command)
        {
            var cmd = command.Trim().ToLower();

            switch (cmd)
            {
                case "clear":
                    Dispatcher.Invoke(() => OutputTextBox.Clear());
                    AppendOutput("FFmpeg 命令提示符已就绪...");
                    AppendOutput("输入 FFmpeg 命令并按 Enter 执行");
                    AppendOutput("输入 'help' 查看可用命令");
                    AppendOutput("输入 'clear' 清空输出");
                    AppendOutput("输入 'exit' 关闭窗口");
                    AppendOutput("");
                    AppendOutput("> ");
                    return true;

                case "help":
                    ShowHelp();
                    return true;
            }

            if (cmd.StartsWith("help "))
            {
                var keyword = cmd.Substring(5).Trim();
                ShowHelpSearch(keyword);
                return true;
            }

            if (cmd.StartsWith("list "))
            {
                var categoryName = cmd.Substring(5).Trim();
                ShowCategoryCommands(categoryName);
                return true;
            }

            switch (cmd)
            {
                case "history":
                    AppendOutput("命令历史:", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
                    for (int i = 0; i < _commandHistory.Count; i++)
                    {
                        AppendOutput($"  {i + 1}: {_commandHistory[i]}");
                    }
                    AppendOutput("");
                    return true;

                case "exit":
                    Close();
                    return true;
            }

            return false;
        }

        private async Task ExecuteFFmpegCommand(string arguments)
        {
            if (_currentProcess != null && !_currentProcess.HasExited)
            {
                AppendOutput("错误: 已有命令正在执行，请等待完成或点击停止按钮", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            var ffmpegPath = FFmpegPathTextBox.Text.Trim();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AppendOutput("错误: 未设置FFmpeg路径", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            if (!File.Exists(ffmpegPath) && ffmpegPath != "ffmpeg.exe")
            {
                AppendOutput($"错误: FFmpeg可执行文件不存在: {ffmpegPath}", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            // 解析命令参数：如果用户输入了完整的"ffmpeg ..."命令，提取参数部分
            var trimmedArgs = arguments.Trim();
            if (trimmedArgs.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                // 移除开头的"ffmpeg"部分
                var ffmpegPrefix = "ffmpeg";
                if (trimmedArgs.Length > ffmpegPrefix.Length &&
                    (trimmedArgs[ffmpegPrefix.Length] == ' ' || trimmedArgs[ffmpegPrefix.Length] == '\t'))
                {
                    arguments = trimmedArgs.Substring(ffmpegPrefix.Length).Trim();
                    AppendOutput($"提取的参数: {arguments}", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                else if (trimmedArgs.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    arguments = "";
                    AppendOutput("执行FFmpeg (无参数)", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Directory.GetCurrentDirectory()
                    },
                    EnableRaisingEvents = true
                };

                _currentProcess = process;

                // 设置进度条
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.Value = 0;
                    ProgressTextBlock.Text = "执行中...";
                    StopButton.IsEnabled = true;
                });

                // 启动进程
                process.Start();

                // 创建任务来读取输出
                var standardOutputBrush = ResolveCommandPromptBrush("Brush.CommandPromptTextOutput", Brushes.White);
                var errorOutputBrush = ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow);
                var outputTask = Task.Run(() => ReadOutput(process.StandardOutput, standardOutputBrush));
                var errorTask = Task.Run(() => ReadError(process.StandardError, errorOutputBrush));

                // 等待进程完成
                await process.WaitForExitAsync(_cancellationTokenSource.Token);

                // 等待输出读取完成
                await Task.WhenAll(outputTask, errorTask);

                // 更新进度条
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.Value = 100;
                    ProgressTextBlock.Text = $"完成 (退出码: {process.ExitCode})";
                    StopButton.IsEnabled = false;
                });

                if (process.ExitCode == 0)
                {
                    AppendOutput("命令执行成功", ResolveCommandPromptBrush("Brush.CommandPromptTextSuccess", Brushes.Green));
                }
                else
                {
                    AppendOutput($"命令执行失败 (退出码: {process.ExitCode})", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                }

                AppendOutput("");
                AppendOutput("> ");
            }
            catch (OperationCanceledException)
            {
                AppendOutput("命令已取消", ResolveCommandPromptBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                AppendOutput("> ");
            }
            catch (Exception ex)
            {
                AppendOutput($"执行命令时出错: {ex.Message}", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                AppendOutput("> ");
            }
            finally
            {
                _currentProcess = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                Dispatcher.Invoke(() =>
                {
                    StopButton.IsEnabled = false;
                });
            }
        }

        private async Task ReadOutput(StreamReader reader, Brush color)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    AppendOutput(line, color);

                    // 解析进度信息
                    ParseProgress(line);
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"读取输出时出错: {ex.Message}", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        private async Task ReadError(StreamReader reader, Brush color)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    AppendOutput(line, color);

                    // 解析进度信息 (FFmpeg进度通常在stderr)
                    ParseProgress(line);
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"读取错误输出时出错: {ex.Message}", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        private void ParseProgress(string line)
        {
            try
            {
                // 匹配FFmpeg进度行，如: "frame=  123 fps= 25 q=28.0 size=     256kB time=00:00:05.00 bitrate= 420.1kbits/s"
                var match = Regex.Match(line, @"frame=\s*(\d+)\s+fps=\s*(\d+(?:\.\d+)?)\s+.*time=(\d{2}:\d{2}:\d{2}(?:\.\d{2})?).*bitrate=\s*(\d+(?:\.\d+)?)kbits/s");

                if (match.Success)
                {
                    var frame = match.Groups[1].Value;
                    var fps = match.Groups[2].Value;
                    var time = match.Groups[3].Value;
                    var bitrate = match.Groups[4].Value;

                    Dispatcher.Invoke(() =>
                    {
                        ProgressTextBlock.Text = $"帧:{frame} FPS:{fps} 时间:{time} 码率:{bitrate}kbps";
                    });
                }
                else
                {
                    // 尝试匹配其他进度模式
                    var durationMatch = Regex.Match(line, @"Duration:\s*(\d{2}:\d{2}:\d{2}(?:\.\d{2})?)");
                    if (durationMatch.Success)
                    {
                        var duration = durationMatch.Groups[1].Value;
                        Dispatcher.Invoke(() =>
                        {
                            ProgressTextBlock.Text = $"时长: {duration}";
                        });
                    }
                }
            }
            catch
            {
                // 忽略解析错误
            }
        }

        private void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    var command = CommandTextBox.Text.Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        ExecuteCommand(command);
                        CommandTextBox.Text = "";
                    }
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (_commandHistory.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            _currentInput = CommandTextBox.Text;
                            _historyIndex = _commandHistory.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }

                        if (_historyIndex >= 0 && _historyIndex < _commandHistory.Count)
                        {
                            CommandTextBox.Text = _commandHistory[_historyIndex];
                            CommandTextBox.Select(CommandTextBox.Text.Length, 0);
                        }
                    }
                    e.Handled = true;
                    break;

                case Key.Down:
                    if (_historyIndex >= 0)
                    {
                        _historyIndex++;
                        if (_historyIndex >= _commandHistory.Count)
                        {
                            CommandTextBox.Text = _currentInput;
                            _historyIndex = -1;
                        }
                        else
                        {
                            CommandTextBox.Text = _commandHistory[_historyIndex];
                        }
                        CommandTextBox.Select(CommandTextBox.Text.Length, 0);
                    }
                    e.Handled = true;
                    break;

                case Key.Tab:
                    // 简单的命令补全
                    TryAutoComplete();
                    e.Handled = true;
                    break;
            }
        }

        private void TryAutoComplete()
        {
            var text = CommandTextBox.Text;
            if (string.IsNullOrEmpty(text)) return;

            // 查找匹配的命令
            var matches = _ffmpegCommands.Where(cmd => cmd.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 1)
            {
                // 只有一个匹配，直接补全
                CommandTextBox.Text = matches[0] + " ";
                CommandTextBox.Select(CommandTextBox.Text.Length, 0);
            }
            else if (matches.Count > 1)
            {
                // 多个匹配，显示可能的补全
                AppendOutput($"可能的补全: {string.Join(", ", matches.Take(5))}", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
            }
        }

        private void CommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 重置历史索引当用户开始输入新内容
            _historyIndex = -1;
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            var command = CommandTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(command))
            {
                ExecuteCommand(command);
                CommandTextBox.Text = "";
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            OutputTextBox.Clear();
            AppendOutput("FFmpeg 命令提示符已就绪...");
            AppendOutput("输入 FFmpeg 命令并按 Enter 执行");
            AppendOutput("输入 'help' 查看可用命令");
            AppendOutput("输入 'clear' 清空输出");
            AppendOutput("输入 'exit' 关闭窗口");
            AppendOutput("");
            AppendOutput("> ");
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopCurrentProcess();
        }

        private void StopCurrentProcess()
        {
            try
            {
                _cancellationTokenSource?.Cancel();

                if (_currentProcess != null && !_currentProcess.HasExited)
                {
                    _currentProcess.Kill();
                    AppendOutput("命令已停止", ResolveCommandPromptBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                    AppendOutput("> ");
                }
            }
            catch (Exception ex)
            {
                AppendOutput($"停止命令时出错: {ex.Message}", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
            }
        }

        private void BrowseFFmpegButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Forms.OpenFileDialog
            {
                Title = "选择FFmpeg可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                FileName = "ffmpeg.exe"
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                FFmpegPathTextBox.Text = dialog.FileName;
            }
        }

        private void CommandPromptWindow_Closed(object? sender, EventArgs e)
        {
            StopCurrentProcess();
        }

        /// <summary>
        /// 显示帮助信息
        /// </summary>
        private void ShowHelp()
        {
            AppendOutput("可用命令:", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            AppendOutput("  help              - 显示此帮助信息");
            AppendOutput("  help <关键词>      - 搜索命令示例");
            AppendOutput("  list <类别>       - 列出指定类别的所有命令");
            AppendOutput("  list categories   - 列出所有命令类别");
            AppendOutput("  clear             - 清空输出窗口");
            AppendOutput("  exit              - 关闭窗口");
            AppendOutput("  history           - 显示命令历史");
            AppendOutput("");
            AppendOutput("命令类别:", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            var categories = _helpService.GetAllCategories();
            foreach (var category in categories)
            {
                AppendOutput($"  • {category.Name} - {category.Description}");
            }
            AppendOutput("");
            AppendOutput("示例:", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
            AppendOutput("  help 裁剪          - 搜索包含'裁剪'的命令");
            AppendOutput("  list 视频剪切      - 列出'视频剪切'类别的所有命令");
            AppendOutput("");
        }

        /// <summary>
        /// 搜索命令
        /// </summary>
        private void ShowHelpSearch(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                AppendOutput("请输入搜索关键词，例如: help 裁剪", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            var results = _helpService.SearchCommands(keyword);
            if (results.Count == 0)
            {
                AppendOutput($"未找到包含 '{keyword}' 的命令", ResolveCommandPromptBrush("Brush.CommandPromptTextNotice", Brushes.Orange));
                AppendOutput("提示: 使用 'list categories' 查看所有类别", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            AppendOutput($"找到 {results.Count} 个匹配的命令:", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            AppendOutput("");
            foreach (var result in results)
            {
                AppendOutput($"【{result.Name}】", ResolveCommandPromptBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));
                AppendOutput($"  说明: {result.Description}");
                AppendOutput($"  命令: {result.Command}", ResolveCommandPromptBrush("Brush.CommandPromptTextOutput", Brushes.White));
                if (!string.IsNullOrWhiteSpace(result.Parameters))
                {
                    AppendOutput($"  参数: {result.Parameters.Replace("\n", "\n        ")}", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                AppendOutput("");
            }
        }

        /// <summary>
        /// 显示类别命令
        /// </summary>
        private void ShowCategoryCommands(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                AppendOutput("请输入类别名称，例如: list 视频剪切", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                return;
            }

            if (categoryName.Equals("categories", StringComparison.OrdinalIgnoreCase))
            {
                AppendOutput("所有命令类别:", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
                var allCategories = _helpService.GetAllCategories();
                foreach (var cat in allCategories)
                {
                    AppendOutput($"  • {cat.Name} - {cat.Description} ({cat.Examples.Count} 个示例)");
                }
                AppendOutput("");
                AppendOutput("使用 'list <类别名>' 查看该类别的详细命令", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            var foundCategory = _helpService.GetCategoryByName(categoryName);
            if (foundCategory == null)
            {
                AppendOutput($"未找到类别 '{categoryName}'", ResolveCommandPromptBrush("Brush.CommandPromptTextError", Brushes.Red));
                AppendOutput("使用 'list categories' 查看所有类别", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                return;
            }

            AppendOutput($"【{foundCategory.Name}】", ResolveCommandPromptBrush("Brush.CommandPromptTextHighlight", Brushes.Yellow));
            AppendOutput($"{foundCategory.Description}");
            AppendOutput("");
            AppendOutput($"共 {foundCategory.Examples.Count} 个命令示例:", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
            AppendOutput("");

            foreach (var example in foundCategory.Examples)
            {
                AppendOutput($"【{example.Name}】", ResolveCommandPromptBrush("Brush.CommandPromptTextCommand", Brushes.Cyan));
                AppendOutput($"  说明: {example.Description}");
                AppendOutput($"  命令: {example.Command}", ResolveCommandPromptBrush("Brush.CommandPromptTextOutput", Brushes.White));
                if (!string.IsNullOrWhiteSpace(example.Parameters))
                {
                    AppendOutput($"  参数: {example.Parameters.Replace("\n", "\n        ")}", ResolveCommandPromptBrush("Brush.CommandPromptTextInfo", Brushes.Gray));
                }
                AppendOutput("");
            }
        }
    }
}
