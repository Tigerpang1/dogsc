# DogSC 原功能迁移清单

此清单以锁定的 `upstream/dogsc` 为原版证据，覆盖 README 明示能力、RecorderCore 项目模型和关键 UI 页面。每行描述一个用户任务或产品入口；不把技术子步骤拆开计数。验收状态、已实现但待设备验证的项目，以及仍缺少的行为见后面的逐项复核表。

> **最新范围覆盖（2026-10-10）：** 当前收尾按 [95% 收尾计划](95-percent-plan.md) 执行：35 项原表扣除上游捐赠入口与 Mac 工程导入/互通目标后，现行分母为 33 项，目标为 32/33（97.0%）；摄像头实机条件和在线更新/公开反馈暂时延期，仍保留在分母；托盘常驻、独立浮窗和提词器按最新用户反馈优先修正。Windows 与 Mac 功能及交互对齐仍在范围内，预览、导出效果与 Mac 参考对照继续保留。下文旧分母及真实 Mac 工程导入验收记录属于历史口径，不代表当前目标。

核对基线：`upstream/dogsc` 固定提交 `e207d11b111c5e58152238fb64115b2009850547`，上游 README 当前版本号 `0.1.2`。README 四张 UI 图及静态参考见 [`visual-spec.md`](visual-spec.md)。

## 原功能与验收依据

