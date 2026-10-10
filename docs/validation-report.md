# FrameTrail 验证报告

## 当前发行候选：0.2.4（源码提交 `1a13291115536787f01ceb65ee99efcd64b4d166`）

0.2.4便携包、安装/卸载、包内真实录制暂停编辑重开导出和源码归档闭环通过。`artifacts/validation/package-0.2.4/result.json` 全部7项通过，包内清单核验477个文件；源码包验证和内容核验见同目录 `source-validation.json`、`content-result.json`。该发行包对应的源码归档固定在上述提交，不包含其后的验证文档更新，也不包含后来在独立开发快照中验证的200%初始窗口定位修复。

当前桌面证据包括 `ui-full-package-024-v1.json` 43/43、`ui-transient-package-024-v1.json` 19/19、导出面板15项（`export-panel-surfaces-net10-v5.json`）、包内应用编辑回归53项与录制栏生命周期5项（`package-024-extra-v1/result.json`）。100%旧包Smoke工作区适配20/20通过；该项由harness移动测试窗，不验证产品主窗启动定位。200%旧0.2.4包负对照 `ui-startup-package-024-dpi200-negative-v1.json` 如预期记录初始窗口超出工作区；新开发快照 `ui-startup-placement-dpi200-v2.json`（应用快照目录为 `window-placement-app-v2/`）为20/20，且只读检查未调用 `SetWindowPos`：DPI=192 (200%)，窗口边界 `[32,32..3808,2032]`，工作区 `[0,0..3840,2064]`。该快照不是0.2.5发行包，不能把修复归到0.2.4包或视作发行验收。

## 0.2.5 开发快照（未打包）

辅助素材重定位专项尚未取得有效通过结果。`ui-auxiliary-relink-v8.json` 记录 Windows 拒绝文件选择模态的一次性前台激活，输入守卫在输入前中止；此前的 UIA 模态查询出现缓存和瞬态缺失，不能以“对话框消失”证明选择成功。测试现改为验证工程内副本的精确SHA256与解码，并提供等待精确模态前台的入口。需要一次真实焦点交接后续验，当前不将其计入素材重定位通过范围。

本候选还修复了撤销时内存与磁盘项目状态可能分离的问题。artifacts/validation/persistence-negative-v2/result.json 保留旧行为的失败复现；修复后 artifacts/validation/persistence-fixed-v1/result.json 的12项真实文件锁检查通过，覆盖普通保存、撤销/重做写入失败后的状态与历史一致性、解锁重试和保存重开，并核对测试素材未变。Core测试通过。范围音频导出探针 artifacts/validation/range-audio-export-v1/result.json 与标准合成媒体集成 artifacts/validation/integration-after-range-v1/result.json 通过，分别验证范围裁切、多段音轨与视频导出。导出面板新回归 artifacts/validation/export-progress-025-v2/result.json 为21项通过，包含真实进度采样、取消时保留既有目标文件、同窗重试、完整解码及临时文件清理；取消前进度采样4个值，范围0.055–0.059，阶段为“合成与编码”；重试采样146个值，范围0.055–1.000，覆盖合成编码、媒体核验、完成和导出完成阶段。driver-result.json 记录 PreferencesRestored=true、退出码0；该运行应用SHA256为3B8DEA573CB43057BF7D5E40089F88187F454D819FFC58D64AAFE75CAF57C4D3。此证据来自合成工程与当前开发快照，不代表0.2.5发行包验证；发行包尚未生成。

高DPI主窗启动定位修复已在新开发快照验证。旧0.2.4包的200%只读负对照失败；修复版200%定位20/20通过，详见 `artifacts/validation/ui-startup-placement-dpi200-v2.json`。在150%单显示器环境，`recording-isolation-app-v4` 快照的 `artifacts/validation/ui-full-placement-dpi150-v1.json` 为44/44、`status=passed`、`complete=true`；启动时窗口边界 `[480,107..3360,1981]` 位于工作区 `[0,0..3840,2088]`。后续备忘录重启步骤仍由harness定位窗口，不能据此声称全程不调整窗口。`artifacts/validation/visual-025-offscreen-v1/result.json` 的离屏视觉探针19项通过。0.2.5发行包尚未生成，本节不代表安装、卸载或发行包验收。

