# FrameTrail 0.2.5 本机包验收

0.2.5完整包验收现为8/8通过，结果见 `artifacts/validation/package-0.2.5/result.json`（2026-10-10 04:00:41–04:01:40 UTC）。检查包括交互前偏好快照、ZIP内容核验、便携播放、便携录制/编辑/导出、安装版播放、卸载、偏好逐字节恢复，以及最后对发布目录477个文件与原始ZIP逐字节核对。偏好快照ID为`402fe3a1fbf84680b7ccd668512cdfe1`，恢复检查通过；便携归档未变检查通过。随后生成的manifest状态为`validated-local-delivery`，源码提交`e4bfd5ed44deb93272df4d3f52217e3cb8e43420`、DogSC基线`e207d11b111c5e58152238fb64115b2009850547`及归档SHA见 `artifacts/release/FrameTrail-0.2.5-manifest.json`。

旧7项验收及两次ContentOnly失败保留为历史：早期便携工作流曾把发布目录Demo工程`camera.assetAspectRatio`从`null`写成`1.77777`；ZIP和安装器未变，样例已恢复。失败与恢复证据见 `artifacts/validation/package-025-post-ui-content-failure-v1/`。修复版使用隔离播放副本，最终新增检查确认本轮运行后发布目录477个文件仍与原ZIP一致。

源码ZIP独立解压构建见 `artifacts/validation/package-0.2.5/source-validation.json`，SHA256为 `7bd42394ceae21fb84113c303628a6b85054c8b43fd8685d8e855d5ad42fc0dd`。包内编辑53项、导出21项、文件锁12项、辅助素材重定位10项均通过；App DLL SHA256为 `D46FE2FDBA4393E0D6925EC57598A42EDC9CD388FF30F9FCA624DF86298C6491`。重定位使用Demo隔离副本，验证四类素材的副本SHA、保存重开、MP4/M4A解码和信号/像素结果。

同包100%、150%、200%单显示器全量UIA各44/44，三档浮层UIA各20/20。全量UIA使用旧合成testsrc2工程；首次启动窗口边界通过只读工作区检查，备忘录重启步骤由harness调用`SetWindowPos`重新定位。三档指定浮层截图均已人工查看；150%截图包含浅色主窗、深色下拉、嵌套菜单和预设确认弹窗，目录为 `artifacts/validation/ui-transient-package-025-dpi150-v1.surfaces-92102496fec545cb8772fc8faf736b4c/`。

0.2.5同包30秒音画探针 `artifacts/validation/capture-025-av-30s-v1/` 检出18/18组cue。1800秒同包验收 `artifacts/validation/capture-025-av-1800s-audit-v1.json` 通过：1071个flash、1070个tone、匹配1070组（1070/1080名义事件，99.07%）；双向覆盖99.9066%，事件跨度1797.866667秒。raw音画绝对偏差P95为80.9143ms、最大99.2483ms；映射并导出后P95为43.333ms、最大63.333ms。360次会话音量租约观察及音量/静音状态恢复均通过。

同包窗口目标探针 `artifacts/validation/capture-025-target-v1/target.target.json` 通过。Capture DLL SHA256为`30B5F8737D8692EC7229BF46E41490AE4AB66DD25D808D64BCE7547B8DDF20BA`；遮挡前、遮挡中和移动/缩放后各三个颜色样点均保持预期，关闭目标拒绝、录制中关闭/最小化收尾及区域录制回归通过。这些结果限于本机Windows 11 x64单显示器和测试窗口；不覆盖混合DPI、多显示器、真实Mac工程、物理摄像头、干净虚拟机或公开分发许可审查。 同一App SHA另有真实PNG贴图导入生命周期22/22专项，证据见 artifacts/validation/png-overlay-undo-025-v1.json；导入副本哈希与源文件一致，Undo/Redo、保存重开后基线保持通过，测试driver确认原包Demo样例未变；Smoke在退出前恢复偏好。该测试只覆盖PNG引用、复制和持久化。