| 区域 | 原功能 | 原版证据 | 可核验的迁移验收 |
|---|---|---|---|
| 录制 | 显示器录制 | `README.md`；`Sources/DogSCApp/RecorderSetupSurface.swift`、`ScreenRecorder.swift` | 选择显示器、开始/停止录制，产生可播放媒体且工程关联正确 |
| 录制 | 窗口录制 | `README.md`；`CaptureWindowSelection.swift`、`ScreenRecorder.swift` | 选择窗口并录制；窗口变化、遮挡和窗口消失的行为有验收 |
| 录制 | 区域录制 | `README.md`；`CaptureAreaSelection.swift` | 拖选区域并录制，边界/屏幕缩放坐标正确 |
| 录制 | 设备来源 | `RecorderSetupSurface.swift`；`CaptureSources.swift` | 设备来源入口行为明确，实际捕获类型与系统能力一致 |
| 录制 | 同录摄像头、麦克风、系统声音 | README；`RecorderSetupSurface.swift`、`MicrophoneRecorder.swift`、`CoreAudioSystemAudioTap.swift`、`CameraRecorder.swift` | 可独立启停和选择设备；录制轨道真实可解码，长录对齐另行测试 |
| 录制 | 授权和首次使用引导 | README；`PermissionTourFlow.swift`、`FirstLaunchIntroduction.swift`、`FirstUseTour.swift` | 缺少设备权限时有明确说明及操作入口，不伪造权限状态 |
| 录制 | 浮动录制条与备忘录（FT-07） | README；`RecorderSetupSurface.swift`、`RecorderMemo.swift` | 录制工具与编辑器生命周期独立；应用关闭主窗后可驻留托盘并找回；备忘录可编辑/阅读、手动滚动及自动提词滚动，可调字号和背景透明度并自动保存；录制浮窗可独立找回 |
| 录制 | 录制标记 | README；`RecordingMarkers.swift`、`RecordingMarker.swift` | 按钮和 `Ctrl+Alt+M` 写时间标记；标记随项目保存且不进入导出画面 |
| 录制 | 录制时继续编辑旧项目；自录屏排除窗口 | README；`CaptureWindowPresentationPolicy.swift`、`ScreenRecorder.swift` | 录制期间旧项目可编辑；本程序浮条/备忘录排除规则在目标采集类型下验证 |
| 工程 | 保存、自动保存、恢复和最近项目 | `ProjectStore.swift`、`ProjectRepository`、`ProjectRecoveryJournal` | 工程包和媒体引用稳定；保存中断时保留最近有效版本；打开路径和最近列表可用 |
| 工程 | 项目素材资产管理 | `ProjectStore.swift`、`ProjectMediaModel.swift` | 外部资源导入后工程内路径可随包移动；禁止路径越界；删除/回滚不会误删外部原件 |
| 工程 | 导入原 `.dogscproject` | `RecorderCore/ProjectMediaModel.swift`、`ProjectTimeline.swift`、`ProjectStore.swift` | 只读解析 schema 23–27；复制屏幕/摄像头/麦克风/指针素材到新 `.frametrail` 目录；映射裁剪片段、基本缩放、指针时钟、标记和部分画布；输出 `dogsc-import-report.json`；原工程字节不变 |
| 编辑 | 播放、暂停、逐帧定位和时间线拖动 | `EditorView.swift`、`EditorTimelineView.swift`、`EditorPlaybackController.swift` | 播放头、预览帧、轨道时间和时码一致；播放/暂停及逐帧响应可测 |
| 编辑 | 分割、裁短、恢复剪辑、重排、加速/恢复速度 | README；`ProjectTimelineEditing.swift`、`ProjectTimelineRetime.swift` | 主片段只引用源素材区间；操作可保存重载；音画、标记和动画仍映射到正确源时间 |
| 编辑 | 缩略图和音频波形 | README；`EditorTimelineFilmstrip.swift`、`EditorTimelineWaveformCache.swift` | 有媒体时生成正确缩略图/波形并按视口显示；空媒体无占位假内容 |
| 编辑 | 轨道显隐、吸附、悬浮预览、范围导出选择 | `EditorTimelineTrackVisibility.swift`、`EditorTimelineMagneticSnap.swift`、`EditorPlaybackHoverPreview.swift` | 控件作用域和片段编辑结果一致；吸附、Shift 绕过、预览轴操作可重复验证 |
| 编辑 | 区间/多选/剪辑快捷操作 | `EditorTimelinePrimaryEditing.swift`、`EditorShortcutCheatsheet.swift` | 快捷键与菜单说明一致；多选只改变预期范围，不污染持久项目单选状态 |
| 运镜 | 开场动效 | README；`OpeningSequence`、`EditorOpening*` | 可设预设、时长、缓动、元素集合和顺序；预览与导出一致 |
| 运镜 | 自动跟随与手动缩放 | README；`AutoZoomPlanner.swift`、`ZoomAnimation.swift`、`ZoomInspector` | 自动焦点从指针事件确定；手动焦点/缩放、转场和时间编辑可保存，暂停编辑时能看目标构图 |
| 运镜 | 屏幕 3D | README；`ScreenMotion.swift`、`ScreenProjection.swift` | 位置、缩放、倾斜、透视、聚焦虚化和动画插值正确；预览/导出场景一致 |
| 运镜 | 摄像头位置和动画 | README；`CameraMotion.swift`、`CameraStyle`、`CameraCaptureFormatSelector.swift` | 形状/位置/大小/裁切及时间变化可编辑；媒体同步正确 |
| 指针 | 指针样式、点击反馈、静止隐藏、始终隐藏 | README；`CursorStyle`、`CursorAssetLibrary.swift`、`SharedFrameCursorRenderer.swift` | 光标轨迹对齐、主题/点击效果/隐藏策略可导出，素材许可符合自身授权 |
| 指针 | 单独校准指针时间，不改变移动平滑 | README；`PointerTimelineAlignment.swift`、`PointerTrack.swift` | 校准只改变时间映射；平滑参数保持；跨剪切点位置可核验 |
| 画面 | 比例、分辨率、裁切、留白、内容位置/缩放 | README；`CanvasStyle`、`NormalizedCrop`、`EditorCropPresentation.swift` | 各比例与裁切边界可操作，导出尺寸符合设置，坐标归一化不因 DPI 改变 |
| 画面 | 背景、壁纸/网格/动效/自定素材 | `BackgroundSource`、`BackgroundPatternPreset`、`DynamicBackgroundPreset` | 背景素材/循环视频打包或路径可解析；预览和导出一致 |
| 画面 | 圆角、描边、阴影、样机外框 | README；`ScreenFrameStyle`、`EditorBackgroundInspector.swift` | 样机与内容圆角边界正确；阴影/描边不切边，支持预设样式 |
| 画面 | 贴图与画面柔化 | README；`StickerTransition.swift`、`FocusEffect.swift`、`EditorOverlayInspector.swift` | 图片可导入、定位、动画并随工程保存；柔化区域与时间编辑正确 |
| 画面 | 场景预设与样式预设 | README；`EditorScenePresets.swift`、`EditorStylePresetStore.swift` | 预设应用/保存/重命名/覆盖规则明确；资产复制和撤销安全 |
| 编辑 | 效果复制/粘贴及仅粘贴属性 | README；`EditorEffectTransfer.swift` | `⌘C/⌘V` 复制组件；`⌘⇧V` 只替换同类属性且保留位置/时长；Windows 快捷键需清楚映射 |
| 导出 | MP4 视频；M4A 仅音频 | README；`DirectExportPipeline.swift`、`AudioOnlyExportPipeline.swift` | 媒体可探测且音视频流结构正确；取消后进程/临时文件清理；范围时长正确 |
| 导出 | 原始、1080p、1440p、4K；30/60 FPS | README；`ExportSettings.swift`、`ExportPolicy.swift` | 选项可选；编码尺寸/帧率与容差实测匹配；不以 UI 选择代替结果探测 |
| 导出 | 进度阶段、耗时/剩余估算、取消 | README；`ExportStatus.swift`、`ExportActivityWatchdog.swift` | 进度状态不倒退，剩余时间仅在稳定后显示；取消确认结束并清理事务输出 |
| 设置 | 外观、预览质量、录制和项目偏好 | `AppSettingsView.swift`、`AppPreferences.swift` | 设置持久化范围清晰；外观只作用于 UI，不改变导出画面 |
| 设置 | 关于、版本更新、源码、反馈 | README；`AppAboutSettingsView.swift`、`AppUpdateController.swift` | 不把 macOS Sparkle/Apple 身份机制照搬；Windows 更新渠道、反馈链接及隐私行为待决定与验证 |
| 其他 | 自愿捐赠/“请杯咖啡”入口 | README；`AppSupportSheet.swift`、`Resources/Support/` | 迁移时不携带或展示未经确认的上游收款码；需要明确移除或由项目方提供新的合法入口 |