0.2.4精确发行包麦克风探针 `artifacts/validation/microphone-package-024-v1/microphone-capture.summary.json` 完成15秒录制，其中请求暂停约2秒；WAV有效时长13.008秒、48kHz、624400帧、两个活动时钟段，数值样本非零。报告明确没有对声音内容或语音质量分类。200%窗口排除报告 `artifacts/validation/exclusion-package-024-dpi200-v1/exclusion.exclusion.json` 记录WDA affinity 0x11成功设置和回读；中心像素在DDA中从红色变暗，在gdigrab中也从红色变暗。这只说明本机窗口在这两条受测捕获路径中被排除，不是安全边界。

正确ROI的30分钟视听长测及真实回工程导出分析见 `artifacts/validation/capture-aligned-audio-clock-1800s-v1/offline-analysis-source-rate-v3/validation-summary.json`。v3固定tone检测规则在同一长测原始素材上检测到1071个视频flash、1070个tone并匹配1070对；其中80.316667秒视频flash没有匹配tone，另有9个名义视频事件未检。名义cue匹配率为1070/1080（99.0741%），双向覆盖99.907%，事件跨度1797.843秒，raw P95绝对偏差74.445ms、最大96.112ms；按产品SourceRate映射并经ExportEngine导出的工程成片匹配1070/1080，P95 38.333ms、最大56.667ms。fixture测得的−15ms仅作为检测链基线单列，未从raw或mapped数据扣除。该运行使用Capture.Probe v6快照；TimestampFallbackPackets和TimestampErrorPackets均为0，但它不含后续新增的timestamp-error gap门禁，不能替代最终包版本的全长设备验收。音频SILENT标志比例93.335%，事件检测通过不证明其他音频内容完整。

以下按标题保留0.2.3及更早候选的历史证据；旧的“当前候选”结论仅适用于对应版本，不描述0.2.4。

## 0.2.3 历史候选验证（2026-10-09）

仓库和打包脚本当时处于0.2.3候选。该候选的发行包运行验收尚未完成；以下旧版本测试记录不自动继承到0.2.4。

统一后台回归 `artifacts/validation/net10-023-background-regression-v2.log` exit 0：Core、DogSC导入、Render/GPU、时间线资产20项、逐片段音量、媒体Integration、VisualProbe、7项Capture纯逻辑检查、发行清单16项及源码身份6项均通过；其中媒体与导入为合成/fixture验证，Capture不代表设备实测。WPF离屏主题探针 `artifacts/validation/theme-offscreen-v4/result.json` 与 `theme-offscreen-8e71d3521e604702895b18144e61c0a4/result.json` 均为15/15；当时MainWindow未Show且没有HWND。v6浮层专项为19/19；v5全量43/43和导出15/15属于当时候选。

浮层主题继承修正的 `ui-transient-surfaces-net10-v6.json` 为19/19通过，截图目录为 `artifacts/validation/ui-transient-surfaces-net10-v6.surfaces-719540a267d04fe0b444020240dcf30c/`。新时间线高对比绘制已随App v7构建通过（0警告、0错误），离屏像素探针15/15并保存PNG；尚未取得新版实机主窗口截图。对比度计算不能替代实际显示检查。

窗口捕获旧探针需按目标进程和测试条件区分。0.2.3历史跨进程样本曾出现暗帧，同进程样本也曾出现亮帧，条件不完全相同，不能据此判定进程边界是原因。0.2.4包内 `window-wgc-package-024-v4/target.target.json` 对自有窗口的移动、调整大小、遮挡、最小化和关闭收尾完成专项验证；该证据限定于当前Windows主机、单显示器与测试窗口。

## 0.2.2 历史候选验证（2026-10-09）

`polish-app-build-final8.log` 为0警告、0错误。`polish-tests-v3.log` 中Core、DogSC只读导入、完整GPU/媒体探针、逐片段音量、普通集成及1080p60同步集成均通过；图片LRU测试真实执行A/B/A/C驱逐、超预算素材直用、驱逐后重解码像素一致、Windows路径大小写，以及标题大小写/分隔符身份检查。小预算探针验证8192字节图片及110000字节文字预算，生产默认每个素材加载器256MiB，不能解释为整个应用内存峰值。五类效果分割检查非法切点不污染项目、独立ID/样式副本、保持期接缝及摄像头布局字段保留。

