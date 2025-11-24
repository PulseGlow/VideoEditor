using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 播放队列管理器
    /// </summary>
    public class PlayQueueManager : INotifyPropertyChanged
    {
        private readonly ObservableCollection<VideoFile> _playQueue;
        private readonly List<VideoFile> _originalOrder;
        private int _currentIndex;
        private PlayMode _currentMode;
        private PlayQueueState _state;
        private VideoFile? _currentVideo;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<VideoFile>? CurrentVideoChanged;
        public event EventHandler<PlayQueueState>? StateChanged;

        public PlayQueueManager()
        {
            _playQueue = new ObservableCollection<VideoFile>();
            _originalOrder = new List<VideoFile>();
            _currentIndex = -1;
            _currentMode = PlayMode.Sequential;
            _state = PlayQueueState.Empty;
            _currentVideo = null;
        }

        #region 属性

        /// <summary>
        /// 播放队列
        /// </summary>
        public ObservableCollection<VideoFile> PlayQueue => _playQueue;

        /// <summary>
        /// 当前播放索引
        /// </summary>
        public int CurrentIndex
        {
            get => _currentIndex;
            private set
            {
                if (_currentIndex != value)
                {
                    _currentIndex = value;
                    OnPropertyChanged(nameof(CurrentIndex));
                    OnPropertyChanged(nameof(CurrentVideo));
                    OnPropertyChanged(nameof(HasNext));
                    OnPropertyChanged(nameof(HasPrevious));
                }
            }
        }

        /// <summary>
        /// 当前播放模式
        /// </summary>
        public PlayMode CurrentMode
        {
            get => _currentMode;
            set
            {
                if (_currentMode != value)
                {
                    _currentMode = value;
                    OnPropertyChanged(nameof(CurrentMode));
                    
                    // 如果切换到随机模式，重新打乱队列
                    if (value == PlayMode.Random || value == PlayMode.Shuffle)
                    {
                        ShuffleQueue();
                    }
                    // 如果切换到顺序模式，恢复原始顺序
                    else if (value == PlayMode.Sequential)
                    {
                        RestoreOriginalOrder();
                    }
                }
            }
        }

        /// <summary>
        /// 播放队列状态
        /// </summary>
        public PlayQueueState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged(nameof(State));
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// 当前播放的视频
        /// </summary>
        public VideoFile? CurrentVideo
        {
            get => _currentVideo;
            private set
            {
                if (_currentVideo != value)
                {
                    _currentVideo = value;
                    OnPropertyChanged(nameof(CurrentVideo));
                    if (value != null)
                    {
                        CurrentVideoChanged?.Invoke(this, value);
                    }
                }
            }
        }

        /// <summary>
        /// 是否有下一个视频
        /// </summary>
        public bool HasNext
        {
            get
            {
                if (_playQueue.Count == 0) return false;
                
                return _currentMode switch
                {
                    PlayMode.Sequential => _currentIndex < _playQueue.Count - 1,
                    PlayMode.Random => _playQueue.Count > 1,
                    PlayMode.RepeatOne => true,
                    PlayMode.RepeatAll => _playQueue.Count > 0,
                    PlayMode.Shuffle => _playQueue.Count > 1,
                    _ => false
                };
            }
        }

        /// <summary>
        /// 是否有上一个视频
        /// </summary>
        public bool HasPrevious
        {
            get
            {
                if (_playQueue.Count == 0) return false;
                
                return _currentMode switch
                {
                    PlayMode.Sequential => _currentIndex > 0,
                    PlayMode.Random => _playQueue.Count > 1,
                    PlayMode.RepeatOne => true,
                    PlayMode.RepeatAll => _playQueue.Count > 0,
                    PlayMode.Shuffle => _playQueue.Count > 1,
                    _ => false
                };
            }
        }

        /// <summary>
        /// 队列中的视频数量
        /// </summary>
        public int Count => _playQueue.Count;

        #endregion

        #region 队列管理方法

        /// <summary>
        /// 添加视频到队列
        /// </summary>
        public void AddVideo(VideoFile video)
        {
            if (video == null) return;
            
            _playQueue.Add(video);
            _originalOrder.Add(video);
            
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasNext));
            OnPropertyChanged(nameof(HasPrevious));
            
            // 如果队列为空，设置当前视频
            if (_currentIndex == -1 && _playQueue.Count > 0)
            {
                SetCurrentVideo(0);
                State = PlayQueueState.Ready;
            }
        }

        /// <summary>
        /// 添加多个视频到队列
        /// </summary>
        public void AddVideos(IEnumerable<VideoFile> videos)
        {
            if (videos == null) return;
            
            foreach (var video in videos)
            {
                AddVideo(video);
            }
        }

        /// <summary>
        /// 从队列中移除视频
        /// </summary>
        public void RemoveVideo(VideoFile video)
        {
            if (video == null) return;
            
            var index = _playQueue.IndexOf(video);
            if (index >= 0)
            {
                _playQueue.RemoveAt(index);
                _originalOrder.Remove(video);
                
                // 调整当前索引
                if (index < _currentIndex)
                {
                    CurrentIndex--;
                }
                else if (index == _currentIndex)
                {
                    // 如果移除的是当前视频，选择下一个
                    if (_playQueue.Count > 0)
                    {
                        if (_currentIndex >= _playQueue.Count)
                        {
                            _currentIndex = _playQueue.Count - 1;
                        }
                        SetCurrentVideo(_currentIndex);
                    }
                    else
                    {
                        CurrentIndex = -1;
                        CurrentVideo = null;
                        State = PlayQueueState.Empty;
                    }
                }
                
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(HasNext));
                OnPropertyChanged(nameof(HasPrevious));
            }
        }

        /// <summary>
        /// 清空队列
        /// </summary>
        public void Clear()
        {
            _playQueue.Clear();
            _originalOrder.Clear();
            CurrentIndex = -1;
            CurrentVideo = null;
            State = PlayQueueState.Empty;
            
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasNext));
            OnPropertyChanged(nameof(HasPrevious));
        }

        /// <summary>
        /// 设置当前播放的视频
        /// </summary>
        public void SetCurrentVideo(int index)
        {
            if (index >= 0 && index < _playQueue.Count)
            {
                CurrentIndex = index;
                CurrentVideo = _playQueue[index];
            }
        }

        /// <summary>
        /// 设置当前播放的视频
        /// </summary>
        public void SetCurrentVideo(VideoFile video)
        {
            var index = _playQueue.IndexOf(video);
            if (index >= 0)
            {
                SetCurrentVideo(index);
            }
        }

        #endregion

        #region 播放控制方法

        /// <summary>
        /// 获取下一个要播放的视频（根据当前播放模式）
        /// </summary>
        /// <returns>
        /// Sequential: 顺序返回下一首
        /// Random: 随机返回下一首
        /// RepeatOne: 返回当前视频（单曲循环）
        /// RepeatAll: 循环播放列表
        /// Shuffle: 随机播放
        /// </returns>
        public VideoFile? GetNextVideo()
        {
            if (_playQueue.Count == 0) return null;
            
            return _currentMode switch
            {
                PlayMode.Sequential => GetNextSequential(), // 顺序播放
                PlayMode.Random => GetNextRandom(),         // 随机播放
                PlayMode.RepeatOne => CurrentVideo,         // 单曲循环
                PlayMode.RepeatAll => GetNextRepeatAll(),   // 列表循环
                PlayMode.Shuffle => GetNextShuffle(),       // 随机循环
                _ => null
            };
        }

        /// <summary>
        /// 获取上一个视频
        /// </summary>
        public VideoFile? GetPreviousVideo()
        {
            if (_playQueue.Count == 0) return null;
            
            return _currentMode switch
            {
                PlayMode.Sequential => GetPreviousSequential(),
                PlayMode.Random => GetPreviousRandom(),
                PlayMode.RepeatOne => CurrentVideo,
                PlayMode.RepeatAll => GetPreviousRepeatAll(),
                PlayMode.Shuffle => GetPreviousShuffle(),
                _ => null
            };
        }

        /// <summary>
        /// 播放下一个视频
        /// </summary>
        public void PlayNext()
        {
            var nextVideo = GetNextVideo();
            if (nextVideo != null)
            {
                SetCurrentVideo(nextVideo);
                State = PlayQueueState.Ready;
            }
        }

        /// <summary>
        /// 播放上一个视频
        /// </summary>
        public void PlayPrevious()
        {
            var previousVideo = GetPreviousVideo();
            if (previousVideo != null)
            {
                SetCurrentVideo(previousVideo);
                State = PlayQueueState.Ready;
            }
        }

        /// <summary>
        /// 开始播放
        /// </summary>
        public void StartPlayback()
        {
            if (CurrentVideo != null)
            {
                State = PlayQueueState.Playing;
            }
        }

        /// <summary>
        /// 暂停播放
        /// </summary>
        public void PausePlayback()
        {
            if (State == PlayQueueState.Playing)
            {
                State = PlayQueueState.Paused;
            }
        }

        /// <summary>
        /// 停止播放
        /// </summary>
        public void StopPlayback()
        {
            State = PlayQueueState.Ready;
        }

        /// <summary>
        /// 完成播放
        /// </summary>
        public void CompletePlayback()
        {
            State = PlayQueueState.Completed;
            
            // 自动播放下一个
            if (HasNext)
            {
                PlayNext();
            }
        }

        #endregion

        #region 私有方法

        private VideoFile? GetNextSequential()
        {
            return _currentIndex < _playQueue.Count - 1 ? _playQueue[_currentIndex + 1] : null;
        }

        private VideoFile? GetNextRandom()
        {
            if (_playQueue.Count <= 1) return null;
            
            var random = new Random();
            int nextIndex;
            do
            {
                nextIndex = random.Next(_playQueue.Count);
            } while (nextIndex == _currentIndex);
            
            return _playQueue[nextIndex];
        }

        private VideoFile? GetNextRepeatAll()
        {
            return _currentIndex < _playQueue.Count - 1 ? _playQueue[_currentIndex + 1] : _playQueue[0];
        }

        private VideoFile? GetNextShuffle()
        {
            return GetNextRandom();
        }

        private VideoFile? GetPreviousSequential()
        {
            return _currentIndex > 0 ? _playQueue[_currentIndex - 1] : null;
        }

        private VideoFile? GetPreviousRandom()
        {
            return GetNextRandom();
        }

        private VideoFile? GetPreviousRepeatAll()
        {
            return _currentIndex > 0 ? _playQueue[_currentIndex - 1] : _playQueue[_playQueue.Count - 1];
        }

        private VideoFile? GetPreviousShuffle()
        {
            return GetPreviousRandom();
        }

        /// <summary>
        /// 打乱队列顺序
        /// </summary>
        private void ShuffleQueue()
        {
            if (_playQueue.Count <= 1) return;
            
            var random = new Random();
            var shuffled = _playQueue.OrderBy(x => random.Next()).ToList();
            
            _playQueue.Clear();
            foreach (var item in shuffled)
            {
                _playQueue.Add(item);
            }
            
            // 重新设置当前视频的索引
            if (CurrentVideo != null)
            {
                var newIndex = _playQueue.IndexOf(CurrentVideo);
                if (newIndex >= 0)
                {
                    CurrentIndex = newIndex;
                }
            }
        }

        /// <summary>
        /// 恢复原始顺序
        /// </summary>
        private void RestoreOriginalOrder()
        {
            if (_originalOrder.Count == 0) return;
            
            var currentVideo = CurrentVideo;
            
            _playQueue.Clear();
            foreach (var item in _originalOrder)
            {
                _playQueue.Add(item);
            }
            
            // 重新设置当前视频的索引
            if (currentVideo != null)
            {
                var newIndex = _playQueue.IndexOf(currentVideo);
                if (newIndex >= 0)
                {
                    CurrentIndex = newIndex;
                }
            }
        }

        #endregion

        #region INotifyPropertyChanged

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}