## 当前状态与未完成项复核

状态基于0.2.5本机发行包、截至2026-10-10的行为探针和桌面UIA结果。“部分通过”表示用户行为已有证据但仍有列明边界；“已实现，待设备验证”表示代码路径存在但缺少硬件证据；“待实现”表示用户能力仍未接通。字段存在或菜单可见本身不算通过。发行包清单见 `artifacts/release/FrameTrail-0.2.5-manifest.json`。同包100%、150%、200%全量UIA均44/44，均使用旧合成testsrc2工程；100%、150%、200%浮层UIA均20/20，含19项内容和1项只读启动定位。首次启动边界通过只读检查，但备忘录重启步骤由harness调用SetWindowPos重新定位窗口。200%嵌套菜单/深色下拉和100%部分浮层已人工查看；150%浮层UIA为20/20，三档指定截图均已人工审阅；150%查看浅色主窗、深色下拉、嵌套菜单和预设确认弹窗。辅助素材重定位v12用发行包Demo隔离副本，10/10通过，未改发行包样例。

| 原功能 | 当前状态 | 尚未完成的关键项 |
|---|---|---|
| 显示器录制 | 部分通过 | 同一0.2.5发行包30秒音画实录18/18；1800秒同包采集1071个flash、1070个tone并匹配1070组，名义匹配率99.07%、双向覆盖99.9066%、跨度1797.866667秒。raw绝对偏差P95/最大值80.9143/99.2483ms；映射导出P95/最大值43.333/63.333ms；360次会话状态观察及恢复通过。0.2.4旧包35/74ms及v6长测数值仅为历史。设备复位、热插拔和多显示器仍待验。详见 [`capture-validation.md`](capture-validation.md)。 |
| 窗口录制 | 部分通过 | 同一0.2.5发行包 `capture-025-target-v1/target.target.json` 通过测试窗口遮挡前/中/移动缩放后三阶段共9个颜色样点、关闭目标拒绝、录制中关闭/最小化收尾和区域录制回归；本机单显示器指定窗口通过。跨显示器、混合DPI及其它窗口仍待验。 |
| 区域录制 | 部分通过 | 一种单显示器区域通过；仍需拖选UI、DPI变化和多显示器边界测试。 |
| 设备来源 | 部分通过 | 显示器、窗口、音频端点可枚举；本机没有摄像头设备，物理摄像头和多显示器未测。 |
| 摄像头、麦克风、系统声音同录 | 部分通过 | 系统loopback同包30秒18/18；0.2.5同包1800秒1070/1080名义cue、双向覆盖99.9066%，raw绝对P95/最大值80.9143/99.2483ms，映射导出P95/最大值43.333/63.333ms，租约360次观察及状态恢复通过。发行包麦克风15.0186秒墙钟、暂停2.0104秒、WAV 13.0083秒且帧差0，仅证明本次端点数值采集与暂停映射，不证明语音质量。物理摄像头和多设备同时录制仍待设备验证。旧0.2.4/v6长测数值属于历史。 |
| 权限和首次引导 | 部分通过 | 首次帮助和Windows权限说明已实现；拒绝/撤销权限时的系统反馈与恢复流程待验证。 |
| 浮动录制条与备忘录（FT-07） | 待实现 | 旧证据只覆盖浮条录制流程、备忘录文本/字号/透明度/位置保存，以及主显示器DDA下浮条和备忘录排除。托盘驻留与恢复、主窗关闭后继续运行、录制浮窗独立于主窗的生命周期、备忘录编辑/阅读模式和自动提词滚动均未真实验收；原版仅有编辑/阅读和手动滚动，没有自动滚动，本迁移将补自动滚动。新增范围与普通操作清单见 [`desktop-residency.md`](desktop-residency.md)。 |
| 录制标记 | 部分通过 | 录制 workflow 保存过标记；`RecordingMarkerTimelineProbe` 组合验证暂停压缩后的源时间标记经过裁短、分割、两段不同速率、重排及保存重开后保留ID/名称/源时间，跳转映射正确，裁掉的标记无目标。此Core测试不验证采集端的暂停时钟压缩；标记导出不绘制仍待证明。热键冲突有状态提示。 |
| 录制时继续编辑和排除编辑器 | 通过限定实测 | 0.2.5开发快照的 `artifacts/validation/recording-isolation-v4/recording-isolation-smoke.json` 与 `driver-result.json` 通过：主显示器DDA录像中编辑器/录制条/备忘录基线像素可见，启用产品排除后对应点均变为控制背景色；录制中编辑旧隔离工程并保存重开，ProjectId和修改值保留，素材SHA256不变；停止后主窗affinity恢复0、录制条销毁、备忘录保持17。测试偏好恢复成功。仅为150%单显示器、测试自有Topmost背景布局及主显示器DDA，不覆盖窗口/区域捕获和其它显示器。 |
| 保存、自动保存、恢复和最近项目 | 部分通过 | UIA 保存重开通过；Core 备份恢复及实际终止 FFmpeg 后扫描 manifest、恢复已封段 MP4 均通过。0.2.5发行包App SHA `D46FE2FDBA4393E0D6925EC57598A42EDC9CD388FF30F9FCA624DF86298C6491` 的 `package-025-persistence-v1/result.json` 真实文件锁回归12项通过，覆盖保存/撤销/重做失败后的内存、磁盘、历史一致性与解锁重试，driver报告偏好恢复且演示样例不变；修复前 `persistence-negative-v2` 保留失败证据。自动保存异常和最近项目失效项仍待验证。 |
| 项目素材管理 | 部分通过 | DogSC fixture素材复制和越界路径拒绝通过；预设跨项目图片复制已确认目标工程生成独立文件。发行包辅助素材重定位v12为10/10：四类资源验证工程内副本SHA、保存重开及MP4/M4A解码。另有同一0.2.5 App的PNG导入生命周期22/22：原生菜单/文件对话框导入64×48图片，检查同会话撤销/重做、保存重开、源与副本SHA一致及原件未改。此专项仅证明PNG的引用、复制和持久化，不单独证明渲染或全部素材类型；当前无工程素材自动清理机制。 |
| `.dogscproject` 导入 | 部分通过 | DogSC 文件夹导入已接入菜单；构造 fixture 验证了主视频、编辑片段、缩放/自动运镜、指针时钟、标记、摄像头、麦克风/系统音轨、3D、柔化、贴图和场景字段映射。真实 Mac 工程与报告中的未支持项仍需验证，不能宣称完全互通。 |
| 播放、暂停、逐帧和拖动 | 部分通过 | 短素材实时播放/seek探针通过；逐帧快捷键、长素材吞吐与真实UI音画同步未完整覆盖。 |
| 分割、裁短、恢复、重排和变速 | 部分通过 | 合成媒体闭环覆盖 split/trim/2x/reorder及保存重载。0.2.5发行包App SHA `D46FE2FDBA4393E0D6925EC57598A42EDC9CD388FF30F9FCA624DF86298C6491` 的 `package-025-editor-v1/result.json` 为53项通过，覆盖效果编辑、撤销/重做分割、变速重排、摄像头同步段和保存重开。0.2.4全量UIA43/43是历史包证据；0.2.5发行包100%、150%、200%全量UIA各44/44通过，使用旧合成testsrc2工程；首次启动窗口只读定位通过，备忘录重启步骤由harness重新定位窗口。真实Mac工程、标记跨剪切和极长工程边界仍待验证。 |
| 缩略图和波形 | 部分通过 | `timeline-assets-20261009-190531-715/result.json` 的20项通过，覆盖8秒素材8张缩略图的源帧颜色、8张缩略图跨线程像素读取、真实音轨波形、无音轨和取消。另用1800.021729秒1080p60合成视频测得8张缩略图和1000列波形；单次性能对比为基线18.947秒、候选2.041秒，托管探针进程PrivateBytes增量约21MB，统计不含FFmpeg子进程且未控制OS/磁盘缓存。长素材生成已有实测，任意长片、多编码格式、UI滚动加载、seek刷新和缓存回收仍待覆盖。 |
| 轨道显隐、吸附、悬浮预览和范围导出 | 部分通过 | 轨道显隐、吸附、悬浮预览已有实现和渲染探针；开发快照 `range-audio-export-v1/result.json` 对实际ExportEngine范围MP4/M4A检查203帧、6.766667秒视频、6.75秒音频及四个边界时刻画面/音调；两条独立音轨覆盖SyncSegments、SourceRate和静音。`integration-after-range-v1/result.json` 含117帧MP4、3.9秒M4A和4组预览/导出对照。0.2.5发行包导出专项21项另验证真实M4A、取消事务与同窗重试，但不包含任意范围、多轨重叠操作。 |
| 区间、多选和剪辑快捷操作 | 部分通过 | 0.2.5发行包编辑回归53项实际覆盖时间线拖动、范围1秒MP4、多选组操作及保存重开；开发快照 `range-audio-export-v1/result.json` 检查重排和变速后的两条独立音轨、画面与时长。仍需覆盖用户在桌面上选择任意多轨范围和重叠轨道的交互边界。 |
| 开场动效 | 部分通过 | 0.2.1 Core按Swift公式验证元素顺序和三条曲线；final12新专项11/11包含三预设/节奏和保存重开，综合视频导出包含开场。缺少Mac同工程逐帧对照。 |
| 自动跟随和手动缩放 | 部分通过 | 120Hz镜头跟随、安全区、源裁切/时钟映射、静默后恢复、自定义Bezier、缩放聚焦和进度锚点已有Core探针；真实输入和导出保持原证据。复杂真实Mac剪辑接缝仍待互通验证。 |
| 屏幕3D | 部分通过 | UIA 已通过画布“添加元素”菜单创建片段；渲染探针覆盖投影、选框四角反算和综合 3D 导出。独立清洁工程专项验证属性粘贴后保留目标时间（`ui-effects-all-audit-v38.json`）；物理设备和更复杂的跨剪切构图仍待验。 |
| 摄像头位置和动画 | 部分通过 | 六种布局、圆角/透明度连续变化、缩放联动、阴影和媒体比例探测已接入；`camera-media-20261009-111841`检查Original导出像素比例、阴影差和1440p全屏覆盖。分段同步Core沿用；物理摄像头采集、多设备同步仍待验。 |
| 指针样式和点击策略 | 部分通过 | 本机.cur与矢量回退、跟随录制形态、固定三种系统光标、倾斜和点击参数已接入；`cursor-shapes-v5`实际记录同点三形态；GPU像素与final12界面专项通过。其它Windows主题、外部设备时钟仍待验。 |
| 单独校准指针时间 | 部分通过 | Core 独立检查指针 offset 映射与移动平滑参数保持，并覆盖裁切画布坐标映射；真实录制偏差校准效果仍待实测。 |
| 比例、分辨率、裁切、留白和位置缩放 | 部分通过 | 9:16比例及撤销/重做通过，摄像头画布拖动通过；源裁切边界、DPI归一化和实际导出尺寸需补。 |
| 背景、网格、动效和自定素材 | 部分通过 | UIA 网格设置及项目重开通过；Render.Probe 验证背景视频循环/非循环取帧与预览/导出帧一致。任意用户视频/图片资源随工程搬移和失效恢复仍需覆盖。 |
| 圆角、描边、阴影和样机 | 部分通过 | Render.Probe 对 Phone 与 Tablet 机身、边框像素分别断言通过，并覆盖浏览器框、圆角、描边和阴影；final17 UIA 将框体比例1.12、顶栏1.08、外圆角26写入项目；final19媒体探针覆盖大于50%的相机圆角上限。final20静态截图包含样机页面。与原版所有设备外观静态对照、更多素材组合仍未完成。 |
| 贴图与柔化 | 部分通过 | 原版曲线、斜向进退、外描边、旋转/阴影、背景/相机独立柔化与隐藏已接通；Core覆盖相邻/重叠交接不露底；`overlay-export-20261009-111839`验证实际图片加载、相机之上的叠放、边框投影、隐藏在预览及MP4一致。任意原Mac资源与超过GPU图层容量的项目仍有限制。 |
| 场景和样式预设 | 部分通过 | final17 UIA 验证本机预设新建、改名、安全覆盖、应用；跨项目测试证明预设素材复制到目标工程并生成独立文件。更复杂背景视频/多素材组合仍待验证。 |
| 效果完整复制、属性粘贴 | 部分通过 | 0.2.5发行包 `package-025-editor-v1/result.json` 为53项通过，覆盖五类效果完整复制与属性粘贴、目标保留、撤销分割后的效果选择恢复、重复S分割和失效选择保护；仍是合成工程，未覆盖任意重叠场景或真实Mac工程。发行包全量桌面UIA为44/44通过；该综合工程验收不替代真实Mac工程及所有重叠边界。 |
| MP4与仅音频M4A | 部分通过 | 0.2.5发行包基础工作流实际录制、编辑、重开及导出MP4；同包导出面板21项验证M4A纯音轨/时长、目录失败保护、取消、保留原目标、重试和完整FFmpeg解码。`range-audio-export-v1/result.json` 与 `integration-after-range-v1/result.json` 是未打包开发快照的范围/多轨定量媒体证据。编码器矩阵限于本机硬件；任意重叠工程仍待验。 |
| 原始、1080p、1440p、4K和30/60fps | 部分通过 | UIA 选择 4K/1080p/60 FPS 后摘要显示 2160×3840、1080×1920、60 FPS；渲染探针有 1440p/4K 30/60 编码矩阵。其它 GPU/编码器组合和原始素材异形尺寸待测。 |
| 导出进度、耗时估算和取消 | 部分通过 | 0.2.5发行包同一App SHA的 [`package-025-export-v1/result.json`](../artifacts/validation/package-025-export-v1/result.json) 为21项通过：取消采样4个（0.055–0.059，合成与编码）；重试采样141个（0.055–1.000，覆盖编码、媒体核验和完成阶段）；保留原目标、清理临时文件、同窗重试及完整FFmpeg解码均通过，driver偏好恢复且样例未改。剩余时间估算精度、长导出交互及其它环境仍待验。旧开发快照export-progress-025-v2使用另一App SHA，仅作历史证据。 |
| 外观、预览质量、录制偏好 | 部分通过 | final17 UIA 保存960宽、60fps、录制排除偏好、三档指针平滑、样机参数、时间偏移及开场设置；final20页面截图包含各检查器。预览静帧/实时帧率是否按设置生效及深色主题仍待验。 |
| 关于、更新、源码和反馈 | 部分通过 | 关于版本取程序集元数据；源码/许可证、手动更新说明和反馈说明有本地入口。没有在线更新或公开反馈服务，需由实际发行方补充渠道。 |
| 上游捐赠入口 | 不适用 | 不复用上游品牌、二维码或收款账户；如以后要提供捐赠入口，需项目方单独提供并确认。 |