本轮 Net10 候选验证更新为 `surfaces-net10-v5`：`ui-full-surfaces-net10-v5.json` 43/43通过；`ui-transient-surfaces-net10-v5.json` 19/19通过，人工查看确认浅/深主题下拉与嵌套子菜单截图包含稳定完整的弹层；`export-panel-surfaces-net10-v5.json` 15/15通过。`recordingbar-lifecycle-net10-v4.json` 的5项是实际WPF窗口与虚拟停止回调的生命周期回归，不是设备录制。全量UI运行在本机单一150%缩放显示器环境，不等于多显示器、多DPI验证。

当前QPC实现的30秒区域音画探针 `capture-audio-clock-appsync-30s-v1/validation-summary.json` 检出18/18个cue。原始音画偏差P95绝对值54.736ms；以同一分析器对3个fixture cue测得的−35ms中位数作修正后，估计P95绝对值21.885ms。修正值是fixture校正估计，不是物理时钟校准；该短测不能替代30分钟长测。

0.2.3历史探针曾记录跨进程样本黑帧：v6 `artifacts/validation/window-wgc-product-probe-v6/window-wgc-target.target.json` 首帧预览9/9采样亮度为0，编码后MP4在遮挡前、期间和窗口移动缩放后9/9为 `#000000`；同进程探针则记录亮帧。0.2.4包内窗口目标专项见本报告当前候选段落；旧测试条件不完全相同，不能用来概括所有WGC路径。

后台收尾的 `polish-app-build-release-candidate.log` 再次为0警告0错误；`polish-release-product-hashes.json` 确认本次重建的App/Core/Capture/Media四个产品DLL与final8逐一SHA256相同。`polish-capture-pure-final4.log` 的Core、导入器和六项采集纯逻辑测试通过，`polish-desktop-guard-final.log` 的模拟桌面守卫测试通过；这些检查没有启动GUI、播放音频或采集设备。发行脚本的临时ZIP对照测试确认同长度异内容、缺文件与重复条目均被拒绝。

`editor-polish-final8.json` 为53/53通过。`export-panel-polish-final8.json` 为12/12通过：直接进入M4A模式，真实生成纯音频且时长正确；不可用输出目录失败后恢复按钮、原占位文件不变；真实编码进度出现后取消、控件锁定、清理临时文件；同一面板再次导出MP4并探测流和时长。诊断调用实际WPF按钮和真实ExportEngine，没有用假进度替代导出。`ui-shortcuts-polish-final10.json` 为12/12通过，使用App final7，覆盖鼠标定位、快捷键及主窗口自然关闭。

Net10候选 `ui-full-surfaces-net10-v5.json` 为43/43，`passed=true`、`complete=true`；独立 `ui-transient-surfaces-net10-v5.json` 为19/19，人工查看确认嵌套子菜单和稳定下拉截图完整；`export-panel-surfaces-net10-v5.json` 为15/15。此前v2/v3浮层截图缺陷和较早 `export-panel-surfaces-net10-v1.json` 均保留为历史候选结果，不替代v5。全量UI流程覆盖合成工程打开、面板编辑、设置、预设、时间线操作、保存关闭重开及正常退出。
0.2.3候选另有v6浮层主题继承结果19/19及时间线离屏像素验收；这些是历史版本证据。0.2.4桌面UIA与高DPI浮层专项结果见本报告开头。

0.2.3历史音频包时钟纯测试、短测和队列测试见 `capture-validation.md`。0.2.4长测v3和工程映射结果见当前候选段落；更早的到达时刻音轨长测不用于证明当前QPC实现。

53项编辑回归覆盖效果分割、主片段按播放头分割、重排/变速后Q/W映射，以及屏幕和相机运动裁短后的返回时长上限。快捷键专项包含中文IME的`ImeProcessedKey`修复和撤销后的效果选择恢复。早期专项保留失败报告：测试曾未定位播放头或未滚到标尺；五帧键的测试注入修正为带左右修饰键扫描码的输入。

参考工程11页和原创填充样例12页分别保存于 `visual-polish-final4/`、`visual-polish-populated-final4/`，后一组实际展示开启的开场、摄像头、选中缩放和贴图参数。截图范围及不同构图时间见 `visual-spec.md`。

