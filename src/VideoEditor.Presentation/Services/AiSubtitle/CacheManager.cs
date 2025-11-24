using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 缓存管理服务
    /// 缓存转录结果，避免重复处理相同内容
    /// </summary>
    public class CacheManager
    {
        private readonly string _cacheDirectory;

        public CacheManager(string cacheDirectory)
        {
            _cacheDirectory = cacheDirectory;
            Directory.CreateDirectory(cacheDirectory);
        }

        /// <summary>
        /// 获取缓存的转录结果
        /// </summary>
        public async Task<string?> GetCachedResultAsync(string cacheKey)
        {
            var cacheFile = GetCacheFilePath(cacheKey);
            if (!File.Exists(cacheFile))
                return null;

            try
            {
                var content = await File.ReadAllTextAsync(cacheFile);
                var cacheEntry = JsonSerializer.Deserialize<CacheEntry>(content);
                
                if (cacheEntry != null && cacheEntry.ExpiresAt > DateTime.UtcNow)
                {
                    return cacheEntry.Content;
                }

                // 过期，删除缓存文件
                File.Delete(cacheFile);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存转录结果到缓存
        /// </summary>
        public async Task SaveCacheAsync(string cacheKey, string content, TimeSpan? expiration = null)
        {
            var expirationTime = expiration ?? TimeSpan.FromHours(24);
            var cacheEntry = new CacheEntry
            {
                Content = content,
                ExpiresAt = DateTime.UtcNow.Add(expirationTime),
                CreatedAt = DateTime.UtcNow
            };

            var cacheFile = GetCacheFilePath(cacheKey);
            var json = JsonSerializer.Serialize(cacheEntry, new JsonSerializerOptions 
            { 
                WriteIndented = false 
            });
            await File.WriteAllTextAsync(cacheFile, json);
        }

        /// <summary>
        /// 生成缓存键
        /// 基于文件路径、大小、修改时间和供应商信息
        /// </summary>
        public string GenerateCacheKey(string audioPath, Models.AiSubtitleProviderProfile provider)
        {
            var fileInfo = new FileInfo(audioPath);
            var keyData = $"{audioPath}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc:O}|{provider.Id}|{provider.Model}";
            
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyData));
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 获取缓存文件路径
        /// </summary>
        private string GetCacheFilePath(string cacheKey)
        {
            return Path.Combine(_cacheDirectory, $"{cacheKey}.json");
        }

        /// <summary>
        /// 清理过期缓存
        /// </summary>
        public void CleanExpiredCache()
        {
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                    return;

                var files = Directory.GetFiles(_cacheDirectory, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var content = File.ReadAllText(file);
                        var cacheEntry = JsonSerializer.Deserialize<CacheEntry>(content);
                        
                        if (cacheEntry != null && cacheEntry.ExpiresAt <= DateTime.UtcNow)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // 忽略损坏的缓存文件
                    }
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }

        private class CacheEntry
        {
            public string Content { get; set; } = string.Empty;
            public DateTime ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }
}

