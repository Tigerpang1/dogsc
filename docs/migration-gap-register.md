# 0.2.4开发候选迁移差距与验收分母

盘点时点：2026-10-10。发行包记录对应0.2.4 commit `1a13291115536787f01ceb65ee99efcd64b4d166`。源码包重建、便携包内容、便携/安装工作流及桌面UIA均已通过本机验收；结果见 `artifacts/validation/package-0.2.4/`。100%旧包Smoke工作区适配20/20通过（harness移动测试窗，不代表产品主窗启动定位）；200%新开发快照的只读启动定位20/20通过，旧0.2.4包负对照仍显示未适配，修复尚未进入该发行包。完整DPI交互/多显示器矩阵未完成，1800秒音画长测按下文记录采集器版本和分析结果边界。本文件区分代码、测试与设备证据，不据此宣称达到95%源功能覆盖。

## 可审计分母

`parity-checklist.md` 的“原功能”主表按一个用户任务一行，共35行；其中“上游捐赠入口”标为不适用，因为Windows产品不复用原作者品牌、收款码或账户。因此当前源功能覆盖分母为34项。每项等权，只有该行所有明确用户行为均有当前候选的验收证据才计为完成；按这一口径至少需完成33/34项，才达到95%门槛。行数可逐行复核；它不是按钮数或代码类数。

等权口径下达到95%至少要完成33/34项；未完整通过的复合行按未完成处理，不按子按钮或测试次数拆分加分。当前仍没有逐行证据支持33项完整完成，因此不报告完成百分比。0.2.4发行包全量UIA43/43、浮层19/19；包内编辑53、导出15、录制条虚拟生命周期5和窗口状态截图12项通过。100%旧包工作区适配通过；200%只读启动定位在新开发快照20/20通过，但旧0.2.4包没有该修复，且这不是0.2.5发行验收。完整DPI交互、真实硬件、真实DogSC工程和部分长时/设备边界仍待验证。合成媒体不能代替物理设备或真实Mac项目。

分母来源：[`parity-checklist.md`](parity-checklist.md) 主表；上游提交固定为 `e207d11b111c5e58152238fb64115b2009850547`，本机 `upstream/dogsc` 的 `HEAD` 与其相同。原功能依据为 [`upstream/dogsc/README.md`](../upstream/dogsc/README.md)、`Sources/RecorderCore` 项目/场景/时间线实现、`Sources/DogSCApp` 页面实现及 README 引用的四张截图 `docs/images/{editor,recorder,motion,export}.png`。

## 当前差距