历史 `ui-full-polish-final10` 在7/32后由前台保护中断；final9曾出现多实例守卫误判，已修正。更早的 `ui-full-polish-final3.json` 42/42及final7失败日志属于历史证据。0.2.4全量桌面UIA为43/43，详见当前候选段落。

较早 `surfaces-app-v2` 候选全量桌面报告 `artifacts/validation/ui-full-surfaces-v2.json` 为41/43；v2全量UIA曾通过43/43，v3浮层截图存在覆盖缺陷。当前v5全量UIA为43/43、浮层专项为19/19且经人工检查截图完整；旧报告仅保留为版本历史，不覆盖v5结果。

当前0.2.2便携包ContentOnly报告验证535个包文件及ZIP内容一致，源码归档报告在无Git源码ZIP中完成测试、App构建和无FFmpeg/跳过安装器打包。它们没有执行包内GUI、录制或安装器。报告位于 `artifacts/validation/package-0.2.2/content-result.json` 与 `source-validation.json`。

较早QPC候选v8的30分钟同步试验曾只匹配485/1080个名义事件，音频后段为全零；这是历史结果，不能代表当前短测。历史20秒交互短测收到2020个包、969600帧，均为SILENT；只记录为该次刺激缺失诊断，不代表当前30秒短测状态。后续30秒受控App同步样本检出18/18 cue，raw P95绝对偏差54.736ms；fixture校正估计P95为21.885ms，但fixture只含3个cue，不能作为物理时钟校准或长时结论。系统默认耳机端点未静音；另一次5秒ffplay会话诊断观察到该进程音频会话`Mute=true`、`Volume=0.0`（36次采样）。v8没有会话状态记录，不能据此追溯归因。诊断文件为 `artifacts/validation/loopback-session-diagnostic-20261009-170820-retry/audio-session-diagnostic.json`；20秒采集媒体、汇总、音频分桶和事件表位于 `artifacts/validation/capture-qpc-interactive-v2/run20s/`，文件前缀为 `qpc-interactive-20s-v2`。

## 0.2.1 新增验证（2026-10-09）

最终候选final19构建为0警告0错误，`all-port-final19.log` 中 Core、DogSC 导入、完整 Render.Probe、逐片段音量与辅助时钟探针、普通媒体集成和1080p60同步集成均通过。这组验证包含Spotlight、源区域投影往返、显式相机圆角、分段音量、分段媒体速率，以及慢放、自动镜头、自定义曲线、开场、光标和贴图样式。

final19的新增媒体证据为 `camera-clock-media-20261009-114914/result.json`（2×源时钟和重排/变速后，预览及MP4依次显示绿、红、黄，3.5秒105帧）；`overlay-export-20261009-114909/result.json`（Spotlight、贴图样式与背景处理）；`audio-overrides-20261009-054919/audio-overrides.summary.json`（分段音量、静音、分段时钟及真实M4A）。音频首段两路幅度比均约0.49976，静音泄漏约2.3e-8。独立非零偏移音轨测试也检查3.000秒总长、前导1秒与尾部0.5秒静音，以及两个速率区间约0.4997的音量比，防止后段提前叠入首段。普通集成报告为 `synthetic-testsrc2-20261009-114924-057/result.json`；同步报告为 `sync-1080p60-20261009-114932-409/result.json`，最大音画偏差20ms。

桌面新增控件专项 `ui-advanced-final17-audit.json`、`ui-spotlight-audio-final17-audit.json` 均为5/5通过，覆盖ZoomFocus、摄像头动画和基础样式、贴图边框/阴影/背景柔化/隐藏、Spotlight、所选片段音量及保存重开。`motion-curves-ui-final15.json` 全项通过，覆盖曲线、弹簧、开场、光标和慢放。final19在此后修正了相机大于50%的圆角上限和音频混音滤镜，两者均在final19媒体测试中重新验证。桌面完整流程和应用内部回归见下方，不把一项自动化点击失败计作应用内部回归失败，也不把后续专项结果改写成全量42项通过。

final15媒体产物为 `overlay-export-20261009-112311`、`camera-media-20261009-112313`、`synthetic-testsrc2-20261009-112320-646` 和 `sync-1080p60-20261009-112327-899`；均有成功报告。普通集成四帧比较PSNR为23.68–25.17dB、MAE为3.94–5.27，1080p60/360帧耗时1.653秒；同步样本最大20ms。

