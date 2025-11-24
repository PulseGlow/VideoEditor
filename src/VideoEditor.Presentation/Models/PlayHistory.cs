using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 播放历史记录
    /// </summary>
    public class PlayHistory : INotifyPropertyChanged
    {
        private VideoFile _video;
        private DateTime _playTime;
        private TimeSpan _playedDuration;
        private TimeSpan _totalDuration;
        private bool _isCompleted;

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlayHistory(VideoFile video, DateTime playTime, TimeSpan playedDuration, TimeSpan totalDuration)
        {
            _video = video ?? throw new ArgumentNullException(nameof(video));
            _playTime = playTime;
            _playedDuration = playedDuration;
            _totalDuration = totalDuration;
            _isCompleted = playedDuration.TotalSeconds / totalDuration.TotalSeconds >= 0.95;
        }

        /// <summary>
        /// 视频文件
        /// </summary>
        public VideoFile Video
        {
            get => _video;
            set
            {
                if (_video != value)
                {
                    _video = value;
                    OnPropertyChanged(nameof(Video));
                }
            }
        }

        /// <summary>
        /// 播放时间
        /// </summary>
        public DateTime PlayTime
        {
            get => _playTime;
            set
            {
                if (_playTime != value)
                {
                    _playTime = value;
                    OnPropertyChanged(nameof(PlayTime));
                }
            }
        }

        /// <summary>
        /// 已播放时长
        /// </summary>
        public TimeSpan PlayedDuration
        {
            get => _playedDuration;
            set
            {
                if (_playedDuration != value)
                {
                    _playedDuration = value;
                    OnPropertyChanged(nameof(PlayedDuration));
                    OnPropertyChanged(nameof(CompletionPercentage));
                    OnPropertyChanged(nameof(IsCompleted));
                }
            }
        }

        /// <summary>
        /// 总时长
        /// </summary>
        public TimeSpan TotalDuration
        {
            get => _totalDuration;
            set
            {
                if (_totalDuration != value)
                {
                    _totalDuration = value;
                    OnPropertyChanged(nameof(TotalDuration));
                    OnPropertyChanged(nameof(CompletionPercentage));
                    OnPropertyChanged(nameof(IsCompleted));
                }
            }
        }

        /// <summary>
        /// 完成百分比
        /// </summary>
        public double CompletionPercentage
        {
            get
            {
                if (_totalDuration.TotalSeconds == 0) return 0;
                return Math.Min(100, (_playedDuration.TotalSeconds / _totalDuration.TotalSeconds) * 100);
            }
        }

        /// <summary>
        /// 是否已完成播放
        /// </summary>
        public bool IsCompleted
        {
            get => _isCompleted;
            private set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged(nameof(IsCompleted));
                }
            }
        }

        /// <summary>
        /// 更新播放进度
        /// </summary>
        public void UpdateProgress(TimeSpan playedDuration)
        {
            PlayedDuration = playedDuration;
            IsCompleted = CompletionPercentage >= 95;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 播放历史管理器
    /// </summary>
    public class PlayHistoryManager : INotifyPropertyChanged
    {
        private readonly ObservableCollection<PlayHistory> _playHistory;
        private readonly int _maxHistoryCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlayHistoryManager(int maxHistoryCount = 100)
        {
            _playHistory = new ObservableCollection<PlayHistory>();
            _maxHistoryCount = maxHistoryCount;
        }

        /// <summary>
        /// 播放历史记录
        /// </summary>
        public ObservableCollection<PlayHistory> PlayHistory => _playHistory;

        /// <summary>
        /// 添加播放记录
        /// </summary>
        public void AddPlayRecord(VideoFile video, DateTime playTime, TimeSpan playedDuration, TimeSpan totalDuration)
        {
            if (video == null) return;

            // 检查是否已存在该视频的播放记录
            var existingRecord = _playHistory.FirstOrDefault(h => h.Video.FilePath == video.FilePath);
            if (existingRecord != null)
            {
                // 更新现有记录
                existingRecord.UpdateProgress(playedDuration);
                existingRecord.PlayTime = playTime;
                
                // 移动到列表顶部
                _playHistory.Remove(existingRecord);
                _playHistory.Insert(0, existingRecord);
            }
            else
            {
                // 创建新记录
                var newRecord = new PlayHistory(video, playTime, playedDuration, totalDuration);
                _playHistory.Insert(0, newRecord);
                
                // 限制历史记录数量
                if (_playHistory.Count > _maxHistoryCount)
                {
                    _playHistory.RemoveAt(_playHistory.Count - 1);
                }
            }
        }

        /// <summary>
        /// 获取最近播放的视频
        /// </summary>
        public VideoFile? GetRecentlyPlayedVideo()
        {
            return _playHistory.FirstOrDefault()?.Video;
        }

        /// <summary>
        /// 获取最近播放的视频列表
        /// </summary>
        public IEnumerable<VideoFile> GetRecentlyPlayedVideos(int count = 10)
        {
            return _playHistory.Take(count).Select(h => h.Video);
        }

        /// <summary>
        /// 获取未完成的播放记录
        /// </summary>
        public IEnumerable<PlayHistory> GetIncompletePlayRecords()
        {
            return _playHistory.Where(h => !h.IsCompleted);
        }

        /// <summary>
        /// 清空播放历史
        /// </summary>
        public void ClearHistory()
        {
            _playHistory.Clear();
        }

        /// <summary>
        /// 移除指定视频的播放历史
        /// </summary>
        public void RemoveVideoHistory(VideoFile video)
        {
            if (video == null) return;
            
            var recordsToRemove = _playHistory.Where(h => h.Video.FilePath == video.FilePath).ToList();
            foreach (var record in recordsToRemove)
            {
                _playHistory.Remove(record);
            }
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

