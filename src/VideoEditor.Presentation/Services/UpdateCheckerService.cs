using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Presentation.Models;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 更新检查服务
    /// </summary>
    public class UpdateCheckerService
    {
        private const string GitHubApiBaseUrl = "https://api.github.com";
        private const string RepositoryOwner = "PulseGlow";
        private const string RepositoryName = "VideoEditor";
        private const int TimeoutSeconds = 10;

        private readonly HttpClient _httpClient;

        public UpdateCheckerService()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };
            // GitHub API 要求 User-Agent
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "VideoEditor-UpdateChecker");
        }

        /// <summary>
        /// 获取当前应用版本
        /// </summary>
        public string GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }

                // 如果 Assembly 版本不可用，尝试从 AssemblyInformationalVersion 获取
                var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (informationalVersion != null && !string.IsNullOrEmpty(informationalVersion.InformationalVersion))
                {
                    return informationalVersion.InformationalVersion;
                }

                return "1.0.0"; // 默认版本
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"获取当前版本失败: {ex.Message}");
                return "1.0.0";
            }
        }

        /// <summary>
        /// 检查更新
        /// </summary>
        public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var updateInfo = new UpdateInfo
            {
                CurrentVersion = GetCurrentVersion()
            };

            try
            {
                var apiUrl = $"{GitHubApiBaseUrl}/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
                DebugLogger.LogInfo($"正在检查更新: {apiUrl}");

                var response = await _httpClient.GetAsync(apiUrl, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        DebugLogger.LogInfo("未找到 Releases，可能仓库中没有发布版本");
                        updateInfo.HasUpdate = false;
                        return updateInfo;
                    }

                    throw new HttpRequestException($"检查更新失败: {response.StatusCode} - {response.ReasonPhrase}");
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var release = JsonSerializer.Deserialize<GitHubRelease>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (release == null)
                {
                    DebugLogger.LogWarning("解析 GitHub Release 数据失败");
                    updateInfo.HasUpdate = false;
                    return updateInfo;
                }

                // 解析版本号（移除 'v' 前缀）
                var latestVersion = release.TagName?.StartsWith("v", StringComparison.OrdinalIgnoreCase) == true
                    ? release.TagName.Substring(1)
                    : release.TagName ?? "0.0.0";

                updateInfo.Version = latestVersion;
                updateInfo.TagName = release.TagName ?? string.Empty;
                updateInfo.Name = release.Name ?? release.TagName ?? "最新版本";
                updateInfo.ReleaseNotes = release.Body ?? "无更新说明";
                updateInfo.DownloadUrl = release.HtmlUrl ?? $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/latest";
                updateInfo.ReleaseDate = release.PublishedAt;
                updateInfo.IsPrerelease = release.Prerelease;

                // 比较版本
                updateInfo.HasUpdate = CompareVersions(updateInfo.CurrentVersion, latestVersion) < 0;

                if (updateInfo.HasUpdate)
                {
                    DebugLogger.LogInfo($"发现新版本: {latestVersion} (当前: {updateInfo.CurrentVersion})");
                }
                else
                {
                    DebugLogger.LogInfo($"当前已是最新版本: {updateInfo.CurrentVersion}");
                }

                return updateInfo;
            }
            catch (TaskCanceledException)
            {
                DebugLogger.LogWarning("检查更新超时");
                throw new Exception("检查更新超时，请检查网络连接");
            }
            catch (HttpRequestException ex)
            {
                DebugLogger.LogError($"检查更新网络错误: {ex.Message}");
                throw new Exception($"检查更新失败: {ex.Message}");
            }
            catch (Exception ex)
            {
                DebugLogger.LogError($"检查更新异常: {ex.Message}\n{ex.StackTrace}");
                throw new Exception($"检查更新时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 比较两个版本号
        /// </summary>
        /// <param name="version1">版本1</param>
        /// <param name="version2">版本2</param>
        /// <returns>小于0表示version1小于version2，等于0表示相等，大于0表示version1大于version2</returns>
        private int CompareVersions(string version1, string version2)
        {
            try
            {
                var v1 = ParseVersion(version1);
                var v2 = ParseVersion(version2);

                // 比较主版本号
                if (v1.Major != v2.Major)
                    return v1.Major.CompareTo(v2.Major);

                // 比较次版本号
                if (v1.Minor != v2.Minor)
                    return v1.Minor.CompareTo(v2.Minor);

                // 比较修订号
                if (v1.Build != v2.Build)
                    return v1.Build.CompareTo(v2.Build);

                return 0;
            }
            catch
            {
                // 如果解析失败，使用字符串比较
                return string.Compare(version1, version2, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 解析版本号字符串为 Version 对象
        /// </summary>
        private Version ParseVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
                return new Version(0, 0, 0);

            // 移除可能的 'v' 前缀
            versionString = versionString.TrimStart('v', 'V');

            // 尝试解析为 Version
            if (Version.TryParse(versionString, out var version))
                return version;

            // 如果解析失败，尝试手动解析
            var parts = versionString.Split('.');
            var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
            var build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;

            return new Version(major, minor, build);
        }

        /// <summary>
        /// GitHub Release API 响应模型
        /// </summary>
        private class GitHubRelease
        {
            public string? TagName { get; set; }
            public string? Name { get; set; }
            public string? Body { get; set; }
            public string? HtmlUrl { get; set; }
            public DateTime? PublishedAt { get; set; }
            public bool Prerelease { get; set; }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}