| 优先级 | 功能任务 | 当前证据和边界 | 尚需完成的验收 |
|---|---|---|---|
| P0 | QPC采集的连续音画同步与复位边界 | 0.2.4固定候选30秒实录18/18：raw P95 35.22095ms、最大38.054ms；保存重开并导出M4A亦18/18：raw P95 32.91695ms、最大40ms。1800秒采集及冻结v3离线分析已完成：1070/1080名义cue（99.0741%），双向覆盖99.9066%，span 1797.8425秒；raw P95 74.4448ms、max 96.1118ms，映射P95 38.333ms、max 56.667ms。分段raw P95为44.445/61.445/81.112ms，映射P95为38.333/38.333/40ms；检测到1071个flash、1070个tone并匹配1070对；一个80.316667秒视频flash未匹配tone，另有9个名义视频事件未检。采集使用capture v6冻结程序集；0.2.4新增timestamp-errorgap可靠性条件本轮未触发，因此不是最终包逐字节录制的长测。旧v2分析失败仍作为历史保留。音频复位边界纯逻辑测试通过；麦克风v3只证明本机一次15秒采集与暂停恢复。详见 [`capture-validation.md`](capture-validation.md)。 | 若把严格同包版本作为门槛，使用最终候选补做1800秒或等价可靠性验证；另覆盖物理设备复位、热插拔和更长运行。短测、fixture偏差估计与旧版本结果均不能替代。 |
| P1 | 窗口目标捕获矩阵和跨显示器边界 | `wgc-first-frame-matrix-v1/matrix-summary.json` 与包快照 `window-wgc-package-024-v4/target.target.json` 通过同工具链首帧对照、目标窗口遮挡/移动/缩放、关闭/最小化收尾及区域录制回归。`capture-package-024-snapshot-v3/snapshot-provenance.json` 对应0.2.4 commit和包内Capture/Probe/ScreenRecorderLib哈希。早期v1黑帧探针未声明PerMonitorV2，物理测试坐标被DPI虚拟化。见 [`capture-validation.md`](capture-validation.md)。 | 在第二显示器和混合DPI显示器验证窗口捕获及源窗口边界；当前产品目标已覆盖本机单显示器指定窗口。 |
| P0 | 发行包完整交互验收 | `artifacts/validation/package-0.2.4/result.json` 状态passed：477个便携包文件核验、隔离PATH播放/暂停、便携版真实录制暂停编辑重开导出、per-user安装播放、卸载清理及偏好逐字节恢复均通过。`content-result.json`通过；`source-validation.json`源码归档测试、App Release构建和无FFmpeg打包均exit0，源码提交为`1a13291115536787f01ceb65ee99efcd64b4d166`。 | 本机发行包闭环已通过。干净虚拟机、其它Windows版本及外部机器安装仍未验收；新v3分析器和Smoke fitworkarea修正不在0.2.4 source zip中。 |
| P0 | 候选全量桌面操作 | [`ui-full-package-024-v1.json`](../artifacts/validation/ui-full-package-024-v1.json) 为43/43，`status=passed`、`complete=true`；包含打开项目、编辑撤销、预设、设置、时间线、保存重开和退出。[`ui-transient-package-024-v1.json`](../artifacts/validation/ui-transient-package-024-v1.json) 为19/19，覆盖浅/深主题下拉、嵌套菜单、提示和对话框。包内附加回归及12张窗口状态图见 [`package-024-extra-v1/result.json`](../artifacts/validation/package-024-extra-v1/result.json)。 | 100%旧包工作区适配测试通过；200%新开发快照只读启动定位20/20通过，旧0.2.4负对照失败且该修复未进入0.2.4发行包。完整多DPI交互、混合DPI和多显示器仍待完成；150%发行包通过不外推至这些环境。 |
| P1 | 物理摄像头、麦克风和多显示器 | 0.2.4包对应Capture快照的麦克风实录 `microphone-package-024-v1/microphone-capture.summary.json`：15.0186秒墙钟，暂停2.0104秒，WAV 13.0083秒/624400帧、FrameDifference=0、2个时钟段。峰值3.89e-5、RMS 1.31e-6；仅证明数值采集和暂停映射，不证明语音质量。系统loopback短测和长测另见P0。没有物理摄像头、第二显示器或多显示器覆盖。 | 在有设备的机器上验证摄像头真实录制、错误收尾及跨轨同步；对麦克风补语音内容、热插拔及更多端点验证；按多显示器及DPI覆盖。此项属于“已实现待设备验收”，不是缺少代码。 |
| P1 | DogSC项目互操作 | `DogScProjectImporter` 按schema 23–27只读导入。0.2.4的`final-024-camera-import.log`已通过合成160×90相机素材探测、16:9比例映射、保存重开及目标文件锁失败传播；这证明合成导入链路，不是实际Mac项目验收。新工程为 `.frametrail`，不支持把编辑结果无损回写为 `.dogscproject`。 | 用真实Mac项目验证字段映射、媒体复制和损失报告；逐项记录不支持或近似字段。需要双向互通时另行实现并测试DogSC格式写出。 |
| P1 | 时间线全量边界与长素材 | `ui-full-surfaces-net10-v5.json` 在该候选合成工程上43/43通过，时间线拖动、区间、分割/撤销等全流程通过。`timeline-assets-20261009-190531-715/result.json` 20项通过，覆盖8秒素材缩略图、音频波形、无音轨、取消与解码结果；另以1800.02秒1080p60本机合成视频测8张缩略图和1000列波形，见 `timeline-assets-performance/comparison.json`。该比较是单次顺序观察，缓存未控制且内存统计不含FFmpeg子进程。 | 保留长素材真实用户工程、长时 seek 与 UI滚动/缓存回收的边界验收；不能把单个30分钟合成素材样本外推到任意编码器、超长素材或所有系统内存。 |
| P1 | 多轨范围导出与辅助音轨映射 | 未打包0.2.5开发快照的 [`range-audio-export-v1/result.json`](../artifacts/validation/range-audio-export-v1/result.json) 实际调用ExportEngine导出范围MP4/M4A：跨片段切口、不同速率与重排后检查203帧/6.766667秒视频、6.75秒音频及四个时刻的画面/音调；两条独立音轨覆盖SyncSegments、SourceRate和分段静音。标准集成 [`integration-after-range-v1/result.json`](../artifacts/validation/integration-after-range-v1/result.json) 也通过（117帧、3.9秒M4A、4组预览/导出对照）。 | 证据覆盖合成项目及指定轨道组合，不替代桌面UI任意选择范围、多轨重叠/增益切换和真实录制工程边界验收；这些仍按实际产品路径补测。 |
| P1 | 文件锁下保存、撤销与重做一致性 | 未打包0.2.5开发快照 [`persistence-fixed-v1/result.json`](../artifacts/validation/persistence-fixed-v1/result.json) 通过12项真实文件锁回归：自动保存、撤销、重做遇锁时失败报告、内存/磁盘/历史一致、解除占用后重试成功、重开保留身份和素材哈希。修复前 [`persistence-negative-v2/result.json`](../artifacts/validation/persistence-negative-v2/result.json) 捕获撤销保存失败时内存历史先变更的问题；该缺陷已修复并测试，尚未纳入0.2.4发行包。 | 后续发行候选需重新运行文件锁回归；当前证据只覆盖本机Windows文件占用场景，不外推到网络盘或第三方同步过滤驱动。 |
| P1 | 导出进度与取消事务 | 未打包0.2.5开发快照 [`export-progress-025-v2/result.json`](../artifacts/validation/export-progress-025-v2/result.json) 及 [`driver-result.json`](../artifacts/validation/export-progress-025-v2/driver-result.json) 通过21项实际WPF面板验收：进度阶段单调至1、取消时不覆盖已有目标且无临时文件、同一面板重试成功、完整FFmpeg解码验证及窗口关闭等待清理。 | 已覆盖单机实际导出与取消；剩余时间估算精度、长导出过程的完整交互以及发行包/其它环境仍需验收。 |
| P1 | 整体视觉品质与交互一致性 | 0.2.4发行包全量UIA43/43、浮层19/19；另有导出15项、编辑53项、虚拟浮条生命周期5项及12张窗口状态图。单显示器150%缩放发行包测试通过；100%旧包工作区适配通过，200%新开发快照只读启动定位通过，但该修复未进入0.2.4且新快照不是0.2.5发行包。完整多DPI交互、多显示器、高对比系统模式和DogSC逐像素外观仍未验收。见 [`visual-spec.md`](visual-spec.md)。 | 在常见窗口尺寸及多DPI显示器下对照层级、字体、间距、状态反馈、动效连续性与取消返回。Windows可采用合适的原生交互，不以逐像素差异作为失败。 |
| P2 | 在线更新和公开反馈服务 | 原README通过Sparkle更新和GitHub问题入口。FrameTrail关于页明确说明当前未配置在线更新或公开反馈后台，仅给手动发行渠道说明；相关入口实现不存在自动更新服务或反馈提交。 | 若作为产品承诺，需要项目维护者指定发行渠道/反馈服务后再实现；在此之前将其标记为明确未实现，不复用上游凭据或账号。 |
| P2 | 原生DogSC格式写回 | 上游项目可保存并重开 `.dogscproject`。Windows导入器只读转换为自有 `.frametrail`；没有DogSC格式导出/无损写回实现。 | 如要兼容Mac继续编辑，需单独实现格式写出并通过真实工程往返对照；当前不属于已迁移能力。 |
| P2 | FFmpeg公开分发合规 | 本机验证构建使用Gyan GPLv3 full build。打包材料记录了对应源码与构建配置，但所有静态依赖的对应源码、补丁和完整许可证链尚未核实；文档明确暂不公开发布。 | 完成实际二进制全部依赖、源码、补丁和许可证对应核验，随后才评估公开分发。此项是发行条件，不等于媒体导出功能未实现。 |

