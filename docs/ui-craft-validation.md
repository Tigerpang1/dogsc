# 0.2.6–0.2.7 界面打磨与验收

## 0.2.7 非摄像头交互收尾

2026-10-10，按用户要求暂缓摄像头，继续处理编辑与导出中的实际问题。时间线不接收组合文字输入，中文输入法下的 `I`/`O` 范围快捷键恢复；普通文本字段保留中文输入。设置目录错误改在字段下方提示，修改输入会清除提示。导出成功后的退出按钮改为“完成”。文本字段的默认英文直角菜单统一为应用的中文圆角菜单，并明确将编辑命令指向对应文本框。

### 本轮补充的有限证据

| 场景 | 证据与可核对结果 |
| --- | --- |
| 素材定位、轨道与时间线预览 | `artifacts/validation/noncamera-closeout-v3/recovery/cancel-result.json` 记录主素材副本完成后取消辅助定位，工程 SHA 不变且没有遗留副本；`success-result.json` 记录主/辅素材复制哈希匹配并从最近项目重开。`timeline-view-result.json` 记录轨道隐藏/恢复、吸附开启定位 6.0 秒/关闭定位 5.9 秒，以及悬浮预览约 8.47 秒且播放头保持 5.9 秒。 |
| 变速与成片 | `artifacts/validation/noncamera-closeout-v4/menu/locked-speed-result.json`、`retry-saved.txt`、`undo-result.json` 和对应截图记录文件锁下变速失败、解锁重试及撤销。`artifacts/validation/noncamera-closeout-v4/editing/verification/result.json` 对实际 UI 导出的 19.3 秒音视频成片完整解码，并以独立源时间参考核验四段顺序和 2×片段映射。 |
| 右键菜单 | `artifacts/validation/noncamera-closeout-v4/menu/speed-selected-two-light.jpg`、`speed-selected-two-dark.jpg`、`first-disabled-left.jpg`、`last-disabled-right.jpg` 显示浅/深主题、当前 2×档位及首尾片段的重排禁用状态。 |
| 打开状态与辅助素材警告 | `artifacts/validation/noncamera-closeout-v4/open-status-probe/result.json` 的 47 项离屏探针调用辅助素材时长校验，确认自动裁短警告作为返回值保留，并与成功打开文案组合、清除旧取消提示。OpenProject 在启动异步缩略图/波形任务前设置此状态；该项没有伪称为实机窗口验收。 |
| 指针、校准和画布裁切 | `artifacts/validation/noncamera-closeout-v5/noncamera-parity/result.json` 的 FT21–FT23 使用导出 MP4 实际解码帧核对点击反馈/隐藏、剪切前后指针校准位置，以及非居中 Crop 的坐标图案和 Full 负对照。 |
| 偏好与固定参数导出对照 | `artifacts/validation/noncamera-closeout-v4/preferences-export/result.json` 应用浅/深主题及预览分辨率/帧率偏好，再以相同固定 `ExportOptions` 直接调用 `ExportEngine` 对照 30 帧像素哈希。它没有经过真实导出设置窗口路径，不能作为该窗口路径的验收证据。 |
| FT-17 开场 | 实机 UI 配置和保存重开证据见 `artifacts/validation/noncamera-closeout-v5/intro-zoom/intro-configured.jpg`、`intro-reopened.jpg`、`intro-reopened.txt`。`artifacts/validation/noncamera-closeout-v5/intro-zoom/opening-probe/result.json` 用真实 `PreviewPlaybackSession` 及解码后的 MP4 帧核对配置开场，并以关闭开场、反向顺序和仅屏幕素材作为负对照；该探针不声称键盘或桌面交互覆盖。 |
| FT-18 自动/手动缩放 | `artifacts/validation/noncamera-closeout-v5/intro-zoom/desktop-result.json` 记录 UI 生成自动镜头、手动缩放与预览点焦点后关闭重开，工程 SHA 前后相同。`artifacts/validation/noncamera-closeout-v5/intro-zoom/zoom-probe/result.json` 对自动镜头和手动镜头使用真实预览会话与解码 MP4 帧对照，并包含错误焦点及 Scale 1 负对照；不是全功能或全媒体类型覆盖。 |

当前验收记录为24项通过、6项待验、3项延期。6项待验受桌面自动化能力限制，具体见 `artifacts/validation/noncamera-closeout-v5/desktop-automation-limits.json`：录制器控件状态刷新/截图限制、无法执行带修饰键的 Control-click，以及不能处理系统隐私授权恢复。FT-17/18 已通过上述 UI 配置、保存重开和实际预览/导出帧验证。摄像头延期，Mac 工程互通不属于要求；有限证据不代表整体 95% 已验收。

