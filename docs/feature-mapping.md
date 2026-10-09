# DogSC 功能迁移映射

固定基线 `e207d11b111c5e58152238fb64115b2009850547`。此表关联原实现、Windows模块、复用语义、依赖和验收方法；逐项验收状态以 [parity-checklist.md](parity-checklist.md) 为准，性能与硬件边界以 [capture-validation.md](capture-validation.md)、[media-effects-validation.md](media-effects-validation.md) 为准。

当前文档记录0.2.2开发预览增量；0.2.0/0.2.1验收作为历史证据保留在验证报告中。已实现表示可执行代码路径存在；设备待测、兼容近似和未实现功能分别记录，不用按钮数量计算迁移比例。

| 原实现 | Windows实现 | 复用内容 | 依赖 | 阶段/状态 | 验收方法 |
|---|---|---|---|---|---|
| ScreenRecorder、CaptureSources、CaptureAreaSelection | FrameTrail.Capture、MainWindow.Recorder、RegionSelector | 来源、物理像素范围、状态和错误收尾 | DXGI Desktop Duplication、FFmpeg、ScreenRecorderLib | 0.2.2：显示器/窗口/区域代码已实现；当前QPC三次短测有有效结果，30分钟候选v8的视频流为1800.383秒、108023帧；该轮音频脉冲覆盖仅44.9%且只到约994秒，整段同步验收失败，原因未定位。旧4K视频压测只作历史记录 | 当前QPC短测和长时视频封装有证据；完整音画同步覆盖、窗口/显示器设备矩阵仍待验 |
| MicrophoneRecorder、CoreAudioSystemAudioTap | CaptureTrackService | 独立音轨及单调时钟/暂停映射 | NAudio WASAPI | 0.2.2：WASAPI QPC包时钟基础探针及30秒暂停队列测试通过；三次短测可计算原始偏差；v8长测WAV虽持续1800.239秒，classified 1kHz cue在约994秒后停止，导致同步覆盖失败，音源/采集原因未定位；麦克风设备待测 | 当前QPC长时音频信号连续性、音画同步覆盖及不同音频端点仍待验 |
| CameraRecorder、CameraCaptureFormatSelector | CaptureTrackService、CameraTrack | 设备格式、独立相机素材 | Media Foundation、SRLib | 0.2：接口与导入渲染已实现，物理设备待测 | 真实设备录制/热插拔；导入素材布局与音频实测 |
| PointerEventRecorder、RecordingMarkers | InputSampler、MainWindow.RecordingTracks | 归一化轨迹、按键修饰、时钟、标记 | Win32、Stopwatch | 0.2：已实现 | 实录JSONL、按首帧/暂停映射、项目重载 |
| RecorderMemo、CaptureWindowPresentationPolicy | MemoWindow、NativeWindows | 本地提纲、辅助窗口排除 | WPF、WDA_EXCLUDEFROMCAPTURE | 0.2：已实现，DDA/gdigrab本机排除验证通过 | 实录抽帧、字号/透明度/尺寸/位置保存重开 |
| ProjectStore、ProjectRecoveryJournal | Persistence、MainWindow.Recovery | 临时文件原子替换、备份、相对素材路径 | System.Text.Json、文件系统 | 0.2：已实现 | 异常项目/备份恢复、越界拒绝、媒体重定位 |
| ProjectTimeline、ProjectTimelineEditing/Retime | Core.Models、MainWindow.Timeline、EffectTimelineOperations | 非破坏编辑、源时间与输出时间映射；Zoom、屏幕/摄像头运动、贴图和模糊五类效果可独立拆分 | Core、FFmpeg | 0.2.2：App final8构建0警告/0错误；`editor-polish-final8.json` 应用内部回归53/53；`ui-shortcuts-polish-final10.json` 在App final7上的真实键盘UIA为12/12。最新全量 `ui-full-polish-final10.json` 为7/32且`passed=false`：外部应用成为前台后触发输入守卫，未执行项不能视为产品失败或通过。final9的9/34受multi-ownedguard工具误判影响，已修复。历史 `ui-full-polish-final3.json` 的42/42仅属于较早候选，不继承为当前结果 | split/trim/rate/reorder/undo，效果拆分切口、序列化、导出抽帧及音频频率；报告见对应 `artifacts/validation` 文件 |
| EditorTimelineFilmstrip/WaveformCache/MagneticSnap | TimelineAssets、EditorTimelineControl | 异步缓存、视口、吸附和多轨编辑 | FFmpeg、WPF | 0.2.2：缩略图/波形已通过截图检查；边界拖动吸附有实现，人工验收范围有限；静态截图23张，见 `artifacts/validation/visual-polish-final4/` 与 `artifacts/validation/visual-polish-populated-final4/` | UIA拖动/多选、波形/缩略图、范围MP4探测；桌面报告见 `artifacts/validation/ui-full-polish-final3.json` |
| EditorEffectTransfer | MainWindow.Timeline | 完整效果复制和保时长/位置属性粘贴；摄像头完整粘贴替换唯一轨道，属性粘贴保留目标媒体/时钟映射 | JSON快照、项目资产复制 | 0.2.2 `editor-polish-final8.json` 应用内部回归53/53通过，覆盖撤销分割后仍选中效果、重复 S 分割与失效选择保护；快捷键 `ui-shortcuts-polish-final10.json` 在App final7上真实键盘UIA 12/12通过。全量UIA final10中止于7/32，不能推断未执行项 | 相同/不同效果类型、跨项目贴图、摄像头替换与撤销；报告见 `artifacts/validation/editor-polish-final8.json` |
| AutoZoomPlanner、ZoomAnimation | Core.Evaluation | 点击分组、目标构图、缓动、相邻片段及进入/退出进度锚点 | 独立Core | 0.2.1 Core/渲染回归已通过；锚点默认0兼容旧数据，0.2.2效果分割回归已通过；快捷键专项final10为12/12。较早完整UIA final3为42/42；当前全量UIA final10因前台守卫在7/32中止，未执行项无验收结论 | 原语义对照用例、非零锚点、边界连续性、暂停目标预览；报告见 `artifacts/validation/ui-shortcuts-polish-final10.json` |
| PointerTrack、PointerTimelineAlignment | PreparedPointerTrack、MouseClockCalibration | 指针采样、独立偏移、不移动主媒体 | Core | 0.2：已实现 | 指针偏移/平滑不耦合、裁切/变速重排映射 |
| CursorStyle、SharedFrameCursorRenderer | CursorSettings、Scene.hlsl | 独立光标、点击和隐藏策略 | D3D11 | 0.2：核心行为已实现，主题为Windows子集 | 合成轨迹和点击像素、静止/始终隐藏 |
| FrameScene、CompositionScene | Core.FrameScene、PreparedFrameSceneEvaluator | UI外的统一场景描述 | Core | 0.2：已实现 | 预览/导出共享场景、逐帧Probe |
| SharedFrameRenderer、ScreenProjection | GpuFrameRenderer、Scene.hlsl | 全场景GPU合成、XYZ透视与图层 | Vortice D3D11、HLSL | 0.2：已实现 | 投影、遮罩、半透明/圆角、像素断言及成片 |
| ScreenMotion、CameraMotion、FocusEffect | Core.Effects、EffectsInspector | 屏幕/相机运动和径向/线性聚焦；摄像头布局/透明度连续过渡、缩放联动和阴影强度 | Core、D3D11 | 0.2.1 Core、渲染和专项UIA验证已通过；0.2.2效果拆分Core回归通过；快捷键专项final10为12/12；较早完整UIA final3为42/42；当前全量UIA final10因前台守卫在7/32中止，未执行项无验收结论；物理摄像头待设备验证 | 目标姿态、曲线、缩放联动、阴影、聚焦随鼠标、实际MP4；UI报告见 `artifacts/validation/ui-shortcuts-polish-final10.json` |
| CanvasStyle、NormalizedCrop、ScreenFrameStyle | CanvasSettings、Inspector、GPU renderer | 比例、裁切、位置、圆角/描边/阴影/样机 | WPF、D3D11、授权字体 | 0.2：已实现 | 数值边界、拖动、透视标题与多分辨率 |
| BackgroundSource/Pattern/Dynamic | BackgroundSettings、SceneAssetLoader | 纯色/渐变/网格/动态/图片/视频背景 | FFmpeg持续解码、D3D11 | 0.2：已实现，系统壁纸不内置 | 视频循环/跳转、预览导出、资源复制 |
| StickerTransition、Mosaic | OverlayClip、BlurClip、GPU | 贴图层级、进出场、柔化/马赛克；背景柔化可选择包含摄像头，并可在贴图时段隐藏屏幕/摄像头 | Core、D3D11 | 0.2.1共享预览/导出媒体探针与 backdrop 像素探针已通过；0.2.2效果拆分Core回归通过；发行包专项待验 | 真实图像素材、源时段映射、图层交接、隐藏/柔化像素、8层/4区域边界 |
| OpeningSequence | OpeningSettings、FrameOpeningState | 元素启用、顺序、错开、预设和曲线 | 共享场景求值与renderer | 0.2：已实现 | 三组开场时序、旧工程默认、逐帧输出 |
| EditorScenePresets/StylePresetStore | MainWindow.Menus | 内建/自建样式和安全资产复制 | 本地文件系统、WPF | 0.2：已实现，新建/改名/覆盖/跨项目UIA通过 | 保存、改名、覆盖、撤销、跨项目应用 |
| ProjectStore schema升级 | DogScProjectImporter | schema23–27、主要轨道与效果字段转换 | Core、只读原素材/复制资产 | 0.2：fixture通过，真实Mac样本待验证 | 原工程hash不变、字段断言、丢失/近似报告 |
| DirectExportPipeline、AudioOnlyExportPipeline | ExportEngine、ExportWindow | 视频与音频相同时间线、取消/进度/回退 | FFmpeg H.264/AAC、GPU | 0.2.2 `export-panel-polish-final8.json` 为应用内部导出面板真实控件回归12/12，不是外部桌面UIA；媒体集成及分辨率/帧率矩阵另有通过证据 | MP4/M4A；1080/1440/4K×30/60真实编码；报告见 `artifacts/validation/export-panel-polish-final8.json` |
| EditorPlaybackController | PreviewPlaybackSession、MainWindow.Playback | 连续源时间采样、共享实际renderer | FFmpeg持久流、D3D11、MediaElement声音 | 0.2：已实现 | 播放/暂停/重开、逐帧、seek取消及子进程清理 |
| AppPreferences、PermissionTour | MainWindow.Menus | 本地偏好、主题、预览质量、权限说明 | Windows设置、JSON | 0.2：已实现 | UIA持久化、首次引导、拒绝权限说明 |
| AppAbout、Sparkle更新、Support | 本地关于/源码/许可/诊断、手动更新说明 | 独立品牌、版本信息 | WPF | 0.2：手动入口；自动签名更新未实现；上游捐赠不适用 | 安装包版本、正确目录、无上游收款信息 |

`.frametrail` 是明确标识的独立格式。导入 `.dogscproject` 不意味着可以将编辑后的工程无损回写给Mac；不能把构造fixture当作真实Mac互通认证。

静态 UI 截图共23张。空状态 `artifacts/validation/visual-polish-final4/` 包含 `audio.png`、`background.png`、`camera.png`、`cursor.png`、`editor.png`、`export.png`、`mockup.png`、`motion.png`、`opening.png`、`recorder.png`、`zoom.png`；填充状态 `artifacts/validation/visual-polish-populated-final4/` 包含以上对应页面及 `overlay.png`。截图仅覆盖相应空/填充状态，不证明视觉完全一致。QPC v8长测的视频封装通过，但音画同步因有效cue覆盖不足而失败，不作为同步通过证据。独立 `capture-qpc-interactive-v2` 的20秒采集样本音频全程为静音；随后独立5秒会话记录到测试用 ffplay 的 `Mute=true`、`Volume=0` 且路由到同一设备，可解释该短测。v8没有记录当时的应用音量，不能据此追溯解释v8长测失败。
