using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 字幕优化服务
    /// 使用 LLM 对字幕进行智能校正和优化
    /// </summary>
    public class SubtitleOptimizer
    {
        private readonly HttpClient _httpClient;
        private const int MaxRetries = 3;

        public SubtitleOptimizer(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// 优化字幕内容
        /// </summary>
        public async Task<string> OptimizeAsync(
            string srtContent,
            AiSubtitleProviderProfile provider,
            string? customPrompt = null,
            CancellationToken cancellationToken = default)
        {
            // 解析 SRT
            var segments = ParseSrt(srtContent);
            
            if (segments.Count == 0)
            {
                return srtContent;
            }

            // 构建优化提示词
            var prompt = BuildOptimizationPrompt(segments, customPrompt);

            // 调用 LLM
            var optimizedJson = await CallLLMForOptimizationAsync(
                prompt,
                provider,
                cancellationToken);

            // 解析结果并重建 SRT
            var optimizedSegments = ParseOptimizationResult(optimizedJson, segments);
            return BuildSrt(optimizedSegments);
        }

        /// <summary>
        /// 构建优化提示词
        /// </summary>
        private string BuildOptimizationPrompt(
            List<SrtSegment> segments,
            string? customPrompt)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你是一位专业的字幕校正专家。请修正以下字幕中的错误，但保持原意和结构不变。");
            sb.AppendLine();
            sb.AppendLine("要求：");
            sb.AppendLine("1. 修正错别字和标点符号");
            sb.AppendLine("2. 移除填充词（嗯、啊、呃、um、uh 等）");
            sb.AppendLine("3. 规范化格式（大小写、数学公式、代码）");
            sb.AppendLine("4. 保持字幕编号对应");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(customPrompt))
            {
                sb.AppendLine($"术语或要求：{customPrompt}");
                sb.AppendLine();
            }

            sb.AppendLine("字幕内容：");
            sb.AppendLine("{");
            foreach (var seg in segments)
            {
                var escapedText = seg.Text.Replace("\"", "\\\"").Replace("\n", "\\n");
                sb.AppendLine($"  \"{seg.Index - 1}\": \"{escapedText}\"");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("请返回修正后的 JSON 对象，格式：{\"0\": \"修正后的字幕\", \"1\": \"...\"}");

            return sb.ToString();
        }

        /// <summary>
        /// 调用 LLM 进行优化
        /// </summary>
        private async Task<string> CallLLMForOptimizationAsync(
            string prompt,
            AiSubtitleProviderProfile provider,
            CancellationToken cancellationToken)
        {
            var baseUrl = provider.BaseUrl.TrimEnd('/');
            if (!baseUrl.EndsWith("/v1"))
            {
                baseUrl = baseUrl.EndsWith("/") ? baseUrl + "v1" : baseUrl + "/v1";
            }

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

            var requestBody = new
            {
                model = provider.Model,
                messages = new[]
                {
                    new { role = "system", content = "你是一位专业的字幕校正专家。" },
                    new { role = "user", content = prompt }
                },
                temperature = 0.3
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var jsonDoc = JsonDocument.Parse(responseText);
                        var content = jsonDoc.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("message")
                            .GetProperty("content")
                            .GetString();

                        if (string.IsNullOrWhiteSpace(content))
                        {
                            throw new InvalidOperationException("LLM 返回空内容");
                        }

                        return content;
                    }

                    if (attempt < MaxRetries - 1 && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                        continue;
                    }

                    throw new HttpRequestException($"API 调用失败: {response.StatusCode} - {responseText}");
                }
                catch (Exception ex) when (attempt < MaxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                    if (attempt == MaxRetries - 1)
                        throw;
                }
            }

            throw new InvalidOperationException("优化失败");
        }

        /// <summary>
        /// 解析 SRT 格式
        /// </summary>
        private List<SrtSegment> ParseSrt(string srtContent)
        {
            var segments = new List<SrtSegment>();
            var blocks = srtContent.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                var lines = block.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
                if (lines.Length < 3) continue;

                if (int.TryParse(lines[0].Trim(), out var index))
                {
                    var timeLine = lines[1].Trim();
                    var timeMatch = Regex.Match(timeLine, 
                        @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
                    
                    if (timeMatch.Success)
                    {
                        var startTime = ParseTimeSpan(
                            timeMatch.Groups[1], timeMatch.Groups[2], 
                            timeMatch.Groups[3], timeMatch.Groups[4]);
                        var endTime = ParseTimeSpan(
                            timeMatch.Groups[5], timeMatch.Groups[6], 
                            timeMatch.Groups[7], timeMatch.Groups[8]);
                        var text = string.Join("\n", lines.Skip(2)).Trim();

                        segments.Add(new SrtSegment
                        {
                            Index = index,
                            StartTime = startTime,
                            EndTime = endTime,
                            Text = text
                        });
                    }
                }
            }

            return segments;
        }

        /// <summary>
        /// 解析时间戳
        /// </summary>
        private TimeSpan ParseTimeSpan(
            Group h, Group m, Group s, Group ms)
        {
            return new TimeSpan(
                0,
                int.Parse(h.Value),
                int.Parse(m.Value),
                int.Parse(s.Value),
                int.Parse(ms.Value));
        }

        /// <summary>
        /// 解析优化结果
        /// </summary>
        private List<SrtSegment> ParseOptimizationResult(string jsonResult, List<SrtSegment> originalSegments)
        {
            var optimizedSegments = new List<SrtSegment>();

            try
            {
                // 尝试提取 JSON 部分（可能包含 markdown 代码块）
                var jsonMatch = Regex.Match(jsonResult, @"\{[^{}]*\{[^{}]*\}[^{}]*\}", RegexOptions.Singleline);
                var jsonText = jsonMatch.Success ? jsonMatch.Value : jsonResult;

                // 如果包含代码块标记，提取内容
                if (jsonText.Contains("```"))
                {
                    var codeBlockMatch = Regex.Match(jsonText, @"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline);
                    if (codeBlockMatch.Success)
                    {
                        jsonText = codeBlockMatch.Groups[1].Value;
                    }
                }

                var jsonDoc = JsonDocument.Parse(jsonText);
                var root = jsonDoc.RootElement;

                // 按原始顺序更新文本
                foreach (var originalSeg in originalSegments)
                {
                    var key = (originalSeg.Index - 1).ToString();
                    if (root.TryGetProperty(key, out var optimizedText))
                    {
                        optimizedSegments.Add(new SrtSegment
                        {
                            Index = originalSeg.Index,
                            StartTime = originalSeg.StartTime,
                            EndTime = originalSeg.EndTime,
                            Text = optimizedText.GetString() ?? originalSeg.Text
                        });
                    }
                    else
                    {
                        // 如果找不到对应的优化结果，使用原始文本
                        optimizedSegments.Add(originalSeg);
                    }
                }
            }
            catch
            {
                // 解析失败，返回原始字幕
                return originalSegments;
            }

            return optimizedSegments;
        }

        /// <summary>
        /// 构建 SRT 格式
        /// </summary>
        private string BuildSrt(List<SrtSegment> segments)
        {
            var sb = new StringBuilder();
            int index = 1;

            foreach (var seg in segments.OrderBy(s => s.StartTime))
            {
                sb.AppendLine(index.ToString());
                sb.AppendLine($"{FormatTimeSpan(seg.StartTime)} --> {FormatTimeSpan(seg.EndTime)}");
                sb.AppendLine(seg.Text);
                sb.AppendLine();
                index++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// 格式化时间戳为 SRT 格式
        /// </summary>
        private string FormatTimeSpan(TimeSpan ts)
        {
            return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
        }

        private class SrtSegment
        {
            public int Index { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
            public string Text { get; set; } = string.Empty;
        }
    }
}

