using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 视频片段数据模型
    /// </summary>
    public class VideoClip : INotifyPropertyChanged
    {
        private string _name;
        private long _startTime;
        private long _endTime;
        private bool _isSelected;
        private int _order;
        private string _sourceFilePath = string.Empty;

        /// <summary>
        /// 片段名称
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        /// <summary>
        /// 显示名称（用于UI显示，带序号）
        /// </summary>
        public string DisplayName => $"片段{Order}";

        /// <summary>
        /// 自定义标题（可编辑）
        /// </summary>
        public string CustomTitle
        {
            get => string.IsNullOrWhiteSpace(_name) || _name.StartsWith("片段") ? "" : _name;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    Name = $"片段{Order}";
                }
                else
                {
                    Name = value;
                }
            }
        }

        /// <summary>
        /// 编辑标题（用于TextBox绑定，支持双向绑定）
        /// </summary>
        public string EditableTitle
        {
            get => string.IsNullOrWhiteSpace(CustomTitle) ? $"片段{Order}" : CustomTitle;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == $"片段{Order}")
                {
                    CustomTitle = ""; // 清空自定义标题，使用默认序号
                }
                else
                {
                    CustomTitle = value; // 设置自定义标题
                }
            }
        }

        /// <summary>
        /// 开始时间（毫秒）
        /// </summary>
        public long StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime != value)
                {
                    _startTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedStartTime));
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        /// <summary>
        /// 结束时间（毫秒）
        /// </summary>
        public long EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime != value)
                {
                    _endTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedEndTime));
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(FormattedDuration));
                }
            }
        }

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 顺序号
        /// </summary>
        public int Order
        {
            get => _order;
            set
            {
                if (_order != value)
                {
                    _order = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(EditableTitle));
                }
            }
        }

        private bool _isFirst;
        private bool _isLast;

        /// <summary>
        /// 是否在顶部（用于禁用上移按钮）
        /// </summary>
        public bool IsFirst
        {
            get => _isFirst;
            set
            {
                if (_isFirst != value)
                {
                    _isFirst = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 是否在底部（用于禁用下移按钮）
        /// </summary>
        public bool IsLast
        {
            get => _isLast;
            set
            {
                if (_isLast != value)
                {
                    _isLast = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 格式化的开始时间
        /// </summary>
        public string FormattedStartTime => FormatTime(StartTime);

        /// <summary>
        /// 格式化的结束时间
        /// </summary>
        public string FormattedEndTime => FormatTime(EndTime);

        /// <summary>
        /// 时长（毫秒）
        /// </summary>
        public long Duration => EndTime - StartTime;

        /// <summary>
        /// 格式化的时长
        /// </summary>
        public string FormattedDuration => FormatTime(Duration);

        /// <summary>
        /// 时间范围描述
        /// </summary>
        public string TimeRange => $"{FormattedStartTime} → {FormattedEndTime}";

        /// <summary>
        /// <summary>
        /// 视频源文件路径
        /// </summary>
        public string SourceFilePath
        {
            get => _sourceFilePath;
            set
            {
                if (_sourceFilePath != value)
                {
                    _sourceFilePath = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 构造函数
        /// </summary>
        public VideoClip(string name, long startTime, long endTime, int order = 0, string sourceFilePath = "")
        {
            _name = name;
            _startTime = startTime;
            _endTime = endTime;
            _order = order;
            _isSelected = true; // 默认选中
            _sourceFilePath = sourceFilePath ?? string.Empty;
        }

        /// <summary>
        /// 格式化时间为 HH:MM:SS.fff 格式
        /// </summary>
        private static string FormatTime(long milliseconds)
        {
            if (milliseconds < 0) return "00:00:00.000";

            var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
            return $"{timeSpan.Hours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}.{timeSpan.Milliseconds:D3}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
