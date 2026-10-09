# DogSC 功能迁移映射

固定基线 `e207d11b111c5e58152238fb64115b2009850547`。此表关联原实现、Windows模块、复用语义、依赖和验收方法；逐项验收状态以 [parity-checklist.md](parity-checklist.md) 为准，性能与硬件边界以 [capture-validation.md](capture-validation.md)、[media-effects-validation.md](media-effects-validation.md) 为准。

当前记录为0.2.3候选，发行包运行验收尚未完成。统一后台回归 `artifacts/validation/net10-023-background-regression-v2.log` exit0，覆盖Core、导入、GPU/Render、时间线资产20项、逐片段音量、Integration、离屏视觉与Capture纯逻辑；不含完整桌面UIA或设备访问。v6浮层实机截图专项19/19通过；时间线新高对比有15项离屏像素验收，PNG证据已保存，但没有新版时间线的实机主窗口截图。0.2.2及更早测试仅属于对应候选。已实现表示可执行代码路径存在；设备待测、兼容近似和未实现功能分别记录，不用按钮数量计算迁移比例。

| 原实现 | Windows实现 | 复用内容 | 依赖 | 阶段/状态 | 验收方法 |
|---|---|---|---|---|---|
| ScreenRecorder、CaptureSources、CaptureAreaSelection | FrameTrail.Capture、MainWindow.Recorder、RegionSelector | 来源、物理像素范围、状态和错误收尾 | DXGI Desktop Duplication、FFmpeg、ScreenRecorderLib | 0.2.3候选的显示器/区域使用DDA；窗口使用ScreenRecorderLib `WindowRecordingSource` / Windows Graphics Capture，并以source preview observation记录首图时钟。100ms监视检查HWND和边界状态，不按DWM矩形重裁桌面。特定窗口探针有黑帧观测，条件混杂，当前候选未完成同条件复测。30秒QPC/设备帧位置映射实录18/18 cue；设备时钟/位置复位代码与纯逻辑回归已修，物理设备复位未测 | 当前窗口捕获同条件raw WGC/SLR矩阵、QPC长测与设备及显示器组合仍待验 |
| MicrophoneRecorder、CoreAudioSystemAudioTap | CaptureTrackService | 独立音轨及单调时钟/暂停映射 | NAudio WASAPI | 0.2.3候选：按设备帧位置映射、以QPC锚定/估漂移的系统回环30秒真实短测检出18/18 cue；raw P95 54.736ms、fixture偏差校正估计21.885ms（非物理校准）。设备时钟/位置复位已修正代码路径并通过纯逻辑回归，物理复位未测。旧v8长测只配对485/1080 cue。麦克风只有CLI构建和guard检查，没有设备实录 | 当前映射长测、设备复位物理验证、完整诊断metadata随工程正向恢复、物理麦克风实录仍待验 |
| CameraRecorder、CameraCaptureFormatSelector | CaptureTrackService、CameraTrack | 设备格式、独立相机素材 | Media Foundation、SRLib | 0.2：接口与导入渲染已实现，物理设备待测 | 真实设备录制/热插拔；导入素材布局与音频实测 |
| PointerEventRecorder、RecordingMarkers | InputSampler、MainWindow.RecordingTracks | 归一化轨迹、按键修饰、时钟、标记 | Win32、Stopwatch | 0.2：已实现 | 实录JSONL、按首帧/暂停映射、项目重载 |
| RecorderMemo、CaptureWindowPresentationPolicy | MemoWindow、NativeWindows | 本地提纲、辅助窗口排除 | WPF、WDA_EXCLUDEFROMCAPTURE | 0.2：已实现，DDA/gdigrab本机排除验证通过 | 实录抽帧、字号/透明度/尺寸/位置保存重开 |
| ProjectStore、ProjectRecoveryJournal | Persistence、MainWindow.Recovery | 临时文件原子替换、备份、相对素材路径 | System.Text.Json、文件系统 | 0.2：已实现 | 异常项目/备份恢复、越界拒绝、媒体重定位 |
| ProjectTimeline、ProjectTimelineEditing/Retime | Core.Models、MainWindow.Timeline、EffectTimelineOperations | 非破坏编辑、源时间与输出时间映射；Zoom、屏幕/摄像头运动、贴图和模糊五类效果可独立拆分 | Core、FFmpeg | 0.2.3后台统一回归通过Core编辑、导入持久化、渲染和媒体Integration。v5全量桌面43/43和v6浮层19/19属于对应旧候选；0.2.3全量桌面UIA尚未运行 | split/trim/rate/reorder/undo、效果切口、序列化与导出抽帧见 `artifacts/validation/net10-023-background-regression-v2.log`；另需当前候选桌面验收 |
| EditorTimelineFilmstrip/WaveformCache/MagneticSnap | TimelineAssets、EditorTimelineControl | 异步缓存、视口、吸附和多轨编辑 | FFmpeg、WPF | 0.2.3后台 `TimelineAssets` 20项回归通过；v6实机浮层截图包含主界面/时间线，时间线高对比绘制另有15项离屏像素验收，PNG已保存但未截新版实机主窗口。缩略图/波形已有早期实机截图检查；边界拖动吸附已实现，人工验收仍有限；23张静态页面图属于旧候选 | 素材解码/取消及像素绘制有自动回归；新时间线高对比实机截图、吸附拖动更多人工场景待验 |
| EditorEffectTransfer | MainWindow.Timeline | 完整效果复制和保时长/位置属性粘贴；摄像头完整粘贴替换唯一轨道，属性粘贴保留目标媒体/时钟映射 | JSON快照、项目资产复制 | `editor-polish-final8.json` 应用内部回归53/53及`ui-shortcuts-polish-final10.json`（App final7）12/12均为历史候选证据；final10桌面UIA曾在7/32中止，余项无结论。0.2.3后台回归覆盖效果编辑与持久化，不含桌面UIA | 相同/不同效果类型、跨项目贴图、摄像头替换与撤销；当前候选桌面回归仍待执行 |
| AutoZoomPlanner、ZoomAnimation | Core.Evaluation | 点击分组、目标构图、缓动、相邻片段及进入/退出进度锚点 | 独立Core | Core和渲染回归、效果分割回归均有自动测试；快捷键专项final10 12/12及全量UIA final3 42/42属于历史候选。final10另一全量流程7/32后被守卫中止，不能据此判定其余行为。0.2.3后台回归通过Core/渲染，桌面UIA未运行 | 原语义对照用例、非零锚点、边界连续性、暂停目标预览；当前候选桌面验收待执行 |
| PointerTrack、PointerTimelineAlignment | PreparedPointerTrack、MouseClockCalibration | 指针采样、独立偏移、不移动主媒体 | Core | 0.2：已实现 | 指针偏移/平滑不耦合、裁切/变速重排映射 |
| CursorStyle、SharedFrameCursorRenderer | CursorSettings、Scene.hlsl | 独立光标、点击和隐藏策略 | D3D11 | 0.2：核心行为已实现，主题为Windows子集 | 合成轨迹和点击像素、静止/始终隐藏 |
| FrameScene、CompositionScene | Core.FrameScene、PreparedFrameSceneEvaluator | UI外的统一场景描述 | Core | 0.2：已实现 | 预览/导出共享场景、逐帧Probe |
| SharedFrameRenderer、ScreenProjection | GpuFrameRenderer、Scene.hlsl | 全场景GPU合成、XYZ透视与图层 | Vortice D3D11、HLSL | 0.2：已实现 | 投影、遮罩、半透明/圆角、像素断言及成片 |
| ScreenMotion、CameraMotion、FocusEffect | Core.Effects、EffectsInspector | 屏幕/相机运动和径向/线性聚焦；摄像头布局/透明度连续过渡、缩放联动和阴影强度 | Core、D3D11 | Core、渲染和效果分割测试通过后台回归；专项UIA与快捷键证据来自旧候选。final10全量UIA曾7/32后中止，未执行项无结论；0.2.3桌面UIA尚未运行，物理摄像头待设备验证 | 目标姿态、曲线、缩放联动、阴影、聚焦随鼠标、实际MP4；需当前候选桌面回归 |
| CanvasStyle、NormalizedCrop、ScreenFrameStyle | CanvasSettings、Inspector、GPU renderer | 比例、裁切、位置、圆角/描边/阴影/样机 | WPF、D3D11、授权字体 | 0.2：已实现 | 数值边界、拖动、透视标题与多分辨率 |
| BackgroundSource/Pattern/Dynamic | BackgroundSettings、SceneAssetLoader | 纯色/渐变/网格/动态/图片/视频背景 | FFmpeg持续解码、D3D11 | 0.2：已实现，系统壁纸不内置 | 视频循环/跳转、预览导出、资源复制 |
| StickerTransition、Mosaic | OverlayClip、BlurClip、GPU | 贴图层级、进出场、柔化/马赛克；背景柔化可选择包含摄像头，并可在贴图时段隐藏屏幕/摄像头 | Core、D3D11 | 0.2.1共享预览/导出媒体探针与 backdrop 像素探针已通过；0.2.2效果拆分Core回归通过；发行包专项待验 | 真实图像素材、源时段映射、图层交接、隐藏/柔化像素、8层/4区域边界 |
| OpeningSequence | OpeningSettings、FrameOpeningState | 元素启用、顺序、错开、预设和曲线 | 共享场景求值与renderer | 0.2：已实现 | 三组开场时序、旧工程默认、逐帧输出 |
| EditorScenePresets/StylePresetStore | MainWindow.Menus | 内建/自建样式和安全资产复制 | 本地文件系统、WPF | 0.2：已实现，新建/改名/覆盖/跨项目UIA通过 | 保存、改名、覆盖、撤销、跨项目应用 |
| ProjectStore schema升级 | DogScProjectImporter | schema23–27、主要轨道与效果字段转换 | Core、只读原素材/复制资产 | 0.2：fixture通过，真实Mac样本待验证 | 原工程hash不变、字段断言、丢失/近似报告 |
| DirectExportPipeline、AudioOnlyExportPipeline | ExportEngine、ExportWindow | 视频与音频相同时间线、取消/进度/回退 | FFmpeg H.264/AAC、GPU | 0.2.3后台真实合成Integration通过；v5导出面板15/15及final8内部12/12属于旧候选，不继承到当前0.2.3候选 | 当前候选发行包MP4/M4A运行、取消清理与完整桌面面板交互；后台日志 `artifacts/validation/net10-023-background-regression-v2.log` |
| EditorPlaybackController | PreviewPlaybackSession、MainWindow.Playback | 连续源时间采样、共享实际renderer | FFmpeg持久流、D3D11、MediaElement声音 | 0.2：已实现 | 播放/暂停/重开、逐帧、seek取消及子进程清理 |
| AppPreferences、PermissionTour | MainWindow.Menus | 本地偏好、主题、预览质量、权限说明 | Windows设置、JSON | 0.2：已实现 | UIA持久化、首次引导、拒绝权限说明 |
| AppAbout、Sparkle更新、Support | 本地关于/源码/许可/诊断、手动更新说明 | 独立品牌、版本信息 | WPF | 0.2：手动入口；自动签名更新未实现；上游捐赠不适用 | 安装包版本、正确目录、无上游收款信息 |

`.frametrail` 是明确标识的独立格式。导入 `.dogscproject` 不意味着可以将编辑后的工程无损回写给Mac；不能把构造fixture当作真实Mac互通认证。

静态 UI 截图共23张。空状态 `artifacts/validation/visual-polish-final4/` 包含 `audio.png`、`background.png`、`camera.png`、`cursor.png`、`editor.png`、`export.png`、`mockup.png`、`motion.png`、`opening.png`、`recorder.png`、`zoom.png`；填充状态 `artifacts/validation/visual-polish-populated-final4/` 包含以上对应页面及 `overlay.png`。截图仅覆盖相应空/填充状态，不证明视觉完全一致。QPC v8长测的视频封装通过，但音画同步因有效cue覆盖不足而失败，不作为同步通过证据。旧 `capture-qpc-interactive-v2` 20秒样本音频全程为静音；另一次5秒会话观察到ffplay自身静音且音量为0，但并非同一段采集的完整会话记录，不能确认原因或解释其他版本。v8也没有记录应用音量，不能据此追溯解释长测后段静音。