## 0.2.2 历史验证说明

以下0.2.2验证流程只记录当时的版本状态，不代表当前用户授权或当前包验收。0.2.2当时要求使用 scripts/validate-package.ps1 -Version 0.2.2 -InteractiveDesktop 和 scripts/validate-source-package.ps1 -Version 0.2.2 -SourceZip <ZIP>；前者需要交互桌面，后者可后台运行。0.2.2旧版完整包口径为五项；后续0.2.5早期流程扩展到七项，本次最终版本新增归档未变第八项，现行完整验收为八项。

0.2.2当时的后台候选使用 `validate-package.ps1 -Version 0.2.2 -ContentOnly`，报告范围标记为`content-only`；候选manifest由 `write-release-manifest.ps1 -Version 0.2.2 -Candidate` 生成，并保留待验项、不覆盖当时已验版本的通用清单。具体历史结果应以 `artifacts/validation/package-0.2.2/` 为准。

## 0.2.0 历史包验证

`scripts/validate-package.ps1 -Version 0.2.0` 于2026-10-09完成预检，五项检查全部通过：必要程序/原创样例/许可证齐备；便携版有声播放与暂停恢复；真实屏幕录制、暂停、编辑、重开与导出；安装版样例播放；卸载后的主程序及 HKCU 安装注册清除。预检运行于08:21:13–08:21:59 UTC。

验证进程将 PATH 限制为 Windows 系统目录、将 DOTNET_ROOT 指向不存在的目录，并移除外部 FFmpeg 配置，确认包内自带工具可用。验证仍在已安装 VC++ 运行库的开发机上，不能等同于干净虚拟机。最终产物重新构建后会重复相同验证，结果覆盖于 `artifacts/validation/package-0.2.0/result.json`，具体媒体数据以该报告关联的 workflow JSON 为准。

安装脚本只在无既有 FrameTrail 安装时运行，安装到验证目录；启动自己的卸载程序，并核验该目录内主程序和 HKCU 注册项被清除。用户项目与偏好保留。安装器只支持 Windows 11 x64，不会安装或升级系统级 VC++ 运行库。

## 源码归档验证

`scripts/package-source.ps1` 只归档干净 HEAD，并追加固定 DogSC 源码基线。`scripts/validate-source-package.ps1 -SourceZip <ZIP>` 将归档解压到工作区以外的 TEMP UUID 目录，检查无 `.git`、验证元数据，再运行 Core/导入器及采集纯逻辑测试、App Release build 和 `package.ps1 -WithoutFfmpeg -SkipInstaller`。它检查打包元数据确实使用 `source archive` 回退，而不是误找到外层仓库。

2026-10-09 09:04:34–09:05:00 UTC 的源码归档预验已通过：Core/导入器测试、App Release 构建及重新打包三步退出码均为 0，验证了工作区外解压、无 `.git` 及 source archive 元数据回退。

源码归档的实际验收状态由 `artifacts/validation/package-0.2.0/source-validation.json` 决定；该报告必须为 `status: passed` 才视为此项通过。日志与解压副本会保留，未通过不会自动标记成功。

## 发行边界

包内附带原创8秒合成样例、使用说明、架构、迁移映射、测试报告、MIT及第三方许可。无60秒录制上限；长时性能和同步验收见采集报告。Windows 实录桌面和本地重建的视觉fixture不会随包分发；源码归档包含固定上游快照自身的README示例图片。

当前 Gyan FFmpeg 8.1.1 full static 构建包含 GPLv3 组件。已随包保存 FFmpeg 本体对应源码、构建配置、二进制哈希与 GPLv3 文本；静态依赖的全部对应源码、补丁和构建脚本仍未完整核实。此包用于本地交付，公开发布保持未启用。没有商业代码签名、干净虚拟机或全部物理设备矩阵通过的声明。
