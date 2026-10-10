# 功能验收台账

更新：2026-10-10。编号固定为 `FT-01` 至 `FT-33`，对应 [`parity-checklist.md`](parity-checklist.md) 原功能表按原顺序移除“导入原 `.dogscproject`”和“上游捐赠入口”后的行。复合功能不拆分计数。每项只认本表写明的有限场景；状态限定为通过、待修、待验、缺资源、延期。摄像头先延期但保留在整体分母；本阶段继续完成非摄像头收尾，不据此删改 33 行或宣称达到95%。

Git 差异核对：从 0.2.5 验收提交 `df3ed90` 到旧基线 `889db72`，Core、Capture、Media 源码及依赖未变；从 `889db72` 到 `02faac7`，Core 新增 `AssetCandidateResolver.cs`、`ProjectAssetRelocationTransaction.cs`，Capture 与 Media 未变。新增 Core 素材事务已由 Core tests、Integration 与素材恢复/重定位场景验证；App 控件主题、时间线控件、主菜单和导出窗口也有变动，版本配置为0.2.7。本轮新增编辑保存失败提示、主素材时长校验、设置页就地校验及时间线输入法快捷键。离屏保存失败、视觉和文本菜单命令探针均通过；具体证据见相关行。素材重定位取消、成功复制及重开已实机验证；设置页错误、保存和重启持久化已实机验证。`backup-recovery/result.json` 补齐确认备份恢复后取消素材定位的同流程边界，见 FT-10。`preferences-export/result.json` 增补真实偏好应用路径、外观资源切换及导出像素对照证据，见 FT-32。旧报告只在实现及配置未变且完整覆盖本行场景时继承；不能将旧版本或离屏证据扩成当前整行通过。

## 逐项记录