## 不能按“缺实现”处理的项目

录制期间继续编辑旧工程并排除编辑器、录制条和备忘录已有0.2.5开发快照实测，见 `artifacts/validation/recording-isolation-v4/recording-isolation-smoke.json` 与 `driver-result.json`：主显示器DDA基线确认三窗可见，排除后采样点回到受控背景色；旧隔离工程保存重开后修改保留且原媒体哈希不变，driver退出码0并恢复偏好。此证据只覆盖150%单显示器、测试自有Topmost背景布局和主显示器DDA，不是正式发行包或窗口/区域录制验收。v1设置错误、v2/v3窗口被遮挡的失败记录仍保留，v4才是有效产品验收。

物理摄像头、多显示器、混合DPI和其它GPU/编码器组合仍缺设备覆盖。麦克风已有15秒含暂停实录；音画30秒与工程映射导出均18/18。1800秒音画实录及v3分析已完成，但采集使用capture v6冻结程序集，最终0.2.4新增可靠性条件未触发。语音质量、热插拔和同版本复验仍有边界。自动更新服务、公开反馈后台、DogSC格式写回是明确未实现项。上游捐赠入口被有意移除。窗口目标WGC在当前指定目标场景通过；未覆盖边界见P1条目。

## 当前验证记录边界

`editor-polish-final8.json` 的53/53是应用内部编辑回归；`ui-shortcuts-polish-final10.json` 的12/12在App final7上；`export-panel-polish-final8.json` 的12/12是实际控件触发ExportEngine的内部回归。它们分别证明相应旧候选上的行为，不自动覆盖发行包。媒体探针的合成测试与此前构建也不能取代当前包测试。