| 新增范围 | 结果及本地证据 |
|---|---|
| 真实采集色彩与光标形态 | `cursor-shapes-v5/cursor-shapes.summary.json`：DDA/NVENC 440×240、30fps、73帧；七色最大误差2/255，门槛8/255；同点箭头、手形、文本切换均通过。 |
| 慢放与色彩往返 | `color-slow-20261009-111836/result.json`：预览最大2/255、软件/硬件编码再解码最大4/255；0.25×/0.5×/2×重排输出6.25秒、375帧和同长M4A；音调保持与源时间映射通过。 |
| 相机真实素材链路 | `camera-media-20261009-111841/result.json`：无比例字段素材探测为16:9；960×540导出中相机绿区432×242；相机阴影采样蓝通道146，对照无阴影255；2560×1440全屏相机中心和角点及30帧均通过。此项使用合成相机素材，不是物理相机实采。 |
| 贴图完整媒体链路 | `overlay-export-20261009-111839/result.json`：边框、投影、摄像头之上的叠放、隐藏底图/摄像头在共享预览和真实MP4解码中均通过；36帧。独立像素探针验证背景与相机分开高斯柔化、贴图保持清晰、下一帧渲染状态恢复。 |
| 新检查器桌面操作 | `motion-curves-ui-final12-locator1.json`：11/11，包括三类自定义曲线、共享弹簧、开场预设与节奏、点击参数、0.5×慢放和保存重开。高级样式及最终全量UIA另行记录。 |
| 普通媒体集成 | `synthetic-testsrc2-20261009-111922-285/result.json`：117帧/3.9秒MP4及M4A；四个预览/导出比较PSNR 23.58–25.20dB、MAE 3.92–5.38；1080p60/360帧输出耗时1.703秒。 |
| 音画同步合成样本 | `sync-1080p60-20261009-111848-579`：1080p60、300帧，三处闪帧与音频脉冲最大偏差20ms。 |

以下保留0.2.0的硬件和交付基线；本轮没有重做这些30分钟测试，不把旧硬件证据视为新增设备覆盖。

## 0.2.0 历史基线

验证日期：2026-10-09。当时结论：Core、GPU合成、实时预览、主要编辑流程和数项真实录制闭环已有本机自动与桌面验收；单机长时视频采集、WASAPI测量及正确区域视听长测达到各自记录的阈值。其他硬件/音源组合、干净环境和最终发行包仍待验收。此段为0.2.0历史基线。

## 验证环境

Windows 11 Pro x64（`10.0.26200`），AMD Ryzen 7 9850X3D、NVIDIA GeForce RTX 5070 Ti，主显示器3840×2160，.NET SDK 8.0.303、Vortice 3.8.3、FFmpeg 8.1.1。测试使用硬件 D3D11、NVENC 及本机 Gyan FFmpeg；这不是可直接再分发的发行依赖组合。未在干净虚拟机或其他显卡上验证。

## 自动与桌面验证

