using System.Collections.Generic;
using System.IO;
using System.Text;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services;

/// <summary>
/// 负责 PotPlayer (.dpl) 播放列表的导入/导出
/// </summary>
public class DplPlaylistService
{
    private const string Header = "DAUMPLAYLIST";

    /// <summary>
    /// 读取 DPL 文件并返回解析出的绝对路径列表
    /// </summary>
    public async Task<List<string>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("未找到 DPL 播放列表文件", filePath);
        }

        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8, cancellationToken);
        if (lines.Length == 0 || !lines[0].Trim().Equals(Header, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("该文件不是有效的 PotPlayer 播放列表");
        }

        var entries = new Dictionary<int, string>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.Trim();
            var firstStar = line.IndexOf('*');
            if (firstStar <= 0 || firstStar == line.Length - 1)
            {
                continue;
            }

            var secondStar = line.IndexOf('*', firstStar + 1);
            if (secondStar <= firstStar)
            {
                continue;
            }

            if (!int.TryParse(line[..firstStar], out var index))
            {
                continue;
            }

            var key = line[(firstStar + 1)..secondStar];
            if (!key.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(secondStar + 1)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                entries[index] = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
            }
            catch
            {
                entries[index] = value.Trim();
            }
        }

        return entries.OrderBy(pair => pair.Key).Select(pair => pair.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 将当前播放列表导出为 DPL 文件
    /// </summary>
    public async Task SaveAsync(string filePath, IEnumerable<VideoFile> files, CancellationToken cancellationToken = default)
    {
        var fileList = files?.ToList() ?? new List<VideoFile>();
        if (fileList.Count == 0)
        {
            throw new InvalidOperationException("没有可导出的文件");
        }

        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine("topindex=0");
        sb.AppendLine("saveplaypos=0");

        int index = 1;
        foreach (var file in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.AppendLine($"{index}*file*{file.FilePath}");
            sb.AppendLine($"{index}*title*{file.FileName}");
            sb.AppendLine($"{index}*duration*{(int)file.Duration.TotalSeconds}");
            index++;
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, cancellationToken);
    }
}

