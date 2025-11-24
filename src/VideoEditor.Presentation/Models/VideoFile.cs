using System;
using System.ComponentModel;
using System.IO;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 视频文件信息模型
    /// </summary>
    public class VideoFile : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        private string _fileName = string.Empty;
        private long _fileSize;
        private string _formattedFileSize = string.Empty;
        private TimeSpan _duration;
        private string _formattedDuration = string.Empty;
        private int _width;
        private int _height;
        private string _videoCodec = string.Empty;
        private string _audioCodec = string.Empty;
        private string _videoBitrate = string.Empty;
        private string _audioBitrate = string.Empty;
        private int _audioChannels;
        private int _sampleRate;
        private double _frameRate;
        private bool _isSelected;
        private bool _isPlaying; // 是否正在播放
        private bool? _isFormatValidated; // 格式验证状态
        private string _formatValidationMessage = string.Empty; // 格式验证消息
        private bool _hasVideoTrack; // 是否有视频轨道
        private bool _hasAudioTrack; // 是否有音频轨道

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 文件完整路径
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set
            {
                _filePath = value;
                OnPropertyChanged(nameof(FilePath));
            }
        }

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName
        {
            get => _fileName;
            set
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
            }
        }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize
        {
            get => _fileSize;
            set
            {
                _fileSize = value;
                FormattedFileSize = FormatFileSize(value);
                OnPropertyChanged(nameof(FileSize));
            }
        }

        /// <summary>
        /// 格式化的文件大小
        /// </summary>
        public string FormattedFileSize
        {
            get => _formattedFileSize;
            set
            {
                _formattedFileSize = value;
                OnPropertyChanged(nameof(FormattedFileSize));
            }
        }

        /// <summary>
        /// 视频时长
        /// </summary>
        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                _duration = value;
                FormattedDuration = FormatDuration(value);
                OnPropertyChanged(nameof(Duration));
            }
        }

        /// <summary>
        /// 格式化的视频时长 (HH:MM:SS.FFF)
        /// </summary>
        public string FormattedDuration
        {
            get => _formattedDuration;
            set
            {
                _formattedDuration = value;
                OnPropertyChanged(nameof(FormattedDuration));
            }
        }

        /// <summary>
        /// 视频宽度
        /// </summary>
        public int Width
        {
            get => _width;
            set
            {
                _width = value;
                OnPropertyChanged(nameof(Width));
            }
        }

        /// <summary>
        /// 视频高度
        /// </summary>
        public int Height
        {
            get => _height;
            set
            {
                _height = value;
                OnPropertyChanged(nameof(Height));
            }
        }

        /// <summary>
        /// 视频编解码器
        /// </summary>
        public string VideoCodec
        {
            get => _videoCodec;
            set
            {
                _videoCodec = value;
                OnPropertyChanged(nameof(VideoCodec));
            }
        }

        /// <summary>
        /// 音频编解码器
        /// </summary>
        public string AudioCodec
        {
            get => _audioCodec;
            set
            {
                _audioCodec = value;
                OnPropertyChanged(nameof(AudioCodec));
            }
        }

        /// <summary>
        /// 视频比特率
        /// </summary>
        public string VideoBitrate
        {
            get => _videoBitrate;
            set
            {
                _videoBitrate = value;
                OnPropertyChanged(nameof(VideoBitrate));
            }
        }

        /// <summary>
        /// 音频比特率
        /// </summary>
        public string AudioBitrate
        {
            get => _audioBitrate;
            set
            {
                _audioBitrate = value;
                OnPropertyChanged(nameof(AudioBitrate));
            }
        }

        /// <summary>
        /// 音频声道数
        /// </summary>
        public int AudioChannels
        {
            get => _audioChannels;
            set
            {
                _audioChannels = value;
                OnPropertyChanged(nameof(AudioChannels));
            }
        }

        /// <summary>
        /// 音频采样率
        /// </summary>
        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                _sampleRate = value;
                OnPropertyChanged(nameof(SampleRate));
                OnPropertyChanged(nameof(FormattedSampleRate));
            }
        }

        /// <summary>
        /// 格式化的采样率（例如：48000 Hz 或 48 kHz）
        /// </summary>
        public string FormattedSampleRate
        {
            get
            {
                if (_sampleRate <= 0)
                    return "未知";
                
                if (_sampleRate >= 1000)
                    return $"{_sampleRate / 1000.0:F1} kHz";
                else
                    return $"{_sampleRate} Hz";
            }
        }

        /// <summary>
        /// 视频帧率
        /// </summary>
        public double FrameRate
        {
            get => _frameRate;
            set
            {
                _frameRate = value;
                OnPropertyChanged(nameof(FrameRate));
            }
        }

        /// <summary>
        /// 选择状态改变事件
        /// </summary>
        public event EventHandler? SelectionChanged;

        /// <summary>
        /// 是否被选中
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 是否正在播放
        /// </summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged(nameof(IsPlaying));
                }
            }
        }

        /// <summary>
        /// 格式验证状态 (null=未验证, true=支持, false=不支持)
        /// </summary>
        public bool? IsFormatValidated
        {
            get => _isFormatValidated;
            set
            {
                if (_isFormatValidated != value)
                {
                    _isFormatValidated = value;
                    OnPropertyChanged(nameof(IsFormatValidated));
                    OnPropertyChanged(nameof(FormatValidationStatus));
                    OnPropertyChanged(nameof(FormatValidationIcon));
                }
            }
        }

        /// <summary>
        /// 格式验证消息
        /// </summary>
        public string FormatValidationMessage
        {
            get => _formatValidationMessage;
            set
            {
                if (_formatValidationMessage != value)
                {
                    _formatValidationMessage = value;
                    OnPropertyChanged(nameof(FormatValidationMessage));
                }
            }
        }

        /// <summary>
        /// 是否有视频轨道
        /// </summary>
        public bool HasVideoTrack
        {
            get => _hasVideoTrack;
            set
            {
                if (_hasVideoTrack != value)
                {
                    _hasVideoTrack = value;
                    OnPropertyChanged(nameof(HasVideoTrack));
                }
            }
        }

        /// <summary>
        /// 是否有音频轨道
        /// </summary>
        public bool HasAudioTrack
        {
            get => _hasAudioTrack;
            set
            {
                if (_hasAudioTrack != value)
                {
                    _hasAudioTrack = value;
                    OnPropertyChanged(nameof(HasAudioTrack));
                }
            }
        }

        /// <summary>
        /// 格式验证状态文本
        /// </summary>
        public string FormatValidationStatus
        {
            get
            {
                if (!IsFormatValidated.HasValue)
                    return "未验证";
                else if (IsFormatValidated.Value)
                    return HasVideoTrack && HasAudioTrack ? "视频+音频" :
                           HasVideoTrack ? "仅视频" :
                           HasAudioTrack ? "仅音频" : "媒体文件";
                else
                    return "不支持";
            }
        }

        /// <summary>
        /// 格式验证状态图标
        /// </summary>
        public string FormatValidationIcon
        {
            get
            {
                if (!IsFormatValidated.HasValue)
                    return "❓";
                else if (IsFormatValidated.Value)
                    return "✅";
                else
                    return "❌";
            }
        }

        /// <summary>
        /// 分辨率字符串
        /// </summary>
        public string Resolution => $"{Width}x{Height}";

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="filePath">文件路径</param>
        public VideoFile(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            
            // 设置默认值
            VideoCodec = "加载中...";
            AudioCodec = "加载中...";
            VideoBitrate = "未知";
            AudioBitrate = "未知";
            Duration = TimeSpan.Zero;
            Width = 0;
            Height = 0;
            FrameRate = 0;
            AudioChannels = 0;
            SampleRate = 0;
            
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                FileSize = fileInfo.Length;
            }
        }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// 格式化时长
        /// </summary>
        private string FormatDuration(TimeSpan duration)
        {
            return duration.ToString(@"hh\:mm\:ss\.fff");
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
