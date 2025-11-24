# 视频编辑器 (VideoEditor)

面向长视频处理与批量工作流的 Windows 端 WPF 应用，围绕 **播放控制、裁剪/合并、AI 字幕、批量转码** 等高频需求进行深度定制。项目使用 `.NET 9 + WPF + LibVLCSharp`，集成 FFmpeg 与 MediaInfo 工具，可在离线环境下完成大部分视频处理任务。

---

## 功能亮点

- **专业播放区**
  - LibVLC 播放内核，支持 4K/多格式视频
  - 播放列表/播放区域双拖放入口，播放时拖入只入列，避免崩溃
  - 主题联动：暗/亮主题下的播放背景、裁剪框、遮罩统一调色

- **可视化裁剪工具**
  - Popup 叠加裁剪框，九宫格辅助线、八个拖拽点、比例锁定
  - 裁剪区域遮罩透明度可控，保证观感与准确度
  - 裁剪参数与 FFmpeg 命令生成器、批量裁剪任务打通

- **AI 字幕与批量流程**
  - 支持 FasterWhisper、本地/远端 ASR（参考 `Services/AiSubtitle`）
  - 批量转码、滤镜、合并、Watermark 等操作统一封装
  - DebugLogger + Toast 提示，问题可追溯

- **启动与体验优化**
  - 关键 I/O 延迟初始化，播放区黑屏/白闪问题已消除
  - 主题系统 (`Resources/Themes/*.xaml`) 提供完整颜色资源

---

## 环境需求

| 组件 | 版本/说明 |
| --- | --- |
| 操作系统 | Windows 10/11 x64 |
| .NET SDK | .NET 9.0 (用于开发/构建) |
| IDE | Visual Studio 2022 / JetBrains Rider / VS Code + C# 扩展 |
| 依赖 | LibVLCSharp、VideoLAN.LibVLC.Windows、FFmpeg（已随项目提供） |

> 发布版本可使用 `--self-contained true`，无需目标机器额外安装 .NET Runtime。

---

## 目录结构

```
VideoEditor/
├─ README.md
├─ VideoEditor.sln
├─ docs/                      # 设计/需求文档 (可选)
├─ tools/
│  ├─ ffmpeg/                 # 内置 ffmpeg.exe / ffprobe.exe
│  └─ MediaInfo/              # MediaInfo CLI 及依赖
└─ src/
   └─ VideoEditor.Presentation/
      ├─ App.xaml(.cs)        # 启动入口
      ├─ MainWindow.*         # 主界面 & 交互逻辑
      ├─ ViewModels/          # MVVM 层 (播放/列表等)
      ├─ Services/            # FFmpeg/AI/日志/命令等服务
      ├─ Views/               # 辅助窗口、弹窗
      ├─ Resources/
      │  ├─ Themes/           # Dark/Light 主题
      │  └─ FasterWhisper...  # AI 相关模型/资源
      └─ VideoEditor.Presentation.csproj
```

---

## 开发现状 & 构建

1. **恢复依赖**
   ```powershell
   dotnet restore VideoEditor.sln
   ```

2. **调试运行**
   ```powershell
   dotnet build src/VideoEditor.Presentation/VideoEditor.Presentation.csproj
   dotnet run --project src/VideoEditor.Presentation/VideoEditor.Presentation.csproj
   ```

3. **生成发布包**
   ```powershell
   dotnet publish src/VideoEditor.Presentation/VideoEditor.Presentation.csproj `
     -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```
   - 输出位于 `src/VideoEditor.Presentation/bin/Release/net9.0-windows/win-x64/publish`
   - 目录已包含 .NET 运行时、LibVLC、FFmpeg、MediaInfo 等，可直接打包 Zip/安装包

---

## 运行配置

- **工具路径**：`VideoEditor.Presentation.csproj` 中将 `tools/ffmpeg` 与 `tools/MediaInfo` 链接并复制到输出目录，无需手工配置。
- **日志**：`Services/DebugLogger` 默认写入 `debug.log`（若需调整路径/保留策略，可在发布前修改）。
- **主题切换**：`ThemeManager` 负责加载 `ThemeDark.xaml` / `ThemeLight.xaml`，所有 UI 元素应引用 `DynamicResource` 以获得一致体验。
- **裁剪框状态**：`MainWindow.xaml.cs` 中的 Popup 裁剪框（`ShowPopupCropSelector`）已支持窗口激活/失活状态保存。

---

## 测试建议

- **播放/拖放**
  - 列表拖入 vs 播放区拖入（正在播放与未播放两种情况）
  - 文件、文件夹混合拖放

- **裁剪与批处理**
  - 显示/隐藏裁剪框、拖拽、预设、遮罩透明度
  - 批量裁剪/转码/AI 字幕流程

- **主题与 DPI**
  - 亮暗主题切换
  - 125%/150% 缩放下 Popup 对齐与文本清晰度

---

## 已知警告/后续优化

- 发布构建存在部分 **nullable 及 CA2022** 警告（见 `dotnet publish` 输出），不会阻挡发布，建议列入后续技术债清理计划。
- `System.Windows.Forms 4.0.0` 以 .NET Framework 目标还原（NU1701），来自 WPF 对 WinForms 的依赖，属预期。

---

## 许可证 & 第三方依赖

- **LibVLC / LibVLCSharp**：遵循 LGPL，详见 `VideoLAN.LibVLC.Windows` 包许可证
- **FFmpeg**：GPL/LGPL（根据编译选项），请遵循 FFmpeg LICENSE
- **MediaInfo CLI**：许可证随 `tools/MediaInfo` 目录提供
- 其他 .NET 包遵循各自 NuGet 许可


