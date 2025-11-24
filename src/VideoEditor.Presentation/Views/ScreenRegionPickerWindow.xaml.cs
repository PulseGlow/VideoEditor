using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VideoEditor.Presentation.Views
{
    public partial class ScreenRegionPickerWindow : Window
    {
        private bool _isSelecting;
        private Point _startPoint;
        private Rect _currentRect;

        public ScreenRegionPickerWindow()
        {
            InitializeComponent();
            Loaded += ScreenRegionPickerWindow_Loaded;
        }

        public Rect? SelectedRegion { get; private set; }

        private void ScreenRegionPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isSelecting = true;
            _startPoint = e.GetPosition(RootCanvas);
            _currentRect = new Rect(_startPoint, _startPoint);
            UpdateSelectionVisual(_currentRect);
            CaptureMouse();
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isSelecting)
            {
                return;
            }

            var current = e.GetPosition(RootCanvas);
            _currentRect = new Rect(_startPoint, current);
            UpdateSelectionVisual(_currentRect);
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSelecting)
            {
                return;
            }

            _isSelecting = false;
            ReleaseMouseCapture();

            if (_currentRect.Width < 5 || _currentRect.Height < 5)
            {
                SelectionRectangle.Visibility = Visibility.Collapsed;
                SelectionInfoText.Text = "区域太小，请重新选择";
                SelectedRegion = null;
                return;
            }

            SelectionInfoText.Text = $"{(int)_currentRect.Width} × {(int)_currentRect.Height}";
            SelectedRegion = ConvertToPixelRect(_currentRect);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
            else if (e.Key == Key.Enter && SelectedRegion.HasValue)
            {
                DialogResult = true;
                Close();
            }
        }

        private void UpdateSelectionVisual(Rect rect)
        {
            var normalized = new Rect(
                Math.Min(rect.Left, rect.Right),
                Math.Min(rect.Top, rect.Bottom),
                Math.Abs(rect.Width),
                Math.Abs(rect.Height));

            SelectionRectangle.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelectionRectangle, normalized.Left);
            Canvas.SetTop(SelectionRectangle, normalized.Top);
            SelectionRectangle.Width = normalized.Width;
            SelectionRectangle.Height = normalized.Height;
            SelectionInfoText.Text = $"{(int)normalized.Width} × {(int)normalized.Height}";
        }

        private Rect ConvertToPixelRect(Rect dipRect)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = (dipRect.Left + Left) * dpi.DpiScaleX;
            var top = (dipRect.Top + Top) * dpi.DpiScaleY;
            var width = dipRect.Width * dpi.DpiScaleX;
            var height = dipRect.Height * dpi.DpiScaleY;

            return new Rect(
                Math.Max(0, Math.Round(left)),
                Math.Max(0, Math.Round(top)),
                Math.Max(1, Math.Round(width)),
                Math.Max(1, Math.Round(height)));
        }
    }
}