## 达到 95% 迁移目标前的主要缺口

当前清单不合并成一个百分比，因为未完成项的用户影响不同。按34项等权分母至少需33项完整通过。源码ZIP独立构建通过。0.2.5最终包8/8通过，新增检查确认运行后发布目录477个文件与原ZIP逐字节一致；早期7项流程改写Demo工程字段的历史问题已修复，样例已恢复。包内30秒音画18/18、1800秒同包采集和窗口目标专项通过。三档全量UIA各44/44、三档浮层UIA各20/20；同包编辑53、导出21、文件锁12、素材重定位10项专项通过。全量UIA用合成testsrc2工程，备忘录重启步骤由harness重定位窗口。真实硬件、真实DogSC工程和混合DPI仍待验证。PNG贴图的导入、撤销/重做及保存重开副本保留已有22项实测；其他素材类型和渲染未由该专项证明。合成媒体不代替物理设备或真实Mac项目。

- 在有设备的环境验证物理摄像头、多设备同时采集及第二显示器/混合DPI；长录资源阈值触发后的工程恢复仍需针对性实测。当前窗口目标探针已通过所测本机目标场景，后续边界是其他窗口、跨屏和混合DPI。
- 0.2.4旧长测使用冻结的分析器v3和Capture.Probe v6，相关结果保留为历史证据；当前0.2.5同包长测及窗口目标结果见本清单的录制行与采集验证文档。后续重点是设备复位、热插拔、其它窗口/显示器和音频内容完整性边界。
- 进一步核实首帧映射在不同采集后端和设备上的偏差；当前实录长测及70秒录制导出通过，但不能据此宣称所有捕获时钟组合均已验收。
- 用原 Mac DogSC 项目验证导入、资产复制和高级效果映射，逐项审阅导入报告里的未保留字段。
- 对照原版界面图继续完成静态验收；0.2.5三档全量UIA44/44及浮层20/20通过。200%嵌套菜单/深色下拉、100%部分浮层已人工查看；150%浮层UIA为20/20，三档指定截图均已人工审阅；150%查看浅色主窗、深色下拉、嵌套菜单和预设确认弹窗。多显示器及所有状态的对照尚未完成，3D投影边界仍有位置差异。
- 扩充时间线多轨边界、长时 seek、缓存回收、失效辅助素材重定位及恢复场景；长素材缩略图/波形生成已有30分钟合成素材实测，不能继续写成未测，但仍需验证更长/不同格式素材和UI滚动刷新。

