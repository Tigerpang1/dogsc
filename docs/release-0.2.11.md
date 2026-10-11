# 0.2.11 托盘菜单修复候选

0.2.10将托盘菜单延后展开，用户实机复验仍发现菜单立即消失。0.2.11改用可激活、无Owner的独立窗口承载纵向菜单；不再把隐藏消息窗口设为前台，也不再使用根ContextMenu的自动鼠标捕获关闭路径。菜单沿用现有圆角、阴影、浅深主题、选择态与最近项目子菜单。

窗口在失活、叶子命令执行或Esc时关闭；Esc优先收起子菜单。上下/Home/End可导航根条目，重复托盘调用收起当前菜单，后来的激活请求会取消旧的排队展开。窗口按实际像素定位到对应显示器工作区，录制时排除此辅助窗口。最多64KiB的本地 `logs/tray-menu.log` 记录窗口打开、激活、失活、命令和关闭事件，不记录菜单标题、工程名或文稿内容。

后台菜单检查13项通过，覆盖条目移交、命令单次触发、最近项目、浅深主题像素和工作区边界；独立录制浮窗11项通过。截图已核对圆角和留白；证据位于 `artifacts/validation/package-0.2.11/tray-menu-final/` 与 `desktop-surfaces/`。未以这些结果宣称托盘实机稳定。

用户已在0.2.11确认托盘菜单停留约5秒仍稳定，能打开提词器，文稿和外观重启后恢复；另一次直接从托盘退出后，日志记录命令且应用进程结束。文稿文件SHA与重启前一致；外观恢复以用户观察为依据，不把后续调整后的设置文件当作逐字节一致。证据在 `artifacts/validation/package-0.2.11/basic-desktop-v1/03-tray-exit-user-confirmed.json`、`05-menu-memo-user-confirmed.json`。再次启动后仍只有原应用进程，见 `09-repeat-launch-observation.json`；用户还确认关闭主窗后录制继续并可从录制条正常结束，见 `10-recording-lifecycle-user-confirmed.json`。`11-recording-bar-menu-user-issue.json` 记录用户无法打开录制条菜单的问题，由0.2.12加入齿轮菜单处理。浮窗辅助窗口的录屏排除和新菜单排除仍待验。

0.2.11这组托盘和录制生命周期结果继续作为历史实机证据。0.2.12新增的预览调度、清晰度策略及其待验边界见 [`release-0.2.12.md`](release-0.2.12.md)；它们不改变本版包、哈希或安装验收结论。

便携包、安装器和源码包已生成，均对应源码提交 `7dc2bb52ec6f6c2a4106ac235baf77eae913fbfe`。`content-result.json` 通过，便携目录493个文件与ZIP逐文件一致；`source-validation.json` 记录源码ZIP在无Git元数据目录中完成测试、构建和无FFmpeg打包，runId为 `6aba46ffbd58406abd83951267019ce1`。包大小与SHA见 `artifacts/release/FrameTrail-0.2.11-candidate-manifest.json`。安装器已编译，当前版本实际安装卸载尚未验收；清单保持候选状态。摄像头延期，复杂权限场景暂缓，未达到整体95%门槛。
