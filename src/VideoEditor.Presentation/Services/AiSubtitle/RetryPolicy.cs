using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VideoEditor.Presentation.Services.AiSubtitle
{
    /// <summary>
    /// 请求重试策略服务
    /// 提供自动重试机制，处理网络波动和 API 限流
    /// </summary>
    public class RetryPolicy
    {
        private const int MaxRetries = 3;
        private const int BaseDelaySeconds = 2;

        /// <summary>
        /// 执行带重试的操作
        /// </summary>
        public async Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            Func<Exception, bool> shouldRetry,
            IProgress<(int attempt, string message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        var delay = TimeSpan.FromSeconds(BaseDelaySeconds * Math.Pow(2, attempt - 1));
                        progress?.Report((attempt, $"重试中... ({delay.TotalSeconds:F0}秒后)"));
                        await Task.Delay(delay, cancellationToken);
                    }

                    return await operation(cancellationToken);
                }
                catch (Exception ex) when (shouldRetry(ex))
                {
                    lastException = ex;
                    progress?.Report((attempt + 1, $"请求失败: {GetErrorMessage(ex)}，准备重试..."));
                    
                    if (attempt == MaxRetries)
                    {
                        throw new InvalidOperationException(
                            $"请求失败，已重试 {MaxRetries} 次", ex);
                    }
                }
            }

            throw lastException ?? new InvalidOperationException("未知错误");
        }

        /// <summary>
        /// 判断异常是否可重试
        /// </summary>
        public static bool IsRetryableException(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                var message = httpEx.Message.ToLowerInvariant();
                return message.Contains("429") || // Rate Limit
                       message.Contains("503") || // Service Unavailable
                       message.Contains("502") || // Bad Gateway
                       message.Contains("timeout") ||
                       message.Contains("timed out") ||
                       message.Contains("connection");
            }

            if (ex is TaskCanceledException)
            {
                return true; // 超时可以重试
            }

            return false;
        }

        private string GetErrorMessage(Exception ex)
        {
            if (ex is HttpRequestException httpEx)
            {
                return httpEx.Message;
            }
            if (ex is TaskCanceledException)
            {
                return "请求超时";
            }
            return ex.Message;
        }
    }
}