| 范围 | 结果与证据 |
|---|---|
| Core 时间线和持久化 | `dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj -c Release` 通过。覆盖分割、裁短、变速、重排、重复/不连续源区间映射、鼠标时钟校准、自动缩放、项目备份恢复、路径安全，全源鼠标焦点到 `Canvas.Crop` 的转换，以及摄像头暂停分段、重排映射、分段裁切/滑移和备份恢复序列化。RecordingTimelineMapper覆盖首帧偏移、启动延迟、三次暂停及重叠暂停合并、首尾裁切和空交集。新增 `RecordingMarkerTimelineProbe` 组合验证：暂停压缩后的源时间标记经过裁短、分割、两段不同速率、重排和保存重开后保留ID/名称/源时间，映射到预期输出跳转点，裁掉的标记无跳转目标；这不验证采集端暂停时钟压缩本身。源视频帧率元数据要求有限且 `0 < fps ≤ 240`，旧项目缺字段时读取为30fps。 |
| 动画 | 同一 Core 测试通过相邻和重叠 zoom、ScreenMotion、CameraMotion 接缝；核对 Cubic、Quintic 和默认 Spring 数值样本，并检查重排后的目标源时间。Opening的8种预设均检查初始画面、元素错开顺序和完成状态。这些是根据上游Swift源码语义建立的C#样本；本机没有运行Swift实现，不能称为跨语言逐帧golden对照。 |
| GPU与场景 | `dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release` 通过，报告后端为 `Direct3D 11 GPU hardware`。覆盖裁切/缩放/圆角/光标、Phone与Tablet各自的机身和边框像素、焦点透视点反算、屏幕标题和窗口控制点透视、背景视频真实帧变化、循环与非循环末帧行为、预览与导出共享视频背景帧。GPU帧合成加回读40帧平均0.15ms；仅代表该小尺寸probe。 |
| 实时预览 | Render.Probe的静态与动态1080p源缩至960输出，stdout背压期间decoder Private Bytes分别179→179MiB、181→181MiB；均值1.90/1.67ms，p95 2.77/2.12ms，对照30fps的33.33ms预算。Probe还覆盖seek、倒退seek、取消和Dispose清理。此结果来自本机合成输入和硬件，不保证复杂真实素材在所有机器上的实时帧率。 |
| 媒体导出 | Render.Probe通过范围MP4的ffprobe、抽帧顺序和音频范围检查；1440p与4K、30/60fps矩阵通过。组合场景导出36帧，独立音频及全部场景层通过。暂停后同一WAV映射出的多个音频片段，经2倍速和重排后可正常导出M4A；无主素材音轨时，相机容器音轨按多个同步片段导出AudioOnly M4A通过。Integration的NVENC/D3D11 1080p60同步样片输出300帧，三处闪帧时间为0.5/2/4秒，音频脉冲为0.48/1.98/3.98秒，最大测得偏差20ms。真实70秒录制工程编辑后导出52.9秒工程长度的MP4通过；此前复跑工程的53.200秒输出为另一份旧编辑状态。Integration还生成117帧、320×180、30fps、3.9秒MP4及同长M4A，ffprobe确认音频流和时长。另有1920×1080、60fps、360帧、6秒NVENC/D3D11输出，耗时2.106秒。 |
| 时间线资产与长素材 | `timeline-assets-20261009-190531-715/result.json` 20/20通过，覆盖8秒视频缩略图时间/颜色、缩略图跨线程像素读取、真实波形、无音轨和取消。`timeline-assets-performance/comparison.json` 用1800.021729秒、1080p60合成输入产出8张缩略图及1000列波形：基线18.947秒、候选2.041秒，托管探针进程PrivateBytes增量约21MB。该顺序比较未控制磁盘/系统缓存且不含FFmpeg子进程内存；它只证明本机合成长素材生成，不代表播放seek、UI滚动缓存或整个应用峰值内存。 |
| 长时区域视听采集 | `artifacts/validation/long-capture/ffplay-sync-correct-region-30m-report.json` 与对应媒体记录：正确区域同时采集30分钟，H.264 320×180、60/1 CFR、1800.683333秒、108041帧；WAV 1800.532472秒。检测到1072组闪帧/音频cue，1072组均一一配对（配对容差±250ms），覆盖名义1080组的99.26%；另有8组未检出，原因未分类。音频相对视频中位−45.790ms、绝对P95 70.790ms、P99 79.606ms、最大94.123ms，漂移估计+0.02696ms/min，满足本次长时100ms验收阈值。该试验的音频来自系统回环，FFplay fixture 同时播放画面和声音，可能混入桌面其他声源，不能当作无污染独立声源测试。启动前20秒12组的P95为65.623ms，未满足短测50ms阈值；另一15秒短探针9组最大47.594ms仅说明该次结果，不能据此泛化。 |
| 预览与导出构图 | Integration在四个成片时间点比较真实 `PreviewPlaybackSession` 帧与导出帧，源时间完全对齐；PSNR为24.06–25.58dB，MAE为3.78–5.03。阈值按低分辨率H.264压缩设置为PSNR≥23.5dB且MAE≤8；这是有损编码容差，不是逐像素相等。 |
| 用户界面编辑 | final20 应用内部回归 `editor-regression-final20-audit.json` 为39/39通过，覆盖逐帧、多选重排与撤销、摄像头分段编辑、音轨组删除、四类效果复制/属性粘贴、1秒范围MP4和保存重开，并含声音检查器偏移同步段的保存重载。桌面 UIA `ui-full-final17-audit.json` 为41/42；唯一失败是贴图属性粘贴用例点击到重排片段边界，目标未选中。后续 `ui-overlays-reordered-final20-audit.json` 保留重排工程并在可见片段内部复测，贴图复制/属性粘贴通过；这补充了该单项证据，不改变全量报告的41/42记录。`playback-regression-v4.json` 验证54帧/1.766秒暂停后续播18帧至2.333秒。 |
| DogSC导入 | `dotnet run --project tests/FrameTrail.DogScImport.Tests/FrameTrail.DogScImport.Tests.csproj -c Release` 的构造schema fixtures通过，覆盖版本字段、素材复制安全和当前已映射效果。未用真实Mac工程互操作；导入报告逐项保留损失，Windows自有工程格式不伪装成原格式完全兼容。 |
| 原创样例 | `scripts/create-sample.ps1` 重新生成相对路径工程、合成屏幕/摄像头视频、独立tone、角标和8秒导出；脚本保存并重开工程后调用实际ExportEngine，再由ffprobe确认1280×720、30fps、8秒且有音轨。成品与来源说明位于 `artifacts/sample/FrameTrail-Demo/`。 |
| 便携版包内验证 | `artifacts/validation/package-0.2.0/result.json` 通过：隔离开发环境的 `PATH` 与 `DOTNET_ROOT` 后，便携版有声播放、暂停恢复，以及真实录制/暂停/编辑/重开/导出均通过。每次workflow的时长、指针与点击数以同目录当前workflow-result.json为准；真实受控点击运镜已有另一个input-workflow-final证据。此验证仍在开发机Windows 11上，不等价于无运行库干净虚拟机；per-user安装版样例播放与卸载清理也通过；最终源码归档另由source-validation.json核验。 |
| 桌面录制闭环 | `artifacts/validation/input-workflow-final/workflow-result.json` 通过：真实输入录制7.9秒，检测27个指针事件、2次鼠标点击和1个自动缩放片段；暂停后编辑为2倍速并重排，工程时长为5.925秒，导出成功。`artifacts/validation/ddagrab-app-70s-final/workflow-result.json` 通过：70.533秒显示器录制编辑为52.9秒并导出。此前 `final-workflow` 的短桌面流程也保留。 |