第一轮实机预览为 `artifacts/preview/FrameTrail-0.2.7-closeout/app/FrameTrail.exe`。本机 Windows 11、单显示器150% DPI、中文输入环境：`I`/`O` 设置4.99–12.00秒范围；方向键从4.99前进至5.02再返回；空格播放后再次暂停，播放头可从14.43秒拖到8.03秒。未出现输入法候选框或整片时间线虚线焦点框。实际范围导出得到1920×1080、60 FPS、420帧视频，视频7.000秒、AAC音频7.007秒，完整解码通过。对应截图、操作记录和媒体探测均在 `artifacts/validation/noncamera-closeout-v1/`。

保存失败会把检查器恢复为已保存值，并明确告知本次编辑未应用。素材重定位先验证可读性，所有替代副本完成后一次提交；取消或提交失败仅清理该事务创建的副本。辅助文件存在但损坏时也会进入重选。现存可读的较短音轨保留原有裁短处理，替代素材则须覆盖原同步区间。用户先前明确确认的损坏项目备份恢复属于独立操作，保留 `.damaged-*` 和已恢复项目，不因后续取消素材选择而撤销。

Core tests 与 Integration 已通过；标记导出检查对4个标记工程的117个解码帧逐帧比较，与无标记基线完全一致。离屏保存失败探针核对模型、历史、磁盘和检查器回退。现存主视频和替换候选共用时间线覆盖校验；短于所需源区间超过50毫秒时进入重选。证据分别位于 `noncamera-closeout-v2/core-tests-final.log`、`noncamera-closeout-v2/integration-final3/result.json`、`noncamera-closeout-v2/main-media-coverage/result.json` 和 `noncamera-closeout-v1/save-failure-offscreen/result.json`。

第二轮预览 `artifacts/preview/FrameTrail-0.2.7-closeout-final/app/FrameTrail.exe` 实测中文圆角文本菜单浅深色、空目录就地提示、修正清错、有效保存和重启持久化。自动化需重新定位所属窗口才能可靠设置字段；部分模态键盘消息和菜单点击仍受工具焦点影响，文本菜单的全选命令另外用真实 WPF 命令路由离屏验证，未冒充桌面点击通过。完整 VisualProbe 113项通过，其中覆盖设置字段、菜单及主视频范围校验；独立文本菜单命令探针27项通过。截图和结果在 `artifacts/validation/noncamera-closeout-v2/`。

第二轮实际导出包括全24秒1440p/30 FPS视频（2560×1440、720帧、AAC 24秒）及24秒M4A（仅AAC流、48 kHz），两者完整解码通过。浅深色完成页均显示“完成”，点击可回编辑器。4K软件编码在50.6%时取消，恢复可编辑状态，最终输出及同名前缀临时文件均不存在；记录见 `full-export-1440p-probe.json`、`audio-export-probe.json` 和 `export-cancel-check.json`。录制目录测试结束后恢复默认值；本轮没有扩大摄像头、DPI、长录制或干净机器的通过范围。

实现参考 [WPF InputMethod.IsInputMethodEnabled](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.inputmethod.isinputmethodenabled?view=windowsdesktop-10.0)；禁用范围仅为不接受文字的时间线控件。

## 0.2.7 交互预览：移除系统虚线焦点框

2026-10-10 用户明确拒绝截图中围住整片时间线的 Windows/WPF 虚线焦点框。本次在应用级覆盖主题默认焦点样式，补齐自绘时间线与曲线控件；时间线键盘焦点只标记左上“轨道”标题，分隔条使用短条提示，滚动容器不额外占用 Tab 停靠位。悬浮时间游标改为细实线。鼠标点击不额外圈住整片内容，键盘焦点仍可辨认。

实际 0.2.7 预览位于 `artifacts/preview/FrameTrail-0.2.7-focus/app/FrameTrail.exe`。同内容工程的 150% DPI 浅深色实机检查已完成：鼠标选中第二片段、方向键从 00:07.08 前进到 00:07.12、Shift+Tab 到缩放按钮、Tab 返回时间线，均未出现整块虚线框；Tab 返回时标题局部焦点清晰可见。截图位于 `artifacts/validation/focus-polish-v1/timeline-keyboard-{light,dark}.jpg`。此构建供本地交互预览，不替代最终源码归档、安装器或全产品验收。

`artifacts/validation/focus-offscreen-v5/result.json` 共 77 项通过，其中包括全局主题回退的实际焦点模板、连续实线与旧虚线负对照、时间线局部边界、曲线与分隔条可见像素。v1–v3 的探针采样和遍历问题保留为失败记录，v4 是修正后的首次通过，v5 增加了全局回退检查。离屏结果不代表真实系统高对比模式或全 DPI 矩阵通过。

本次 `FrameTrail.Render.Probe` 标准回归通过（退出码 0），使用本机 Direct3D 11 硬件后端验证场景渲染与实际媒体导出；日志位于 `artifacts/validation/focus-polish-v1/render-probe.log`。其中摄像头素材测试验证已有媒体的合成语义，不代表物理摄像头采集验收。

焦点样式遵循 [WPF 官方说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)：应用可覆盖主题焦点资源，FocusVisualStyle 的模板目标仍为 Control，并可用于自绘 FrameworkElement。视觉策略以本项目用户禁用要求为准。

