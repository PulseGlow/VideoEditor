using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 字幕条目
    /// </summary>
    public class SubtitleItem
    {
        public int Index { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// 字幕解析服务
    /// </summary>
    public class SubtitleParser
    {
        /// <summary>
        /// 检测文件编码
        /// </summary>
        private static Encoding DetectEncoding(string filePath)
        {
            try
            {
                // 读取文件前几个字节检测BOM
                var bytes = new byte[4];
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    fs.Read(bytes, 0, 4);
                }

                // UTF-8 BOM: EF BB BF
                if (bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return Encoding.UTF8;

                // UTF-16 LE BOM: FF FE
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                    return Encoding.Unicode;

                // UTF-16 BE BOM: FE FF
                if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return Encoding.BigEndianUnicode;

                // 尝试UTF-8，如果失败则使用系统默认编码（通常是GBK/ANSI）
                try
                {
                    var testContent = File.ReadAllText(filePath, Encoding.UTF8);
                    // 检查是否包含乱码（简单检测：检查是否包含替换字符）
                    if (!testContent.Contains("\uFFFD"))
                        return Encoding.UTF8;
                }
                catch { }

                // 默认使用系统编码（Windows通常是GBK）
                return Encoding.GetEncoding("GB2312");
            }
            catch
            {
                return Encoding.UTF8; // 默认返回UTF-8
            }
        }

        /// <summary>
        /// 解析字幕文件（自动检测格式）
        /// </summary>
        public static List<SubtitleItem> ParseSubtitleFile(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<SubtitleItem>();

            var extension = Path.GetExtension(filePath).ToLower();
            return extension switch
            {
                ".srt" => ParseSrtFile(filePath),
                ".ass" => ParseAssFile(filePath),
                ".ssa" => ParseAssFile(filePath), // SSA和ASS格式类似
                ".vtt" => ParseVttFile(filePath),
                _ => new List<SubtitleItem>()
            };
        }

        /// <summary>
        /// 解析SRT字幕文件
        /// </summary>
        public static List<SubtitleItem> ParseSrtFile(string filePath)
        {
            var items = new List<SubtitleItem>();

            if (!File.Exists(filePath))
            {
                return items;
            }

            try
            {
                var encoding = DetectEncoding(filePath);
                var content = File.ReadAllText(filePath, encoding);
                
                // 处理BOM标记
                if (content.Length > 0 && content[0] == '\uFEFF')
                {
                    content = content.Substring(1);
                }

                // 按空行分割字幕块
                var blocks = Regex.Split(content, @"\r?\n\r?\n", RegexOptions.Multiline);

                foreach (var block in blocks)
                {
                    if (string.IsNullOrWhiteSpace(block))
                        continue;

                    var item = ParseSrtBlock(block);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析SRT文件失败: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// 解析ASS/SSA字幕文件
        /// </summary>
        public static List<SubtitleItem> ParseAssFile(string filePath)
        {
            var items = new List<SubtitleItem>();

            if (!File.Exists(filePath))
                return items;

            try
            {
                var encoding = DetectEncoding(filePath);
                var lines = File.ReadAllLines(filePath, encoding);
                
                bool inEventsSection = false;
                int index = 1;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    
                    // 检测Events部分
                    if (trimmedLine.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase))
                    {
                        inEventsSection = true;
                        continue;
                    }

                    // 检测其他部分（结束Events部分）
                    if (inEventsSection && trimmedLine.StartsWith("[") && !trimmedLine.StartsWith("[Events"))
                    {
                        inEventsSection = false;
                        continue;
                    }

                    // 解析Dialogue行
                    if (inEventsSection && trimmedLine.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                    {
                        var item = ParseAssDialogue(trimmedLine, index++);
                        if (item != null)
                            items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析ASS/SSA文件失败: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// 解析ASS/SSA的Dialogue行
        /// </summary>
        private static SubtitleItem? ParseAssDialogue(string line, int index)
        {
            try
            {
                // Dialogue格式: Dialogue: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
                // 例如: Dialogue: 0,0:00:01.23,0:00:03.45,Default,,0,0,0,,这是字幕文本
                var parts = line.Split(',');
                if (parts.Length < 10)
                    return null;

                // 提取时间（索引1和2）
                var startTimeStr = parts[1].Trim();
                var endTimeStr = parts[2].Trim();

                // 解析时间（格式：H:MM:SS.cc 或 H:MM:SS:cc）
                var startTime = ParseAssTime(startTimeStr);
                var endTime = ParseAssTime(endTimeStr);

                // 提取文本（从索引9开始，因为文本中可能包含逗号）
                var textBuilder = new StringBuilder();
                for (int i = 9; i < parts.Length; i++)
                {
                    if (textBuilder.Length > 0)
                        textBuilder.Append(',');
                    textBuilder.Append(parts[i]);
                }

                // 移除ASS标签（简单处理，移除基本标签）
                var text = textBuilder.ToString();
                text = Regex.Replace(text, @"\{[^}]*\}", ""); // 移除 {标签}
                text = text.Replace("\\N", "\n").Replace("\\n", "\n"); // 换行符

                return new SubtitleItem
                {
                    Index = index,
                    StartTime = startTime,
                    EndTime = endTime,
                    Text = text.Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析ASS时间格式（H:MM:SS.cc 或 H:MM:SS:cc）
        /// </summary>
        private static TimeSpan ParseAssTime(string timeStr)
        {
            try
            {
                // 格式：H:MM:SS.cc 或 H:MM:SS:cc
                var parts = timeStr.Split(':', '.');
                if (parts.Length >= 3)
                {
                    var h = int.Parse(parts[0]);
                    var m = int.Parse(parts[1]);
                    var s = int.Parse(parts[2]);
                    var ms = parts.Length > 3 ? int.Parse(parts[3].PadRight(3, '0').Substring(0, 3)) : 0;

                    return new TimeSpan(0, h, m, s, ms);
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// 解析VTT字幕文件
        /// </summary>
        public static List<SubtitleItem> ParseVttFile(string filePath)
        {
            var items = new List<SubtitleItem>();

            if (!File.Exists(filePath))
                return items;

            try
            {
                var encoding = DetectEncoding(filePath);
                var content = File.ReadAllText(filePath, encoding);
                
                // 移除WEBVTT头部
                content = Regex.Replace(content, @"^WEBVTT\s*\r?\n", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

                // 按空行分割字幕块
                var blocks = Regex.Split(content, @"\r?\n\r?\n", RegexOptions.Multiline);

                int index = 1;
                foreach (var block in blocks)
                {
                    if (string.IsNullOrWhiteSpace(block))
                        continue;

                    var item = ParseVttBlock(block, index++);
                    if (item != null)
                        items.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析VTT文件失败: {ex.Message}");
            }

            return items;
        }

        /// <summary>
        /// 解析VTT字幕块
        /// </summary>
        private static SubtitleItem? ParseVttBlock(string block, int index)
        {
            try
            {
                var lines = block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                if (lines.Length < 2)
                    return null;

                // 查找时间行（格式：00:00:01.234 --> 00:00:03.456）
                string? timeLine = null;
                int textStartIndex = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("-->"))
                    {
                        timeLine = lines[i].Trim();
                        textStartIndex = i + 1;
                        break;
                    }
                }

                if (timeLine == null)
                    return null;

                // 解析时间
                var timeMatch = Regex.Match(timeLine, @"(\d{2}):(\d{2}):(\d{2})[.,](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[.,](\d{3})");
                if (!timeMatch.Success)
                    return null;

                var startTime = ParseTimeSpan(timeMatch.Groups[1].Value, timeMatch.Groups[2].Value,
                    timeMatch.Groups[3].Value, timeMatch.Groups[4].Value);
                var endTime = ParseTimeSpan(timeMatch.Groups[5].Value, timeMatch.Groups[6].Value,
                    timeMatch.Groups[7].Value, timeMatch.Groups[8].Value);

                // 提取文本
                var textBuilder = new StringBuilder();
                for (int i = textStartIndex; i < lines.Length; i++)
                {
                    // 移除VTT标签
                    var line = Regex.Replace(lines[i], @"<[^>]+>", "");
                    if (textBuilder.Length > 0)
                        textBuilder.AppendLine();
                    textBuilder.Append(line);
                }

                return new SubtitleItem
                {
                    Index = index,
                    StartTime = startTime,
                    EndTime = endTime,
                    Text = textBuilder.ToString().Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析单个SRT字幕块
        /// </summary>
        private static SubtitleItem? ParseSrtBlock(string block)
        {
            var lines = block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (lines.Length < 3)
                return null;

            try
            {
                // 第一行：序号
                if (!int.TryParse(lines[0].Trim(), out var index))
                    return null;

                // 第二行：时间码
                // 格式：00:00:00,000 --> 00:00:00,000 或 00:00:00.000 --> 00:00:00.000
                var timeLine = lines[1].Trim();
                var timeMatch = Regex.Match(timeLine, @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
                
                if (!timeMatch.Success)
                    return null;

                var startTime = ParseTimeSpan(timeMatch.Groups[1].Value, timeMatch.Groups[2].Value, 
                    timeMatch.Groups[3].Value, timeMatch.Groups[4].Value);
                var endTime = ParseTimeSpan(timeMatch.Groups[5].Value, timeMatch.Groups[6].Value, 
                    timeMatch.Groups[7].Value, timeMatch.Groups[8].Value);

                // 第三行及以后：字幕文本
                var textBuilder = new StringBuilder();
                for (int i = 2; i < lines.Length; i++)
                {
                    if (textBuilder.Length > 0)
                        textBuilder.AppendLine();
                    textBuilder.Append(lines[i]);
                }

                return new SubtitleItem
                {
                    Index = index,
                    StartTime = startTime,
                    EndTime = endTime,
                    Text = textBuilder.ToString().Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析时间戳
        /// </summary>
        private static TimeSpan ParseTimeSpan(string hours, string minutes, string seconds, string milliseconds)
        {
            var h = int.Parse(hours);
            var m = int.Parse(minutes);
            var s = int.Parse(seconds);
            var ms = int.Parse(milliseconds);

            return new TimeSpan(0, h, m, s, ms);
        }

        /// <summary>
        /// 根据时间获取当前应该显示的字幕
        /// </summary>
        public static SubtitleItem? GetSubtitleAtTime(List<SubtitleItem> subtitles, TimeSpan currentTime)
        {
            foreach (var item in subtitles)
            {
                if (currentTime >= item.StartTime && currentTime <= item.EndTime)
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// 应用时间偏移到字幕列表
        /// </summary>
        public static List<SubtitleItem> ApplyTimeOffset(List<SubtitleItem> subtitles, double offsetSeconds)
        {
            if (Math.Abs(offsetSeconds) < 0.001)
                return subtitles;

            var offset = TimeSpan.FromSeconds(offsetSeconds);
            var adjustedSubtitles = new List<SubtitleItem>();

            foreach (var item in subtitles)
            {
                var newStartTime = item.StartTime.Add(offset);
                var newEndTime = item.EndTime.Add(offset);

                // 确保时间不为负
                if (newStartTime.TotalMilliseconds < 0)
                    newStartTime = TimeSpan.Zero;
                if (newEndTime.TotalMilliseconds < 0)
                    newEndTime = TimeSpan.Zero;

                adjustedSubtitles.Add(new SubtitleItem
                {
                    Index = item.Index,
                    StartTime = newStartTime,
                    EndTime = newEndTime,
                    Text = item.Text
                });
            }

            return adjustedSubtitles;
        }

        /// <summary>
        /// 将字幕列表保存为SRT文件
        /// </summary>
        public static void SaveAsSrtFile(List<SubtitleItem> subtitles, string outputPath)
        {
            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
            
            foreach (var item in subtitles)
            {
                writer.WriteLine(item.Index);
                writer.WriteLine($"{FormatTimeSpan(item.StartTime)} --> {FormatTimeSpan(item.EndTime)}");
                writer.WriteLine(item.Text);
                writer.WriteLine();
            }
        }

        /// <summary>
        /// 格式化时间戳为SRT格式（00:00:00,000）
        /// </summary>
        private static string FormatTimeSpan(TimeSpan time)
        {
            var hours = (int)time.TotalHours;
            var minutes = time.Minutes;
            var seconds = time.Seconds;
            var milliseconds = time.Milliseconds;

            return $"{hours:D2}:{minutes:D2}:{seconds:D2},{milliseconds:D3}";
        }
    }
}