缺少真实硬件或原始工程时，相关状态保持“待设备验证”或“待验证”，不当作已实现通过，也不把平台特有行为误算为代码缺失。窗口捕获已在0.2.5发行包指定目标探针通过所测场景；该结论不外推至其他窗口或多显示器。

## 不计为成功的情形

界面上存在按钮但点击不改变项目或输出，不算功能迁移。只验证合成素材不得称作真实采集验证。存在类似 JSON 字段但未保存、加载或导出透传，不算工程互通。只打开 `.dogscproject` 目录或把旧 `project.json` 原样改扩展名，不算导入器兼容。

导入器接受 schema 23–27 的录制项目，拒绝无主视频、未知版本和越界路径；主窗口菜单已提供导入入口，先探测主视频，再将素材复制到独立 `.frametrail` 包。构造 schema fixture 的导入测试通过，覆盖编辑片段、音频轨、指针时钟、标记、摄像头运动、3D、柔化及贴图，并检查素材复制、路径拒绝和原始 JSON 不变。0.2.1已接入开场交错、Bezier、缩放聚焦/进度锚点、摄像头连续布局/阴影联动和贴图边框/阴影/背景处理，报告不再将它们列为丢失字段。测试命令为 `dotnet run --project tests/FrameTrail.DogScImport.Tests/FrameTrail.DogScImport.Tests.csproj -c Release`。目前没有原 Mac 真实项目，不能声称完整互通。

