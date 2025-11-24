using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 时间码解析服务
    /// </summary>
    public class TimecodeParser
    {
        /// <summary>
        /// 从文本中提取时间码片段
        /// </summary>
        /// <param name="inputText">输入文本</param>
        /// <returns>时间码片段列表</returns>
        public static List<TimecodeSegment> ParseTimecodes(string inputText)
        {
            var segments = new List<TimecodeSegment>();

            if (string.IsNullOrWhiteSpace(inputText))
            {
                return segments;
            }

            // 按行分割
            var lines = inputText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;

                // 尝试解析时间码
                if (TryParseTimecodeLine(trimmedLine, out var startTime, out var endTime))
                {
                    segments.Add(new TimecodeSegment
                    {
                        StartTime = startTime,
                        EndTime = endTime,
                        Index = segments.Count + 1
                    });
                }
            }

            return segments;
        }

        /// <summary>
        /// 尝试解析一行时间码
        /// </summary>
        private static bool TryParseTimecodeLine(string line, out long startTime, out long endTime)
        {
            startTime = 0;
            endTime = 0;

            // 模式1: 00:01:25,510 - 00:02:48,430 (逗号分隔毫秒)
            // 模式2: 00:01:25.510 - 00:02:48.430 (点分隔毫秒)
            // 模式3: 00:19:11,110 --> 00:20:34,020 (箭头分隔)
            // 模式4: [1:23.456, 2:34.567] (数组格式)

            // 先尝试匹配标准格式：HH:mm:ss,mmm - HH:mm:ss,mmm 或 HH:mm:ss.mmm - HH:mm:ss.mmm
            // 支持多种分隔符：-、--、-->、→、–、—
            // 注意：--> 需要单独匹配，因为它是三个字符的序列
            var pattern1 = @"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*(?:-->|--|-|–|—|→)\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})";
            var match1 = Regex.Match(line, pattern1);
            if (match1.Success)
            {
                if (TryParseTime(match1.Groups[1].Value, match1.Groups[2].Value, match1.Groups[3].Value, match1.Groups[4].Value, out startTime) &&
                    TryParseTime(match1.Groups[5].Value, match1.Groups[6].Value, match1.Groups[7].Value, match1.Groups[8].Value, out endTime))
                {
                    return true;
                }
            }

            // 尝试匹配简化格式：mm:ss.mmm - mm:ss.mmm
            // 支持多种分隔符：-、--、-->、→、–、—
            // 注意：--> 需要单独匹配，因为它是三个字符的序列
            var pattern2 = @"(\d{1,2}):(\d{2})[,.](\d{1,3})\s*(?:-->|--|-|–|—|→)\s*(\d{1,2}):(\d{2})[,.](\d{1,3})";
            var match2 = Regex.Match(line, pattern2);
            if (match2.Success)
            {
                if (TryParseTime("0", match2.Groups[1].Value, match2.Groups[2].Value, match2.Groups[3].Value, out startTime) &&
                    TryParseTime("0", match2.Groups[4].Value, match2.Groups[5].Value, match2.Groups[6].Value, out endTime))
                {
                    return true;
                }
            }

            // 尝试匹配数组格式：[1:23.456, 2:34.567]
            var pattern3 = @"\[(\d{1,2}):(\d{2})[,.](\d{1,3}),\s*(\d{1,2}):(\d{2})[,.](\d{1,3})\]";
            var match3 = Regex.Match(line, pattern3);
            if (match3.Success)
            {
                if (TryParseTime("0", match3.Groups[1].Value, match3.Groups[2].Value, match3.Groups[3].Value, out startTime) &&
                    TryParseTime("0", match3.Groups[4].Value, match3.Groups[5].Value, match3.Groups[6].Value, out endTime))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 解析时间字符串为毫秒
        /// </summary>
        private static bool TryParseTime(string hours, string minutes, string seconds, string milliseconds, out long totalMs)
        {
            totalMs = 0;

            if (!int.TryParse(hours, out var h) || h < 0 || h > 23)
                return false;
            if (!int.TryParse(minutes, out var m) || m < 0 || m > 59)
                return false;
            if (!int.TryParse(seconds, out var s) || s < 0 || s > 59)
                return false;

            // 毫秒可能是1-3位数字
            var msStr = milliseconds.PadRight(3, '0').Substring(0, 3);
            if (!int.TryParse(msStr, out var ms) || ms < 0 || ms > 999)
                return false;

            totalMs = (h * 3600L + m * 60L + s) * 1000L + ms;
            return true;
        }

        /// <summary>
        /// 将时间码片段列表格式化为输出文本（一行一码）
        /// </summary>
        public static string FormatTimecodesForOutput(List<TimecodeSegment> segments)
        {
            if (segments == null || segments.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("\r\n", segments.Select(s => s.ToDisplayString()));
        }
    }
}

