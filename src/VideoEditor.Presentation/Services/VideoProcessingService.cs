using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;
using Models = VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 视频处理服务 - 负责视频裁剪等处理操作
    /// </summary>
    public class VideoProcessingService
    {
        private string? _ffmpegPath;
        private readonly VideoInformationService _videoInformationService;

        public VideoProcessingService()
        {
            // 初始化视频信息服务
            _videoInformationService = new VideoInformationService();

            // FFmpeg路径将通过SetFFmpegPath方法设置，避免硬编码路径查找
            _ffmpegPath = null;
        }

        /// <summary>
        /// 设置FFmpeg可执行文件路径
        /// </summary>
        /// <param name="ffmpegPath">FFmpeg.exe的完整路径</param>
        public void SetFFmpegPath(string ffmpegPath)
        {
            if (!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath))
            {
                _ffmpegPath = ffmpegPath;
                Debug.WriteLine($"✅ 设置FFmpeg路径: {_ffmpegPath}");
            }
            else
            {
                _ffmpegPath = null;
                Debug.WriteLine($"❌ 无效的FFmpeg路径: {ffmpegPath}");
            }
        }

        /// <summary>
        /// 获取FFmpeg可执行文件路径
        /// </summary>
        public string? GetFFmpegPath()
        {
            return _ffmpegPath;
        }

        /// <summary>
        /// 裁剪视频
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出视频路径</param>
        /// <param name="cropParameters">裁剪参数</param>
        /// <param name="progressCallback">进度回调函数</param>
        /// <returns>处理结果</returns>
        public async Task<VideoProcessingResult> CropVideoAsync(
            string inputPath,
            string outputPath,
            Models.CropParameters cropParameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件路径不能为空";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                if (!File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入视频文件不存在";
                    return result;
                }

                // 验证裁剪参数
                if (cropParameters.Width <= 0 || cropParameters.Height <= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "裁剪尺寸无效";
                    return result;
                }

                if (cropParameters.X < 0 || cropParameters.Y < 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "裁剪坐标不能为负数";
                    return result;
                }

                // 确保输出目录存在
                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                // 构建FFmpeg命令
                var arguments = BuildCropArguments(inputPath, outputPath, cropParameters, videoCodec, quality, audioCodec, audioBitrate, customArgs);

                Debug.WriteLine($"执行FFmpeg裁剪: {_ffmpegPath} {arguments}");

                // 检查取消状态
                cancellationToken.ThrowIfCancellationRequested();

                // 执行FFmpeg (传入视频总时长用于精确进度计算)
                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                if (result.Success)
                {
                    Debug.WriteLine($"✅ 视频裁剪成功: {outputPath}");
                }
                else
                {
                    Debug.WriteLine($"❌ 视频裁剪失败: {result.ErrorMessage}");
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"裁剪处理异常: {ex.Message}";
                Debug.WriteLine($"裁剪异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 按时间范围剪切视频（默认使用 copy 模式）
        /// </summary>
        public async Task<VideoProcessingResult> CutClipAsync(
            string inputPath,
            string outputPath,
            long startTimeMs,
            long endTimeMs,
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                if (endTimeMs <= startTimeMs)
                {
                    result.Success = false;
                    result.ErrorMessage = "片段时长必须大于 0";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var start = TimeSpan.FromMilliseconds(startTimeMs);
                var end = TimeSpan.FromMilliseconds(endTimeMs);
                var arguments = BuildClipCutArguments(inputPath, outputPath, start, end, customArgs);

                var clipDuration = TimeSpan.FromMilliseconds(endTimeMs - startTimeMs);
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, clipDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"剪切处理异常: {ex.Message}";
                Debug.WriteLine($"剪切异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 构建裁剪FFmpeg命令参数
        /// </summary>
        private string BuildCropArguments(string inputPath, string outputPath, Models.CropParameters parameters,
            string videoCodec = "libx264", int quality = 20, string audioCodec = "aac",
            string audioBitrate = "128k", string customArgs = "")
        {
            // 检查是否可以进行裁剪：复制编码器不能与过滤器同时使用
            if (videoCodec.ToLower() == "copy")
            {
                throw new InvalidOperationException("无法使用复制编码器进行裁剪操作。裁剪需要重新编码视频，请选择H.264或H.265编码器。");
            }

            // 基本裁剪参数: -filter:v "crop=w:h:x:y"
            var cropFilter = $"crop={parameters.Width}:{parameters.Height}:{parameters.X}:{parameters.Y}";

            // 构建基础参数
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\"",
                "-filter:v", $"\"{cropFilter}\""
            };

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                // 简单的参数解析，将空格分隔的参数添加到列表中
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 添加通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建精确剪切FFmpeg命令参数（-ss在-i之前，提升剪切精度）
        /// </summary>
        public static string BuildClipCutArguments(string inputPath, string outputPath, TimeSpan start, TimeSpan end, string customArgs = "")
        {
            var args = new List<string>
            {
                "-ss", FormatTimeSpan(start),  // -ss在-i之前，提升剪切精度
                "-i", $"\"{inputPath}\"",
                "-to", FormatTimeSpan(end),
                "-c", "copy"
            };

            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                args.Add(customArgs.Trim());
            }

            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建合并视频FFmpeg命令参数（使用concat demuxer）
        /// </summary>
        public static string BuildConcatArguments(
            string concatListFilePath,
            string outputPath,
            string videoCodec = "copy",
            string audioCodec = "copy",
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-f", "concat",
                "-safe", "0",
                "-i", $"\"{concatListFilePath}\""
            };

            var isVideoCopy = string.Equals(videoCodec, "copy", StringComparison.OrdinalIgnoreCase);
            var isAudioCopy = string.Equals(audioCodec, "copy", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(audioCodec);

            if (isVideoCopy && isAudioCopy)
            {
                args.AddRange(new[] { "-c", "copy" });
            }
            else
            {
                args.AddRange(new[] { "-c:v", videoCodec });
                if (!isAudioCopy)
                {
                    args.AddRange(new[] { "-c:a", audioCodec });
                }
                else
                {
                    args.AddRange(new[] { "-c:a", "copy" });
                }
            }

            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                args.Add(customArgs.Trim());
            }

            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 创建concat列表文件
        /// </summary>
        public static string CreateConcatListFile(string outputDirectory, List<string> videoFiles, string? fileName = null)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Path.GetTempPath();
            }

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var safeFileName = string.IsNullOrWhiteSpace(fileName)
                ? $"concat_list_{DateTime.Now:yyyyMMddHHmmssfff}.txt"
                : fileName;

            var listFilePath = Path.Combine(outputDirectory, safeFileName);
            var lines = videoFiles.Select(file => $"file '{file.Replace("'", "'\\''")}'");
            File.WriteAllLines(listFilePath, lines);
            return listFilePath;
        }

        /// <summary>
        /// 添加水印
        /// </summary>
        public async Task<VideoProcessingResult> AddWatermarkAsync(
            string inputPath,
            string outputPath,
            Models.WatermarkParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                if (parameters.Type == Models.WatermarkType.None)
                {
                    result.Success = false;
                    result.ErrorMessage = "未选择水印类型";
                    return result;
                }

                // 验证图片水印参数
                if (parameters.Type == Models.WatermarkType.Image)
                {
                    if (string.IsNullOrWhiteSpace(parameters.ImagePath) || !File.Exists(parameters.ImagePath))
                    {
                        result.Success = false;
                        result.ErrorMessage = "水印图片文件不存在";
                        return result;
                    }
                }

                // 验证文字水印参数
                if (parameters.Type == Models.WatermarkType.Text)
                {
                    if (string.IsNullOrWhiteSpace(parameters.Text))
                    {
                        result.Success = false;
                        result.ErrorMessage = "水印文字不能为空";
                        return result;
                    }
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildWatermarkArguments(inputPath, outputPath, parameters, videoCodec, quality, audioCodec, audioBitrate, customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"添加水印异常: {ex.Message}";
                Debug.WriteLine($"添加水印异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 移除水印（模糊化处理）
        /// </summary>
        public async Task<VideoProcessingResult> RemoveWatermarkAsync(
            string inputPath,
            string outputPath,
            Models.RemoveWatermarkParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                if (parameters.Width <= 0 || parameters.Height <= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "移除区域尺寸无效";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildRemoveWatermarkArguments(inputPath, outputPath, parameters, videoCodec, quality, audioCodec, audioBitrate, customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"移除水印异常: {ex.Message}";
                Debug.WriteLine($"移除水印异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 构建添加水印FFmpeg命令参数
        /// </summary>
        public static string BuildWatermarkArguments(
            string inputPath,
            string outputPath,
            Models.WatermarkParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "")
        {
            var args = new List<string>();

            if (parameters.Type == Models.WatermarkType.Image)
            {
                // 图片水印：使用 overlay 滤镜
                // overlay滤镜本身不支持alpha参数，需要使用format和fade滤镜组合来实现透明度
                // -i input.mp4 -i watermark.png -filter_complex "[1:v]format=rgba,fade=t=in:st=0:d=0:alpha=1,setpts=PTS-STARTPTS[wm];[0:v][wm]overlay=x:y" output.mp4
                args.Add("-i");
                args.Add($"\"{inputPath}\"");
                args.Add("-i");
                args.Add($"\"{parameters.ImagePath}\"");

                // 计算透明度 (0-1)
                var opacity = Math.Max(0, Math.Min(1, parameters.ImageOpacity / 100.0));
                // 使用format和fade滤镜处理透明度，然后overlay
                var filterComplex = $"[1:v]format=rgba,geq=r='r(X,Y)':a='{opacity:F2}*alpha(X,Y)'[wm];[0:v][wm]overlay={parameters.X}:{parameters.Y}";

                args.Add("-filter_complex");
                args.Add($"\"{filterComplex}\"");
            }
            else if (parameters.Type == Models.WatermarkType.Text)
            {
                // 文字水印：使用 drawtext 滤镜
                // -vf "drawtext=text='水印文字':x=10:y=10:fontsize=24:fontcolor=white@0.8"
                args.Add("-i");
                args.Add($"\"{inputPath}\"");

                // 计算透明度 (0-1)
                var opacity = Math.Max(0, Math.Min(1, parameters.TextOpacity / 100.0));
                // 转义单引号和特殊字符
                var escapedText = parameters.Text.Replace("'", "\\'").Replace(":", "\\:");
                var drawtextFilter = $"drawtext=text='{escapedText}':x={parameters.X}:y={parameters.Y}:fontsize={parameters.FontSize}:fontcolor={parameters.TextColor}@{opacity:F2}";

                args.Add("-vf");
                args.Add($"\"{drawtextFilter}\"");
            }

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                case "copy":
                    args.AddRange(new[] { "-c:v", "copy" });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建移除水印FFmpeg命令参数（使用 crop + boxblur + overlay 组合模糊化）
        /// </summary>
        public static string BuildRemoveWatermarkArguments(
            string inputPath,
            string outputPath,
            Models.RemoveWatermarkParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 使用改进的模糊方法，让移除区域更柔和、更自然
            // 方案：crop + 高斯模糊(gblur) + 边缘扩展 + overlay
            // 这种方法兼容性好，效果可控，边缘更柔和
            
            // 步骤：
            // 1. 从原视频中裁剪出水印区域（扩展边缘以包含羽化区域）[crop]
            // 2. 对裁剪区域应用高斯模糊（比boxblur更柔和自然）[blur]
            // 3. 将模糊后的区域叠加回原视频的对应位置，使用边缘羽化 [overlay]
            
            // 计算边缘羽化区域大小（约为区域的10-15%）
            var featherSize = Math.Max(2, Math.Min(parameters.Width, parameters.Height) / 10);
            
            // 扩展裁剪区域以包含羽化边缘
            var cropX = Math.Max(0, parameters.X - featherSize);
            var cropY = Math.Max(0, parameters.Y - featherSize);
            var cropW = parameters.Width + featherSize * 2;
            var cropH = parameters.Height + featherSize * 2;
            
            // 使用高斯模糊（gblur）代替boxblur，效果更柔和自然
            // sigma值控制模糊强度，15-20效果较好
            var blurSigma = 18;
            
            // 构建滤镜链：
            // 1. 裁剪扩展区域（包含羽化边缘）
            // 2. 应用高斯模糊（更柔和）
            // 3. 叠加回原视频（overlay会自动处理边缘融合）
            // 扩展的裁剪区域会在边缘自然融合，因为模糊效果会延伸到边缘
            var filterComplex = $"[0:v]crop={cropW}:{cropH}:{cropX}:{cropY}[crop];" +
                               $"[crop]gblur=sigma={blurSigma}[blur];" +
                               $"[0:v][blur]overlay={cropX}:{cropY}";

            args.Add("-filter_complex");
            args.Add($"\"{filterComplex}\"");

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                case "copy":
                    args.AddRange(new[] { "-c:v", "copy" });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 应用去重处理
        /// </summary>
        public async Task<VideoProcessingResult> ApplyDeduplicateAsync(
            string inputPath,
            string outputPath,
            Models.DeduplicateParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                if (parameters.Mode == Models.DeduplicateMode.Off)
                {
                    result.Success = false;
                    result.ErrorMessage = "去重模式已关闭，无需处理";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildDeduplicateArguments(inputPath, outputPath, parameters, videoCodec, quality, audioCodec, audioBitrate, customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"去重处理异常: {ex.Message}";
                Debug.WriteLine($"去重处理异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 构建去重处理FFmpeg命令参数
        /// </summary>
        public static string BuildDeduplicateArguments(
            string inputPath,
            string outputPath,
            Models.DeduplicateParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 构建视频滤镜链
            var filters = new List<string>();

            // 1. 边缘裁剪（如果启用）
            if (parameters.CropEdge > 0)
            {
                var cropValue = (int)parameters.CropEdge;
                // crop=iw-2*X:ih-2*Y:X:Y (从边缘裁剪X像素)
                filters.Add($"crop=iw-{cropValue * 2}:ih-{cropValue * 2}:{cropValue}:{cropValue}");
            }

            // 2. 色彩调整（亮度、对比度、饱和度）
            // 使用eq滤镜，参数格式：eq=brightness=值:contrast=值:saturation=值
            var eqParams = new List<string>();
            if (parameters.Brightness != 0)
            {
                // brightness范围是-1.0到1.0，这里将百分比转换为值
                var brightnessValue = Math.Max(-1.0, Math.Min(1.0, parameters.Brightness / 100.0));
                eqParams.Add($"brightness={brightnessValue:F3}");
            }
            if (parameters.Contrast != 0)
            {
                // contrast范围是-1000到1000，这里将百分比转换为值（1.0为原始对比度）
                var contrastValue = 1.0 + (parameters.Contrast / 100.0);
                contrastValue = Math.Max(0.0, Math.Min(2.0, contrastValue));
                eqParams.Add($"contrast={contrastValue:F3}");
            }
            if (parameters.Saturation != 0)
            {
                // saturation范围是0.0到3.0，这里将百分比转换为值（1.0为原始饱和度）
                var saturationValue = 1.0 + (parameters.Saturation / 100.0);
                saturationValue = Math.Max(0.0, Math.Min(3.0, saturationValue));
                eqParams.Add($"saturation={saturationValue:F3}");
            }

            if (eqParams.Count > 0)
            {
                filters.Add($"eq={string.Join(":", eqParams)}");
            }

            // 3. 噪点（如果启用）
            if (parameters.Noise > 0)
            {
                var noiseValue = (int)parameters.Noise;
                // noise=alls=强度:allf=类型 (alls=所有通道强度, allf=所有通道类型)
                filters.Add($"noise=alls={noiseValue}:allf=t+u");
            }

            // 4. 模糊（如果启用）
            if (parameters.Blur > 0)
            {
                var blurValue = (int)parameters.Blur;
                // boxblur=水平半径:垂直半径
                filters.Add($"boxblur={blurValue}:{blurValue}");
            }

            // 应用滤镜
            if (filters.Count > 0)
            {
                var filterComplex = string.Join(",", filters);
                args.Add("-vf");
                args.Add($"\"{filterComplex}\"");
            }

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                case "copy":
                    args.AddRange(new[] { "-c:v", "copy" });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 应用音频设置（音量、淡入淡出、格式等）
        /// </summary>
        public async Task<VideoProcessingResult> ApplyAudioSettingsAsync(
            string inputPath,
            string outputPath,
            Models.AudioParameters parameters,
            string videoCodec = "copy",
            int quality = 20,
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildAudioSettingsArguments(inputPath, outputPath, parameters, videoCodec, quality, customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"应用音频设置异常: {ex.Message}";
                Debug.WriteLine($"应用音频设置异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 提取音频
        /// </summary>
        public async Task<VideoProcessingResult> ExtractAudioAsync(
            string inputPath,
            string outputPath,
            string audioFormat = "aac",
            string bitrate = "192k",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildExtractAudioArguments(inputPath, outputPath, audioFormat, bitrate);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"提取音频异常: {ex.Message}";
                Debug.WriteLine($"提取音频异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 替换音频
        /// </summary>
        public async Task<VideoProcessingResult> ReplaceAudioAsync(
            string inputVideoPath,
            string inputAudioPath,
            string outputPath,
            string videoCodec = "copy",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputVideoPath) || !File.Exists(inputVideoPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入视频文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(inputAudioPath) || !File.Exists(inputAudioPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入音频文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildReplaceAudioArguments(inputVideoPath, inputAudioPath, outputPath, videoCodec);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputVideoPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"替换音频异常: {ex.Message}";
                Debug.WriteLine($"替换音频异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 删除音频
        /// </summary>
        public async Task<VideoProcessingResult> RemoveAudioAsync(
            string inputPath,
            string outputPath,
            string videoCodec = "copy",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildRemoveAudioArguments(inputPath, outputPath, videoCodec);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"删除音频异常: {ex.Message}";
                Debug.WriteLine($"删除音频异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 构建应用音频设置FFmpeg命令参数
        /// </summary>
        public static string BuildAudioSettingsArguments(
            string inputPath,
            string outputPath,
            Models.AudioParameters parameters,
            string videoCodec = "copy",
            int quality = 20,
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 视频编码（通常保持原样）
            if (videoCodec.ToLower() == "copy")
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }
            else
            {
                args.AddRange(new[] { "-c:v", videoCodec });
            }

            // 音频滤镜链
            var audioFilters = new List<string>();

            // 音量调整
            if (parameters.Volume != 100)
            {
                // volume=0.5 表示50%音量，volume=1.5 表示150%音量
                var volumeValue = parameters.Volume / 100.0;
                audioFilters.Add($"volume={volumeValue:F2}");
            }

            // 淡入淡出
            if (parameters.FadeIn > 0 && parameters.FadeOut > 0)
            {
                // 同时有淡入和淡出，使用afade的链式调用
                // 先淡入，然后淡出（淡出开始时间需要根据视频时长计算，这里使用0作为占位符，实际应该从视频信息获取）
                audioFilters.Add($"afade=t=in:st=0:d={parameters.FadeIn},afade=t=out:st=0:d={parameters.FadeOut}");
            }
            else if (parameters.FadeIn > 0)
            {
                audioFilters.Add($"afade=t=in:st=0:d={parameters.FadeIn}");
            }
            else if (parameters.FadeOut > 0)
            {
                audioFilters.Add($"afade=t=out:st=0:d={parameters.FadeOut}");
            }

            // 应用音频滤镜
            if (audioFilters.Count > 0)
            {
                args.Add("-af");
                args.Add($"\"{string.Join(",", audioFilters)}\"");
            }

            // 音频编码设置
            var audioCodec = GetAudioCodecFromFormat(parameters.Format);
            var bitrate = parameters.Bitrate.Replace(" kbps", "k");

            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", bitrate });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", bitrate });
                    break;
                case "wav":
                case "pcm":
                    args.AddRange(new[] { "-c:a", "pcm_s16le" });
                    break;
                case "flac":
                    args.AddRange(new[] { "-c:a", "flac" });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", bitrate });
                    break;
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建提取音频FFmpeg命令参数
        /// </summary>
        public static string BuildExtractAudioArguments(
            string inputPath,
            string outputPath,
            string audioFormat = "aac",
            string bitrate = "192k")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\"",
                "-vn", // 不包含视频
                "-acodec", "copy" // 先尝试复制
            };

            // 根据格式设置编码器
            var audioCodec = GetAudioCodecFromFormat(audioFormat);
            var bitrateValue = bitrate.Replace(" kbps", "k");

            switch (audioCodec.ToLower())
            {
                case "aac":
                    args.RemoveAt(args.Count - 1); // 移除 "copy"
                    args.RemoveAt(args.Count - 1); // 移除 "-acodec"
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", bitrateValue });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.RemoveAt(args.Count - 1);
                    args.RemoveAt(args.Count - 1);
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", bitrateValue });
                    break;
                case "wav":
                case "pcm":
                    args.RemoveAt(args.Count - 1);
                    args.RemoveAt(args.Count - 1);
                    args.AddRange(new[] { "-c:a", "pcm_s16le" });
                    break;
                case "flac":
                    args.RemoveAt(args.Count - 1);
                    args.RemoveAt(args.Count - 1);
                    args.AddRange(new[] { "-c:a", "flac" });
                    break;
                // copy 保持原样
            }

            args.AddRange(new[] { "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建替换音频FFmpeg命令参数
        /// </summary>
        public static string BuildReplaceAudioArguments(
            string inputVideoPath,
            string inputAudioPath,
            string outputPath,
            string videoCodec = "copy")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputVideoPath}\"",
                "-i", $"\"{inputAudioPath}\"",
                "-c:v", videoCodec.ToLower() == "copy" ? "copy" : videoCodec,
                "-c:a", "aac", // 默认使用AAC编码新音频
                "-map", "0:v:0", // 使用第一个输入的视频流
                "-map", "1:a:0", // 使用第二个输入的音频流
                "-shortest", // 以最短的流为准
                "-y", $"\"{outputPath}\""
            };

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建删除音频FFmpeg命令参数
        /// </summary>
        public static string BuildRemoveAudioArguments(
            string inputPath,
            string outputPath,
            string videoCodec = "copy")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\"",
                "-c:v", videoCodec.ToLower() == "copy" ? "copy" : videoCodec,
                "-an", // 不包含音频
                "-y", $"\"{outputPath}\""
            };

            return string.Join(" ", args);
        }

        /// <summary>
        /// 从格式名称获取FFmpeg编码器名称
        /// </summary>
        private static string GetAudioCodecFromFormat(string format)
        {
            return format.ToUpper() switch
            {
                "AAC" or "AAC (推荐)" => "aac",
                "MP3" => "libmp3lame",
                "WAV" or "WAV (无损)" => "pcm_s16le",
                "FLAC" or "FLAC (无损)" => "flac",
                "复制原格式" => "copy",
                _ => "aac"
            };
        }

        private static string FormatTimeSpan(TimeSpan timeSpan)
        {
            var totalHours = (int)Math.Floor(timeSpan.TotalHours);
            var minutes = timeSpan.Minutes;
            var seconds = timeSpan.Seconds;
            var milliseconds = timeSpan.Milliseconds;

            return $"{totalHours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
        }

        /// <summary>
        /// 执行FFmpeg命令 - 极简化版本使用cmd.exe
        /// </summary>
        /// <summary>
        /// 执行FFmpeg命令 - 极简化版本使用cmd.exe
        /// </summary>
        private async Task<ProcessExecutionResult> ExecuteFFmpegAsync(
            string arguments,
            Action<double>? progressCallback,
            TimeSpan? totalDuration = null,
            CancellationToken cancellationToken = default)
        {
            var result = new ProcessExecutionResult();

            try
            {
                // 直接执行FFmpeg，避免cmd.exe开销
                Debug.WriteLine($"执行FFmpeg命令: {_ffmpegPath} {arguments}");

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = false,
                        RedirectStandardOutput = false,
                        WorkingDirectory = Path.GetDirectoryName(_ffmpegPath) ?? string.Empty
                    }
                };

                // 启动进程
                process.Start();
                Debug.WriteLine($"进程已启动，PID: {process.Id}");

                // 简单的进度模拟（基于时间估算）
                var startTime = DateTime.Now;
                var estimatedDuration = totalDuration?.TotalSeconds ?? 60; // 默认60秒

                // 异步进度更新 - 降低频率避免UI阻塞
                var progressTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                        {
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var progress = Math.Min(elapsed / estimatedDuration, 0.95);
                            progressCallback?.Invoke(progress);
                            await Task.Delay(2000, cancellationToken); // 每2秒更新一次
                        }
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            progressCallback?.Invoke(1.0);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, cancellationToken);

                // 等待进程完成或取消
                var exitTask = process.WaitForExitAsync(cancellationToken);

                try
                {
                    await exitTask;
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    result.Success = false;
                    result.ErrorMessage = "操作被取消";
                    return result;
                }

                // 等待进度任务完成
                await progressTask;

                if (process.ExitCode == 0)
                {
                    result.Success = true;
                    Debug.WriteLine("FFmpeg执行成功");
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"FFmpeg执行失败 (退出码: {process.ExitCode})";
                    Debug.WriteLine($"FFmpeg执行失败，退出码: {process.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"执行异常: {ex.Message}";
                Debug.WriteLine($"执行异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 转码视频
        /// </summary>
        public async Task<VideoProcessingResult> TranscodeAsync(
            string inputPath,
            string outputPath,
            Models.TranscodeParameters parameters,
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件路径不能为空";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                // 构建FFmpeg命令参数
                var arguments = BuildTranscodeArguments(inputPath, outputPath, parameters, customArgs);

                // 执行FFmpeg命令
                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(
                    arguments,
                    progressCallback,
                    totalDuration,
                    cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = processResult.Success ? outputPath : null;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"转码异常: {ex.Message}";
                Debug.WriteLine($"转码异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建转码FFmpeg命令参数
        /// </summary>
        public string BuildTranscodeArguments(
            string inputPath,
            string outputPath,
            Models.TranscodeParameters parameters,
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 根据转码模式设置参数
            switch (parameters.Mode)
            {
                case Models.TranscodeMode.Fast:
                    // 快速模式：使用 -c copy，速度快
                    args.AddRange(new[] { "-c", "copy" });
                    break;

                case Models.TranscodeMode.Standard:
                case Models.TranscodeMode.HighQuality:
                case Models.TranscodeMode.Compress:
                    // 标准/高质量/压缩模式：需要重新编码
                    // 视频编码设置
                    string videoCodec = GetVideoCodecForFFmpeg(parameters.VideoCodec, parameters.Mode, parameters.HardwareAcceleration);
                    string preset = parameters.Mode == Models.TranscodeMode.HighQuality ? "slow" : "faster";
                    
                    if (parameters.HardwareAcceleration && (videoCodec.StartsWith("h264_nvenc") || videoCodec.StartsWith("hevc_nvenc")))
                    {
                        // 硬件加速编码
                        args.AddRange(new[] { "-c:v", videoCodec, "-preset", preset });
                        if (parameters.Mode == Models.TranscodeMode.HighQuality)
                        {
                            args.AddRange(new[] { "-cq", parameters.CRF.ToString() });
                        }
                        else
                        {
                            args.AddRange(new[] { "-b:v", GetBitrateForMode(parameters.Mode) });
                        }
                    }
                    else
                    {
                        // CPU编码
                        args.AddRange(new[] { "-c:v", videoCodec, "-preset", preset, "-crf", parameters.CRF.ToString() });
                    }

                    // 双通道编码
                    if (parameters.DualPass)
                    {
                        // 双通道编码需要两次编码，这里只设置第一次编码的参数
                        // 实际的双通道编码需要在外部处理两次调用
                        args.Add("-pass");
                        args.Add("1");
                        args.Add("-passlogfile");
                        args.Add($"\"{Path.Combine(Path.GetDirectoryName(outputPath) ?? "", Path.GetFileNameWithoutExtension(outputPath))}.ffmpeg2pass\"");
                    }

                    // 音频编码设置
                    string audioCodec = GetAudioCodecForFFmpeg(parameters.AudioCodec);
                    string audioBitrate = parameters.AudioBitrate.Replace(" kbps", "k").Replace(" (推荐)", "");
                    
                    switch (audioCodec.ToLower())
                    {
                        case "copy":
                            args.AddRange(new[] { "-c:a", "copy" });
                            break;
                        case "aac":
                            args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate });
                            break;
                        case "mp3":
                        case "libmp3lame":
                            args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate });
                            break;
                        default:
                            args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate });
                            break;
                    }
                    break;
            }

            // 保留元数据
            if (parameters.KeepMetadata)
            {
                args.Add("-map_metadata");
                args.Add("0");
            }

            // 添加自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                var customParams = customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                args.AddRange(customParams);
            }

            // 通用参数
            args.AddRange(new[] { "-movflags", "+faststart", "-y", $"\"{outputPath}\"" });

            return string.Join(" ", args);
        }

        /// <summary>
        /// 获取视频编码器（根据模式和硬件加速）
        /// </summary>
        private string GetVideoCodecForFFmpeg(string videoCodec, Models.TranscodeMode mode, bool hardwareAccel)
        {
            // 如果选择了复制原编码，返回copy
            if (videoCodec.Contains("复制原编码") || videoCodec.Contains("复制"))
            {
                return "copy";
            }

            // 根据模式选择编码器
            if (mode == Models.TranscodeMode.Compress)
            {
                // 压缩模式使用H.265
                if (hardwareAccel)
                {
                    return "hevc_nvenc"; // NVIDIA硬件加速
                }
                return "libx265";
            }
            else
            {
                // 标准/高质量模式使用H.264
                if (hardwareAccel)
                {
                    return "h264_nvenc"; // NVIDIA硬件加速
                }
                
                // 根据用户选择的编码器
                if (videoCodec.Contains("H.264") || videoCodec.Contains("AVC"))
                {
                    return "libx264";
                }
                else if (videoCodec.Contains("H.265") || videoCodec.Contains("HEVC"))
                {
                    return "libx265";
                }
                else if (videoCodec.Contains("VP9"))
                {
                    return "libvpx-vp9";
                }
                else if (videoCodec.Contains("AV1"))
                {
                    return "libaom-av1";
                }
                
                return "libx264"; // 默认
            }
        }

        /// <summary>
        /// 获取音频编码器
        /// </summary>
        private string GetAudioCodecForFFmpeg(string audioCodec)
        {
            if (audioCodec.Contains("复制原编码") || audioCodec.Contains("复制"))
            {
                return "copy";
            }
            else if (audioCodec.Contains("AAC"))
            {
                return "aac";
            }
            else if (audioCodec.Contains("MP3"))
            {
                return "libmp3lame";
            }
            else if (audioCodec.Contains("Opus"))
            {
                return "libopus";
            }
            else if (audioCodec.Contains("Vorbis"))
            {
                return "libvorbis";
            }
            
            return "aac"; // 默认
        }

        /// <summary>
        /// 根据模式获取比特率
        /// </summary>
        private string GetBitrateForMode(Models.TranscodeMode mode)
        {
            return mode switch
            {
                Models.TranscodeMode.Standard => "5000k",
                Models.TranscodeMode.HighQuality => "8000k",
                Models.TranscodeMode.Compress => "3000k",
                _ => "5000k"
            };
        }

        /// <summary>
        /// 提取视频帧
        /// </summary>
        public async Task<VideoProcessingResult> ExtractFrameAsync(
            string inputPath,
            string outputPath,
            TimeSpan time,
            string format = "png",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件路径不能为空";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                // 构建FFmpeg命令参数
                var arguments = BuildExtractFrameArguments(inputPath, outputPath, time, format);

                // 执行FFmpeg命令（提取帧很快，不需要进度回调）
                var processResult = await ExecuteFFmpegAsync(
                    arguments,
                    null, // 提取帧不需要进度
                    null,
                    cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = processResult.Success ? outputPath : null;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"提取帧异常: {ex.Message}";
                Debug.WriteLine($"提取帧异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建提取帧FFmpeg命令参数
        /// </summary>
        private string BuildExtractFrameArguments(string inputPath, string outputPath, TimeSpan time, string format)
        {
            var args = new List<string>
            {
                "-ss", FormatTimeSpan(time),
                "-i", $"\"{inputPath}\"",
                "-frames:v", "1",  // 只提取一帧
                "-q:v", "2"  // 高质量
            };

            // 根据格式设置编码器
            switch (format.ToLower())
            {
                case "png":
                    args.AddRange(new[] { "-f", "image2", "-vcodec", "png" });
                    break;
                case "jpg":
                case "jpeg":
                    args.AddRange(new[] { "-f", "image2", "-vcodec", "mjpeg", "-q:v", "2" });
                    break;
                case "bmp":
                    args.AddRange(new[] { "-f", "image2", "-vcodec", "bmp" });
                    break;
                case "webp":
                    args.AddRange(new[] { "-f", "image2", "-vcodec", "libwebp", "-quality", "90" });
                    break;
                default:
                    args.AddRange(new[] { "-f", "image2" });
                    break;
            }

            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// 制作GIF
        /// </summary>
        public async Task<VideoProcessingResult> CreateGifAsync(
            string inputPath,
            string outputPath,
            Models.GifParameters parameters,
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件路径不能为空";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                if (parameters.StartTime >= parameters.EndTime)
                {
                    result.Success = false;
                    result.ErrorMessage = "开始时间必须小于结束时间";
                    return result;
                }

                // 构建调色板路径
                var palettePath = Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? "",
                    $"{Path.GetFileNameWithoutExtension(outputPath)}_palette.png");

                // 第一遍：生成调色板
                var paletteArgs = BuildGifPaletteArguments(inputPath, palettePath, parameters);
                var paletteResult = await ExecuteFFmpegAsync(
                    paletteArgs,
                    null, // 调色板生成不需要进度
                    null,
                    cancellationToken);

                if (!paletteResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = $"生成调色板失败: {paletteResult.ErrorMessage}";
                    return result;
                }

                // 第二遍：使用调色板生成GIF
                var gifArgs = BuildGifArguments(inputPath, outputPath, palettePath, parameters);
                var duration = parameters.EndTime - parameters.StartTime;
                var gifResult = await ExecuteFFmpegAsync(
                    gifArgs,
                    progressCallback,
                    duration,
                    cancellationToken);

                // 清理临时调色板文件
                try
                {
                    if (File.Exists(palettePath))
                    {
                        File.Delete(palettePath);
                    }
                }
                catch
                {
                    // 忽略删除失败
                }

                result.Success = gifResult.Success;
                result.ErrorMessage = gifResult.ErrorMessage;
                result.OutputPath = gifResult.Success ? outputPath : null;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"制作GIF异常: {ex.Message}";
                Debug.WriteLine($"制作GIF异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建调色板生成FFmpeg命令参数
        /// </summary>
        public string BuildGifPaletteArguments(string inputPath, string palettePath, Models.GifParameters parameters)
        {
            var args = new List<string>
            {
                "-ss", FormatTimeSpan(parameters.StartTime),
                "-t", FormatTimeSpan(parameters.EndTime - parameters.StartTime),
                "-i", $"\"{inputPath}\"",
                "-vf", $"\"fps={parameters.FPS},scale={parameters.Width}:-1:flags=lanczos,palettegen\"",
                "-y",
                $"\"{palettePath}\""
            };

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建GIF FFmpeg命令参数（使用调色板）
        /// </summary>
        public string BuildGifArguments(string inputPath, string outputPath, string palettePath, Models.GifParameters parameters)
        {
            // 根据质量模式调整dither参数
            string dither = parameters.QualityMode switch
            {
                Models.GifQualityMode.Low => "bayer:bayer_scale=5",
                Models.GifQualityMode.Medium => "bayer:bayer_scale=3",
                Models.GifQualityMode.High => "sierra2_4a",
                _ => "bayer:bayer_scale=3"
            };

            var args = new List<string>
            {
                "-ss", FormatTimeSpan(parameters.StartTime),
                "-t", FormatTimeSpan(parameters.EndTime - parameters.StartTime),
                "-i", $"\"{inputPath}\"",
                "-i", $"\"{palettePath}\"",
                "-lavfi", $"\"fps={parameters.FPS},scale={parameters.Width}:-1:flags=lanczos[x];[x][1:v]paletteuse=dither={dither}\"",
                "-y",
                $"\"{outputPath}\""
            };

            return string.Join(" ", args);
        }

        /// <summary>
        /// 拼接图片
        /// </summary>
        public async Task<VideoProcessingResult> ConcatImagesAsync(
            List<string> imagePaths,
            string outputPath,
            bool horizontal,
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (imagePaths == null || imagePaths.Count < 2)
                {
                    result.Success = false;
                    result.ErrorMessage = "至少需要2张图片进行拼接";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                // 验证所有图片文件存在
                foreach (var imagePath in imagePaths)
                {
                    if (!File.Exists(imagePath))
                    {
                        result.Success = false;
                        result.ErrorMessage = $"图片文件不存在: {Path.GetFileName(imagePath)}";
                        return result;
                    }
                }

                // 构建FFmpeg命令参数
                var arguments = BuildImageConcatArguments(imagePaths, outputPath, horizontal);

                // 执行FFmpeg命令
                var processResult = await ExecuteFFmpegAsync(
                    arguments,
                    progressCallback,
                    null, // 图片拼接不需要时长
                    cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = processResult.Success ? outputPath : null;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"图片拼接异常: {ex.Message}";
                Debug.WriteLine($"图片拼接异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 合并视频（使用concat demuxer）
        /// </summary>
        public async Task<VideoProcessingResult> MergeVideosAsync(
            List<string> inputFiles,
            string outputPath,
            string videoCodec = "copy",
            string audioCodec = "copy",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (inputFiles == null || inputFiles.Count < 2)
                {
                    result.Success = false;
                    result.ErrorMessage = "至少需要两个视频文件才能合并";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                foreach (var file in inputFiles)
                {
                    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    {
                        result.Success = false;
                        result.ErrorMessage = $"视频文件不存在: {file}";
                        return result;
                    }
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var concatListPath = CreateConcatListFile(Path.GetTempPath(), inputFiles, $"merge_{Guid.NewGuid():N}.txt");

                try
                {
                    var arguments = BuildConcatArguments(concatListPath, outputPath, videoCodec, audioCodec, customArgs);

                    TimeSpan totalDuration = TimeSpan.Zero;
                    foreach (var file in inputFiles)
                    {
                        var info = await _videoInformationService.GetVideoInformationAsync(file);
                        if (info != null && info.Duration > TimeSpan.Zero)
                        {
                            totalDuration += info.Duration;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);
                    result.Success = processResult.Success;
                    result.ErrorMessage = processResult.ErrorMessage;
                    result.OutputPath = outputPath;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(concatListPath))
                        {
                            File.Delete(concatListPath);
                        }
                    }
                    catch
                    {
                        // 忽略清理错误
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "合并操作已取消";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"合并视频失败: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 构建图片拼接FFmpeg命令参数
        /// </summary>
        private string BuildImageConcatArguments(List<string> imagePaths, string outputPath, bool horizontal)
        {
            var args = new List<string>();

            // 添加所有输入图片
            foreach (var imagePath in imagePaths)
            {
                args.Add("-i");
                args.Add($"\"{imagePath}\"");
            }

            // 构建filter_complex
            // 水平拼接：hstack
            // 垂直拼接：vstack
            var filter = horizontal ? "hstack" : "vstack";

            // 构建filter_complex字符串
            var filterParts = new List<string>();
            for (int i = 0; i < imagePaths.Count; i++)
            {
                filterParts.Add($"[{i}:v]");
            }
            var filterComplex = $"{string.Join("", filterParts)}{filter}=inputs={imagePaths.Count}";
            
            args.Add("-filter_complex");
            args.Add($"\"{filterComplex}\"");

            // 输出设置
            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// 应用字幕到视频（烧录字幕）
        /// </summary>
        public async Task<VideoProcessingResult> ApplySubtitleAsync(
            string inputPath,
            string outputPath,
            Models.SubtitleParameters parameters,
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入视频文件不存在";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(parameters.SubtitleFilePath) || !File.Exists(parameters.SubtitleFilePath))
                {
                    result.Success = false;
                    result.ErrorMessage = "字幕文件不存在";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查tools/ffmpeg目录";
                    return result;
                }

                // 获取视频信息（包括分辨率）用于字体大小缩放
                int videoWidth = 1920;
                int videoHeight = 1080;
                var duration = TimeSpan.Zero;
                
                if (_videoInformationService != null)
                {
                    var videoInfo = await _videoInformationService.GetVideoInformationAsync(inputPath);
                    if (videoInfo != null)
                    {
                        videoWidth = videoInfo.Width > 0 ? videoInfo.Width : 1920;
                        videoHeight = videoInfo.Height > 0 ? videoInfo.Height : 1080;
                        duration = videoInfo.Duration;
                    }
                }

                // 构建FFmpeg命令参数（传入视频分辨率用于字体大小缩放）
                var arguments = BuildSubtitleArguments(inputPath, outputPath, parameters, out var tempSubtitlePath, videoWidth, videoHeight);
                
                // 调试输出：打印完整的FFmpeg命令
                Debug.WriteLine($"字幕处理FFmpeg命令: {_ffmpegPath} {arguments}");

                // 执行FFmpeg命令
                var processResult = await ExecuteFFmpegAsync(
                    arguments,
                    progressCallback,
                    duration,
                    cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = processResult.Success ? outputPath : null;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"应用字幕异常: {ex.Message}";
                Debug.WriteLine($"应用字幕异常: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 构建字幕FFmpeg命令参数
        /// </summary>
        public string BuildSubtitleArguments(string inputPath, string outputPath, Models.SubtitleParameters parameters, out string? tempSubtitlePath, int videoWidth = 1920, int videoHeight = 1080)
        {
            tempSubtitlePath = null; // 初始化输出参数
            
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 时间偏移处理：如果设置了时间偏移，创建临时字幕文件
            string actualSubtitlePath = parameters.SubtitleFilePath;
            
            if (Math.Abs(parameters.TimeOffset) > 0.01)
            {
                try
                {
                    // 解析原始字幕文件
                    var originalSubtitles = Services.SubtitleParser.ParseSubtitleFile(parameters.SubtitleFilePath);
                    
                    if (originalSubtitles != null && originalSubtitles.Count > 0)
                    {
                        // 应用时间偏移
                        var adjustedSubtitles = Services.SubtitleParser.ApplyTimeOffset(originalSubtitles, parameters.TimeOffset);
                        
                        // 创建临时字幕文件
                        var tempDir = Path.GetTempPath();
                        var tempFileName = $"subtitle_offset_{Guid.NewGuid():N}.srt";
                        tempSubtitlePath = Path.Combine(tempDir, tempFileName);
                        
                        // 保存为SRT格式（FFmpeg支持）
                        Services.SubtitleParser.SaveAsSrtFile(adjustedSubtitles, tempSubtitlePath);
                        
                        // 使用临时字幕文件
                        actualSubtitlePath = tempSubtitlePath;
                        // tempSubtitlePath 已经是输出参数，会自动传递
                        
                        Debug.WriteLine($"时间偏移已应用: {parameters.TimeOffset:F2}秒，临时文件: {tempSubtitlePath}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"应用时间偏移失败: {ex.Message}，将使用原始字幕文件");
                    // 如果失败，继续使用原始字幕文件
                }
            }
            
            // 更新字幕文件路径（使用实际路径，可能是原始文件或临时文件）
            var subtitleParamsForFilter = new Models.SubtitleParameters
            {
                SubtitleFilePath = actualSubtitlePath,
                FontFamily = parameters.FontFamily,
                FontSize = parameters.FontSize,
                FontColor = parameters.FontColor,
                Position = parameters.Position,
                OutlineWidth = parameters.OutlineWidth,
                EnableShadow = parameters.EnableShadow,
                TimeOffset = 0 // 时间偏移已应用到文件，这里设为0
            };

            // 构建字幕滤镜（传入视频分辨率用于字体大小缩放）
            // 注意：如果应用了时间偏移，subtitleParamsForFilter.SubtitleFilePath 已经是临时文件路径
            var subtitleFilter = BuildSubtitleFilter(subtitleParamsForFilter, videoWidth, videoHeight);
            
            if (!string.IsNullOrWhiteSpace(subtitleFilter))
            {
                args.Add("-vf");
                // 滤镜字符串需要用双引号包裹（与其他滤镜保持一致）
                args.Add($"\"{subtitleFilter}\"");
            }
            else
            {
                // 如果无法构建滤镜，使用subtitles滤镜直接加载字幕文件
                var subtitlePath = actualSubtitlePath.Replace("\\", "/");
                // 转义驱动器号后的冒号（Windows路径格式要求）
                var driveColonPattern = @"^([A-Za-z]):";
                subtitlePath = System.Text.RegularExpressions.Regex.Replace(subtitlePath, driveColonPattern, "$1\\:");
                // 转义单引号
                subtitlePath = subtitlePath.Replace("'", "\\'");
                args.Add("-vf");
                args.Add($"\"subtitles='{subtitlePath}'\"");
            }

            // 编码设置
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-c:a");
            args.Add("copy");

            // 输出
            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// 构建字幕滤镜字符串
        /// </summary>
        private string BuildSubtitleFilter(Models.SubtitleParameters parameters, int videoWidth = 1920, int videoHeight = 1080)
        {
            // FFmpeg的subtitles滤镜在Windows上需要特殊处理路径
            // 对于包含中文字符的路径，需要使用正斜杠并正确转义
            var subtitlePath = parameters.SubtitleFilePath;
            
            // Windows路径处理：FFmpeg的subtitles滤镜在Windows上需要特殊格式
            // 1. 将反斜杠转换为正斜杠
            // 2. 转义驱动器号后的冒号（D: -> D\:）
            // 3. 转义单引号（subtitles滤镜使用单引号包裹路径）
            subtitlePath = subtitlePath.Replace("\\", "/");
            
            // 转义驱动器号后的冒号（Windows路径格式要求）
            // 例如：D:/path -> D\:/path
            var driveColonPattern = @"^([A-Za-z]):";
            subtitlePath = System.Text.RegularExpressions.Regex.Replace(subtitlePath, driveColonPattern, "$1\\:");
            
            // 转义单引号
            subtitlePath = subtitlePath.Replace("'", "\\'");
            
            // 构建字幕样式选项
            var options = new List<string>();
            
            // 字体
            if (!string.IsNullOrWhiteSpace(parameters.FontFamily))
            {
                options.Add($"FontName={parameters.FontFamily}");
            }
            
            // 字号 - 根据视频分辨率缩放（与预览保持一致）
            // 基准：1920x1080的视频，字体大小使用用户设置的值
            // 对于其他尺寸的视频，按视频高度比例缩放
            var baseVideoHeight = 1080.0;
            var scaledFontSize = parameters.FontSize;
            
            if (videoHeight > 0 && videoHeight != 1080)
            {
                // 按视频高度比例缩放字体大小
                scaledFontSize = (int)(parameters.FontSize * (videoHeight / baseVideoHeight));
                // 限制字体大小范围（最小8，最大200）
                scaledFontSize = Math.Max(8, Math.Min(200, scaledFontSize));
            }
            
            options.Add($"FontSize={scaledFontSize}");
            
            // 颜色（转换为ASS格式的颜色值）
            var color = ConvertColorToAssFormat(parameters.FontColor);
            if (!string.IsNullOrWhiteSpace(color))
            {
                options.Add($"PrimaryColour={color}");
            }
            
            // 位置
            var alignment = parameters.Position switch
            {
                Models.SubtitlePosition.Top => 8,      // 顶部
                Models.SubtitlePosition.Center => 5,   // 居中
                Models.SubtitlePosition.Bottom => 2,  // 底部
                _ => 2
            };
            options.Add($"Alignment={alignment}");
            
            // 描边 - 根据视频分辨率缩放（与字体大小保持一致）
            if (parameters.OutlineWidth > 0)
            {
                var scaledOutline = parameters.OutlineWidth;
                
                if (videoHeight > 0 && videoHeight != 1080)
                {
                    // 按视频高度比例缩放描边宽度（重用baseVideoHeight变量）
                    scaledOutline = parameters.OutlineWidth * (videoHeight / baseVideoHeight);
                    // 限制描边宽度范围（最小0.5，最大10）
                    scaledOutline = Math.Max(0.5, Math.Min(10, scaledOutline));
                }
                
                options.Add($"Outline={scaledOutline:F1}");
            }
            
            // 阴影
            if (parameters.EnableShadow)
            {
                options.Add("Shadow=1");
            }
            else
            {
                options.Add("Shadow=0");
            }

            // 对于SRT/VTT格式，subtitles滤镜的样式选项有限
            // 为了支持样式参数，我们需要将SRT转换为ASS格式，或使用ass滤镜
            // FFmpeg的subtitles滤镜对SRT格式支持force_style参数（从FFmpeg 4.0+）
            // 但为了更好的兼容性，我们统一使用ass滤镜，并动态生成ASS文件内容
            
            if (parameters.SubtitleFilePath.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) ||
                parameters.SubtitleFilePath.EndsWith(".ssa", StringComparison.OrdinalIgnoreCase))
            {
                // ASS/SSA格式，使用ass滤镜，支持更多样式选项
                // 构建force_style参数（ass滤镜使用force_style来覆盖样式）
                if (options.Count > 0)
                {
                    var forceStyle = string.Join(",", options);
                    // ass滤镜的force_style参数需要转义
                    forceStyle = forceStyle.Replace("'", "\\'");
                    return $"ass='{subtitlePath}':force_style='{forceStyle}'";
                }
                else
                {
                    return $"ass='{subtitlePath}'";
                }
            }
            else
            {
                // SRT/VTT格式，使用subtitles滤镜
                // 从FFmpeg 4.0+开始，subtitles滤镜支持force_style参数
                // force_style参数的格式：subtitles='路径':force_style='样式选项'
                // 样式选项格式：FontName=字体,FontSize=字号,PrimaryColour=颜色,Alignment=对齐,Outline=描边,Shadow=阴影
                if (options.Count > 0)
                {
                    // 构建force_style参数
                    var forceStyle = string.Join(",", options);
                    // force_style参数中的单引号需要转义
                    forceStyle = forceStyle.Replace("'", "\\'");
                    // 使用subtitles滤镜的force_style参数
                    return $"subtitles='{subtitlePath}':force_style='{forceStyle}'";
                }
                else
                {
                    // 没有样式选项，使用subtitles滤镜（更简单）
                    return $"subtitles='{subtitlePath}'";
                }
            }
        }

        /// <summary>
        /// 将颜色名称转换为ASS格式的颜色值（BGR格式，十六进制）
        /// </summary>
        private string ConvertColorToAssFormat(string colorName)
        {
            // 常见颜色映射
            var colorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "white", "&H00FFFFFF" },
                { "black", "&H00000000" },
                { "red", "&H000000FF" },
                { "green", "&H0000FF00" },
                { "blue", "&H00FF0000" },
                { "yellow", "&H0000FFFF" },
                { "cyan", "&H00FFFF00" },
                { "magenta", "&H00FF00FF" }
            };

            if (colorMap.TryGetValue(colorName, out var assColor))
            {
                return assColor;
            }

            // 如果是十六进制格式（如#FFFFFF），转换为ASS格式
            if (colorName.StartsWith("#") && colorName.Length == 7)
            {
                try
                {
                    var r = colorName.Substring(1, 2);
                    var g = colorName.Substring(3, 2);
                    var b = colorName.Substring(5, 2);
                    // ASS使用BGR格式
                    return $"&H00{b}{g}{r}";
                }
                catch
                {
                    return "&H00FFFFFF"; // 默认白色
                }
            }

            return "&H00FFFFFF"; // 默认白色
        }

        /// <summary>
        /// 应用翻转/旋转/转置操作
        /// </summary>
        public async Task<VideoProcessingResult> ApplyFlipAsync(
            string inputPath,
            string outputPath,
            Models.FlipParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                // 检查是否有任何变换操作
                if (parameters.FlipType == Models.FlipType.None &&
                    parameters.RotateType == Models.RotateType.None &&
                    parameters.TransposeType == Models.TransposeType.None)
                {
                    result.Success = false;
                    result.ErrorMessage = "没有选择任何翻转或旋转操作";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildFlipArguments(inputPath, outputPath, parameters, videoCodec, quality, audioCodec, audioBitrate, customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"应用翻转操作时发生错误: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 构建翻转/旋转/转置的FFmpeg参数
        /// </summary>
        public string BuildFlipArguments(
            string inputPath,
            string outputPath,
            Models.FlipParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "")
        {
            var args = new List<string>
            {
                "-i", $"\"{inputPath}\""
            };

            // 构建视频滤镜链
            var filters = new List<string>();

            // 1. 转置操作（优先级最高，因为它是组合操作）
            if (parameters.TransposeType != Models.TransposeType.None)
            {
                var transposeValue = parameters.TransposeType switch
                {
                    Models.TransposeType.Transpose0 => "0",
                    Models.TransposeType.Transpose1 => "1",
                    Models.TransposeType.Transpose2 => "2",
                    Models.TransposeType.Transpose3 => "3",
                    _ => "1"
                };
                filters.Add($"transpose={transposeValue}");
            }
            else
            {
                // 2. 翻转操作
                if (parameters.FlipType == Models.FlipType.Horizontal)
                {
                    filters.Add("hflip");
                }
                else if (parameters.FlipType == Models.FlipType.Vertical)
                {
                    filters.Add("vflip");
                }
                else if (parameters.FlipType == Models.FlipType.Both)
                {
                    filters.Add("hflip");
                    filters.Add("vflip");
                }

                // 3. 旋转操作（使用transpose滤镜，符合FFmpeg标准）
                if (parameters.RotateType == Models.RotateType.Rotate90)
                {
                    // 顺时针90度：transpose=2
                    filters.Add("transpose=2");
                }
                else if (parameters.RotateType == Models.RotateType.Rotate180)
                {
                    // 180度：两次顺时针90度旋转
                    filters.Add("transpose=2");
                    filters.Add("transpose=2");
                }
                else if (parameters.RotateType == Models.RotateType.Rotate270)
                {
                    // 逆时针90度（270度）：transpose=1
                    filters.Add("transpose=1");
                }
                else if (parameters.RotateType == Models.RotateType.Custom && Math.Abs(parameters.CustomRotateAngle) > 0.01)
                {
                    // 自定义角度：只支持90度的倍数，使用transpose
                    // 将角度转换为90度的倍数
                    var normalizedAngle = ((int)Math.Round(parameters.CustomRotateAngle / 90.0) * 90) % 360;
                    if (normalizedAngle < 0) normalizedAngle += 360;
                    
                    if (normalizedAngle == 90)
                    {
                        filters.Add("transpose=2"); // 顺时针90度
                    }
                    else if (normalizedAngle == 180)
                    {
                        filters.Add("transpose=2");
                        filters.Add("transpose=2"); // 180度
                    }
                    else if (normalizedAngle == 270)
                    {
                        filters.Add("transpose=1"); // 逆时针90度（270度）
                    }
                    // 如果角度不是90度的倍数，忽略（或者可以提示用户）
                }
                else if (parameters.RotateType == Models.RotateType.Auto)
                {
                    // 自动检测：使用transpose=1（逆时针90度，常用于校正竖屏视频）
                    filters.Add("transpose=1");
                }
            }

            // 应用滤镜
            if (filters.Count > 0)
            {
                var filterComplex = string.Join(",", filters);
                // 使用-filter:v而不是-vf，与其他功能保持一致
                args.Add("-filter:v");
                args.Add($"\"{filterComplex}\"");
            }

            // 视频编码设置
            switch (videoCodec.ToLower())
            {
                case "libx264":
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString(), "-tune", "zerolatency" });
                    break;
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                case "copy":
                    // 注意：翻转和旋转需要重新编码，不能使用copy
                    args.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                default:
                    args.AddRange(new[] { "-c:v", videoCodec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            // 音频编码设置
            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k").Replace(" (推荐)", "") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k").Replace(" (推荐)", "") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k").Replace(" (推荐)", "") });
                    break;
            }

            // 自定义参数
            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                args.Add(customArgs);
            }

            // 输出
            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        /// <summary>
        /// 应用滤镜调整
        /// </summary>
        public async Task<VideoProcessingResult> ApplyFiltersAsync(
            string inputPath,
            string outputPath,
            Models.FilterParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "",
            Action<double>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new VideoProcessingResult();

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输入文件不存在或不可用";
                    return result;
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "输出文件路径不能为空";
                    return result;
                }

                if (_ffmpegPath == null || !File.Exists(_ffmpegPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "FFmpeg工具不存在，请检查路径设置";
                    return result;
                }

                if (parameters == null || !parameters.HasAdjustments())
                {
                    result.Success = false;
                    result.ErrorMessage = "未配置任何滤镜调整";
                    return result;
                }

                var outputDirectory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var arguments = BuildFilterArguments(
                    inputPath,
                    outputPath,
                    parameters,
                    videoCodec,
                    quality,
                    audioCodec,
                    audioBitrate,
                    customArgs);

                var videoInfo = await _videoInformationService?.GetVideoInformationAsync(inputPath);
                var totalDuration = videoInfo?.Duration ?? TimeSpan.Zero;
                var processResult = await ExecuteFFmpegAsync(arguments, progressCallback, totalDuration, cancellationToken);

                result.Success = processResult.Success;
                result.ErrorMessage = processResult.ErrorMessage;
                result.OutputPath = outputPath;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"应用滤镜时发生错误: {ex.Message}";
            }

            return result;
        }

        public static string BuildFilterArguments(
            string inputPath,
            string outputPath,
            Models.FilterParameters parameters,
            string videoCodec = "libx264",
            int quality = 20,
            string audioCodec = "aac",
            string audioBitrate = "128k",
            string customArgs = "")
        {
            if (videoCodec.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("无法使用复制编码器进行滤镜处理，请选择H.264或H.265编码器。");
            }

            var filters = BuildFilterFilterChain(parameters);
            if (filters.Count == 0)
            {
                throw new InvalidOperationException("未检测到任何滤镜调整。");
            }

            var args = new List<string>
            {
                "-i", $"\"{inputPath}\"",
                "-filter:v", $"\"{string.Join(",", filters)}\""
            };

            switch (videoCodec.ToLower())
            {
                case "libx265":
                    args.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", quality.ToString() });
                    break;
                case "libx264":
                default:
                    var codec = videoCodec.Equals("libx264", StringComparison.OrdinalIgnoreCase) ? "libx264" : videoCodec;
                    args.AddRange(new[] { "-c:v", codec, "-preset", "faster", "-crf", quality.ToString() });
                    break;
            }

            switch (audioCodec.ToLower())
            {
                case "copy":
                    args.AddRange(new[] { "-c:a", "copy" });
                    break;
                case "aac":
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                case "mp3":
                case "libmp3lame":
                    args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
                default:
                    args.AddRange(new[] { "-c:a", audioCodec, "-b:a", audioBitrate.Replace(" kbps", "k") });
                    break;
            }

            if (!string.IsNullOrWhiteSpace(customArgs))
            {
                args.Add(customArgs);
            }

            args.Add("-y");
            args.Add($"\"{outputPath}\"");

            return string.Join(" ", args);
        }

        public static List<string> BuildFilterFilterChain(Models.FilterParameters parameters)
        {
            var filters = new List<string>();
            if (parameters == null)
            {
                return filters;
            }

            var eqParts = new List<string>();
            var brightness = parameters.Brightness / 100.0;
            var contrast = parameters.Contrast / 100.0;
            var saturation = parameters.Saturation / 100.0;

            if (Math.Abs(brightness) > 0.001)
            {
                eqParts.Add($"brightness={FormatInvariant(brightness)}");
            }
            if (Math.Abs(contrast) > 0.001)
            {
                eqParts.Add($"contrast={FormatInvariant(1 + contrast)}");
            }
            if (Math.Abs(saturation) > 0.001)
            {
                eqParts.Add($"saturation={FormatInvariant(1 + saturation)}");
            }
            if (eqParts.Count > 0)
            {
                filters.Add($"eq={string.Join(":", eqParts)}");
            }

            if (Math.Abs(parameters.Temperature) > 0.001)
            {
                var value = Math.Clamp(parameters.Temperature / 100.0, -1, 1);
                var warm = value > 0 ? value : 0;
                var cool = value < 0 ? -value : 0;
                var warmValue = FormatInvariant(warm);
                var coolValue = FormatInvariant(cool);

                filters.Add($"colorbalance=rs={warmValue}:gs={FormatInvariant(warm / 2)}:bs=-{coolValue}:rm={warmValue}:gm={FormatInvariant(warm / 2)}:bm=-{coolValue}");
            }

            if (parameters.Blur > 0.01)
            {
                filters.Add($"gblur=sigma={FormatInvariant(parameters.Blur)}");
            }

            if (parameters.Sharpen > 0.01)
            {
                var amount = Math.Clamp(parameters.Sharpen / 5.0, 0, 3);
                filters.Add($"unsharp=luma_msize_x=5:luma_msize_y=5:luma_amount={FormatInvariant(amount)}:chroma_amount={FormatInvariant(amount / 2)}");
            }

            if (parameters.Vignette > 0.01)
            {
                // 当前FFmpeg构建不支持vignette的strength选项，改用半径参数控制晕影强度
                // radius越小，晕影越明显；这里将0-100映射到0.4-1.5范围
                var t = Math.Clamp(parameters.Vignette / 100.0, 0, 1);
                var radius = 1.5 - t * 1.1; // 100%时接近0.4，0%时约1.5
                filters.Add($"vignette={FormatInvariant(radius)}");
            }

            return filters;
        }

        private static string FormatInvariant(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }


    /// <summary>
    /// 视频处理结果
    /// </summary>
    public class VideoProcessingResult
    {
        public bool Success { get; set; }
        public string? OutputPath { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// 进程执行结果
    /// </summary>
    internal class ProcessExecutionResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