## 当前桌面自动化证据

0.2.5发行包：[package-0.2.5/result.json](../artifacts/validation/package-0.2.5/result.json) 最终8/8通过，含便携归档运行后477个文件逐字节未变；偏好快照已恢复。早期7项流程及Demo字段副作用保留为历史，样例已恢复。源码归档独立构建通过，manifest见 [FrameTrail-0.2.5-manifest.json](../artifacts/release/FrameTrail-0.2.5-manifest.json)。同一App SHA的编辑53项、导出21项和文件锁12项专项通过；全量UIA 100%、150%、200%各44/44且complete，浮层各20/20；素材重定位v12为10/10。全量UIA使用旧合成testsrc2工程，备忘录重启步骤由harness调用SetWindowPos重新定位；四类素材重定位使用发行包Demo隔离副本并完成哈希、保存重开和MP4/M4A解码。PNG贴图的撤销/重做与保存重开副本保留已有22项实测，但该专项不证明所有资产类型或渲染；混合DPI、多显示器、真实Mac工程或设备矩阵仍未覆盖。
0.2.4的UIA43/43、浮层19/19及包内导出15项/编辑53项保留为历史证据。0.2.5发行包在100%、150%、200%全量UIA各44/44、浮层UIA各20/20；备忘录重启步骤由harness SetWindowPos定位。100%旧包工作区适配测试窗由harness定位，不能证明产品主窗启动定位。200%发行包UIA的首次启动边界 [32,32..3808,2032] 位于工作区 [0,0..3840,2064]。混合DPI和多显示器仍待验。

