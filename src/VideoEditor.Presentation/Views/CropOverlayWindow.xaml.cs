using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Presentation.Services;

namespace VideoEditor.Presentation.Views
{
    public partial class CropOverlayWindow : Window
    {
        private bool _draggingSelector = false;
        private bool _draggingHandle = false;
        private string? _handleTag;
        private Point _startPoint;
        private double _startLeft, _startTop, _startWidth, _startHeight;
        private bool _lockAspectRatio = false;
        private Rect _videoDisplayRect = new Rect(0, 0, 1920, 1080);
        private bool _showOperationHint = false;

        public Action<Rect>? OnCanvasRectChanged { get; set; }
        public Action? OnCropConfirmed { get; set; }
        public Action? OnCropCancelled { get; set; }

        /// <summary>
        /// 获取当前比例锁定状态
        /// </summary>
        public bool IsAspectRatioLocked() => _lockAspectRatio;

        public CropOverlayWindow()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                UpdateMask();
                UpdateSelectorAppearance(); // 设置初始外观
            };
        }

        /// <summary>
        /// 设置比例锁定状态
        /// </summary>
        public void SetLockAspect(bool locked)
        {
            _lockAspectRatio = locked;
            UpdateAspectLockVisuals();
            UpdateSizeDisplay(); // 重新计算显示格式
        }

        /// <summary>
        /// 更新比例锁定相关的视觉元素
        /// </summary>
        private void UpdateAspectLockVisuals()
        {
            if (_lockAspectRatio)
            {
                // 显示比例锁定指示器
                AspectLockIndicator.Visibility = Visibility.Visible;

                // 更新操作提示
                if (OperationHint.Visibility == Visibility.Visible)
                {
                    int width = (int)Selector.Width;
                    int height = (int)Selector.Height;
                    int gcd = GCD(width, height);
                    int ratioWidth = width / gcd;
                    int ratioHeight = height / gcd;
                    AspectHintText.Text = $"• 当前比例: {ratioWidth}:{ratioHeight} (已锁定)";
                }

                // 改变边框颜色表示锁定状态
                Selector.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色表示锁定
            }
            else
            {
                // 隐藏比例锁定指示器
                AspectLockIndicator.Visibility = Visibility.Collapsed;

                // 更新操作提示
                if (OperationHint.Visibility == Visibility.Visible)
                {
                    AspectHintText.Text = "• 当前比例: 自由调整";
                }

                // 恢复默认边框颜色
                Selector.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // 蓝色表示自由
            }
        }

        /// <summary>
        /// 设置视频显示区域，用于边界可视化和约束
        /// videoRect参数现在是基于裁剪框实际坐标系的坐标
        /// </summary>
        public void SetVideoDisplayRect(Rect videoRect)
        {
            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] SetVideoDisplayRect: X={videoRect.X:F1}, Y={videoRect.Y:F1}, W={videoRect.Width:F1}, H={videoRect.Height:F1}");
            _videoDisplayRect = videoRect;

            // 设置边界矩形位置和大小
            Canvas.SetLeft(VideoBoundaryRect, videoRect.X);
            Canvas.SetTop(VideoBoundaryRect, videoRect.Y);
            VideoBoundaryRect.Width = videoRect.Width;
            VideoBoundaryRect.Height = videoRect.Height;

            // 确保裁剪框不超出视频边界
            ConstrainSelectorToVideoBounds();
        }

        /// <summary>
        /// 确保裁剪框在视频边界内
        /// </summary>
        private void ConstrainSelectorToVideoBounds()
        {
            var currentRect = GetCanvasRect();

            double constrainedLeft = Math.Max(_videoDisplayRect.X,
                Math.Min(currentRect.X, _videoDisplayRect.X + _videoDisplayRect.Width - currentRect.Width));
            double constrainedTop = Math.Max(_videoDisplayRect.Y,
                Math.Min(currentRect.Y, _videoDisplayRect.Y + _videoDisplayRect.Height - currentRect.Height));
            double constrainedWidth = Math.Min(currentRect.Width, _videoDisplayRect.Width);
            double constrainedHeight = Math.Min(currentRect.Height, _videoDisplayRect.Height);

            // 只在超出边界时才调整
            if (constrainedLeft != currentRect.X || constrainedTop != currentRect.Y ||
                constrainedWidth != currentRect.Width || constrainedHeight != currentRect.Height)
            {
                SetCanvasRect(new Rect(constrainedLeft, constrainedTop, constrainedWidth, constrainedHeight));
            }
        }

        public void ResetToCenter()
        {
            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] ResetToCenter called, videoRect: X={_videoDisplayRect.X:F1}, Y={_videoDisplayRect.Y:F1}, W={_videoDisplayRect.Width:F1}, H={_videoDisplayRect.Height:F1}");
            // 在视频区域内居中（使用裁剪框实际坐标系）
            double w = Math.Max(50, _videoDisplayRect.Width * 0.8);
            double h = Math.Max(50, _videoDisplayRect.Height * 0.8);
            double left = _videoDisplayRect.X + (_videoDisplayRect.Width - w) / 2.0;
            double top = _videoDisplayRect.Y + (_videoDisplayRect.Height - h) / 2.0;
            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] ResetToCenter setting: X={left:F1}, Y={top:F1}, W={w:F1}, H={h:F1}");
            Canvas.SetLeft(Selector, left);
            Canvas.SetTop(Selector, top);
            Selector.Width = w;
            Selector.Height = h;
            UpdateMask();
            FireRectChanged();
        }

        /// <summary>
        /// 设置指定尺寸的居中裁剪框
        /// </summary>
        public void SetCropRectCentered(double width, double height)
        {
            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] SetCropRectCentered: W={width:F1}, H={height:F1}, videoRect: X={_videoDisplayRect.X:F1}, Y={_videoDisplayRect.Y:F1}, W={_videoDisplayRect.Width:F1}, H={_videoDisplayRect.Height:F1}");

            // 确保尺寸在合理范围内
            width = Math.Max(50, Math.Min(width, _videoDisplayRect.Width));
            height = Math.Max(50, Math.Min(height, _videoDisplayRect.Height));

            // 计算居中位置
            double left = _videoDisplayRect.X + (_videoDisplayRect.Width - width) / 2.0;
            double top = _videoDisplayRect.Y + (_videoDisplayRect.Height - height) / 2.0;

            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] SetCropRectCentered final: X={left:F1}, Y={top:F1}, W={width:F1}, H={height:F1}");

            Canvas.SetLeft(Selector, left);
            Canvas.SetTop(Selector, top);
            Selector.Width = width;
            Selector.Height = height;

            UpdateMask();
            FireRectChanged();
        }

        public void SetCanvasRect(Rect r)
        {
            System.Diagnostics.Debug.WriteLine($"[CropOverlayWindow] SetCanvasRect: X={r.X:F1}, Y={r.Y:F1}, W={r.Width:F1}, H={r.Height:F1}");
            Canvas.SetLeft(Selector, r.X);
            Canvas.SetTop(Selector, r.Y);
            Selector.Width = r.Width;
            Selector.Height = r.Height;
            UpdateMask();
            FireRectChanged();
        }

        public Rect GetCanvasRect()
        {
            return new Rect(Canvas.GetLeft(Selector), Canvas.GetTop(Selector), Selector.Width, Selector.Height);
        }

        private void FireRectChanged()
        {
            UpdateSizeDisplay();
            OnCanvasRectChanged?.Invoke(GetCanvasRect());
        }

        /// <summary>
        /// 更新尺寸显示，包括比例信息
        /// </summary>
        private void UpdateSizeDisplay()
        {
            int width = (int)Selector.Width;
            int height = (int)Selector.Height;

            if (_lockAspectRatio)
            {
                // 计算最简比例（使用更精确的算法）
                int gcd = GCD(width, height);
                int ratioWidth = width / gcd;
                int ratioHeight = height / gcd;

                // 对于常用比例，显示标准名称
                string ratioText = GetStandardRatioName(ratioWidth, ratioHeight);
                if (ratioText != null)
                {
                    SizeText.Text = $"{width} × {height} ({ratioText})";
                }
                else
                {
                    SizeText.Text = $"{width} × {height} ({ratioWidth}:{ratioHeight})";
                }
            }
            else
            {
                // 显示精确的小数比例
                double ratio = (double)width / height;
                SizeText.Text = $"{width} × {height} ({ratio:F3}:1)";
            }
        }

        /// <summary>
        /// 获取标准比例名称
        /// </summary>
        private string? GetStandardRatioName(int width, int height)
        {
            // 常用比例映射
            var standardRatios = new Dictionary<(int, int), string>
            {
                {(16, 9), "16:9"},
                {(9, 16), "9:16"},
                {(4, 3), "4:3"},
                {(3, 4), "3:4"},
                {(21, 9), "21:9"},
                {(1, 1), "1:1"},
                {(16, 10), "16:10"},
                {(5, 4), "5:4"},
                {(3, 2), "3:2"},
                {(2, 3), "2:3"}
            };

            if (standardRatios.TryGetValue((width, height), out string? name))
            {
                return name;
            }

            // 检查黄金比例 (约1.618:1)
            double ratio = (double)width / height;
            if (Math.Abs(ratio - 1.618) < 0.01)
            {
                return "黄金比例";
            }

            return null;
        }

        /// <summary>
        /// 计算最大公约数
        /// </summary>
        private static int GCD(int a, int b)
        {
            while (b != 0)
            {
                int temp = b;
                b = a % b;
                a = temp;
            }
            return a;
        }

        private void UpdateMask()
        {
            var left = Canvas.GetLeft(Selector);
            var top = Canvas.GetTop(Selector);
            var geo = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)),
                new RectangleGeometry(new Rect(left, top, Selector.Width, Selector.Height)));
            MaskPath.Data = geo;
        }

        private void Selector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSelector = true;
            _startPoint = e.GetPosition(RootCanvas);
            _startLeft = Canvas.GetLeft(Selector);
            _startTop = Canvas.GetTop(Selector);
            Selector.CaptureMouse();
            e.Handled = true;
        }

        private void Selector_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSelector && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(RootCanvas);
                var dx = p.X - _startPoint.X;
                var dy = p.Y - _startPoint.Y;

                // 限制在视频边界内移动（使用裁剪框实际坐标系）
                var maxLeft = _videoDisplayRect.X + _videoDisplayRect.Width - Selector.Width;
                var maxTop = _videoDisplayRect.Y + _videoDisplayRect.Height - Selector.Height;
                var newLeft = Math.Max(_videoDisplayRect.X, Math.Min(_startLeft + dx, maxLeft));
                var newTop = Math.Max(_videoDisplayRect.Y, Math.Min(_startTop + dy, maxTop));

                Canvas.SetLeft(Selector, newLeft);
                Canvas.SetTop(Selector, newTop);
                UpdateMask();
                FireRectChanged();
            }
        }

        private void Selector_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggingSelector)
            {
                _draggingSelector = false;
                Selector.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                _draggingHandle = true;
                _handleTag = tag;
                _startPoint = e.GetPosition(RootCanvas);
                _startLeft = Canvas.GetLeft(Selector);
                _startTop = Canvas.GetTop(Selector);
                _startWidth = Selector.Width;
                _startHeight = Selector.Height;
                fe.CaptureMouse();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingHandle && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(RootCanvas);
                var dx = p.X - _startPoint.X;
                var dy = p.Y - _startPoint.Y;

                double left = _startLeft;
                double top = _startTop;
                double width = _startWidth;
                double height = _startHeight;

                switch (_handleTag)
                {
                    case "TopLeft":
                        left = Math.Max(0, Math.Min(_startLeft + dx, _startLeft + _startWidth - 50));
                        top = Math.Max(0, Math.Min(_startTop + dy, _startTop + _startHeight - 50));
                        width = _startWidth - (left - _startLeft);
                        height = _startHeight - (top - _startTop);
                        break;
                    case "Top":
                        top = Math.Max(0, Math.Min(_startTop + dy, _startTop + _startHeight - 50));
                        height = _startHeight - (top - _startTop);
                        break;
                    case "TopRight":
                        top = Math.Max(0, Math.Min(_startTop + dy, _startTop + _startHeight - 50));
                        width = Math.Max(50, Math.Min(_startWidth + dx, ActualWidth - _startLeft));
                        height = _startHeight - (top - _startTop);
                        break;
                    case "Left":
                        left = Math.Max(0, Math.Min(_startLeft + dx, _startLeft + _startWidth - 50));
                        width = _startWidth - (left - _startLeft);
                        break;
                    case "Right":
                        width = Math.Max(50, Math.Min(_startWidth + dx, ActualWidth - _startLeft));
                        break;
                    case "BottomLeft":
                        left = Math.Max(0, Math.Min(_startLeft + dx, _startLeft + _startWidth - 50));
                        width = _startWidth - (left - _startLeft);
                        height = Math.Max(50, Math.Min(_startHeight + dy, ActualHeight - _startTop));
                        break;
                    case "Bottom":
                        height = Math.Max(50, Math.Min(_startHeight + dy, ActualHeight - _startTop));
                        break;
                    case "BottomRight":
                        width = Math.Max(50, Math.Min(_startWidth + dx, ActualWidth - _startLeft));
                        height = Math.Max(50, Math.Min(_startHeight + dy, ActualHeight - _startTop));
                        break;
                }

                // 应用比例锁定（如果启用）
                if (_lockAspectRatio)
                {
                    double ar = _startWidth / _startHeight;

                    // 根据拖拽的点位类型决定如何应用比例
                    switch (_handleTag)
                    {
                        case "Left":
                        case "Right":
                            // 左右点：宽度为主，高度按比例调整
                            height = width / ar;
                            break;

                        case "Top":
                        case "Bottom":
                            // 上下点：高度为主，宽度按比例调整
                            width = height * ar;
                            break;

                        default:
                            // 四个角：根据鼠标移动方向决定以哪个维度为主
                            if (Math.Abs(dx) > Math.Abs(dy))
                            {
                                // 水平移动更多，以宽度为主
                                height = width / ar;
                            }
                            else
                            {
                                // 垂直移动更多，以高度为主
                                width = height * ar;
                            }
                            break;
                    }
                }

                // 改进的边界约束 - 更自然的用户体验
                // 首先确保最小尺寸
                width = Math.Max(50, width);
                height = Math.Max(50, height);

                // 根据拖拽点位类型应用不同的边界约束策略
                switch (_handleTag)
                {
                    case "Left":
                        // 左边点：可以调整左侧边界，但不能超过右边界
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _startLeft + _startWidth - 50));
                        width = _startLeft + _startWidth - left;
                        // 重新计算高度以保持比例
                        if (_lockAspectRatio)
                            height = width / (_startWidth / _startHeight);
                        // 确保不超出上下边界
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _videoDisplayRect.Y + _videoDisplayRect.Height - height));
                        break;

                    case "Right":
                        // 右边点：可以调整宽度，但不能超过视频宽度
                        width = Math.Max(50, Math.Min(width, _videoDisplayRect.X + _videoDisplayRect.Width - left));
                        // 重新计算高度以保持比例
                        if (_lockAspectRatio)
                            height = width / (_startWidth / _startHeight);
                        // 确保不超出上下边界
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _videoDisplayRect.Y + _videoDisplayRect.Height - height));
                        break;

                    case "Top":
                        // 顶部点：可以调整上边界，但不能超过下边界
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _startTop + _startHeight - 50));
                        height = _startTop + _startHeight - top;
                        // 重新计算宽度以保持比例
                        if (_lockAspectRatio)
                            width = height * (_startWidth / _startHeight);
                        // 确保不超出左右边界
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _videoDisplayRect.X + _videoDisplayRect.Width - width));
                        break;

                    case "Bottom":
                        // 底部点：可以调整高度，但不能超过视频高度
                        height = Math.Max(50, Math.Min(height, _videoDisplayRect.Y + _videoDisplayRect.Height - top));
                        // 重新计算宽度以保持比例
                        if (_lockAspectRatio)
                            width = height * (_startWidth / _startHeight);
                        // 确保不超出左右边界
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _videoDisplayRect.X + _videoDisplayRect.Width - width));
                        break;

                    case "TopLeft":
                        // 左上角：两个方向都可以调整，但有限制
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _startLeft + _startWidth - 50));
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _startTop + _startHeight - 50));
                        width = _startLeft + _startWidth - left;
                        height = _startTop + _startHeight - top;
                        // 如果锁定比例，需要重新计算以保持比例
                        if (_lockAspectRatio)
                        {
                            double ar = _startWidth / _startHeight;
                            // 选择较小的尺寸来保持比例
                            double maxWidth = width;
                            double maxHeight = height;
                            if (width / ar <= height)
                            {
                                height = width / ar;
                            }
                            else
                            {
                                width = height * ar;
                            }
                        }
                        break;

                    case "TopRight":
                        // 右上角
                        width = Math.Max(50, Math.Min(width, _videoDisplayRect.X + _videoDisplayRect.Width - left));
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _startTop + _startHeight - 50));
                        height = _startTop + _startHeight - top;
                        // 保持比例
                        if (_lockAspectRatio)
                        {
                            double ar = _startWidth / _startHeight;
                            height = width / ar;
                            height = Math.Max(50, Math.Min(height, _startTop + _startHeight - _videoDisplayRect.Y));
                            width = height * ar;
                        }
                        break;

                    case "BottomLeft":
                        // 左下角
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _startLeft + _startWidth - 50));
                        height = Math.Max(50, Math.Min(height, _videoDisplayRect.Y + _videoDisplayRect.Height - top));
                        width = _startLeft + _startWidth - left;
                        // 保持比例
                        if (_lockAspectRatio)
                        {
                            double ar = _startWidth / _startHeight;
                            width = height * ar;
                            width = Math.Max(50, Math.Min(width, _startLeft + _startWidth - _videoDisplayRect.X));
                            height = width / ar;
                        }
                        break;

                    case "BottomRight":
                        // 右下角：最自由的调整
                        width = Math.Max(50, Math.Min(width, _videoDisplayRect.X + _videoDisplayRect.Width - left));
                        height = Math.Max(50, Math.Min(height, _videoDisplayRect.Y + _videoDisplayRect.Height - top));
                        // 保持比例
                        if (_lockAspectRatio)
                        {
                            double ar = _startWidth / _startHeight;
                            // 选择能保持比例的最大尺寸
                            double widthByHeight = height * ar;
                            double heightByWidth = width / ar;

                            if (widthByHeight <= _videoDisplayRect.Y + _videoDisplayRect.Height - top)
                            {
                                height = width / ar;
                            }
                            else
                            {
                                width = height * ar;
                            }
                        }
                        break;

                    default:
                        // 移动整个选取框
                        left = Math.Max(_videoDisplayRect.X, Math.Min(left, _videoDisplayRect.X + _videoDisplayRect.Width - width));
                        top = Math.Max(_videoDisplayRect.Y, Math.Min(top, _videoDisplayRect.Y + _videoDisplayRect.Height - height));
                        break;
                }

                Canvas.SetLeft(Selector, left);
                Canvas.SetTop(Selector, top);
                Selector.Width = width;
                Selector.Height = height;

                UpdateMask();
                FireRectChanged();
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingHandle)
            {
                _draggingHandle = false;
                _handleTag = null;
                Mouse.Capture(null);
            }
        }

        /// <summary>
        /// 选取框鼠标悬停 - 显示操作提示
        /// </summary>
        private void Selector_MouseEnter(object sender, MouseEventArgs e)
        {
            OperationHint.Visibility = Visibility.Visible;

            // 更新比例提示信息
            UpdateAspectHintText();

            // 增强边框视觉反馈
            var currentBrush = _lockAspectRatio ?
                new SolidColorBrush(Color.FromRgb(76, 175, 80)) : // 绿色表示锁定
                new SolidColorBrush(Color.FromRgb(33, 150, 243)); // 蓝色表示自由

            Selector.BorderBrush = currentBrush;
            Selector.BorderThickness = new Thickness(3);

            var backgroundColor = _lockAspectRatio ?
                Color.FromArgb(77, 76, 175, 80) : // 绿色背景
                Color.FromArgb(77, 33, 150, 243); // 蓝色背景

            Selector.Background = new SolidColorBrush(backgroundColor);
        }

        /// <summary>
        /// 选取框鼠标离开 - 隐藏操作提示
        /// </summary>
        private void Selector_MouseLeave(object sender, MouseEventArgs e)
        {
            OperationHint.Visibility = Visibility.Collapsed;

            // 恢复正常边框和颜色
            UpdateSelectorAppearance();
        }

        /// <summary>
        /// 更新选取框的外观（边框颜色、背景等）
        /// </summary>
        private void UpdateSelectorAppearance()
        {
            if (_lockAspectRatio)
            {
                // 锁定比例时的外观
                Selector.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
                Selector.Background = new SolidColorBrush(Color.FromArgb(51, 76, 175, 80)); // 绿色背景
            }
            else
            {
                // 自由调整时的外观
                Selector.BorderBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243)); // 蓝色
                Selector.Background = new SolidColorBrush(Color.FromArgb(51, 33, 150, 243)); // 蓝色背景
            }

            Selector.BorderThickness = new Thickness(2);
        }

        /// <summary>
        /// 更新比例提示文本
        /// </summary>
        private void UpdateAspectHintText()
        {
            if (_lockAspectRatio)
            {
                int width = (int)Selector.Width;
                int height = (int)Selector.Height;
                int gcd = GCD(width, height);
                int ratioWidth = width / gcd;
                int ratioHeight = height / gcd;
                AspectHintText.Text = $"• 当前比例: {ratioWidth}:{ratioHeight} (已锁定)";
            }
            else
            {
                double ratio = Selector.Width / Selector.Height;
                AspectHintText.Text = $"• 当前比例: {ratio:F2}:1 (自由调整)";
            }
        }

        /// <summary>
        /// 选取框双击 - 快速应用裁剪
        /// </summary>
        private void Selector_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 防止事件冒泡
            e.Handled = true;

            // 确认裁剪
            OnCropConfirmed?.Invoke();

            // 提供视觉反馈
            var originalBackground = Selector.Background;
            Selector.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色反馈
            var timer = new System.Timers.Timer(200);
            timer.Elapsed += (s, _) =>
            {
                Dispatcher.Invoke(() => Selector.Background = originalBackground);
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }

        /// <summary>
        /// 右键菜单 - 应用裁剪
        /// </summary>
        private void ApplyCropMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OnCropConfirmed?.Invoke();
        }

        /// <summary>
        /// 右键菜单 - 重置到中心
        /// </summary>
        private void ResetToCenterMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ResetToCenter();
            Services.ToastNotification.ShowInfo("已重置到中心位置");
        }

        /// <summary>
        /// 右键菜单 - 取消裁剪
        /// </summary>
        private void CancelCropMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OnCropCancelled?.Invoke();
        }

        /// <summary>
        /// 键盘事件处理 - 支持快捷键和方向键微调
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            double moveStep = isCtrlPressed ? 10 : 1; // Ctrl键时大步移动

            switch (e.Key)
            {
                // 确认和取消
                case Key.Enter:
                    OnCropConfirmed?.Invoke();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    OnCropCancelled?.Invoke();
                    e.Handled = true;
                    break;

                // 方向键 - Ctrl键时调整大小，否则移动
                case Key.Left:
                    if (isCtrlPressed)
                        ResizeSelector(-moveStep, 0);
                    else
                        MoveSelector(-moveStep, 0);
                    e.Handled = true;
                    break;

                case Key.Right:
                    if (isCtrlPressed)
                        ResizeSelector(moveStep, 0);
                    else
                        MoveSelector(moveStep, 0);
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (isCtrlPressed)
                        ResizeSelector(0, -moveStep);
                    else
                        MoveSelector(0, -moveStep);
                    e.Handled = true;
                    break;

                case Key.Down:
                    if (isCtrlPressed)
                        ResizeSelector(0, moveStep);
                    else
                        MoveSelector(0, moveStep);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// 方向键移动选取框
        /// </summary>
        private void MoveSelector(double deltaX, double deltaY)
        {
            var currentRect = GetCanvasRect();
            var newLeft = currentRect.X + deltaX;
            var newTop = currentRect.Y + deltaY;

            // 限制在视频边界内
            newLeft = Math.Max(_videoDisplayRect.X,
                Math.Min(newLeft, _videoDisplayRect.X + _videoDisplayRect.Width - currentRect.Width));
            newTop = Math.Max(_videoDisplayRect.Y,
                Math.Min(newTop, _videoDisplayRect.Y + _videoDisplayRect.Height - currentRect.Height));

            if (Math.Abs(newLeft - currentRect.X) > 0.1 || Math.Abs(newTop - currentRect.Y) > 0.1)
            {
                Canvas.SetLeft(Selector, newLeft);
                Canvas.SetTop(Selector, newTop);
                UpdateMask();
                FireRectChanged();
            }
        }

        /// <summary>
        /// Ctrl+方向键调整大小
        /// </summary>
        private void ResizeSelector(double deltaWidth, double deltaHeight)
        {
            var currentRect = GetCanvasRect();
            var newWidth = Math.Max(50, currentRect.Width + deltaWidth);
            var newHeight = Math.Max(50, currentRect.Height + deltaHeight);

            // 如果锁定比例，保持宽高比
            if (_lockAspectRatio)
            {
                double aspectRatio = currentRect.Width / currentRect.Height;

                if (Math.Abs(deltaWidth) > Math.Abs(deltaHeight))
                {
                    // 以宽度变化为主
                    newHeight = newWidth / aspectRatio;
                }
                else
                {
                    // 以高度变化为主
                    newWidth = newHeight * aspectRatio;
                }
            }

            // 确保不超过视频边界
            newWidth = Math.Min(newWidth, _videoDisplayRect.Width);
            newHeight = Math.Min(newHeight, _videoDisplayRect.Height);

            // 调整位置以保持中心点（如果需要）
            var centerX = currentRect.X + currentRect.Width / 2;
            var centerY = currentRect.Y + currentRect.Height / 2;

            var newLeft = centerX - newWidth / 2;
            var newTop = centerY - newHeight / 2;

            // 确保不超出边界
            newLeft = Math.Max(_videoDisplayRect.X,
                Math.Min(newLeft, _videoDisplayRect.X + _videoDisplayRect.Width - newWidth));
            newTop = Math.Max(_videoDisplayRect.Y,
                Math.Min(newTop, _videoDisplayRect.Y + _videoDisplayRect.Height - newHeight));

            if (Math.Abs(newWidth - currentRect.Width) > 0.1 || Math.Abs(newHeight - currentRect.Height) > 0.1)
            {
                Canvas.SetLeft(Selector, newLeft);
                Canvas.SetTop(Selector, newTop);
                Selector.Width = newWidth;
                Selector.Height = newHeight;
                UpdateMask();
                FireRectChanged();
            }
        }
    }
}


