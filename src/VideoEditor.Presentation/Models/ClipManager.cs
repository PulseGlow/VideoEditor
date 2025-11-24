using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VideoEditor.Presentation.Models
{
    /// <summary>
    /// 片段管理器 - 管理视频片段列表
    /// </summary>
    public class ClipManager : INotifyPropertyChanged
    {
        private ObservableCollection<VideoClip> _clips;
        private int _nextClipNumber = 1;

        /// <summary>
        /// 片段列表
        /// </summary>
        public ObservableCollection<VideoClip> Clips
        {
            get => _clips;
            private set
            {
                if (_clips != value)
                {
                    _clips = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ClipCount));
                    OnPropertyChanged(nameof(SelectedClipCount));
                }
            }
        }

        /// <summary>
        /// 片段数量
        /// </summary>
        public int ClipCount => Clips.Count;

        /// <summary>
        /// 选中的片段数量
        /// </summary>
        public int SelectedClipCount => Clips.Count(c => c.IsSelected);

        /// <summary>
        /// 构造函数
        /// </summary>
        public ClipManager()
        {
            _clips = new ObservableCollection<VideoClip>();
            _clips.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(ClipCount));
                OnPropertyChanged(nameof(SelectedClipCount));
                UpdateClipOrders();
                
                // 为新添加的片段订阅PropertyChanged事件，以便在IsSelected改变时更新SelectedClipCount
                if (e.NewItems != null)
                {
                    foreach (VideoClip clip in e.NewItems)
                    {
                        clip.PropertyChanged += OnClipPropertyChanged;
                    }
                }
                
                // 为移除的片段取消订阅
                if (e.OldItems != null)
                {
                    foreach (VideoClip clip in e.OldItems)
                    {
                        clip.PropertyChanged -= OnClipPropertyChanged;
                    }
                }
            };
        }
        
        /// <summary>
        /// 处理片段属性变化事件
        /// </summary>
        private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VideoClip.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedClipCount));
            }
        }

        /// <summary>
        /// 添加片段
        /// </summary>
        public VideoClip AddClip(string name, long startTime, long endTime, string sourceFilePath)
        {
            // 自动生成片段名称（如果没有提供或为空）
            if (string.IsNullOrWhiteSpace(name))
                name = $"片段{Clips.Count + 1}";

            var clip = new VideoClip(name, startTime, endTime, Clips.Count + 1, sourceFilePath);
            Clips.Add(clip);

            return clip;
        }

        /// <summary>
        /// 添加片段（带验证）
        /// </summary>
        public (bool success, string errorMessage) TryAddClip(string? name, long startTime, long endTime, string? sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return (false, "无法确定片段的源文件，请重新加载视频后再试");
            }

            // 验证时间范围
            if (startTime < 0)
                return (false, "开始时间不能为负数");

            if (endTime <= startTime)
                return (false, "结束时间必须大于开始时间");

            // 检查是否与现有片段重叠（可选）
            foreach (var existingClip in Clips)
            {
                if ((startTime >= existingClip.StartTime && startTime < existingClip.EndTime) ||
                    (endTime > existingClip.StartTime && endTime <= existingClip.EndTime) ||
                    (startTime <= existingClip.StartTime && endTime >= existingClip.EndTime))
                {
                    // 允许重叠，用户可以手动调整顺序
                }
            }

            AddClip(name ?? string.Empty, startTime, endTime, sourceFilePath);
            return (true, string.Empty);
        }

        /// <summary>
        /// 删除片段
        /// </summary>
        public bool RemoveClip(VideoClip clip)
        {
            if (Clips.Remove(clip))
            {
                UpdateClipOrders();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 删除选中的片段
        /// </summary>
        public int RemoveSelectedClips()
        {
            var selectedClips = Clips.Where(c => c.IsSelected).ToList();
            foreach (var clip in selectedClips)
            {
                Clips.Remove(clip);
            }
            UpdateClipOrders();
            return selectedClips.Count;
        }

        /// <summary>
        /// 清空所有片段
        /// </summary>
        public void ClearAllClips()
        {
            Clips.Clear();
            _nextClipNumber = 1;
        }

        /// <summary>
        /// 上移片段
        /// </summary>
        public bool MoveClipUp(VideoClip clip)
        {
            var index = Clips.IndexOf(clip);
            if (index > 0)
            {
                Clips.Move(index, index - 1);
                UpdateClipOrders();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 下移片段
        /// </summary>
        public bool MoveClipDown(VideoClip clip)
        {
            var index = Clips.IndexOf(clip);
            if (index >= 0 && index < Clips.Count - 1)
            {
                Clips.Move(index, index + 1);
                UpdateClipOrders();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 检查片段是否在顶部
        /// </summary>
        public bool IsAtTop(VideoClip clip)
        {
            return Clips.IndexOf(clip) == 0;
        }

        /// <summary>
        /// 检查片段是否在底部
        /// </summary>
        public bool IsAtBottom(VideoClip clip)
        {
            var index = Clips.IndexOf(clip);
            return index >= 0 && index == Clips.Count - 1;
        }

        /// <summary>
        /// 移动片段到顶部
        /// </summary>
        public bool MoveClipToTop(VideoClip clip)
        {
            var index = Clips.IndexOf(clip);
            if (index > 0)
            {
                Clips.RemoveAt(index);
                Clips.Insert(0, clip);
                UpdateClipOrders();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 移动片段到底部
        /// </summary>
        public bool MoveClipToBottom(VideoClip clip)
        {
            var index = Clips.IndexOf(clip);
            if (index >= 0 && index < Clips.Count - 1)
            {
                Clips.RemoveAt(index);
                Clips.Add(clip);
                UpdateClipOrders();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取选中的片段
        /// </summary>
        public VideoClip[] GetSelectedClips()
        {
            return Clips.Where(c => c.IsSelected).ToArray();
        }

        /// <summary>
        /// 选择所有片段
        /// </summary>
        public void SelectAllClips()
        {
            foreach (var clip in Clips)
            {
                clip.IsSelected = true;
            }
        }

        /// <summary>
        /// 取消选择所有片段
        /// </summary>
        public void DeselectAllClips()
        {
            foreach (var clip in Clips)
            {
                clip.IsSelected = false;
            }
        }

        /// <summary>
        /// 更新片段顺序号
        /// </summary>
        private void UpdateClipOrders()
        {
            for (int i = 0; i < Clips.Count; i++)
            {
                var clip = Clips[i];
                var oldOrder = clip.Order;
                var newOrder = i + 1;
                
                // 先更新Order
                clip.Order = newOrder;

                // 如果序号改变，需要更新名称
                if (oldOrder != newOrder)
                {
                    // 检查是否有自定义标题（通过CustomTitle判断）
                    // 如果CustomTitle为空，说明使用的是默认名称，需要更新
                    // 如果CustomTitle不为空，说明用户设置了自定义名称，保持不变
                    var hasCustomTitle = !string.IsNullOrWhiteSpace(clip.CustomTitle);
                    
                    if (!hasCustomTitle)
                    {
                        // 使用默认名称，更新为新的默认名称
                        // 直接设置Name，这会触发PropertyChanged，从而更新EditableTitle
                        clip.Name = $"片段{newOrder}";
                    }
                    // 如果有自定义标题，保持不变（Name已经通过Clips.Move()移动了）
                    // Order的setter已经会触发EditableTitle的PropertyChanged，所以不需要手动触发
                }
            }
            
            // 更新所有片段的位置状态（IsFirst/IsLast）
            UpdateClipPositionStates();
        }

        /// <summary>
        /// 更新所有片段的位置状态（用于按钮禁用）
        /// </summary>
        private void UpdateClipPositionStates()
        {
            for (int i = 0; i < Clips.Count; i++)
                {
                Clips[i].IsFirst = (i == 0);
                Clips[i].IsLast = (i == Clips.Count - 1);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
