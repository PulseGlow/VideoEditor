using System;
using System.Diagnostics;
using System.IO;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// 调试日志记录器 - 同时输出到控制台和文件
    /// </summary>
    public static class DebugLogger
    {
        private static string _logFilePath = string.Empty;
        private static readonly object _lockObject = new object();
        private static bool _isInitialized = false;

        /// <summary>
        /// 初始化日志系统 - 删除旧日志,创建新日志文件
        /// </summary>
        public static void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                // 获取项目根目录
                string projectRoot = AppDomain.CurrentDomain.BaseDirectory;
                
                // 向上查找到项目根目录 (包含 .sln 文件的目录)
                DirectoryInfo? dir = new DirectoryInfo(projectRoot);
                while (dir != null && dir.GetFiles("*.sln").Length == 0)
                {
                    dir = dir.Parent;
                }

                string logDirectory = dir?.FullName ?? projectRoot;
                _logFilePath = Path.Combine(logDirectory, "debug.log");

                // 删除旧日志文件
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }

                // 创建新日志文件并写入头部信息
                lock (_lockObject)
                {
                    File.WriteAllText(_logFilePath, 
                        $"========================================\n" +
                        $"VideoEditor 调试日志\n" +
                        $"开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"日志路径: {_logFilePath}\n" +
                        $"========================================\n\n");
                }

                _isInitialized = true;
                
                // 输出到控制台
                Debug.WriteLine($"✅ 日志系统已初始化: {_logFilePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 初始化日志系统失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 写入日志 (同时输出到控制台和文件)
        /// </summary>
        public static void Log(string message)
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logMessage = $"[{timestamp}] {message}";

            // 输出到控制台
            Debug.WriteLine(message);

            // 输出到文件
            try
            {
                lock (_lockObject)
                {
                    File.AppendAllText(_logFilePath, logMessage + "\n");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 写入日志文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 写入错误日志 (红色标记)
        /// </summary>
        public static void LogError(string message)
        {
            Log($"❌ 错误: {message}");
        }

        /// <summary>
        /// 写入警告日志 (黄色标记)
        /// </summary>
        public static void LogWarning(string message)
        {
            Log($"⚠️ 警告: {message}");
        }

        /// <summary>
        /// 写入成功日志 (绿色标记)
        /// </summary>
        public static void LogSuccess(string message)
        {
            Log($"✅ 成功: {message}");
        }

        /// <summary>
        /// 写入信息日志 (蓝色标记)
        /// </summary>
        public static void LogInfo(string message)
        {
            Log($"ℹ️ 信息: {message}");
        }

        /// <summary>
        /// 关闭日志系统
        /// </summary>
        public static void Close()
        {
            if (!_isInitialized) return;

            try
            {
                lock (_lockObject)
                {
                    File.AppendAllText(_logFilePath, 
                        $"\n========================================\n" +
                        $"结束时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"========================================\n");
                }

                Debug.WriteLine($"✅ 日志系统已关闭");
                _isInitialized = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 关闭日志系统失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取日志文件路径
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logFilePath;
        }
    }
}





