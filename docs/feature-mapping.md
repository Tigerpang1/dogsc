# DogSC 功能迁移映射

固定基线 `e207d11b111c5e58152238fb64115b2009850547`。此表关联原实现、Windows模块、复用语义、依赖和验收方法；逐项验收状态以 [parity-checklist.md](parity-checklist.md) 为准，性能与硬件边界以 [capture-validation.md](capture-validation.md)、[media-effects-validation.md](media-effects-validation.md) 为准。

当前记录对应0.2.4干净提交 `1a13291115536787f01ceb65ee99efcd64b4d166`。便携包、安装/卸载、包内真实录制暂停编辑重开导出及源码归档闭环通过，完整UIA为43/43、浮层为19/19；另有导出面板15项、应用编辑回归53项和录制栏生命周期5项通过。证据与范围见 [validation-report.md](validation-report.md) 和 [capture-validation.md](capture-validation.md)。100%/200% DPI浮层专项各20/20，但200%测试揭示产品主窗口初始边界超出工作区，修复与重测尚未完成。表中旧候选数据仍按其版本解释。此表逐项记录功能状态，不据此宣称整体完成比例。

| 原实现 | Windows实现 | 复用内容 | 依赖 | 阶段/状态 | 验收方法 |
|---|---|---|---|---|---|
| ScreenRecorder、CaptureSources、CaptureAreaSelection | FrameTrail.Capture、MainWindow.Recorder、RegionSelector | 来源、物理像素范围、状态和错误收尾 | DXGI Desktop Duplication、FFmpeg、ScreenRecorderLib | 0.2.4便携包完成真实录制、暂停、编辑、重开与导出。包内窗口目标探针覆盖移动、尺寸变化、最小化和关闭收尾；本机测试显示器与物理窗口路径。200% DPI初始主窗口越界问题正在修复，尚未重测。 | `package-0.2.4/result.json`、`window-wgc-package-024-v4/target.target.json`；多显示器和其他设备组合未测 |
| MicrophoneRecorder、CoreAudioSystemAudioTap | CaptureTrackService | 独立音轨及单调时钟/暂停映射 | NAudio WASAPI | 0.2.4包内麦克风探针完成15秒含暂停采集，WAV为48kHz/32-bit float、624400帧、有效时钟13.008秒、两段映射；数值信号可观测，但未分类语音质量。系统回环30分钟v3分析匹配1070/1080名义cue，原始P95 74.445ms，工程映射导出P95 38.333ms；该录制来自v6探针快照且无时间戳错误包，缺少后来新增的错误间隙门禁。 | `microphone-package-024-v1/microphone-capture.summary.json`、`capture-aligned-audio-clock-1800s-v1/offline-analysis-source-rate-v3/validation-summary.json`；物理时钟复位和多设备组合仍未测 |
| CameraRecorder、CameraCaptureFormatSelector | CaptureTrackService、CameraTrack | 设备格式、独立相机素材 | Media Foundation、SRLib | 0.2：接口与导入渲染已实现，物理设备待测 | 真实设备录制/热插拔；导入素材布局与音频实测 |
| PointerEventRecorder、RecordingMarkers | InputSampler、MainWindow.RecordingTracks | 归一化轨迹、按键修饰、时钟、标记 | Win32、Stopwatch | 0.2：已实现 | 实录JSONL、按首帧/暂停映射、项目重载 |
| RecorderMemo、CaptureWindowPresentationPolicy | MemoWindow、NativeWindows | 本地提纲、辅助窗口排除 | WPF、WDA_EXCLUDEFROMCAPTURE | 0.2：已实现，DDA/gdigrab本机排除验证通过 | 实录抽帧、字号/透明度/尺寸/位置保存重开 |
| ProjectStore、ProjectRecoveryJournal | Persistence、MainWindow.Recovery | 临时文件原子替换、备份、相对素材路径 | System.Text.Json、文件系统 | 0.2：已实现 | 异常项目/备份恢复、越界拒绝、媒体重定位 |
| ProjectStore、ProjectRecoveryJournal | Persistence、MainWindow.Recovery | 临时文件原子替换、备份、相对素材路径 | System.Text.Json、文件系统 | 0.2.4包保存/恢复回归通过；未打包0.2.5开发快照的 `persistence-fixed-v1` 以真实文件锁验证自动保存、撤销、重做失败状态一致与解除占用后重试，12项通过；`persistence-negative-v2` 留存修复前撤销不一致失败记录。 | `package-0.2.4/result.json`、`artifacts/validation/persistence-fixed-v1/result.json`、`artifacts/validation/persistence-negative-v2/result.json`；新修复未进入0.2.4包 |
| ProjectTimeline、ProjectTimelineEditing/Retime | Core.Models、MainWindow.Timeline、EffectTimelineOperations | 非破坏编辑、源时间与输出时间映射；Zoom、屏幕/摄像头运动、贴图和模糊五类效果可独立拆分 | Core、FFmpeg | 0.2.4桌面UIA 43/43及应用编辑回归53项通过；未打包0.2.5开发快照另通过范围导出媒体回归，覆盖多片段变速/重排、两条独立音轨时钟段和静音段。 | `ui-full-package-024-v1.json`、`package-024-extra-v1/editor.json`、`artifacts/validation/range-audio-export-v1/result.json`、`integration-after-range-v1/result.json`；新媒体回归不是0.2.4包UI验收，Mac原工程兼容另见导入行 |
| EditorTimelineFilmstrip/WaveformCache/MagneticSnap | TimelineAssets、EditorTimelineControl | 异步缓存、视口、吸附和多轨编辑 | FFmpeg、WPF | 0.2.4桌面UIA覆盖主界面和时间线；时间线资产20项回归、离屏绘制检查通过。100%/200% DPI浮层专项均20/20；200%初始窗口定位缺口待修复重测。 | `ui-transient-package-024-dpi100-v1.json`、`ui-transient-package-024-dpi200-v1.json`、`timeline-assets-20261009-190531-715/result.json` |
| EditorEffectTransfer | MainWindow.Timeline | 完整效果复制和保时长/位置属性粘贴；摄像头完整粘贴替换唯一轨道，属性粘贴保留目标媒体/时钟映射 | JSON快照、项目资产复制 | 0.2.4应用编辑回归53项与桌面UIA 43/43覆盖效果编辑、摄像头替换、撤销和保存重开。 | `package-024-extra-v1/editor.json`、`ui-full-package-024-v1.json` |
| AutoZoomPlanner、ZoomAnimation | Core.Evaluation | 点击分组、目标构图、缓动、相邻片段及进入/退出进度锚点 | 独立Core | 当前包的桌面UIA及应用内部回归已覆盖快捷键、编辑和撤销路径；自动数值样例由Core/Render探针覆盖。 | `ui-full-package-024-v1.json`、`package-024-extra-v1/editor.json`；硬件光标主题仍为Windows资源子集 |
| PointerTrack、PointerTimelineAlignment | PreparedPointerTrack、MouseClockCalibration | 指针采样、独立偏移、不移动主媒体 | Core | 0.2：已实现 | 指针偏移/平滑不耦合、裁切/变速重排映射 |
| CursorStyle、SharedFrameCursorRenderer | CursorSettings、Scene.hlsl | 独立光标、点击和隐藏策略 | D3D11 | 0.2：核心行为已实现，主题为Windows子集 | 合成轨迹和点击像素、静止/始终隐藏 |
| FrameScene、CompositionScene | Core.FrameScene、PreparedFrameSceneEvaluator | UI外的统一场景描述 | Core | 0.2：已实现 | 预览/导出共享场景、逐帧Probe |
| SharedFrameRenderer、ScreenProjection | GpuFrameRenderer、Scene.hlsl | 全场景GPU合成、XYZ透视与图层 | Vortice D3D11、HLSL | 0.2：已实现 | 投影、遮罩、半透明/圆角、像素断言及成片 |
| ScreenMotion、CameraMotion、FocusEffect | Core.Effects、EffectsInspector | 屏幕/相机运动和径向/线性聚焦；摄像头布局/透明度连续过渡、缩放联动和阴影强度 | Core、D3D11 | 0.2.4 Core/Render和效果拆分回归通过，应用编辑回归53项、桌面UIA 43/43通过；物理摄像头待设备验证。 | 相机姿态、曲线、缩放联动、阴影、聚焦随鼠标和实际MP4由合成工程测试；不代表真实摄像头设备已测 |
| CanvasStyle、NormalizedCrop、ScreenFrameStyle | CanvasSettings、Inspector、GPU renderer | 比例、裁切、位置、圆角/描边/阴影/样机 | WPF、D3D11、授权字体 | 0.2：已实现 | 数值边界、拖动、透视标题与多分辨率 |
| BackgroundSource/Pattern/Dynamic | BackgroundSettings、SceneAssetLoader | 纯色/渐变/网格/动态/图片/视频背景 | FFmpeg持续解码、D3D11 | 0.2：已实现，系统壁纸不内置 | 视频循环/跳转、预览导出、资源复制 |
| StickerTransition、Mosaic | OverlayClip、BlurClip、GPU | 贴图层级、进出场、柔化/马赛克；背景柔化可选择包含摄像头，并可在贴图时段隐藏屏幕/摄像头 | Core、D3D11 | 0.2.4 UIA、应用编辑和实际导出检查包含贴图/背景效果与素材重载；GPU backdrop像素专项通过。 | `ui-full-package-024-v1.json`、`package-024-extra-v1/editor.json`；其他显卡/驱动仍需兼容性测试 |
| OpeningSequence | OpeningSettings、FrameOpeningState | 元素启用、顺序、错开、预设和曲线 | 共享场景求值与renderer | 0.2：已实现 | 三组开场时序、旧工程默认、逐帧输出 |
| EditorScenePresets/StylePresetStore | MainWindow.Menus | 内建/自建样式和安全资产复制 | 本地文件系统、WPF | 0.2：已实现，新建/改名/覆盖/跨项目UIA通过 | 保存、改名、覆盖、撤销、跨项目应用 |
| ProjectStore schema升级 | DogScProjectImporter | schema23–27、主要轨道与效果字段转换 | Core、只读原素材/复制资产 | 0.2.4源码与导入fixture回归通过；仍没有真实Mac工程做互操作验收。 | 原工程hash不变、字段断言、丢失/近似报告；构造fixture不能代替真实工程 |
| DirectExportPipeline、AudioOnlyExportPipeline | ExportEngine、ExportWindow | 视频与音频相同时间线、取消/进度/回退 | FFmpeg H.264/AAC、GPU | 0.2.4便携包真实录制工程导出和安装版播放通过；未打包0.2.5开发快照的 `range-audio-export-v1` 实测跨切口/变速/重排范围MP4与M4A，两条独立时钟轨含SourceRate和静音映射；标准Integration回归通过。`export-progress-025-v2` 真实WPF导出面板21项通过，覆盖进度单调至100%、取消保留旧文件并清理、同窗重试及成片完整解码。 | `package-0.2.4/result.json`、`artifacts/validation/range-audio-export-v1/result.json`、`integration-after-range-v1/result.json`、`artifacts/validation/export-progress-025-v2/result.json`、`driver-result.json`；新回归不改变0.2.4包验收状态，剩余时间估算及其它环境仍待验 |
| EditorPlaybackController | PreviewPlaybackSession、MainWindow.Playback | 连续源时间采样、共享实际renderer | FFmpeg持久流、D3D11、MediaElement声音 | 0.2：已实现 | 播放/暂停/重开、逐帧、seek取消及子进程清理 |
| AppPreferences、PermissionTour | MainWindow.Menus | 本地偏好、主题、预览质量、权限说明 | Windows设置、JSON | 0.2：已实现 | UIA持久化、首次引导、拒绝权限说明 |
| AppAbout、Sparkle更新、Support | 本地关于/源码/许可/诊断、手动更新说明 | 独立品牌、版本信息 | WPF | 0.2：手动入口；自动签名更新未实现；上游捐赠不适用 | 安装包版本、正确目录、无上游收款信息 |

`.frametrail` 是明确标识的独立格式。导入 `.dogscproject` 不意味着可以将编辑后的工程无损回写给Mac；不能把构造fixture当作真实Mac互通认证。

静态 UI 截图共23张。空状态 `artifacts/validation/visual-polish-final4/` 包含 `audio.png`、`background.png`、`camera.png`、`cursor.png`、`editor.png`、`export.png`、`mockup.png`、`motion.png`、`opening.png`、`recorder.png`、`zoom.png`；填充状态 `artifacts/validation/visual-polish-populated-final4/` 包含以上对应页面及 `overlay.png`。截图仅覆盖相应空/填充状态，不证明视觉完全一致。QPC v8长测的视频封装通过，但音画同步因有效cue覆盖不足而失败，不作为同步通过证据。旧 `capture-qpc-interactive-v2` 20秒样本音频全程为静音；另一次5秒会话观察到ffplay自身静音且音量为0，但并非同一段采集的完整会话记录，不能确认原因或解释其他版本。v8也没有记录应用音量，不能据此追溯解释长测后段静音。
