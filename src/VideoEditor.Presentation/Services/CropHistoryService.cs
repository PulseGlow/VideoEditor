using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using VideoEditor.Presentation.Models;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 裁剪历史管理服务
    /// </summary>
    public class CropHistoryService
    {
        private const string HistoryFileName = "crop_history.json";
        private readonly string _historyFilePath;
        private readonly int _maxHistoryItems = 100; // 最大历史记录数量

        public CropHistoryService()
        {
            // 获取历史文件路径
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoEditor");

            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }

            _historyFilePath = Path.Combine(appDataPath, HistoryFileName);

            // 初始化历史记录集合
            HistoryItems = new ObservableCollection<CropHistory>();
            LoadHistory();
        }

        /// <summary>
        /// 历史记录集合
        /// </summary>
        public ObservableCollection<CropHistory> HistoryItems { get; }

        /// <summary>
        /// 添加新的裁剪记录
        /// </summary>
        public void AddCropRecord(CropHistory record)
        {
            try
            {
                // 检查是否已存在相同的记录（基于输出路径）
                var existingRecord = HistoryItems.FirstOrDefault(h =>
                    h.OutputVideoPath.Equals(record.OutputVideoPath, StringComparison.OrdinalIgnoreCase));

                if (existingRecord != null)
                {
                    // 更新现有记录
                    existingRecord.Timestamp = record.Timestamp;
                    existingRecord.Parameters = record.Parameters;
                    existingRecord.Status = record.Status;
                    existingRecord.ProcessingTimeMs = record.ProcessingTimeMs;
                    existingRecord.ErrorMessage = record.ErrorMessage;
                    existingRecord.OutputFileSize = record.OutputFileSize;
                }
                else
                {
                    // 添加新记录
                    HistoryItems.Insert(0, record); // 插入到开头

                    // 限制历史记录数量
                    while (HistoryItems.Count > _maxHistoryItems)
                    {
                        HistoryItems.RemoveAt(HistoryItems.Count - 1);
                    }
                }

                SaveHistory();
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"添加裁剪历史记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新裁剪记录状态
        /// </summary>
        public void UpdateCropStatus(string recordId, CropStatus status, string? errorMessage = null, long processingTimeMs = 0, long outputFileSize = 0)
        {
            try
            {
                var record = HistoryItems.FirstOrDefault(h => h.Id == recordId);
                if (record != null)
                {
                    record.Status = status;
                    record.ErrorMessage = errorMessage;
                    record.ProcessingTimeMs = processingTimeMs;
                    record.OutputFileSize = outputFileSize;
                    SaveHistory();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"更新裁剪状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除指定的历史记录
        /// </summary>
        public void RemoveCropRecord(string recordId)
        {
            try
            {
                var record = HistoryItems.FirstOrDefault(h => h.Id == recordId);
                if (record != null)
                {
                    HistoryItems.Remove(record);
                    SaveHistory();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"删除裁剪历史记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空所有历史记录
        /// </summary>
        public void ClearHistory()
        {
            try
            {
                HistoryItems.Clear();
                SaveHistory();
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"清空裁剪历史记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取成功的裁剪记录
        /// </summary>
        public IEnumerable<CropHistory> GetSuccessfulCrops()
        {
            return HistoryItems.Where(h => h.Status == CropStatus.Completed);
        }

        /// <summary>
        /// 获取最近的裁剪记录
        /// </summary>
        public IEnumerable<CropHistory> GetRecentCrops(int count = 10)
        {
            return HistoryItems.OrderByDescending(h => h.Timestamp).Take(count);
        }

        /// <summary>
        /// 加载历史记录
        /// </summary>
        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyFilePath))
                {
                    return;
                }

                var json = File.ReadAllText(_historyFilePath);
                var historyList = JsonSerializer.Deserialize<List<CropHistory>>(json);

                if (historyList != null)
                {
                    foreach (var item in historyList.OrderByDescending(h => h.Timestamp))
                    {
                        HistoryItems.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"加载裁剪历史记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存历史记录
        /// </summary>
        private void SaveHistory()
        {
            try
            {
                var historyList = HistoryItems.ToList();
                var json = JsonSerializer.Serialize(historyList, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(_historyFilePath, json);
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"保存裁剪历史记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取文件大小的可读字符串
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes <= 0)
                return "0 B";

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unitIndex = 0;
            double size = bytes;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:F1} {units[unitIndex]}";
        }

        /// <summary>
        /// 获取处理时间的可读字符串
        /// </summary>
        public static string FormatProcessingTime(long milliseconds)
        {
            if (milliseconds < 1000)
                return $"{milliseconds}ms";

            var seconds = milliseconds / 1000.0;
            if (seconds < 60)
                return $"{seconds:F1}s";

            var minutes = seconds / 60;
            return $"{minutes:F1}min";
        }
    }
}
