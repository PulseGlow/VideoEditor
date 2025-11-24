using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 分块转录结果合并服务
    /// 将多个音频块的转录结果合并为完整的字幕文件
    /// </summary>
    public class ChunkMerger
    {
        private const int OverlapMilliseconds = 10000; // 10秒重叠

        /// <summary>
        /// 合并多个块的转录结果
        /// </summary>
        public string MergeChunkResults(
            List<(AudioChunker.AudioChunkInfo chunk, string srtContent)> chunkResults)
        {
            if (chunkResults.Count == 0)
                return string.Empty;

            if (chunkResults.Count == 1)
                return chunkResults[0].srtContent;

            var mergedSegments = new List<SrtSegment>();
            int globalIndex = 1;

            for (int i = 0; i < chunkResults.Count; i++)
            {
                var (chunk, srtContent) = chunkResults[i];
                var segments = ParseSrt(srtContent);

                // 调整时间戳（加上块的起始时间）
                var adjustedSegments = segments.Select(seg => new SrtSegment
                {
                    Index = globalIndex++,
                    StartTime = seg.StartTime + chunk.StartTime,
                    EndTime = seg.EndTime + chunk.StartTime,
                    Text = seg.Text
                }).ToList();

                // 处理重叠区域（如果是最后一个块，不需要处理）
                if (i < chunkResults.Count - 1)
                {
                    var nextChunk = chunkResults[i + 1].chunk;
                    var overlapEnd = chunk.EndTime;
                    var overlapStart = overlapEnd - TimeSpan.FromMilliseconds(OverlapMilliseconds);

                    // 移除重叠区域的重复内容
                    adjustedSegments = RemoveOverlapSegments(
                        adjustedSegments,
                        overlapStart,
                        overlapEnd);
                }

                mergedSegments.AddRange(adjustedSegments);
            }

            return BuildSrt(mergedSegments);
        }

        /// <summary>
        /// 解析 SRT 格式内容
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
        /// 移除重叠区域的重复内容
        /// </summary>
        private List<SrtSegment> RemoveOverlapSegments(
            List<SrtSegment> segments,
            TimeSpan overlapStart,
            TimeSpan overlapEnd)
        {
            return segments.Where(seg =>
                seg.EndTime <= overlapStart || seg.StartTime >= overlapEnd
            ).ToList();
        }

        /// <summary>
        /// 构建 SRT 格式内容
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