| ID | 功能 | 有限验收场景 | 证据 | 状态 |
|---|---|---|---|---|
| FT-01 | 显示器录制 | 本机主显示器录制30秒；成片音画可解码、时间对应。 | [`capture-validation.md`](capture-validation.md)；`artifacts/validation/capture-025-av-30s-v1/run-result.json` 与 `capture.sync-playback.json`，同包18/18。 | 通过 |
| FT-02 | 窗口录制 | 指定测试窗口遮挡前、中、移动缩放后取9个颜色样点；关闭目标被拒绝，录制中关闭/最小化可收尾。 | `artifacts/validation/capture-025-target-v1/target.target.json`。 | 通过 |
| FT-03 | 区域录制 | 单显示器拖选一个区域，停止后核对成片边界与选区。 | 现有窗口目标专项含区域录制回归，但不覆盖拖选及边界核对；需补此场景。 | 待验 |
| FT-04 | 设备来源 | 打开来源选择器，核对显示器、窗口、音频端点的枚举类型与可选状态。 | 现有 `capture-validation.md` 仅记枚举结论，未保存可核对的固定场景记录。最近实机还发现来源菜单列出应用自身不可录制的浮动窗口；修复处理中，仍按本行原有限场景复验，不增加验收项。 | 待验 |
| FT-05 | 摄像头、麦克风、系统声音同录 | 同一真实会话独立启停摄像头、麦克风、loopback；导出三轨并核对解码和起止时间。 | 现有实测证明 loopback 和麦克风端点映射；没有物理摄像头设备及三轨同录证据，摄像头条件本轮延期。 | 延期 |
| FT-06 | 授权和首次引导 | 拒绝一次采集权限，检查说明与恢复入口，再授权并成功开始录制。 | 现有报告覆盖首次帮助/权限说明；拒绝后恢复未验。 | 待验 |
| FT-07 | 浮动录制条与备忘录 | 移动备忘录、改字号/透明度、重启检查保存值；录屏核对浮条和备忘录排除。 | `ui-memo-audit-v24.json`、`artifacts/validation/recording-isolation-v4/`；但共享控件样式已变，当前界面场景需复验。 | 待验 |
| FT-08 | 录制标记 | 录制中用按钮和可用全局快捷键写标记；首选 `Ctrl+Alt+M`，系统占用时显示并使用 `Ctrl+Alt+Shift+M`。保存重开核对时间，导出抽帧确认无标记绘制。 | 0.2.7 首次人工录制仅保存一条，首选键被其他应用占用；初次记录见 `artifacts/validation/package-0.2.7/recording-markers/initial-result.json`。本机 0.2.8 测试候选以备用键完成补验：`artifacts/validation/marker-hotkey-fix/recording-proof/desktop-result.json` 记录用户确认的按钮1次、热键2次，正常关闭并从最近项目 UI 重开；三个标记 sourceTime 为 2.5939237、11.2726287、14.2698502，逐条与 origin 日志一致，重开前后 Project SHA 均为 `29AE09B6C37D57C778CDADBACAA2DC0BA977502DB7F5C32405022F4A18F80C23`。`saved-menu-0.jpg`、`saved-menu-1.jpg`、`reopened-menu-0.jpg`、`reopened-menu-1.jpg` 留存界面证据。`recording-proof/probe/result.json` 以真实 ExportEngine 比较有/无3条标记的短区间输出，3个时点均在选区内，解码后380帧逐帧像素相同，原录制与输出均完整解码、退出码0。该运行时 App DLL SHA 为 `AA1A8ED9D46FC5334ABAE2177DFF823311336F97C5403B4FF75D22C6AEFD4AF6`；仅覆盖此备用键冲突和实机录制，不代表最终 0.2.8 包或所有系统组合。历史探针曾观察到381帧送入编码器、380帧解码；源码修复及编码结果见 `artifacts/validation/fractional-frame-fix/result.json`，Core tests 和 Integration 分别见同目录测试日志及 `fractional-frame-fix-integration/result.json`。此修复尚未更新最终交付包。 | 通过 |
| FT-09 | 录制时继续编辑和排除编辑器 | 主显示器 DDA 录制时编辑既有工程并保存重开；对比编辑器/浮条/备忘录排除前后像素及停止后窗口状态。 | `artifacts/validation/recording-isolation-v4/recording-isolation-smoke.json`、`driver-result.json`。 | 通过 |
| FT-10 | 保存、自动保存、恢复和最近项目 | 以文件锁触发编辑保存失败，核对画面/历史仍与磁盘一致、控件恢复；解除占用后重试，并取消一次素材重定位事务核对清理。用户明确确认恢复损坏项目的备份后，恢复到原路径是单独接受的恢复动作；之后取消素材定位不回滚该恢复。 | `artifacts/validation/noncamera-closeout-v3/recovery/locked-edit-result.json`、`success-result.json`、`cancel-result.json` 覆盖文件锁编辑失败且 SHA 不变、解锁后重试/撤销，以及普通素材定位取消清理；`main-media-coverage/result.json` 覆盖主素材时长恢复校验。新增 `artifacts/validation/noncamera-closeout-v4/backup-recovery/result.json`（FrameTrail-0.2.7-menu，Runtime DLL SHA `445AADB4C819E8A24A9761DCBEBBA91DE3FAFDA966A74006AB4EB9E8691CBF2B`）及 `confirm.jpg`、`relocation-after-restore.jpg`、`canceled.jpg` 记录用户确认备份恢复后取消缺失主素材定位。恢复后工程 SHA 与备份均为 `337B4933…EC937D64`，保留1份 damaged 文件（SHA `E5CADB37…0DEF7FA`），无 relocated 副本，证明取消未回滚已接受恢复。 | 通过 |
| FT-11 | 项目素材资产管理 | 对主素材和辅助素材分别取消/失败重定位，确认工程及本轮复制副本的事务结果；成功重定位后保存重开并核对哈希。素材事务只管理本轮新增的重定位副本与最终工程保存。若用户已明确确认恢复备份，原路径恢复及保留的 `.damaged-*` 文件属于该已接受恢复动作，不是素材事务的临时副本，也不因后续素材取消而删除。 | `artifacts/validation/noncamera-closeout-v3/recovery/README.md`、`cancel-result.json`、`success-result.json`、`fixture-hashes.json` 和 `reopened-editor.jpg`：主素材已复制后取消辅助素材选择时工程 SHA 不变且无重定位副本；短主素材候选被拒后完成主/辅素材复制，文件哈希与候选一致，保存后可从最近项目重开。 | 通过 |
| FT-12 | 播放、暂停、逐帧定位和时间线拖动 | 中文输入环境下聚焦时间线，验证播放/暂停、逐帧前进后退和拖动；同时确认 `I`/`O` 范围快捷键不会被输入法截获。 | `artifacts/validation/noncamera-closeout-v1/desktop-observations.json` 记录 `I` 起点4.99s、`O` 终点12.00s，右箭头到5.02s、左箭头回4.99s，播放至8.90s持续推进、暂停于14.43s，拖动到8.03s；范围状态和中文输入环境均记录。 | 通过 |
| FT-13 | 分割、裁短、恢复、重排和变速 | 固定合成工程执行分割、裁短/恢复、2倍速、重排并保存重开，核对源时间与成片顺序；另验证文件锁时编辑失败不会提前改变项目/历史。 | `artifacts/validation/noncamera-closeout-v4/editing/01-split.json` 至 `07-undo-restore.json` 记录实机分割、撤销、左裁至3.4秒、2倍速、重排、恢复全长及撤销恢复；末态序列为6/11/3.4/18秒，19.3秒并从最近项目重开。`editing/verification/result.json` 五项通过：实际UI导出1920×1080@30完整解码，多段关键帧匹配独立源时间参考，反事实校验区分段顺序及2×映射。文件锁证据 `noncamera-closeout-v3/recovery/locked-edit-result.json` 与 `noncamera-closeout-v4/menu/speed-save-failed.jpg`、`locked-speed-result.json`、`retry-saved.jpg/.txt`、`undo-result.json` 覆盖失败时保留2×模型值、解除占用后重试保存及撤销精确恢复。旧 `package-025-editor-v1/result.json`（53项）和离屏失败探针仅作补充。 | 通过 |
| FT-14 | 缩略图和音频波形 | 8秒含音轨素材显示8张源帧缩略图和波形；检查无音轨、取消生成两种结果。 | `artifacts/validation/timeline-assets-20261009-190531-715/result.json`，20项通过；缓存实现未变。 | 通过 |
| FT-15 | 轨道显隐、吸附、悬浮预览和范围导出 | 中文输入环境下按 `I`、`O` 设置范围并核对区间，再检查轨道显隐、吸附、悬浮预览及选区导出头尾帧。 | `artifacts/validation/noncamera-closeout-v1/desktop-observations.json` 记录 4.99–12.00 范围导出，1920×1080、60 FPS、420帧且完整解码；`artifacts/validation/noncamera-closeout-v3/recovery/timeline-view-result.json` 验证轨道隐藏/恢复、吸附开启定位6秒而关闭定位5.9秒、悬浮预览输出时间约8.47秒且播放头保持5.9秒；`artifacts/validation/noncamera-closeout-v2/integration-final3/result.json` 验证标记帧与基线一致。 | 通过 |
| FT-16 | 区间、多选和剪辑快捷操作 | 先设置并核对 I/O 范围，再对不连续多选片段执行一次快捷操作、撤销并重开。 | 用户协助在最终 0.2.7 包中完成有限场景：`artifacts/validation/package-0.2.7/manual-acceptance/range-before-selection.txt/.jpg` 记录 I/O 范围 1.99–9.97 秒；`selected-1-and-3.jpg` 记录第1/3段不连续选择；`after-multidelete.json/.jpg/.txt` 记录按 D 后仅保留原第2/4段（11/7/1、18/6/1），总长13秒；`after-undo.json` 与 `before.json` 整份工程语义相同，SHA `955EC016…CABFF00`；`reopened.jpg/.txt` 及 `result.json` 记录关闭后从最终包重开，仍为4段/19.30秒且项目语义相同、字节未变化。运行时 App DLL SHA `A03DD45233E132172169D754F6D6816EE5DE54F8690B2AD6FA934BC50C2BACE1`。 | 通过 |
| FT-17 | 开场动效 | 在固定工程中设置时长、缓动、元素集合和顺序，保存重开，并对照预览与导出关键时点。 | `artifacts/validation/noncamera-closeout-v5/intro-zoom/intro-configured.jpg`、`intro-reopened.jpg/.txt`、`01-intro-configured.json` 与 `desktop-result.json` 记录设置与重开。配置截图来自同 ProjectId 的源工程；冻结 fixture 保留同一开场设置并加入19.3秒贴图，重开 SHA 与 fixture 相同。`opening-probe/result.json` 与 `Program.cs` 使用 overlay 起点0、时长19.3秒、首段源起点6秒；仅缩短测试片段以导出4.5秒，不缩短 overlay。真实 `PreviewPlaybackSession` 和 `ExportEngine` 在0/1.18/2.2/4秒取帧，独立进度/透明度公式匹配，并以关闭开场、仅屏幕和反转顺序作实际导出负对照。 | 通过 |
| FT-18 | 自动跟随和手动缩放 | 在固定指针片段分别启用自动跟随与手动焦点/缩放，保存重开并核对两个关键时点构图。 | `artifacts/validation/noncamera-closeout-v5/intro-zoom/02-auto-generated.json`、`03-manual-configured.json`、`manual-configured.jpg`、`manual-reopened.txt` 与 `desktop-result.json` 记录自动生成、手动焦点/比例设置及相同 SHA 重开。`zoom-probe/result.json` 使用同快照实际 `PreviewPlaybackSession` 和 `ExportEngine` 解码帧核对；手动帧16.233s的缩放、裁切原点匹配独立焦点公式，预览/导出差值均值1.88；镜像焦点和1倍比例负对照分别有71.2%、72.0%变化像素。自动固定焦点负对照在输出2.0s产生19.0%变化像素，另有 Core 自动跟随回归通过。`Automatic.Samples[].ManualSceneMatchesIndependent=false` 是仅在手动样本时才判真的诊断字段，不表示自动场景失败。 | 通过 |
| FT-19 | 屏幕3D | 固定工程检查投影参数与选框四角反算，并完成一段综合3D导出。 | Render.Probe 投影、选框反算及综合3D导出证据；相关实现未变。 | 通过 |
| FT-20 | 摄像头位置和动画 | 真实摄像头片段设圆角/位置并跨一个剪辑点变化，核对成片媒体比例与同步。 | 有合成媒体布局探测；无真实摄像头采集，多设备同步未验，本轮摄像头条件延期。 | 延期 |
| FT-21 | 指针样式、点击反馈、静止隐藏、始终隐藏 | 对同一固定轨迹分别检查样式、点击环、静止时隐藏和始终隐藏的导出帧。 | `cursor-shapes-v5` 在光标实现未变时继承三种形态与色卡像素证据；`artifacts/validation/noncamera-closeout-v4/noncamera-parity/result.json` 的同Scale对照在点击时间观察885个变化像素、点击窗结束后差异为0，另验证可见指针差异242像素、静止隐藏与始终隐藏差异0。 | 通过 |
| FT-22 | 单独校准指针时间 | 对含一次剪切的固定轨迹加时间偏移，核对剪切两侧的指针位置，并确认平滑参数不变。 | `artifacts/validation/noncamera-closeout-v4/noncamera-parity/result.json` 以实际导出MP4解码帧验证剪切前后校准位置；相同输出时刻的未校准对照各产生125个像素差并落在反向位置，评价器在对应源时刻的平滑结果相等。 | 通过 |
| FT-23 | 比例、分辨率、裁切、留白和位置缩放 | 固定源素材设 9:16、非居中裁切和留白，导出核对尺寸及四角边界。 | `artifacts/validation/noncamera-closeout-v5/noncamera-parity/result.json` 的 FT23 通过：360×640@30实片解码核对四角留白、内容四象限与四边内外样点；另以坐标编码灰度源分别导出 Crop=(.58,.1,.32,.8) 与 Full 负对照，在同一时刻核对四个源边缘位置的独立坐标预期。四点均匹配 Crop 映射、匹配各自预期且与 Full 负对照明显不同（差异69至507），因此忽略 Crop 会使断言失败。样本由测试代码按生成图案坐标独立计算，实际像素来自导出后解码帧。 | 通过 |
| FT-24 | 背景、壁纸/网格/动效/自定素材 | 使用固定循环背景视频，比较循环边界预览与导出帧；重开工程后资源仍可解析。 | Render.Probe 循环/非循环取帧及预览/导出一致；实现未变。 | 通过 |
| FT-25 | 圆角、描边、阴影、样机外框 | 固定 Phone/Tablet 样机工程核对机身、边框像素及圆角/阴影不切边。 | Render.Probe Phone/Tablet 机身边框断言及浏览器框效果；实现未变。 | 通过 |
| FT-26 | 贴图与画面柔化 | 导入一张 PNG，定位并设入场动画及柔化；保存重开后对比预览和导出帧。 | `artifacts/validation/overlay-export-20261009-111839/`；图片加载、叠放、隐藏及预览/MP4一致。 | 通过 |
| FT-27 | 场景预设和样式预设 | UI 新建、改名、安全覆盖并应用一个本机预设；另核对跨项目应用后素材为独立副本。 | final17 UIA预设操作及跨项目素材复制证据；预设实现未变。此项不覆盖复杂多素材组合。 | 通过 |
| FT-28 | 效果复制/粘贴及仅粘贴属性 | 对五类效果各做一次完整复制和仅属性粘贴，核对目标 ID、位置/时长及撤销结果。 | `artifacts/validation/package-025-editor-v1/result.json` 明确覆盖五类效果复制、属性粘贴、目标保留及撤销；对应转移逻辑和快捷键实现未变。 | 通过 |
| FT-29 | MP4视频与仅音频M4A | 固定工程分别导出 MP4/M4A，探测流类型、时长和可解码性；取消一次导出并核对临时文件清理。 | `artifacts/validation/noncamera-closeout-v2/full-export-1440p-probe.json` 与完整解码记录覆盖24秒MP4；`audio-export-probe.json` 确认独立M4A仅含48kHz单声道AAC、时长24秒，`audio-export-decode.log` 退出码0，`audio-export-complete-light.jpg` 记录完成态；`export-cancel-check.json` 及 `integration-final3/result.json` 覆盖取消清理。 | 通过 |
| FT-30 | 原始、1080p、1440p、4K及30/60 FPS | 选1080p/60和1440p/30各导出一次，探测实际尺寸、帧率。 | `artifacts/validation/noncamera-closeout-v2/integration-final3/result.json` 记录1080p60硬件导出、1920×1080、60 FPS、360帧；`full-export-1440p.mp4`、`full-export-1440p-probe.json` 与 `full-export-1440p-decode.log` 记录全24秒1440p30导出，ffprobe为2560×1440、30/1、720帧，AAC 24秒且完整解码通过；`export-complete-dark.jpg` 记录完成态。 | 通过 |
| FT-31 | 导出进度、估时和取消 | 固定工程完成一次导出，记录进度不倒退；另取消一次并确认进程结束、目标文件保留。 | `artifacts/validation/ui-craft-v1/export-panel-final-result.json` 对应本轮未改动的导出引擎取消路径，验证取消进度单调、保留已有目标文件字节、清除临时文件；同一工程重试完成时进度单调到100%且成片完整解码。当前实机 `artifacts/validation/noncamera-closeout-v2/export-cancel-check.json` 另验证4K任务在50.6%取消后无输出/临时文件并返回编辑器；本次取消未专门使用已有目标文件，保留目标的判断继承自未变引擎的旧精确场景。 | 通过 |
| FT-32 | 外观、预览质量、录制和项目偏好 | 输入无效录制目录并检查就地提示/焦点；修正后保存、重启核对偏好，并确认外观设置不改变导出像素。 | `settings-empty-error-dark.jpg` 与 `settings-corrected-dark.jpg` 实机记录空目录就地提示及改值后提示清除；`settings-saved.json`、`settings-restarted-tree.txt` 和 `settings-after-restart.json` 证明保存的中文目录及设置值在重启后保留。`main-media-coverage/result.json`（113项）、`text-menu-command/result.json` 为已有验证。新增 `artifacts/validation/noncamera-closeout-v4/preferences-export/result.json` 经真实 `ApplyUserPreferences` 路径分别应用 Light/960p/30 与 Dark/1920p/60，确认 App Surface 资源及预览选项值切换；两种偏好各导出并解码30帧MP4，逐帧 framemd5 一致，工程设置和用户 `settings.json` 未被改写，探针窗口无 HWND。该有限场景由实机设置页与重启证据、离屏真实偏好应用和成片像素对照共同覆盖。 | 通过 |
| FT-33 | 关于、版本更新、源码和反馈 | 打开关于页核对版本/许可证/源码入口；检查本地更新说明和反馈入口文案。 | 本地入口已有；在线更新和公开反馈服务没有实现且需发行渠道，该服务延期。 | 延期 |

