using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// Bilibili Bcut ASR 服务实现
    /// 参考 VideoCaptioner 的 BcutASR
    /// </summary>
    public class BcutAsrService
    {
        private const string API_BASE_URL = "https://member.bilibili.com/x/bcut/rubick-interface";
        private const string API_REQ_UPLOAD = API_BASE_URL + "/resource/create";
        private const string API_COMMIT_UPLOAD = API_BASE_URL + "/resource/create/complete";
        private const string API_CREATE_TASK = API_BASE_URL + "/task";
        private const string API_QUERY_RESULT = API_BASE_URL + "/task/result";

        private readonly HttpClient _httpClient;

        public BcutAsrService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// 执行 ASR 转录
        /// </summary>
        public async Task<string> TranscribeAsync(
            string audioFilePath,
            bool needWordTimeStamp = false,
            IProgress<(int progress, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var audioBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);

            // 1. 上传音频
            progress?.Report((10, "上传音频文件..."));
            var uploadResult = await UploadAudioAsync(audioBytes, cancellationToken);

            // 2. 创建任务
            progress?.Report((30, "创建转录任务..."));
            var taskId = await CreateTaskAsync(uploadResult.DownloadUrl, cancellationToken);

            // 3. 轮询结果
            progress?.Report((50, "等待转录完成..."));
            var result = await PollResultAsync(taskId, progress, cancellationToken);

            // 4. 转换为 SRT
            progress?.Report((95, "生成字幕文件..."));
            return ConvertToSrt(result, needWordTimeStamp);
        }

        private async Task<UploadResult> UploadAudioAsync(byte[] audioBytes, CancellationToken cancellationToken)
        {
            // 1. 请求上传授权
            var requestPayload = new
            {
                type = 2,
                name = "audio.mp3",
                size = audioBytes.Length,
                ResourceFileType = "mp3",
                model_id = "8"
            };

            var requestJson = JsonSerializer.Serialize(requestPayload);
            var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var requestResponse = await _httpClient.PostAsync(API_REQ_UPLOAD, requestContent, cancellationToken);
            requestResponse.EnsureSuccessStatusCode();

            var requestData = await requestResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var data = requestData.GetProperty("data");

            var inBossKey = data.GetProperty("in_boss_key").GetString() ?? string.Empty;
            var resourceId = data.GetProperty("resource_id").GetString() ?? string.Empty;
            var uploadId = data.GetProperty("upload_id").GetString() ?? string.Empty;
            var uploadUrls = data.GetProperty("upload_urls").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            var perSize = data.GetProperty("per_size").GetInt32();

            // 2. 分块上传
            var etags = new List<string>();
            for (int i = 0; i < uploadUrls.Count; i++)
            {
                var startRange = i * perSize;
                var endRange = Math.Min((i + 1) * perSize, audioBytes.Length);
                var chunk = audioBytes[startRange..endRange];

                var chunkContent = new ByteArrayContent(chunk);
                var putResponse = await _httpClient.PutAsync(uploadUrls[i], chunkContent, cancellationToken);
                putResponse.EnsureSuccessStatusCode();

                if (putResponse.Headers.TryGetValues("Etag", out var etagValues))
                {
                    etags.Add(etagValues.First());
                }
            }

            // 3. 提交上传
            var commitPayload = new
            {
                InBossKey = inBossKey,
                ResourceId = resourceId,
                Etags = string.Join(",", etags),
                UploadId = uploadId,
                model_id = "8"
            };

            var commitJson = JsonSerializer.Serialize(commitPayload);
            var commitContent = new StringContent(commitJson, Encoding.UTF8, "application/json");

            var commitResponse = await _httpClient.PostAsync(API_COMMIT_UPLOAD, commitContent, cancellationToken);
            commitResponse.EnsureSuccessStatusCode();

            var commitData = await commitResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var downloadUrl = commitData.GetProperty("data").GetProperty("download_url").GetString() ?? string.Empty;

            return new UploadResult { DownloadUrl = downloadUrl };
        }

        private async Task<string> CreateTaskAsync(string downloadUrl, CancellationToken cancellationToken)
        {
            var payload = new
            {
                resource = downloadUrl,
                model_id = "8"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(API_CREATE_TASK, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return data.GetProperty("data").GetProperty("task_id").GetString() ?? string.Empty;
        }

        private async Task<JsonElement> PollResultAsync(
            string taskId,
            IProgress<(int progress, string message)>? progress,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 500;
            const int delayMs = 1000;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await _httpClient.GetAsync($"{API_QUERY_RESULT}?model_id=7&task_id={taskId}", cancellationToken);
                response.EnsureSuccessStatusCode();

                var data = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                var state = data.GetProperty("data").GetProperty("state").GetInt32();

                if (state == 4) // 完成
                {
                    return data.GetProperty("data");
                }

                if (state == 5) // 失败
                {
                    throw new InvalidOperationException("ASR 任务失败");
                }

                // 更新进度
                var progressPercent = 50 + (int)((attempt * 1.0 / maxAttempts) * 40);
                progress?.Report((progressPercent, $"等待转录完成... ({attempt + 1}/{maxAttempts})"));

                await Task.Delay(delayMs, cancellationToken);
            }

            throw new TimeoutException("ASR 任务超时");
        }

        private string ConvertToSrt(JsonElement result, bool needWordTimeStamp)
        {
            var resultJson = result.GetProperty("result").GetString() ?? "{}";
            var resultData = JsonDocument.Parse(resultJson).RootElement;

            var srtBuilder = new StringBuilder();
            var utterances = resultData.GetProperty("utterances").EnumerateArray().ToList();

            if (needWordTimeStamp)
            {
                // 词级时间戳
                int index = 1;
                foreach (var utterance in utterances)
                {
                    var words = utterance.GetProperty("words").EnumerateArray();
                    foreach (var word in words)
                    {
                        var text = word.GetProperty("label").GetString()?.Trim() ?? string.Empty;
                        var startTime = word.GetProperty("start_time").GetInt32();
                        var endTime = word.GetProperty("end_time").GetInt32();

                        srtBuilder.AppendLine(index.ToString());
                        srtBuilder.AppendLine($"{FormatTime(startTime)} --> {FormatTime(endTime)}");
                        srtBuilder.AppendLine(text);
                        srtBuilder.AppendLine();
                        index++;
                    }
                }
            }
            else
            {
                // 句子级时间戳
                int index = 1;
                foreach (var utterance in utterances)
                {
                    var text = utterance.GetProperty("transcript").GetString()?.Trim() ?? string.Empty;
                    var startTime = utterance.GetProperty("start_time").GetInt32();
                    var endTime = utterance.GetProperty("end_time").GetInt32();

                    srtBuilder.AppendLine(index.ToString());
                    srtBuilder.AppendLine($"{FormatTime(startTime)} --> {FormatTime(endTime)}");
                    srtBuilder.AppendLine(text);
                    srtBuilder.AppendLine();
                    index++;
                }
            }

            return srtBuilder.ToString();
        }

        private string FormatTime(int milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(milliseconds);
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
        }

        private class UploadResult
        {
            public string DownloadUrl { get; set; } = string.Empty;
        }
    }
}

