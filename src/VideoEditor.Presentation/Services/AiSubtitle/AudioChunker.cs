using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 音频分块处理服务
    /// 将长音频文件分割为多个块，以便并发处理和避免 API 超时
    /// </summary>
    public class AudioChunker
    {
        private const int DefaultChunkLengthSeconds = 600; // 10分钟
        private const int DefaultChunkOverlapSeconds = 10;  // 10秒重叠
        private const int MaxChunkSizeMB = 25; // API 限制（约25MB）
        private string? _ffmpegPath;

        /// <summary>
        /// 音频块信息
        /// </summary>
        public class AudioChunkInfo
        {
            public string FilePath { get; set; } = string.Empty;
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public TimeSpan Duration => EndTime - StartTime;
            public int Index { get; set; }
        }

        /// <summary>
        /// 将音频文件分割为多个块
        /// </summary>
        public async Task<List<AudioChunkInfo>> SplitAudioAsync(
            string audioPath,
            string ffmpegPath,
            int chunkLengthSeconds = DefaultChunkLengthSeconds,
            int chunkOverlapSeconds = DefaultChunkOverlapSeconds,
            IProgress<(int progress, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // 1. 获取音频信息
            var audioInfo = await GetAudioInfoAsync(audioPath, ffmpegPath, cancellationToken);
            var totalDuration = audioInfo.Duration;

            // 2. 如果音频较短，直接返回
            if (totalDuration.TotalSeconds <= chunkLengthSeconds)
            {
                progress?.Report((100, "音频无需分块"));
                return new List<AudioChunkInfo>
                {
                    new AudioChunkInfo
                    {
                        FilePath = audioPath,
                        StartTime = TimeSpan.Zero,
                        EndTime = totalDuration,
                        Index = 0
                    }
                };
            }

            // 3. 计算分块数量
            var chunks = new List<AudioChunkInfo>();
            var chunkLength = TimeSpan.FromSeconds(chunkLengthSeconds);
            var chunkOverlap = TimeSpan.FromSeconds(chunkOverlapSeconds);
            var tempDir = Path.Combine(Path.GetTempPath(), $"ve_chunks_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var currentStart = TimeSpan.Zero;
                int index = 0;

                while (currentStart < totalDuration)
                {
                    var currentEnd = currentStart + chunkLength;
                    if (currentEnd > totalDuration)
                    {
                        currentEnd = totalDuration;
                    }

                    var chunkPath = Path.Combine(tempDir, $"chunk_{index:D4}.wav");
                    
                    var progressPercent = (int)(currentStart.TotalSeconds / totalDuration.TotalSeconds * 100);
                    progress?.Report((
                        progressPercent,
                        $"正在分块 {index + 1} ({FormatTimeSpan(currentStart)} - {FormatTimeSpan(currentEnd)})"
                    ));

                    // 使用 FFmpeg 提取音频块
                    await ExtractChunkAsync(
                        ffmpegPath,
                        audioPath,
                        chunkPath,
                        currentStart,
                        currentEnd,
                        cancellationToken);

                    chunks.Add(new AudioChunkInfo
                    {
                        FilePath = chunkPath,
                        StartTime = currentStart,
                        EndTime = currentEnd,
                        Index = index
                    });

                    // 移动到下一个块（考虑重叠）
                    currentStart = currentEnd - chunkOverlap;
                    index++;
                }

                progress?.Report((100, $"音频已分为 {chunks.Count} 块"));
                return chunks;
            }
            catch
            {
                // 清理临时文件
                try { Directory.Delete(tempDir, true); } catch { }
                throw;
            }
        }

        /// <summary>
        /// 获取音频文件信息
        /// 参考 VideoCaptioner 的实现，使用 ffmpeg -i 从 stderr 解析 Duration
        /// </summary>
        private async Task<AudioInfo> GetAudioInfoAsync(
            string audioPath,
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            // 方法1: 使用 ffprobe 获取时长（更可靠）
            try
            {
                var ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe").Replace("ffmpeg", "ffprobe");
                if (File.Exists(ffprobePath))
                {
                    var args = $"-i \"{audioPath}\" -show_entries format=duration -v quiet -of csv=p=0";
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = ffprobePath,
                            Arguments = args,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = System.Text.Encoding.UTF8
                        }
                    };

                    process.Start();
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync(cancellationToken);

                    if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, 
                        System.Globalization.CultureInfo.InvariantCulture, out var durationSeconds) 
                        && durationSeconds > 0)
                    {
                        return new AudioInfo
                        {
                            Duration = TimeSpan.FromSeconds(durationSeconds)
                        };
                    }
                }
            }
            catch
            {
                // 如果 ffprobe 失败，继续尝试方法2
            }

            // 方法2: 使用 ffmpeg -i 从 stderr 解析 Duration（参考 VideoCaptioner）
            try
            {
                var args = $"-i \"{audioPath}\"";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    }
                };

                process.Start();
                var errorOutput = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);

                // 解析 Duration: HH:MM:SS.mm 格式
                var durationMatch = System.Text.RegularExpressions.Regex.Match(
                    errorOutput,
                    @"Duration:\s*(\d+):(\d+):(\d+\.\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (durationMatch.Success)
                {
                    var hours = double.Parse(durationMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var minutes = double.Parse(durationMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var seconds = double.Parse(durationMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var totalSeconds = hours * 3600 + minutes * 60 + seconds;

                    if (totalSeconds > 0)
                    {
                        return new AudioInfo
                        {
                            Duration = TimeSpan.FromSeconds(totalSeconds)
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法获取音频时长信息: {ex.Message}", ex);
            }

            throw new InvalidOperationException("无法获取音频时长信息：所有方法均失败");
        }

        /// <summary>
        /// 提取音频块
        /// </summary>
        private async Task ExtractChunkAsync(
            string ffmpegPath,
            string inputPath,
            string outputPath,
            TimeSpan startTime,
            TimeSpan endTime,
            CancellationToken cancellationToken)
        {
            var duration = endTime - startTime;
            var args = $"-y -i \"{inputPath}\" -ss {startTime.TotalSeconds:F3} " +
                      $"-t {duration.TotalSeconds:F3} -acodec pcm_s16le -ar 16000 -ac 1 \"{outputPath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 提取音频块失败 (退出码: {process.ExitCode})");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException($"音频块文件未生成: {outputPath}");
            }
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        }

        /// <summary>
        /// 从视频文件提取完整音频
        /// </summary>
        public async Task<string> ExtractAudioAsync(string videoFilePath, CancellationToken cancellationToken = default)
        {
            var ffmpegPath = FindFFmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException("未找到 FFmpeg 可执行文件");
            }

            var tempAudio = Path.Combine(Path.GetTempPath(), $"ve_asr_{Guid.NewGuid():N}.wav");
            var args = $"-y -i \"{videoFilePath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{tempAudio}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 提取音频失败 (退出码: {process.ExitCode})");
            }

            if (!File.Exists(tempAudio))
            {
                throw new FileNotFoundException($"音频文件未生成: {tempAudio}");
            }

            return tempAudio;
        }

        /// <summary>
        /// 从视频片段提取音频
        /// </summary>
        public async Task<string> ExtractAudioSegmentAsync(
            string videoFilePath,
            long startTimeMs,
            long endTimeMs,
            CancellationToken cancellationToken = default)
        {
            var ffmpegPath = FindFFmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException("未找到 FFmpeg 可执行文件");
            }

            var startTime = TimeSpan.FromMilliseconds(startTimeMs);
            var duration = TimeSpan.FromMilliseconds(endTimeMs - startTimeMs);
            var tempAudio = Path.Combine(Path.GetTempPath(), $"ve_asr_clip_{Guid.NewGuid():N}.wav");
            
            var args = $"-y -i \"{videoFilePath}\" -ss {startTime.TotalSeconds:F3} " +
                      $"-t {duration.TotalSeconds:F3} -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{tempAudio}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 提取音频片段失败 (退出码: {process.ExitCode})");
            }

            if (!File.Exists(tempAudio))
            {
                throw new FileNotFoundException($"音频文件未生成: {tempAudio}");
            }

            return tempAudio;
        }

        /// <summary>
        /// 设置FFmpeg路径
        /// </summary>
        public void SetFFmpegPath(string ffmpegPath)
        {
            if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
            {
                _ffmpegPath = ffmpegPath;
                Debug.WriteLine($"✅ AudioChunker设置FFmpeg路径: {_ffmpegPath}");
            }
            else
            {
                _ffmpegPath = null;
                Debug.WriteLine($"❌ AudioChunker无效的FFmpeg路径: {ffmpegPath}");
            }
        }

        /// <summary>
        /// 查找 FFmpeg 路径
        /// </summary>
        private string? FindFFmpegPath()
        {
            // 1. 如果已设置路径，直接使用
            if (!string.IsNullOrWhiteSpace(_ffmpegPath) && File.Exists(_ffmpegPath))
            {
                return _ffmpegPath;
            }

            // 2. 尝试在常见位置查找
            var commonPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    _ffmpegPath = path; // 缓存找到的路径
                    return path;
                }
            }

            // 3. 尝试从 PATH 环境变量查找
            try
            {
                var process = new Process
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

        private class AudioInfo
        {
            public TimeSpan Duration { get; set; }
        }
    }
}