独立主素材重定位 UIA [`ui-relink-audit-v5.json`](../artifacts/validation/ui-relink-audit-v5.json) 通过：在工程副本中删除主视频，打开时重新选择原视频；项目将其复制到内部 `media/relocated-*` 路径，哈希与原文件相同，原件保持不变。FFmpeg 单帧解码成功，保存后重开没有再次出现缺失提示。此结果只覆盖主视频路径。

final20 应用内部回归 [`editor-regression-final20-audit.json`](../artifacts/validation/editor-regression-final20-audit.json) 为39/39，包含逐帧前进/后退、分割撤销/重做、多选重排、摄像头分段移动/裁切/滑移、非连续多音轨删除撤销、四类效果复制/属性粘贴、1秒范围MP4和保存重开；新增声音检查器偏移测试确认同步段一起移动且源时间/速率不变并可保存重载。备忘录独立重启 `ui-memo-audit-v24.json` 验证文本、字号32、透明度0.61和位置保存；真实鼠标录制 `input-workflow-final` 捕获27个指针事件、2次点击和1个自动缩放片段，并完成7.9秒录制后的剪辑与5.925秒导出；70秒录制 workflow 完成录制、暂停、编辑和导出。播放回归验证54帧及恢复18帧，恢复回归恢复1条音轨、1个标记和1条指针轨迹。

