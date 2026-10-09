# DogSC 功能迁移映射

固定基线 `e207d11b111c5e58152238fb64115b2009850547`。此表关联原实现、Windows模块、复用语义、依赖和验收方法；逐项验收状态以 [parity-checklist.md](parity-checklist.md) 为准，性能与硬件边界以 [capture-validation.md](capture-validation.md)、[media-effects-validation.md](media-effects-validation.md) 为准。

当前迭代为0.2.0。已实现表示可执行代码路径存在；设备待测、兼容近似和未实现功能分别记录，不用按钮数量计算迁移比例。

| 原实现 | Windows实现 | 复用内容 | 依赖 | 阶段/状态 | 验收方法 |
|---|---|---|---|---|---|
| ScreenRecorder、CaptureSources、CaptureAreaSelection | FrameTrail.Capture、MainWindow.Recorder、RegionSelector | 来源、物理像素范围、状态和错误收尾 | DXGI Desktop Duplication、FFmpeg、ScreenRecorderLib | 0.2：显示器/窗口/区域已实现，本机30分钟长测通过 | 实际全屏/区域/窗口移动、关闭、暂停、30分钟动态与帧率 |
| MicrophoneRecorder、CoreAudioSystemAudioTap | CaptureTrackService | 独立音轨及单调时钟/暂停映射 | NAudio WASAPI | 0.2：系统WAV30分钟通过，麦克风设备待测 | 静音长度、脉冲位置/漂移、独立混音 |
| CameraRecorder、CameraCaptureFormatSelector | CaptureTrackService、CameraTrack | 设备格式、独立相机素材 | Media Foundation、SRLib | 0.2：接口与导入渲染已实现，物理设备待测 | 真实设备录制/热插拔；导入素材布局与音频实测 |
| PointerEventRecorder、RecordingMarkers | InputSampler、MainWindow.RecordingTracks | 归一化轨迹、按键修饰、时钟、标记 | Win32、Stopwatch | 0.2：已实现 | 实录JSONL、按首帧/暂停映射、项目重载 |
| RecorderMemo、CaptureWindowPresentationPolicy | MemoWindow、NativeWindows | 本地提纲、辅助窗口排除 | WPF、WDA_EXCLUDEFROMCAPTURE | 0.2：已实现，DDA/gdigrab本机排除验证通过 | 实录抽帧、字号/透明度/尺寸/位置保存重开 |
| ProjectStore、ProjectRecoveryJournal | Persistence、MainWindow.Recovery | 临时文件原子替换、备份、相对素材路径 | System.Text.Json、文件系统 | 0.2：已实现 | 异常项目/备份恢复、越界拒绝、媒体重定位 |
| ProjectTimeline、ProjectTimelineEditing/Retime | Core.Models、MainWindow.Timeline | 非破坏编辑、源时间与输出时间映射 | Core、FFmpeg | 0.2：已实现 | split/trim/rate/reorder/undo，导出抽帧及音频频率 |
| EditorTimelineFilmstrip/WaveformCache/MagneticSnap | TimelineAssets、EditorTimelineControl | 异步缓存、视口、吸附和多轨编辑 | FFmpeg、WPF | 0.2：已实现 | UIA拖动/多选、波形/缩略图、范围MP4探测 |
| EditorEffectTransfer | MainWindow.Timeline | 完整效果复制和保时长/位置属性粘贴 | JSON快照、项目资产复制 | 0.2：已实现 | 相同/不同效果类型、跨项目贴图、撤销与重载 |
| AutoZoomPlanner、ZoomAnimation | Core.Evaluation | 点击分组、目标构图、缓动及相邻片段 | 独立Core | 0.2：已实现，算法差异有记录 | 原语义对照用例、边界连续性、暂停目标预览 |
| PointerTrack、PointerTimelineAlignment | PreparedPointerTrack、MouseClockCalibration | 指针采样、独立偏移、不移动主媒体 | Core | 0.2：已实现 | 指针偏移/平滑不耦合、裁切/变速重排映射 |
| CursorStyle、SharedFrameCursorRenderer | CursorSettings、Scene.hlsl | 独立光标、点击和隐藏策略 | D3D11 | 0.2：核心行为已实现，主题为Windows子集 | 合成轨迹和点击像素、静止/始终隐藏 |
| FrameScene、CompositionScene | Core.FrameScene、PreparedFrameSceneEvaluator | UI外的统一场景描述 | Core | 0.2：已实现 | 预览/导出共享场景、逐帧Probe |
| SharedFrameRenderer、ScreenProjection | GpuFrameRenderer、Scene.hlsl | 全场景GPU合成、XYZ透视与图层 | Vortice D3D11、HLSL | 0.2：已实现 | 投影、遮罩、半透明/圆角、像素断言及成片 |
| ScreenMotion、CameraMotion、FocusEffect | Core.Effects、EffectsInspector | 屏幕/相机运动和径向/线性聚焦 | Core、D3D11 | 0.2：已实现 | 目标姿态、曲线、聚焦随鼠标、实际MP4 |
| CanvasStyle、NormalizedCrop、ScreenFrameStyle | CanvasSettings、Inspector、GPU renderer | 比例、裁切、位置、圆角/描边/阴影/样机 | WPF、D3D11、授权字体 | 0.2：已实现 | 数值边界、拖动、透视标题与多分辨率 |
| BackgroundSource/Pattern/Dynamic | BackgroundSettings、SceneAssetLoader | 纯色/渐变/网格/动态/图片/视频背景 | FFmpeg持续解码、D3D11 | 0.2：已实现，系统壁纸不内置 | 视频循环/跳转、预览导出、资源复制 |
| StickerTransition、Mosaic | OverlayClip、BlurClip、GPU | 贴图层级、进出场、柔化/马赛克 | Core、D3D11 | 0.2：已实现 | 真实图像素材、源时段映射、8层/4区域边界 |
| OpeningSequence | OpeningSettings、FrameOpeningState | 元素启用、顺序、错开、预设和曲线 | 共享场景求值与renderer | 0.2：已实现 | 三组开场时序、旧工程默认、逐帧输出 |
| EditorScenePresets/StylePresetStore | MainWindow.Menus | 内建/自建样式和安全资产复制 | 本地文件系统、WPF | 0.2：已实现，新建/改名/覆盖/跨项目UIA通过 | 保存、改名、覆盖、撤销、跨项目应用 |
| ProjectStore schema升级 | DogScProjectImporter | schema23–27、主要轨道与效果字段转换 | Core、只读原素材/复制资产 | 0.2：fixture通过，真实Mac样本待验证 | 原工程hash不变、字段断言、丢失/近似报告 |
| DirectExportPipeline、AudioOnlyExportPipeline | ExportEngine、ExportWindow | 视频与音频相同时间线、取消/进度/回退 | FFmpeg H.264/AAC、GPU | 0.2：已实现 | MP4/M4A；1080/1440/4K×30/60真实编码 |
| EditorPlaybackController | PreviewPlaybackSession、MainWindow.Playback | 连续源时间采样、共享实际renderer | FFmpeg持久流、D3D11、MediaElement声音 | 0.2：已实现 | 播放/暂停/重开、逐帧、seek取消及子进程清理 |
| AppPreferences、PermissionTour | MainWindow.Menus | 本地偏好、主题、预览质量、权限说明 | Windows设置、JSON | 0.2：已实现 | UIA持久化、首次引导、拒绝权限说明 |
| AppAbout、Sparkle更新、Support | 本地关于/源码/许可/诊断、手动更新说明 | 独立品牌、版本信息 | WPF | 0.2：手动入口；自动签名更新未实现；上游捐赠不适用 | 安装包版本、正确目录、无上游收款信息 |

`.frametrail` 是明确标识的独立格式。导入 `.dogscproject` 不意味着可以将编辑后的工程无损回写给Mac；不能把构造fixture当作真实Mac互通认证。
