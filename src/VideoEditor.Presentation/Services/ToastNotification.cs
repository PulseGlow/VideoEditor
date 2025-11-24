using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoEditor.Presentation.Services
{
    /// <summary>
    /// Toast通知服务 - 非阻塞式提示
    /// </summary>
    public static class ToastNotification
    {
        private static Border? _toastBorder;
        private static TextBlock? _toastText;
        private static DispatcherTimer? _hideTimer;

        /// <summary>
        /// 显示成功提示
        /// </summary>
        public static void ShowSuccess(string message, int durationMs = 2000) =>
            Show(message, "Brush.ToastSuccessBackground", "#4CAF50", durationMs);

        /// <summary>
        /// 显示信息提示
        /// </summary>
        public static void ShowInfo(string message, int durationMs = 2000) =>
            Show(message, "Brush.ToastInfoBackground", "#2196F3", durationMs);

        /// <summary>
        /// 显示警告提示
        /// </summary>
        public static void ShowWarning(string message, int durationMs = 3000) =>
            Show(message, "Brush.ToastWarningBackground", "#FF9800", durationMs);

        /// <summary>
        /// 显示错误提示
        /// </summary>
        public static void ShowError(string message, int durationMs = 4000) =>
            Show(message, "Brush.ToastErrorBackground", "#F44336", durationMs);

        /// <summary>
        /// 显示Toast通知
        /// </summary>
        private static void Show(string message, string backgroundResourceKey, string fallbackHex, int durationMs)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;
                    if (mainWindow == null) return;

                    // 查找或创建Toast容器
                    var toastContainer = FindOrCreateToastContainer(mainWindow);
                    if (toastContainer == null) return;

                    // 创建Toast元素
                    var backgroundBrush = ResolveBrush(backgroundResourceKey, fallbackHex);
                    var textBrush = ResolveBrush("Brush.ToastText", "#FFFFFF");

                    _toastBorder = new Border
                    {
                        Background = backgroundBrush,
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(20, 12, 20, 12),
                        Margin = new Thickness(0, 0, 0, 20),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Opacity = 0,
                        MaxWidth = 600
                    };

                    _toastText = new TextBlock
                    {
                        Text = message,
                        Foreground = textBrush,
                        FontSize = 14,
                        FontWeight = FontWeights.Medium,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center
                    };

                    _toastBorder.Child = _toastText;

                    // 添加到容器
                    toastContainer.Children.Clear();
                    toastContainer.Children.Add(_toastBorder);

                    // 淡入动画
                    var fadeIn = new DoubleAnimation
                    {
                        From = 0,
                        To = 0.95,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    _toastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                    // 设置自动隐藏定时器
                    _hideTimer?.Stop();
                    _hideTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(durationMs)
                    };
                    _hideTimer.Tick += (s, e) =>
                    {
                        HideToast();
                        _hideTimer?.Stop();
                    };
                    _hideTimer.Start();

                    DebugLogger.LogInfo($"Toast: {message}");
                }
                catch (Exception ex)
                {
                    DebugLogger.LogError($"显示Toast失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 隐藏Toast
        /// </summary>
        private static void HideToast()
        {
            if (_toastBorder == null) return;

            // 淡出动画
            var fadeOut = new DoubleAnimation
            {
                From = _toastBorder.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                var container = _toastBorder?.Parent as Panel;
                container?.Children.Clear();
            };

            _toastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private static Brush ResolveBrush(string resourceKey, string fallbackHex)
        {
            if (Application.Current != null &&
                Application.Current.TryFindResource(resourceKey) is Brush resourceBrush)
            {
                return resourceBrush;
            }

            var fallbackColor = (Color)ColorConverter.ConvertFromString(fallbackHex);
            return new SolidColorBrush(fallbackColor);
        }

        /// <summary>
        /// 查找或创建Toast容器
        /// </summary>
        private static Panel? FindOrCreateToastContainer(Window window)
        {
            // 查找名为ToastContainer的Grid
            var toastContainer = FindChild<Grid>(window, "ToastContainer");
            return toastContainer;
        }

        /// <summary>
        /// 在可视树中查找子元素
        /// </summary>
        private static T? FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null) return null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T typedChild && (child as FrameworkElement)?.Name == childName)
                {
                    return typedChild;
                }

                var result = FindChild<T>(child, childName);
                if (result != null) return result;
            }

            return null;
        }
    }
}




