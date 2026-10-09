# DogSC 功能迁移映射

本表按当前 Windows 代码和验证证据描述迁移进度。FrameTrail 是独立 Windows 产品及项目格式；`.frametrail` JSON 不是 `.dogscproject` 的兼容实现，也不承诺读取、编辑后保留或往返保存原项目。原仓库 `upstream/dogsc` 的固定审计基线为 `e207d11b111c5e58152238fb64115b2009850547`。

状态定义：**已实现且自动实测**表示代码与可重复的自动化/Probe 验证均已具备；**已实现，设备/人工待验证**表示主要代码路径已存在，但尚缺真实设备或外部行为验证；**部分实现**表示只完成了列出的子集；**未实现**表示目前没有可用功能实现。自动合成素材测试不等同于真实设备、真人操作或长时间压力验收。

## 采集与录制工作台

| 原实现位置 | Windows 实现方案/文件 | 可复用内容 | 依赖 | 阶段 | 验收方法 | 当前状态 |
|---|---|---|---|---|---|---|
| `PermissionTourFlow.swift`、`FirstUseTour.swift` | 尚无首用引导和权限页；录屏能力由 `src/FrameTrail.Capture/FrameCaptureService.cs` 提供 | 引导文案和权限顺序可参考，macOS 权限 API 不复用 | WPF；Windows 隐私设置 | 阶段 2 | 新用户首次启动、拒绝/恢复录屏权限时验证提示和系统设置跳转 | 未实现 |
| `ScreenRecorder.swift`、`CaptureSources.swift` | 显示器、窗口、单显示器区域由 `FrameCaptureService` 接入 ScreenRecorderLib；选择器在 `MainWindow.xaml.cs`、`AuxiliaryWindows.cs` | 录制来源、像素矩形与停止结果语义 | ScreenRecorderLib 7.0.1、DXGI/WGC、Media Foundation | 阶段 1 闭环 | Probe 已自动枚举并完成短显示器录制；窗口和区域需用真实目标录制并检查裁切、DPI、跨屏 | 部分实现：显示器短实录自动通过；窗口、区域已接入 UI 与采集接口，尚未实录 |
| `RecorderViews.swift`、`ScreenRecorder.swift` | 录制控制、来源和标记在 `src/FrameTrail.App/MainWindow.xaml(.cs)`；浮动录制条在 `AuxiliaryWindows.cs` | 开始/结束与录制状态语义、标记流程 | WPF、FrameTrail.Capture | 阶段 1 闭环 | `--workflow-smoke` 已自动完成录制、编辑、重载和导出；还需人工检查录制中窗口行为 | 已实现且自动实测：短桌面闭环通过；暂停、录制中编辑等交互仍有限 |
| README 录制辅助窗口排除说明；`RecorderViews.swift` | `AuxiliaryWindows.cs` 调用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 排除录制条、选区和备忘录 | 只复用“辅助窗口不进入采集画面”的行为目标 | Win32 `SetWindowDisplayAffinity`、WPF | 阶段 2 | 使用实际采集后端及另一种常见录屏工具，分别检查辅助窗口是否被排除 | 已实现，设备/人工待验证：Win32 调用已接入，外部采集行为尚未验证 |
| `CameraRecorder.swift`、`CameraLetterboxAnalysis.swift`、`EditorTimelineCameraSync.swift` | 当前 Capture、项目模型和渲染器都没有摄像头轨道；预计增加设备采集、代理和 `FrameScene` 图层 | 摄像头素材、布局和同步规则可作为需求参考 | 摄像头设备 API、Media Foundation、GPU 合成 | 阶段 2 | 真实摄像头录制、热插拔、旋转、裁黑边、同步及导出对照 | 未实现 |
| `MicrophoneRecorder.swift`、`CoreAudioSystemAudioTap.swift` | `FrameCaptureService` 接 WASAPI 系统回环和麦克风来源；当前音频混入录制主视频 | 音频设备选择与主混音需求；CoreAudio 实现不可直接复用 | ScreenRecorderLib、WASAPI、Media Foundation | 阶段 1/2 | 已用本机生成的 440Hz 音调自动录入系统回环并验证成片音轨结构；真实声音与漂移未测，麦克风未实录；独立音轨未实现 | 部分实现：系统回环短测通过；麦克风设备可枚举但未实录，输入没有独立轨道 |
| `ScreenRecorder.swift`、`RecordingRun.swift`、`RecordingRecoveryJournal.swift` | Capture API 支持开始/停止/失败状态；UI 记录 `recording-recovery.json` 并可从封装完整的视频重建项目 | 状态生命周期和恢复意图 | ScreenRecorderLib、文件系统、FFmpeg probe | 阶段 2 | 取消、设备中断、崩溃和未完成 MP4 的恢复矩阵；确认鼠标时钟与素材长度 | 部分实现：停止、错误处理和可读视频重建已有代码；暂停/无缝继续与未完成封装修复缺失 |
| `PointerEventRecorder.swift`、`RecordingMarkers.swift` | `InputSampler.cs` 约 60Hz 记录归一化鼠标/按键 JSONL；`MainWindow.xaml.cs` 读取并以 Ctrl+Alt+M 添加标记 | 事件类型、归一化位置、Stopwatch tick、修饰键和标记语义 | Win32 输入采样、WPF 热键 | 阶段 1 闭环 | Core 测试覆盖负校准偏移；短闭环自动保存/重开项目；真实点击位置和输入覆盖率仍需设备检查 | 部分实现：采样与标记路径已落地；精确首帧时钟锚点未解决 |
| `PointerTrack.swift`、`ProjectTimeline.swift` | `MouseClockCalibration` 位于 `src/FrameTrail.Core/Models.cs`，独立于视频片段存储偏移和倍率；`MainWindow.xaml.cs` 提供鼠标毫秒偏移校准，并据此重算纯自动缩放轨道 | 鼠标事件时钟到源视频时钟的独立映射 | Core、WPF 项目持久化 | 阶段 1 闭环 | Core 自动测试覆盖负 offset 和时间映射；校准值随项目保存并重载；真实首帧精确校准仍需要闪屏/声音实测 | 已实现且自动实测：独立校准模型/UI 与负偏移测试通过；缺少真实采集时钟锚点 |