复现Core、GPU和集成检查：

```powershell
dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj -c Release
dotnet run --project tests/FrameTrail.DogScImport.Tests/FrameTrail.DogScImport.Tests.csproj -c Release
dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release
dotnet run --project tests/FrameTrail.Integration/FrameTrail.Integration.csproj -c Release
```

## 仍有限或待验的验收

以下旧长测作为历史候选记录保留。ScreenRecorderLib旧后端30分钟样片有4,095帧、1800.049秒视频，平均约2.27fps；它不构成60fps验收。FFmpeg Desktop Duplication/NVENC全屏视频压测完成4K缩放至1920×1080、60fps、1800秒及108000帧，但没有音轨或同步刺激。独立WASAPI WAV曾完成30分钟测量，时长1800.037188秒，221/221脉冲检测；这些数据只代表当时本机端点。旧区域视听试验为1072/1080配对、P95 70.790ms、最大94.123ms。当前0.2.4同步长测数据及其v6快照限制以本报告开头为准。

本机存在单显示器DDA与窗口录制证据；物理摄像头及真实Mac工程仍未测试。0.2.4麦克风数值采集和窗口排除证据已列于本报告开头。App恢复、便携包/安装包和源码包闭环也已由0.2.4验证。没有干净VM；`WindowsSandbox.exe` 不存在，不能报告干净系统安装验收。当前FFmpeg验证构建含GPL组件；公开再分发仍需随发行包提供对应依赖的许可材料。本机安装验收不等于干净机器验证。

本报告和测试结果不代表完整DogSC兼容或整个迁移阶段结束。平台光标资源、部分原生采集时钟精度、真实设备矩阵以及长录制质量仍有限制。

0.2.0至0.2.3的包验收只属于各自候选。当前0.2.4程序包及源码包文件、提交、哈希和验收范围见 `artifacts/release/FrameTrail-0.2.4-manifest.json`、`artifacts/validation/package-0.2.4/result.json`、`source-validation.json` 和 `content-result.json`。

## 当前运行时说明