0.2.4包当前桌面UI证据为全量43/43、浮层19/19；包内导出15项、编辑53项、浮条虚拟生命周期5项及12张状态图通过。早期v2/v3报告及0.2.2/0.2.3候选数据均作为历史保留，不替代当前包。单显示器150%缩放结果不代表多DPI覆盖。另有100%旧包工作区适配测试通过；200%只读启动定位见新开发快照报告，不属于0.2.4包。

0.2.4包与源码归档验收结果见 `artifacts/validation/package-0.2.4/`；候选manifest为 `artifacts/release/FrameTrail-0.2.4-manifest.json`。v3分析器及Smoke work-area修正未纳入该源码归档。功能目标按34项分母至少33项完整通过；现有记录不支持宣称达到95%。真实Mac项目、物理摄像头、DPI/多显示器和采集版本边界仍需分别验收。

## 建议的剩余工作顺序

1. 核对1800秒音画v3报告并审阅采集版本边界；必要时以最终候选补做同版本采集。
2. 在第二显示器和混合DPI环境复核WGC窗口路径，并验收其它窗口目标。
3. 在100%/200%DPI补齐完整交互与窗口缩放边界验收；另以真实Mac项目及物理摄像头补齐相应设备边界。100%旧包工作区适配已通过，200%只读启动定位仅在未发行的新开发快照通过。
4. 在有物理摄像头/麦克风和多显示器的Windows设备上做录制、错误收尾和跨轨同步；用真实Mac `.dogscproject` 核验导入映射及损失报告。
5. 按视觉品质合同在多DPI环境验收主窗口、检查器、录制条、弹出菜单、下拉框和进度浮层的可读性、键盘焦点、取消返回；不以静态像素差异判失败。