## 时间线、指针与画面

| 原实现位置 | Windows 实现方案/文件 | 可复用内容 | 依赖 | 阶段 | 验收方法 | 当前状态 |
|---|---|---|---|---|---|---|
| `PointerTrack.swift`、`CursorAssetLibrary.swift`、`CursorRenderGeometry.swift` | `src/FrameTrail.Core/Evaluation.cs` 预排序指针轨道并插值；`FrameTrail.Media/GpuFrameRenderer.cs` 绘制独立光标和点击环；UI 可切换光标显示 | 归一化指针、点击强度、时间采样等基础语义 | Core、Direct3D 11、HLSL | 阶段 1/2 | 自动测试覆盖插值、点击脉冲、裁切范围、不同分辨率的相对光标尺寸；光标皮肤和真人点击未测 | 部分实现：基础箭头与点击反馈已自动实测；皮肤、速度旋转、静止隐藏等未实现 |
| `AutoZoomPlanner.swift`、`ZoomAnimation.swift`、`EditorZoomViews.swift` | `FrameTrail.Core/Evaluation.cs` 的 `AutoZoomPlanner`、`PreparedFrameSceneEvaluator`；UI 自动生成、清除和添加手动焦点 | 点击分组、焦点与进入/保持/退出曲线的主要规则 | Core、鼠标轨道、GPU renderer | 阶段 1 闭环 | Core 测试及 6 秒合成点击项目重载/导出测试；真人点击效果需人工检查 | 部分实现：自动缩放测试通过，行为按算法移植记录为近似；无完整手动动画曲线编辑器 |
| `FrameScene.swift`、`CompositionScene.swift`、`SharedFrameRenderer.swift` | `FrameTrail.Core/Models.cs` 定义 `FrameScene`；`FrameTrail.Media/GpuFrameRenderer.cs` 与 `Scene.hlsl` 做 GPU 合成 | 统一场景输入给预览与导出的方式 | Vortice.Direct3D11 3.8.3、D3DCompiler | 阶段 1 闭环 | `tests/FrameTrail.Render.Probe` 检查背景、裁切/缩放、留白、圆角、阴影、光标和输出尺寸比例；工作流/合成测试核对预览和成片容差 | 已实现且自动实测：当前基础屏幕场景与 GPU Probe 通过；不包含完整原场景图层 |
| `CanvasCustomAspectRatio.swift`、`FrameScene.swift` | 画布宽高保存在 `FrameTrailSettings`；UI 可调背景色、留白、圆角、阴影和光标；渲染按画布比例保持源画面宽高比 | 背景、padding、radius、shadow、画布比例参数 | WPF、Core、Direct3D 11 | 阶段 1/2 | 保存重开项目，并对不同素材/输出宽高比检查不拉伸、裁切和填充 | 部分实现：color/padding/radius/shadow 已实现；自定义画布比例编辑、源画面 crop 编辑未来支持 |
| `ScreenProjection.swift`、`ScreenMotion.swift`、`CameraMotion.swift` | 当前 `FrameScene` 只含二维归一化裁切与缩放，无三维透视或摄像头运动层 | 可参考原有运动参数和交互意图 | Direct3D 11 shader、动画模型 | 阶段 2 | 对开场、倾斜、透视和动画关键帧做逐帧预览/导出对照 | 未实现 |
| `EditorTimelinePrimaryEditing.swift`、`ProjectTimelineEditing.swift`、`ProjectTimelineRetime.swift` | `FrameTrail.Core/Models.cs` 提供分割、裁短、重排、变速、输出/源时间映射；`Persistence.cs` 的 `UndoRedoHistory`；UI 在 `MainWindow.xaml(.cs)` | 非破坏片段、源时间与输出时间分离、恢复历史 | Core、WPF、FFmpeg concat/filter | 阶段 1 闭环 | Core 自动测试覆盖分割、裁短、重排、速度、接点/重复源区间和撤销恢复；工作流会保存重开并导出 | 部分实现：核心分割/trim/变速/重排/undo-redo 已通过；UI 仅有 1/1.5/2/4 倍速度，暂无多效果轨道、片段删除和波纹编辑 |
| `EditorTimelineFilmstrip.swift`、`EditorTimelineMergedWaveform.swift`、`EditorTimelineMagneticSnap.swift` | `src/FrameTrail.App/TimelineAssets.cs` 异步用 FFmpeg 生成缩略图和限量波形；Timeline UI 绘制缩略图/波形，播放头拖动可吸附片段边界 | 长素材异步处理、峰值采样和时间线素材缩略图 | FFmpeg 子进程、WPF BitmapSource | 阶段 2 基础 | [编辑器截图](../artifacts/validation/editor-with-thumbnails.png) 已确认缩略图和波形可见；播放头吸附按片段边界和控件宽度计算，需继续人工检查拖动手感及长素材响应 | 部分实现：缩略图/波形已截图检查通过；边界吸附已实现，人工验收有限；片段拖拽重排和时间线缩放未实现 |
| `RecordingMarker.swift`、`RecordingMarkers.swift` | 标记从录制条和 Ctrl+Alt+M 写入 `FrameTrailProject.Markers`，双击跳转、可删除 | 标记时间和标签随项目保存 | WPF RegisterHotKey、Core 持久化 | 阶段 1 闭环 | 桌面工作流自动添加标记并验证项目重载；检查裁剪/重复源片段后的跳转 | 部分实现：录入、保存、跳转、删除已实现；重复播放时源时间标记选择首次匹配位置 |
| `EditorScenePresets.swift`、`EditorStylePresetStore.swift` | 当前项目只存单套设置，无预设库 | 预设结构和默认参数可参考 | Core 项目存储、WPF UI | 阶段 2 | 保存/载入预设并验证素材复制和跨项目行为 | 未实现 |
| `StickerTransition.swift`、`FrameScene.swift`、`ProjectStore.swift` | 当前模型、GPU shader 和导出路径没有贴图或多图层效果轨 | 仅复用时间线与场景评估结构 | WPF 文件导入、GPU shader、Core 模型 | 阶段 2 | 多贴图导入、动画、层级、裁剪、重载和导出逐帧对照 | 未实现 |
| `FocusEffect.swift`、`ProjectTimeline.swift`、`FrameScene.swift` | 没有马赛克、模糊或可移动的效果片段 | 可复用片段时间映射及场景求值管线 | GPU shader、Core 效果轨道 | 阶段 2 | 多区域效果在预览、导出和重排后的时间边界一致 | 未实现 |
| `EditorScenePresets.swift`、`SharedFrameRenderer.swift` | 目前没有样机外框、标题/网址、授权字体或复杂装饰图层 | 可复用共享 renderer 和画布布局 | Direct3D 11、字体/素材授权 | 阶段 2 | 检查文字排版、多分辨率渲染及导出一致性 | 未实现 |

