using System;
using System.ComponentModel;
using System.IO;

namespace VideoEditor.Presentation.Models
{
    public class MergeItem : INotifyPropertyChanged
    {
        private int _order;
        private TimeSpan _duration = TimeSpan.Zero;
        private string _durationText = "--:--";
        private string _videoCodec = string.Empty;
        private string _audioCodec = string.Empty;
        private string _resolution = string.Empty;

        public MergeItem(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        public string FileName => Path.GetFileName(FilePath);

        public int Order
        {
            get => _order;
            set
            {
                if (_order != value)
                {
                    _order = value;
                    OnPropertyChanged(nameof(Order));
                }
            }
        }

        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    DurationText = FormatDuration(_duration);
                    OnPropertyChanged(nameof(Duration));
                }
            }
        }

        public string DurationText
        {
            get => _durationText;
            private set
            {
                if (_durationText != value)
                {
                    _durationText = value;
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        public string VideoCodec
        {
            get => _videoCodec;
            set
            {
                if (_videoCodec != value)
                {
                    _videoCodec = value;
                    OnPropertyChanged(nameof(VideoCodec));
                    OnPropertyChanged(nameof(TechnicalSummary));
                }
            }
        }

        public string AudioCodec
        {
            get => _audioCodec;
            set
            {
                if (_audioCodec != value)
                {
                    _audioCodec = value;
                    OnPropertyChanged(nameof(AudioCodec));
                    OnPropertyChanged(nameof(TechnicalSummary));
                }
            }
        }

        public string Resolution
        {
            get => _resolution;
            set
            {
                if (_resolution != value)
                {
                    _resolution = value;
                    OnPropertyChanged(nameof(Resolution));
                    OnPropertyChanged(nameof(TechnicalSummary));
                }
            }
        }

        public string TechnicalSummary
        {
            get
            {
                var codec = string.IsNullOrWhiteSpace(VideoCodec) ? "未知编码" : VideoCodec;
                var audio = string.IsNullOrWhiteSpace(AudioCodec) ? "音频: 未知" : $"音频: {AudioCodec}";
                var resolution = string.IsNullOrWhiteSpace(Resolution) ? "分辨率: 未知" : Resolution;
                return $"{resolution} · {codec} · {audio}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? duration.ToString(@"hh\:mm\:ss")
                : duration.ToString(@"mm\:ss");
        }
    }
}