更新：2026-10-10。此记录覆盖共享控件、设置页和导出窗口；0.2.5 的媒体、录制和交付验收仍保留各自版本边界。

## 设计与行为

用户需要在清晰、统一的界面中调整录制偏好、编辑画面并完成导出。对照固定 DogSC 上游截图，沿用浅灰工作区、白色内容层、克制的圆角和低饱和选择色，保留 Windows 的窗口控制与操作习惯。

本轮重点解决四类问题：设置页单列堆叠、基础控件样式不统一、紧凑输入框裁字、导出状态变化挤动按钮。

- 设置页按录制、音视频来源、编辑外观与预览分组，相关字段并列，底部操作固定。编辑暂存在窗口内，保存通过校验后应用；取消或关闭放弃修改。
- 开关采用 120 ms 的位置反馈，跟随实际选中值，连续点击可中断；关闭系统动画时直接到达目标状态。冻结的 WPF 模板变换在动画前克隆，避免带已选设置的窗口打开失败。
- 复选框、滑块、滚动条、按钮与输入框使用共享主题。滑块保留 24 DIP 命中区域，滚动条保留 10 DIP 命中区域；可见线条更细。输入框仅计算一次内边距，非编辑下拉框不再受隐藏编辑框撑高。
- 导出准备、运行、失败和完成时，取消及主操作保持原位置。完成操作提前保留空间；进度显示真实引擎阶段和百分比。失败可打开、滚动、选择和复制详情，重试清除旧错误。

## 验证记录

| 范围 | 证据与结果 |
| --- | --- |
| 设置页实机 | 本机 Windows 11，单显示器 150% DPI。560×720 DIP 首屏完整显示三个分组、全部选项和提示；目录文字、紧凑下拉框无裁切。 |
| 取消与深色主题 | 麦克风修改后取消，settings.json 开始和结束都不存在；重开显示原值。深色设置、下拉与导出准备/运行/完成可读。Esc 收起下拉已确认。 |
| 编辑交互 | 边距从 70 拖到 151.103，Ctrl+Z 恢复 70；时间线缩放 1→9.25，滚轮平移、横向滚动条右拖通过，滚动值到达 3488.733。 |
| 实际导出 | Sky 操作生成的 sky-completed.mp4 经 ffprobe 确认为 H.264 1920×1080、AAC、24.000 秒。 |
| 导出状态回归 | export-panel-final-result.json：34/34。包括真实音视频导出、失败详情全文和关闭、重试清除、取消保留原目标、临时文件清理、完整解码、准备/运行/完成按钮边界不变。 |
| 同内容视觉对照 | comparison-before 与 comparison-after 各 11 张，使用独立副本及相同工程。已查看编辑器、摄像头空状态、录制浮条、导出窗口；原版参考为 upstream/dogsc/docs/images/editor.png 和 export.png。 |
| 共享控件离屏 | ui-craft-offscreen-final/result.json：63/63。覆盖实际字形、紧凑字段高度、开关位置变化、滚动条方向与厚度、浅深色按钮文字对比。高对比资源补强后，ui-craft-offscreen-release-final/result.json 再次 63/63 通过；该探针未切换或模拟系统高对比模式。 |

上表实机、导出和对照材料均位于 `artifacts/validation/ui-craft-v1/`。最初实机 app-v5 DLL SHA256 为 `CF2D1ABD4B3B8794FE85D6F432E096871883F0770F86E7E4381B9382331A8D96`；之后修复导出按钮位置和详情布局。34项回归以 `export-panel-final-result.json` 为准，不把最初 DLL 当作最终产物。

原版截图与本轮画面夹具只用于本地比较，不进入源码归档或发行样例。所有 artifacts 均不提交。

## 交付与边界

0.2.6 为本轮 UI 候选。便携包、安装器、源码归档及 SHA256 通过既有打包脚本产生，材料位置为 `artifacts/release/FrameTrail-0.2.6-*`，对应包与源码验证报告位于 `artifacts/validation/package-0.2.6/`。生成的 candidate manifest 记录实际验证范围，不能用旧包验收替代新包。

本轮没有重新运行 100%/200% 系统缩放矩阵，尚未验证系统高对比模式、混合 DPI、多显示器及干净机器。高对比资源的离屏检查不等于切换系统主题后的实机验收。自动化对模态窗口 Esc 的送达未确认，未记为通过；保存校验与 Enter 路径沿用原逻辑，本轮未重做全部设置保存场景。

这些结果证明记录范围内的布局与功能，不能量化为全产品 95% 覆盖，也不代表用户已经认可最终审美。
最终 0.2.6 x64 Release 构建为 0 警告、0 错误；App DLL SHA256：`FEC66CD8188E2A9DE734D67572CFACF6EAC06753914BA5B40032DA4DEBA9A015`。打包时版本元数据可能生成不同程序集哈希，以包内实际文件和清单为准。