## 项目、导出与应用支撑

| 原实现位置 | Windows 实现方案/文件 | 可复用内容 | 依赖 | 阶段 | 验收方法 | 当前状态 |
|---|---|---|---|---|---|---|
| `ProjectStore.swift`、`ProjectDocument.swift` | `src/FrameTrail.Core/Persistence.cs` 保存 FrameTrail 自有 `FrameTrailProject` v1 JSON，使用相对素材路径、临时文件原子替换、`.bak` 与恢复；Core 还含 `RecordingMarker`、媒体和设置模型 | 项目字段语义、原子写入、备份恢复、路径约束；未复用原包格式 | System.Text.Json、文件系统 | 阶段 1 闭环 | 自动测试覆盖保存/重开、素材相对路径安全、备份恢复和未来版本拒绝；需要用损坏文件/跨盘搬移再检查用户流程 | 已实现且自动实测：FrameTrail v1 持久化与恢复通过；不支持直接读写原 `.dogscproject` |
| `DirectExportPipeline.swift`、`VideoExporter.swift` | `src/FrameTrail.Media/ExportEngine.cs` 解码、按时间线映射场景、D3D11 合成、FFmpeg H.264/AAC 封装；UI 支持源分辨率/1080p/1440p/4K 与 30/60fps | 时间线与场景模型，预览/导出使用同一 evaluator 和 renderer | FFmpeg、D3D11、Media Foundation/可用编码器 | 阶段 1 闭环 | 自动合成素材测试覆盖 360 帧 1080p60、同步点、容差和导出核验；真实高分辨率复杂素材需压力测 | 已实现且自动实测：合成素材与短工作流通过；不能外推到 4K60 或长片实时性能 |
| `AudioOnlyExportPipeline.swift` | `ExportEngine` 的 AudioOnly 分支输出 M4A/AAC；UI 提供“导出 M4A 音频” | 音频时间线裁切、重排和速度过滤 | FFmpeg AAC | 阶段 1 闭环 | Integration 自动验证音频输出与时间长度，另核对变速后音画同步 | 已实现且自动实测：M4A 输出及合成脉冲对齐通过 |
| `ExportProgressRelay.swift`、`ExportActivityWatchdog.swift`、`DirectExportPipeline.swift` | 导出阶段、进度、预计剩余和取消在 `ExportEngine.cs`、`MainWindow.xaml.cs`；取消终止 FFmpeg 并清理临时文件 | 阶段进度和取消语义 | .NET Process、FFmpeg | 阶段 1 闭环 | 自动在帧处理阶段取消，验证临时文件删除并成功再次导出；进度准确度需不同素材复核 | 已实现且自动实测：取消清理与后续再次导出通过 |
| `RecorderMemo.swift` | `AuxiliaryWindows.cs` 中 MemoWindow 保存 LocalAppData `FrameTrail/memo.txt`，窗口调用辅助窗口排除 | 本机备忘录内容和自动保存行为 | WPF、文件系统、WDA | 阶段 2 基础 | 人工检查编辑、关闭重开、录制时排除行为 | 已实现，设备/人工待验证：代码含自动保存，录制排除尚无外部实测 |
| `Resources/zh-Hans.lproj/Localizable.strings`、`Resources/en.lproj/Localizable.strings`、`RecorderAccessibility.swift` | 当前 WPF 界面主要为简体中文，少量系统控件依 Windows 语言；无完整资源字典或无障碍审计 | 中文产品词汇与原功能标签 | WPF 资源、Windows UI Automation | 阶段 2 | 中英切换、键盘导航、屏幕阅读器和高对比度检查 | 部分实现：简体中文界面已实现；英文本地化和完整可访问性未实现 |
| `AppAboutSettingsView.swift`、`AppUpdateController.swift`、README | 当前没有关于页、更新检查器或用户反馈入口 | 产品说明和更新需求可参考 | WPF、签名更新服务 | 阶段 2 | 检查签名、失败回滚和支持入口 | 未实现 |
| `AppSupportSheet.swift`、`Resources/Support/AppreciationCode.jpg` | Windows 端没有赞赏码 UI 或该图片资源 | 只复用自愿、不构成使用门槛的产品原则 | 经审核的素材授权与支付适用性 | 阶段 2 | 确认资源权利和展示行为 | 未实现 |

## 阶段性验收限制

阶段 1 的首个录制、编辑、运镜和导出闭环已在本机自动跑通；这不表示阶段 2 完成。启用低延迟的 300 秒普通 MP4 加 loopback 样片中，Private Bytes 前段增长后约在 1.28 GiB 平台；30 分钟内存稳定性仍未验证。真实采集首帧时钟锚点、长期音画漂移、干净 Windows 环境安装和设备验收也未完成。短录制、Probe 或合成素材结果不能替代 30 分钟长录制稳定性证明。

具体环境、命令和证据见 [`validation-report.md`](validation-report.md)、[`capture-validation.md`](capture-validation.md)；当前缺口见 [`known-issues.md`](known-issues.md)。自动缩放与指针算法的近似和未移植部分见 [`algorithm-port.md`](algorithm-port.md)。发布用 FFmpeg 构建及许可证材料仍待审计，不要把本地验证用 Gyan GPL full build 当作可公开发布的依赖。
