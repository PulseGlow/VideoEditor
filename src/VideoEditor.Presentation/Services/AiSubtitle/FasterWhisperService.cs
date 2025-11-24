using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// Faster Whisper 本地 ASR 服务实现
    /// 参考 VideoCaptioner 的 FasterWhisperASR
    /// </summary>
    public class FasterWhisperService
    {
        private readonly string _programPath;
        private readonly string _modelName;
        private readonly string _modelsRootDir;
        private readonly string _modelDir;
        private readonly string _language;
        private readonly string _device;
        private readonly bool _needWordTimeStamp;
        private readonly bool _vadFilter;
        private readonly double _vadThreshold;
        private readonly string? _vadMethod;
        private readonly string? _prompt;

        public FasterWhisperService(
            string programPath,
            string modelName,
            string modelsRootDir,
            string modelDir,
            string language = "zh",
            string device = "cpu",
            bool needWordTimeStamp = false,
            bool vadFilter = true,
            double vadThreshold = 0.4,
            string? vadMethod = null,
            string? prompt = null)
        {
            _programPath = programPath;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                throw new ArgumentException("必须指定 Faster Whisper 模型名称", nameof(modelName));
            }

            if (string.IsNullOrWhiteSpace(modelsRootDir) || !Directory.Exists(modelsRootDir))
            {
                throw new DirectoryNotFoundException($"模型根目录不存在: {modelsRootDir}");
            }

            if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            {
                throw new DirectoryNotFoundException($"模型目录不存在: {modelDir}");
            }

            var requiredFiles = new[]
            {
                "model.bin",
                "tokenizer.json",
                "vocabulary.json",
                "config.json"
            };

            foreach (var file in requiredFiles)
            {
                var path = Path.Combine(modelDir, file);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"模型目录缺少必要文件: {path}");
                }
            }

            _modelName = modelName;
            _modelsRootDir = modelsRootDir;
            _modelDir = modelDir;
            _language = language;
            _device = device;
            _needWordTimeStamp = needWordTimeStamp;
            _vadFilter = vadFilter;
            _vadThreshold = vadThreshold;
            _vadMethod = vadMethod;
            _prompt = prompt;
        }

        /// <summary>
        /// 执行 ASR 转录
        /// </summary>
        public async Task<string> TranscribeAsync(
            string audioFilePath,
            IProgress<(int progress, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_programPath))
            {
                throw new FileNotFoundException($"Faster Whisper 程序不存在: {_programPath}");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"ve_fw_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var outputPath = Path.Combine(tempDir, "output.srt");
                var cmd = BuildCommand(audioFilePath, outputPath);
                
                // 记录命令用于调试
                Debug.WriteLine($"FasterWhisper命令: {_programPath} {cmd}");

                progress?.Report((5, "启动 Faster Whisper..."));

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _programPath,
                        Arguments = cmd,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                process.Start();

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                // 异步读取输出
                var outputTask = Task.Run(async () =>
                {
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                    {
                        var line = await process.StandardOutput.ReadLineAsync();
                        if (line != null)
                        {
                            outputBuilder.AppendLine(line);
                            ParseProgress(line, progress);
                        }
                        else
                        {
                            await Task.Delay(100, cancellationToken);
                        }
                    }
                }, cancellationToken);

                var errorTask = Task.Run(async () =>
                {
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line != null)
                        {
                            errorBuilder.AppendLine(line);
                            ParseProgress(line, progress);
                        }
                        else
                        {
                            await Task.Delay(100, cancellationToken);
                        }
                    }
                }, cancellationToken);

                await Task.WhenAll(outputTask, errorTask);
                await process.WaitForExitAsync(cancellationToken);

                var errorOutput = errorBuilder.ToString();
                var standardOutput = outputBuilder.ToString();
                
                // 记录详细输出用于调试
                DebugLogger.LogInfo($"FasterWhisper命令: {_programPath} {cmd}");
                DebugLogger.LogInfo($"FasterWhisper退出码: {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(standardOutput))
                {
                    DebugLogger.LogInfo($"FasterWhisper标准输出: {standardOutput}");
                }
                if (!string.IsNullOrWhiteSpace(errorOutput))
                {
                    DebugLogger.LogInfo($"FasterWhisper错误输出: {errorOutput}");
                }
                
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Faster Whisper 执行失败 (退出码: {process.ExitCode}): {errorOutput}\n命令: {_programPath} {cmd}\n标准输出: {standardOutput}");
                }

                // 检查输出目录中的所有文件
                var outputDir = Path.GetDirectoryName(outputPath);
                var allFiles = Directory.Exists(outputDir) 
                    ? string.Join(", ", Directory.GetFiles(outputDir).Select(f => Path.GetFileName(f)))
                    : "目录不存在";
                
                DebugLogger.LogInfo($"FasterWhisper输出目录: {outputDir}");
                DebugLogger.LogInfo($"FasterWhisper输出目录中的所有文件: {allFiles}");

                var finalOutputPath = ResolveOutputFile(outputPath);
                if (finalOutputPath == null)
                {
                    var errorMsg = $"输出文件不存在: {outputPath}\n" +
                                  $"输出目录: {outputDir}\n" +
                                  $"目录中的所有文件: {allFiles}\n" +
                                  $"命令: {_programPath} {cmd}\n" +
                                  $"标准输出: {standardOutput}\n" +
                                  $"错误输出: {errorOutput}";
                    DebugLogger.LogError($"FasterWhisper输出文件未找到: {errorMsg}");
                    throw new FileNotFoundException(errorMsg);
                }
                
                DebugLogger.LogInfo($"FasterWhisper找到输出文件: {finalOutputPath}");

                progress?.Report((100, "转录完成"));
                return await File.ReadAllTextAsync(finalOutputPath, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // 忽略清理错误
                }
            }
        }

        private string BuildCommand(string audioPath, string outputPath)
        {
            var cmd = new StringBuilder();
            
            // 模型参数（必须在最前面）
            cmd.Append($"--model \"{_modelName}\"");

            if (!string.IsNullOrWhiteSpace(_modelsRootDir))
            {
                cmd.Append($" --model_dir \"{_modelsRootDir}\"");
            }

            // 设备参数
            cmd.Append($" --device {_device}");
            
            // 语言参数（必须在设备参数之后）
            cmd.Append($" --language {_language}");
            
            // 输出格式和路径
            cmd.Append(" --output_format srt");
            cmd.Append($" --output_dir \"{Path.GetDirectoryName(outputPath)}\"");

            // VAD设置（根据错误信息，需要布尔字符串 true/false）
            if (_vadFilter)
            {
                cmd.Append(" --vad_filter true");
                cmd.Append($" --vad_threshold {_vadThreshold:F2}");
                if (!string.IsNullOrWhiteSpace(_vadMethod))
                {
                    cmd.Append($" --vad_method {_vadMethod}");
                }
            }
            else
            {
                cmd.Append(" --vad_filter false");
            }

            // 时间戳设置（根据usage，--word_timestamps需要布尔值）
            if (_needWordTimeStamp)
            {
                cmd.Append(" --word_timestamps true");
                cmd.Append(" --one_word 1");
            }
            else
            {
                // 注意：--sentence 要求 --word_timestamps=True，所以不能同时使用
                // 当不需要单词级时间戳时，只设置 --word_timestamps false 和 --one_word 0
                cmd.Append(" --word_timestamps false");
                cmd.Append(" --one_word 0");
                // 不添加 --sentence，因为它要求 word_timestamps=True
            }

            // 提示词
            if (!string.IsNullOrWhiteSpace(_prompt))
            {
                cmd.Append($" --initial_prompt \"{_prompt}\"");
            }

            // 进度显示
            cmd.Append(" --print_progress");
            
            // 其他选项
            cmd.Append(" --beep_off");
            
            // 音频文件路径（必须在最后，作为位置参数）
            cmd.Append($" \"{audioPath}\"");

            return cmd.ToString();
        }

        private string? ResolveOutputFile(string expectedPath)
        {
            if (File.Exists(expectedPath))
            {
                return expectedPath;
            }

            var directory = Path.GetDirectoryName(expectedPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                DebugLogger.LogWarning($"FasterWhisper输出目录不存在: {directory}");
                return null;
            }

            // 查找所有SRT文件
            var candidates = Directory.GetFiles(directory, "*.srt", SearchOption.TopDirectoryOnly);
            DebugLogger.LogInfo($"FasterWhisper在目录 {directory} 中找到 {candidates.Length} 个SRT文件");
            foreach (var file in candidates)
            {
                DebugLogger.LogInfo($"  - {Path.GetFileName(file)}");
            }

            if (candidates.Length == 0)
            {
                // 也查找所有文件，看看生成了什么
                var allFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
                DebugLogger.LogInfo($"FasterWhisper目录中的所有文件 ({allFiles.Length} 个):");
                foreach (var file in allFiles)
                {
                    DebugLogger.LogInfo($"  - {Path.GetFileName(file)}");
                }
                return null;
            }

            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            // 多个文件时，选择最新的
            Array.Sort(candidates, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            DebugLogger.LogInfo($"FasterWhisper选择最新的文件: {Path.GetFileName(candidates[0])}");
            return candidates[0];
        }

        private void ParseProgress(string line, IProgress<(int progress, string message)>? progress)
        {
            if (progress == null) return;

            // 解析进度百分比，例如: "50%"
            var match = Regex.Match(line, @"(\d+)%");
            if (match.Success)
            {
                var percent = int.Parse(match.Groups[1].Value);
                var mappedProgress = 5 + (int)(percent * 0.9);
                progress.Report((mappedProgress, $"转录中: {percent}%"));
            }

            if (line.Contains("Subtitles are written to", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report((100, "字幕已生成"));
            }
        }
    }
}