本报告较早的机器环境记录仍写.NET SDK 8.0.303；那是当时运行的历史验证环境。当前仓库根 `global.json` 锁定SDK `10.0.401`，主App目标为 `net10.0-windows`。隔离升级结果 `artifacts/validation/net10-upgrade-result.json` 记录了App和Smoke Release构建0警告、0错误，以及Core、DogSC导入、GPU渲染、音频覆写与合成媒体集成检查。该后台结果未启动WPF GUI，也未访问录制设备；本报告前文所有具名旧候选和旧硬件数据继续按各自版本解释。

桌面截图 `visual-final20-audit/` 含主界面及10个检查器/录制/导出页面，共11张，使用同一合成参考工程。截图用于结构与控件顺序检查，不证明与Mac版逐像素一致；投影四角仍有位置/姿态差异，缺少原Mac工程参数，暂不能归因于整体平移或渲染误差。其余硬件、真实Mac工程互操作及95%全功能目标仍未得到证明。

补充异常与重定位验证：

- 主素材重定位 UIA `ui-relink-audit-v5.json` 为2/2通过：缺失素材重新选择后复制入工程、哈希一致、原文件未改、可解码、关闭重开不再提示缺失。
- `fault-lifecycle-probe/synthetic-runtime-fault.mp4` 在真实受控窗口录制中注入服务故障通知，再执行实际 StopAsync。结果 Completed、完成事件仅一次、录制器释放，ffprobe为H.264、640×360、31帧、1.0333秒。这证明服务故障收尾路径，不代表原生设备故障、热拔或损坏残片恢复已实测。屏幕/摄像头Faulted状态可停止，主界面会停止独立轨道；在验证主视频前即保存独立轨道时钟，媒体成功打开后才删除恢复记录。
## 0.2.3 历史附加后台验收

- `captured-project-synthetic-cli-v2/validation-summary.json` 记录完整合成metadata驱动的素材复制、实际App音轨映射、保存重开、M4A导出与派生分析输入；5秒素材的3个合成视听脉冲全部配对。这是CLI正向接线验证，尚不能替代真实录制的完整metadata随工程恢复。
- `analyze-ffplay-sync-v2.py` 改用ffprobe JSON读取帧时间戳，忽略H.264 SEI side data；解析器纯测与H.264合成3/3验证通过。原30秒实录离线重分析仍为1820帧、18/18配对，原始中位数/P95/P99/最大值逐项不变，见 `capture-audio-clock-appsync-30s-v1/analyzer-json-fix-v1/comparison.json`。未改变cue判定或偏差阈值。
- 麦克风时钟段纯测接受暂停前后两段及增加合法重锚段的情况，拒绝时间或帧倒序、重叠、缺口、活动覆盖不足和暂停内容泄漏；它不访问麦克风。该项在后台回归v2之后增加，当前 `-IncludeCaptureProbe` 共八组；`package-0.2.3/source-validation-logs-6fefeebe14554efbb5ac6386e0d8b73a/tests.stdout.log` 已记录独立源码包八组通过。真实输入信号和物理设备复位仍待实录。
- `runtime-lock-stability-v1.json` 确认13份NuGet锁文件在后台测试后哈希不变。发布使用锁定恢复。源码身份测试也覆盖深层解压路径与父Git仓库，独立源码构建的每轮报告保存在对应日志目录中。
- 源码ZIP结构策略的17项合成用例通过，拒绝根目录外文件、规范路径重复、文件目录冲突、Windows路径歧义及无效元数据类型；只测试临时归档，不构建或运行应用。独立验证的子PowerShell使用UTF-8输出，`package-log-encoding-repro-v2/summary.json` 确认中文stdout/stderr完整、特殊字符参数不被执行、失败退出码传播及父进程代码页保持。
- 后续完整源码验证暴露了UTF-8启动脚本的命名参数传递错误，失败报告保留在 `source-validation-logs-82092e451a504be7a23f390e093d620a/result.json`。已改成JSON对象与命名参数映射，并将 `test-utf8-script.ps1` 纳入发行规则回归，覆盖实际配置参数和开关绑定；该失败报告不会被改写成通过。

0.2.3程序包与源码包的历史文件、提交和哈希以 `artifacts/release/FrameTrail-0.2.3-candidate-manifest.json`、`artifacts/validation/package-0.2.3/source-validation.json` 和 `content-result.json` 为准。当前0.2.4状态见报告开头。
