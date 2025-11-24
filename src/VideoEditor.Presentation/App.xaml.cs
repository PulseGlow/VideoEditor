using System.Configuration;
using System.Data;
using System.Windows;
using LibVLCSharp.Shared;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupArgs = e.Args ?? Array.Empty<string>();

        // 初始化日志系统
        DebugLogger.Initialize();
        DebugLogger.LogInfo("应用程序启动");
        if (StartupArgs.Length > 0)
        {
            DebugLogger.LogInfo($"启动参数: {string.Join(" ", StartupArgs)}");
        }

        // 全局异常处理
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            DebugLogger.LogError($"未处理的异常(AppDomain): {exception?.Message}\n{exception?.StackTrace}");
            MessageBox.Show($"应用程序发生未处理的异常:\n{exception?.Message}\n\n详细信息已记录到 debug.log", 
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        this.DispatcherUnhandledException += (sender, args) =>
        {
            DebugLogger.LogError($"未处理的异常(Dispatcher): {args.Exception.Message}\n{args.Exception.StackTrace}");
            MessageBox.Show($"应用程序发生UI异常:\n{args.Exception.Message}\n\n详细信息已记录到 debug.log", 
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // 防止应用崩溃
        };

        // 初始化 LibVLCSharp Core
        try
        {
            DebugLogger.LogInfo("正在初始化 LibVLC...");
            Core.Initialize();
            DebugLogger.LogSuccess("LibVLC 初始化成功");
        }
        catch (Exception ex)
        {
            DebugLogger.LogError($"初始化 LibVLC 失败: {ex.Message}");
            DebugLogger.LogError($"堆栈跟踪:\n{ex.StackTrace}");
            
            MessageBox.Show(
                $"初始化 LibVLC 失败:\n{ex.Message}\n\n请确保 LibVLC 库文件已正确安装。",
                "启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            
            // 初始化失败时退出应用
            Shutdown();
            return;
        }

        DebugLogger.LogInfo("准备创建主窗口...");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DebugLogger.LogInfo("应用程序退出");
        DebugLogger.Close();
        
        // 清理资源
        base.OnExit(e);
    }
}