当前静态截图共23张，文件名如下。空状态目录 [`visual-polish-final4`](../artifacts/validation/visual-polish-final4/)：`audio.png`、`background.png`、`camera.png`、`cursor.png`、`editor.png`、`export.png`、`mockup.png`、`motion.png`、`opening.png`、`recorder.png`、`zoom.png`。填充状态目录 [`visual-polish-populated-final4`](../artifacts/validation/visual-polish-populated-final4/)：`audio.png`、`background.png`、`camera.png`、`cursor.png`、`editor.png`、`export.png`、`mockup.png`、`motion.png`、`opening.png`、`overlay.png`、`recorder.png`、`zoom.png`。截图覆盖编辑器、录制、导出、背景、相机、指针、样机、运动、开场、缩放和音频面板。UIA 与合成项目结果不证明真实硬件覆盖、所有渲染状态、全部原功能覆盖或 DogSC 真实工程兼容，也不能据此宣称达到95%迁移验收。
## Core 与渲染验证证据

开发快照范围音频导出 [`range-audio-export-v1/result.json`](../artifacts/validation/range-audio-export-v1/result.json) 覆盖重排、速率、跨切口和双音轨；[`integration-after-range-v1/result.json`](../artifacts/validation/integration-after-range-v1/result.json) 为标准合成集成。0.2.5包内文件锁回归改见 [`package-025-persistence-v1/result.json`](../artifacts/validation/package-025-persistence-v1/result.json)，12项通过，App SHA为 `D46FE2FDBA4393E0D6925EC57598A42EDC9CD388FF30F9FCA624DF86298C6491`。修复前 [`persistence-negative-v2/result.json`](../artifacts/validation/persistence-negative-v2/result.json) 保留失败证据。

Core测试覆盖时间线分割/裁剪/速率/重排、重复片段映射、时钟校准、持久化和路径校验，以及Zoom、ScreenMotion、CameraMotion接缝和八个开场预设样本。0.2.5包内全量UIA44/44，编辑53、导出21、文件锁12项通过；0.2.4的43/43、浮层19/19及旧包内编辑导出结果作为历史保留。30秒同包音画18/18；1800秒同包长测1070/1080名义事件、双向覆盖99.9066%，raw绝对P95/最大值80.9143/99.2483ms，映射导出P95/最大值43.333/63.333ms。

时间线资产长素材证据：`timeline-assets-20261009-190531-715/result.json` 的20项测试通过，包含红/蓝源画面缩略图一致、8帧跨线程读取、真实音轨波形、无音轨保留缩略图、取消可及时结束。另在 `timeline-assets-performance/comparison.json` 对1800.021729秒、1080p60合成源测量8张缩略图与1000列波形：基线18.947秒、候选2.041秒；托管探针进程PrivateBytes增量分别21,229,568与20,975,616字节。该单次顺序观测未控制磁盘/系统缓存且排除FFmpeg子进程内存，不能外推到任意素材或整个应用峰值。

渲染探针验证 D3D11、透视变换反算、动态背景循环/非循环取帧，以及预览与导出背景一致性。1080p60 六秒、360 帧 NVENC 样本耗时 2.106 秒；4K/1440p 的 30/60 FPS 组合也有编码样本。静态和动态 1080p 解码背压样本的进程内存分别维持在 179 MiB 和 181 MiB，960 宽度输出的 p95 帧耗时为 2.77 ms 和 2.12 ms。独立媒体集成报告为 `Success=true`，导出 117 帧、320×180/30 MP4 和 3.9 秒 M4A；四个预览播放会话与导出帧的 PSNR 为 24.06–25.58 dB，MAE 为 3.78–5.03。此项验证证明合成工程上的时间线及构图链路，不能替代真实采集同步测试。

历史报告 [`ffplay-sync-correct-region-30m-report.json`](../artifacts/validation/long-capture/ffplay-sync-correct-region-30m-report.json) 来自旧时钟实现，不能代表0.2.4。0.2.4的1800秒采集位于 `artifacts/validation/capture-aligned-audio-clock-1800s-v1/`，v3分析报告 `offline-analysis-source-rate-v3/validation-summary.json` 检出1070/1080名义cue（99.0741%），双向覆盖99.9066%，跨1797.8425秒；raw P95 74.4448ms、最大96.1118ms，映射成片P95 38.333ms、最大56.667ms。长测由capture v6冻结程序集采集；0.2.4新增timestamp-errorgap可靠性条件没有触发，因此不声称最终包完整录制了该长测。旧v2分析结果保留为历史失败。DogSC导入仍只有schema fixture，没有真实Mac工程互操作证据。安装包与许可证结论以发行材料文档为准。
