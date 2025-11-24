using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// AI 字幕生成服务
    /// 统一协调音频分块、转录、合并等流程
    /// </summary>
    public class AiSubtitleService
    {
        private readonly AudioChunker _audioChunker;
        private readonly ChunkMerger _chunkMerger;
        private readonly RetryPolicy _retryPolicy;
        private readonly CacheManager _cacheManager;
        private readonly SubtitleOptimizer _subtitleOptimizer;
        private readonly HttpClient _httpClient;

        public AiSubtitleService(
            AudioChunker audioChunker,
            ChunkMerger chunkMerger,
            RetryPolicy retryPolicy,
            CacheManager cacheManager,
            SubtitleOptimizer subtitleOptimizer,
            HttpClient httpClient)
        {
            _audioChunker = audioChunker;
            _chunkMerger = chunkMerger;
            _retryPolicy = retryPolicy;
            _cacheManager = cacheManager;
            _subtitleOptimizer = subtitleOptimizer;
            _httpClient = httpClient;
        }

        /// <summary>
        /// 生成字幕
        /// </summary>
        public async Task<string> GenerateSubtitlesAsync(
            string mediaFilePath,
            string ffmpegPath,
            AiSubtitleProviderProfile provider,
            TranscriptionOptions? options = null,
            IProgress<(int progress, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new TranscriptionOptions();

            // 1. 提取音频
            progress?.Report((5, "提取音频轨道..."));
            var tempAudio = Path.Combine(Path.GetTempPath(), $"ve_ai_sub_{Guid.NewGuid():N}.wav");
            try
            {
                await ExtractAudioTrackAsync(ffmpegPath, mediaFilePath, tempAudio, cancellationToken);

                // 2. 检查缓存
                if (options.EnableCache)
                {
                    var cacheKey = _cacheManager.GenerateCacheKey(tempAudio, provider);
                    var cachedResult = await _cacheManager.GetCachedResultAsync(cacheKey);
                    if (cachedResult != null)
                    {
                        progress?.Report((100, "使用缓存结果"));
                        return cachedResult;
                    }
                }

                // 3. 分块处理
                List<AudioChunker.AudioChunkInfo> chunks;
                if (options.EnableChunking)
                {
                    progress?.Report((15, "分析音频文件..."));
                    chunks = await _audioChunker.SplitAudioAsync(
                        tempAudio,
                        ffmpegPath,
                        options.ChunkLengthSeconds,
                        options.ChunkOverlapSeconds,
                        new Progress<(int p, string m)>(p => 
                            progress?.Report((15 + (int)(p.p * 0.1), p.m))),
                        cancellationToken);
                }
                else
                {
                    chunks = new List<AudioChunker.AudioChunkInfo>
                    {
                        new AudioChunker.AudioChunkInfo
                        {
                            FilePath = tempAudio,
                            StartTime = TimeSpan.Zero,
                            EndTime = TimeSpan.Zero,
                            Index = 0
                        }
                    };
                }

                // 4. 并发转录
                progress?.Report((25, $"开始转录 {chunks.Count} 个音频块..."));
                var chunkResults = new List<(AudioChunker.AudioChunkInfo chunk, string srtContent)>();

                if (chunks.Count == 1)
                {
                    // 单个块，直接转录
                    var srtContent = await TranscribeChunkWithRetryAsync(
                        chunks[0].FilePath,
                        provider,
                        new Progress<(int a, string m)>(p => 
                            progress?.Report((25 + (int)(p.a * 0.5), $"转录中: {p.m}"))),
                        cancellationToken);
                    chunkResults.Add((chunks[0], srtContent));
                }
                else
                {
                    // 多个块，并发转录
                    var tasks = chunks.Select(async (chunk, index) =>
                    {
                        var chunkProgress = new Progress<(int a, string m)>(p =>
                        {
                            var baseProgress = 25 + (index * 50.0 / chunks.Count);
                            var chunkProgressPercent = p.a * 0.5 / chunks.Count;
                            progress?.Report(((int)(baseProgress + chunkProgressPercent), 
                                $"转录块 {index + 1}/{chunks.Count}: {p.m}"));
                        });

                        var srtContent = await TranscribeChunkWithRetryAsync(
                            chunk.FilePath,
                            provider,
                            chunkProgress,
                            cancellationToken);
                        return (chunk: chunk, srtContent: srtContent);
                    });

                    var results = await Task.WhenAll(tasks);
                    chunkResults.AddRange(results);
                }

                // 5. 合并结果
                progress?.Report((80, "合并转录结果..."));
                var mergedSrt = _chunkMerger.MergeChunkResults(chunkResults);

                // 6. 字幕优化（可选）
                if (options.EnableOptimization)
                {
                    progress?.Report((85, "优化字幕内容..."));
                    try
                    {
                        mergedSrt = await _subtitleOptimizer.OptimizeAsync(
                            mergedSrt,
                            provider,
                            options.OptimizationPrompt,
                            cancellationToken);
                        progress?.Report((95, "字幕优化完成"));
                    }
                    catch (Exception ex)
                    {
                        // 优化失败不影响主流程，记录日志即可
                        System.Diagnostics.Debug.WriteLine($"字幕优化失败: {ex.Message}");
                        progress?.Report((95, "字幕优化跳过（使用原始结果）"));
                    }
                }

                // 7. 保存缓存
                if (options.EnableCache)
                {
                    var cacheKey = _cacheManager.GenerateCacheKey(tempAudio, provider);
                    await _cacheManager.SaveCacheAsync(cacheKey, mergedSrt);
                }

                // 7. 清理临时文件
                foreach (var chunk in chunks)
                {
                    if (chunk.FilePath != tempAudio && File.Exists(chunk.FilePath))
                    {
                        try { File.Delete(chunk.FilePath); } catch { }
                    }
                }

                // 清理临时目录
                var chunkDir = Path.GetDirectoryName(chunks.FirstOrDefault()?.FilePath);
                if (chunkDir != null && chunkDir.Contains("ve_chunks_") && Directory.Exists(chunkDir))
                {
                    try { Directory.Delete(chunkDir, true); } catch { }
                }

                progress?.Report((100, "字幕生成完成"));
                return mergedSrt;
            }
            finally
            {
                // 清理主音频文件
                if (File.Exists(tempAudio))
                {
                    try { File.Delete(tempAudio); } catch { }
                }
            }
        }

        /// <summary>
        /// 提取音频轨道
        /// </summary>
        private async Task ExtractAudioTrackAsync(
            string ffmpegPath,
            string inputFile,
            string outputFile,
            CancellationToken cancellationToken)
        {
            var args = $"-y -i \"{inputFile}\" -vn -ac 1 -ar 16000 -f wav \"{outputFile}\"";
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            if (!File.Exists(outputFile))
            {
                throw new InvalidOperationException("音频提取失败，未生成临时 WAV 文件。");
            }
        }

        /// <summary>
        /// 转录单个音频块（带重试）
        /// </summary>
        private async Task<string> TranscribeChunkWithRetryAsync(
            string audioFilePath,
            AiSubtitleProviderProfile provider,
            IProgress<(int attempt, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await _retryPolicy.ExecuteWithRetryAsync(
                async ct => await RequestTranscriptionAsync(audioFilePath, provider, ct),
                RetryPolicy.IsRetryableException,
                progress,
                cancellationToken);
        }

        /// <summary>
        /// 请求转录 API
        /// </summary>
        private async Task<string> RequestTranscriptionAsync(
            string audioFilePath,
            AiSubtitleProviderProfile provider,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                throw new InvalidOperationException("未配置 API Key。");
            }

            var baseUrl = provider.BaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("Base URL 不能为空。");
            }

            var endpoint = provider.EndpointPath;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = "/v1/audio/transcriptions";
            }
            if (!endpoint.StartsWith("/"))
            {
                endpoint = "/" + endpoint;
            }

            var requestUri = $"{baseUrl}{endpoint}";

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

            using var content = new System.Net.Http.MultipartFormDataContent();
            content.Add(new StringContent(string.IsNullOrWhiteSpace(provider.Model) ? "whisper-1" : provider.Model), "model");

            if (!string.IsNullOrWhiteSpace(provider.ResponseFormat))
            {
                content.Add(new StringContent(provider.ResponseFormat!), "response_format");
            }

            var audioStream = File.OpenRead(audioFilePath);
            using var streamContent = new System.Net.Http.StreamContent(audioStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(streamContent, "file", Path.GetFileName(audioFilePath));

            request.Content = content;

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{provider.DisplayName} API 调用失败 ({(int)response.StatusCode}): {responseText}");
            }

            return responseText;
        }
    }

    /// <summary>
    /// 转录选项
    /// </summary>
    public class TranscriptionOptions
    {
        public bool EnableChunking { get; set; } = true;
        public int ChunkLengthSeconds { get; set; } = 600;
        public int ChunkOverlapSeconds { get; set; } = 10;
        public bool EnableCache { get; set; } = true;
        public bool EnableOptimization { get; set; } = false;
        public string? OptimizationPrompt { get; set; }
        public int MaxRetries { get; set; } = 3;
    }
}

