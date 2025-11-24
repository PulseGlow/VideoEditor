using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 视频信息获取服务
    /// </summary>
    public class VideoInformationService
    {
        private string? _ffprobePath;

        public VideoInformationService()
        {
            // FFprobe路径将通过SetFFprobePath方法设置，避免硬编码路径查找
            _ffprobePath = null;
        }

        /// <summary>
        /// 设置FFprobe可执行文件路径
        /// </summary>
        /// <param name="ffprobePath">FFprobe.exe的完整路径</param>
        public void SetFFprobePath(string ffprobePath)
        {
            if (!string.IsNullOrEmpty(ffprobePath) && File.Exists(ffprobePath))
            {
                _ffprobePath = ffprobePath;
                Debug.WriteLine($"✅ 设置FFprobe路径: {_ffprobePath}");
            }
            else
            {
                _ffprobePath = null;
                Debug.WriteLine($"❌ 无效的FFprobe路径: {ffprobePath}");
            }
        }

        /// <summary>
        /// 获取视频信息
        /// </summary>
        /// <param name="filePath">视频文件路径</param>
        /// <returns>视频文件信息</returns>
        public async Task<VideoFile> GetVideoInformationAsync(string filePath)
        {
            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    Debug.WriteLine("文件路径为空");
                    return CreateBasicVideoInfo(filePath ?? string.Empty, "文件路径为空");
                }

                // 检查FFprobe工具
                if (_ffprobePath == null || !File.Exists(_ffprobePath))
                {
                    Debug.WriteLine($"FFprobe工具不存在: {_ffprobePath}");
                    return CreateBasicVideoInfo(filePath, "FFprobe工具不存在，请检查tools/ffmpeg目录");
                }

                // 检查视频文件
                if (!File.Exists(filePath))
                {
                    Debug.WriteLine($"视频文件不存在: {filePath}");
                    return CreateBasicVideoInfo(filePath, "视频文件不存在");
                }

                // 构建ffprobe命令
                var arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";
                Debug.WriteLine($"执行命令: {_ffprobePath} {arguments}");

                var processInfo = new ProcessStartInfo
                {
                    FileName = _ffprobePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var process = new Process { StartInfo = processInfo };
                
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"启动FFprobe进程失败: {ex.Message}");
                    return CreateBasicVideoInfo(filePath, $"启动FFprobe进程失败: {ex.Message}");
                }

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    Debug.WriteLine($"FFprobe执行失败 (退出码: {process.ExitCode}): {error}");
                    return CreateBasicVideoInfo(filePath, $"FFprobe执行失败: {error}");
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    Debug.WriteLine("FFprobe输出为空");
                    return CreateBasicVideoInfo(filePath, "FFprobe输出为空");
                }

                Debug.WriteLine($"FFprobe输出: {output}");

                // 解析JSON输出
                return ParseVideoInfo(filePath, output);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取视频信息时发生错误: {ex.Message}");
                return CreateBasicVideoInfo(filePath, ex.Message);
            }
        }

        /// <summary>
        /// 解析视频信息
        /// </summary>
        private VideoFile ParseVideoInfo(string filePath, string jsonOutput)
        {
            try
            {
                using var document = JsonDocument.Parse(jsonOutput);
                var root = document.RootElement;

                var videoFile = new VideoFile(filePath);

                // 解析格式信息
                if (root.TryGetProperty("format", out var format))
                {
                    if (format.TryGetProperty("duration", out var durationElement))
                    {
                        if (double.TryParse(durationElement.GetString(), out var durationSeconds))
                        {
                            videoFile.Duration = TimeSpan.FromSeconds(durationSeconds);
                        }
                    }
                }

                // 解析流信息
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("codec_type", out var codecType))
                        {
                            var codecTypeValue = codecType.GetString();

                            if (codecTypeValue == "video")
                            {
                                // 视频流信息
                                if (stream.TryGetProperty("codec_name", out var videoCodec))
                                {
                                    videoFile.VideoCodec = videoCodec.GetString() ?? "Unknown";
                                }

                                if (stream.TryGetProperty("width", out var width))
                                {
                                    videoFile.Width = width.GetInt32();
                                }

                                if (stream.TryGetProperty("height", out var height))
                                {
                                    videoFile.Height = height.GetInt32();
                                }

                                if (stream.TryGetProperty("r_frame_rate", out var frameRate))
                                {
                                    var frameRateStr = frameRate.GetString();
                                    if (!string.IsNullOrEmpty(frameRateStr) && frameRateStr.Contains('/'))
                                    {
                                        var parts = frameRateStr.Split('/');
                                        if (parts.Length == 2 && 
                                            double.TryParse(parts[0], out var numerator) && 
                                            double.TryParse(parts[1], out var denominator) && 
                                            denominator != 0)
                                        {
                                            videoFile.FrameRate = numerator / denominator;
                                        }
                                    }
                                }

                                // 视频比特率
                                if (stream.TryGetProperty("bit_rate", out var videoBitrate))
                                {
                                    var bitrateStr = videoBitrate.GetString();
                                    if (!string.IsNullOrEmpty(bitrateStr) && long.TryParse(bitrateStr, out var bitrate))
                                    {
                                        videoFile.VideoBitrate = FormatBitrate(bitrate);
                                    }
                                }
                            }
                            else if (codecTypeValue == "audio")
                            {
                                // 音频流信息
                                if (stream.TryGetProperty("codec_name", out var audioCodec))
                                {
                                    videoFile.AudioCodec = audioCodec.GetString() ?? "Unknown";
                                }

                                if (stream.TryGetProperty("channels", out var channels))
                                {
                                    videoFile.AudioChannels = channels.GetInt32();
                                }

                                if (stream.TryGetProperty("sample_rate", out var sampleRate))
                                {
                                    var sampleRateStr = sampleRate.GetString();
                                    if (!string.IsNullOrEmpty(sampleRateStr) && int.TryParse(sampleRateStr, out var rate))
                                    {
                                        videoFile.SampleRate = rate;
                                    }
                                }

                                // 音频比特率
                                if (stream.TryGetProperty("bit_rate", out var audioBitrate))
                                {
                                    var bitrateStr = audioBitrate.GetString();
                                    if (!string.IsNullOrEmpty(bitrateStr) && long.TryParse(bitrateStr, out var bitrate))
                                    {
                                        videoFile.AudioBitrate = FormatBitrate(bitrate);
                                    }
                                }
                            }
                        }
                    }
                }

                Debug.WriteLine($"✅ 解析完成:");
                Debug.WriteLine($"   - 时长: {videoFile.Duration}");
                Debug.WriteLine($"   - 分辨率: {videoFile.Width}x{videoFile.Height}");
                Debug.WriteLine($"   - 帧率: {videoFile.FrameRate:F2} fps");
                Debug.WriteLine($"   - 视频编码: {videoFile.VideoCodec}");
                Debug.WriteLine($"   - 视频码率: {videoFile.VideoBitrate}");
                Debug.WriteLine($"   - 音频编码: {videoFile.AudioCodec}");
                Debug.WriteLine($"   - 音频码率: {videoFile.AudioBitrate}");
                Debug.WriteLine($"   - 音频通道: {videoFile.AudioChannels}");
                Debug.WriteLine($"   - 采样率: {videoFile.SampleRate} Hz");
                return videoFile;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"解析视频信息时发生错误: {ex.Message}");
                return CreateBasicVideoInfo(filePath, $"解析错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建基本视频信息
        /// </summary>
        private VideoFile CreateBasicVideoInfo(string filePath, string errorMessage)
        {
            var videoFile = new VideoFile(filePath);
            videoFile.Duration = TimeSpan.Zero;
            videoFile.FormattedDuration = "00:00:00.000";
            videoFile.Width = 0;
            videoFile.Height = 0;
            videoFile.VideoCodec = "Unknown";
            videoFile.AudioCodec = "Unknown";
            videoFile.VideoBitrate = "未知";
            videoFile.AudioBitrate = "未知";
            videoFile.AudioChannels = 0;
            videoFile.SampleRate = 0;
            videoFile.FrameRate = 0;
            
            // 在文件名中添加错误信息
            videoFile.FileName = $"{Path.GetFileName(filePath)} (信息获取失败)";
            
            Debug.WriteLine($"创建基本信息 - 错误: {errorMessage}");
            
            return videoFile;
        }

        /// <summary>
        /// 格式化比特率
        /// </summary>
        private string FormatBitrate(long bitsPerSecond)
        {
            if (bitsPerSecond <= 0)
                return "未知";

            // 转换为 kbps
            double kbps = bitsPerSecond / 1000.0;
            
            if (kbps < 1000)
            {
                return $"{kbps:F0} kbps";
            }
            else
            {
                // 转换为 Mbps
                double mbps = kbps / 1000.0;
                return $"{mbps:F2} Mbps";
            }
        }
    }
}