## 销账

| 状态 | 数量 | 含义 |
|---|---:|---|
| 通过 | 26项 | 仅台账定义的有限场景有证据，不等同于全部功能或全硬件覆盖。此数是证据销账数，不是功能迁移完成率。 |
| 待修 | 0项 | 台账内暂未另列待修状态。短区间末端帧边界已在源码测试候选修复并通过验证，但尚未更新最终交付包，见 FT-08 与收尾计划。 |
| 待验 | 4项 | 场景有限且可执行，缺少覆盖证据或受相关实现变化影响；包括录制与来源交互。 |
| 缺资源 | 0/33 | 当前把本阶段主动延后的摄像头条件记为“延期”；有资源后再评估能否执行。 |
| 延期 | 3/33 | 两行摄像头实机条件及一行在线更新/公开反馈服务。仍计入33项，均不算通过。 |

历史发现与当前结论：真实 ExportEngine 的短区间探针曾记录381帧送入编码器、380帧实际解码。源码修复后，`artifacts/validation/fractional-frame-fix/result.json` 验证 30fps、12.6759265 秒输出381帧（包括末帧绿色色样）及 60fps、1.017 秒输出62帧；有限音轨长度检查通过，Core tests 与 Integration 均通过（`fractional-frame-fix-core-tests.log`、`fractional-frame-fix-integration/result.json`）。当前源码测试候选已覆盖该边界，最终 0.2.7 交付包尚未更新。FT-29 固定整24秒场景原通过状态保留。当前待修计数为0不代表未发生过问题或最终包已包含源码修复。

最短闭环优先清单：

1. 将短区间尾帧源码修复纳入下一交付包并按包验证；再补 FT-03/04/06/07 各自的固定录制/来源交互证据。FT-04 已发现来源菜单列出自身不可录浮窗，修复后仍在原验收项内复验，不扩计数。FT-08 已按本行有限场景通过；摄像头行和在线服务条件保持延期。
