# FrameTrail 0.1.0 本机打包验证

本记录对应固定应用代码提交 `159e17bbf8b8db250358b30363dc16ba97c23180` 构建的本机验证包。包用于本地功能验收，包含 Gyan FFmpeg 8.1.1 GPLv3 构建，不可直接公开发行。逐项路径、哈希、探测结果及源码 ZIP 无 Git 回退验证状态以 [`artifacts/validation/package-final.json`](../artifacts/validation/package-final.json) 为准。

Release `win-x64` 自包含发布、便携 ZIP 与 Inno Setup 安装器均已生成。便携包带 `FrameTrail.exe`、`tools/ffmpeg/ffmpeg.exe`、`ffprobe.exe`、根 LICENSE、README、文档、第三方通知和每次录制 60 秒限制说明。构建元数据记录代码提交和上游 DogSC 基线；打包工作区含脚本及文档改动，因此不能标记为 Git clean。

隔离 PATH（只保留 Windows 系统目录）启动最终包的合成项目并运行 `--smoke` 成功，截图输出到 `artifacts/validation/package-final-open-smoke.png`。相对素材、缩略图和波形均能在样例项目中显示。本轮诊断启动的 FrameTrail 进程已退出。

最终包的短工作流验收使用真实桌面录制作为输入，录制约 3.52 秒，采集得到一条鼠标位置事件并自动添加一个标记，执行 split、变速和重排，重载项目后导出 MP4 成功。工程输出时长约 2.64 秒；完整机器可读结果位于 `artifacts/validation/package-final-workflow/workflow-result.json`。这项短验收用于检查最终包内采集与导出工作流，不代表真人点击运镜、长时间录制稳定性或干净虚拟机结果。

另外，媒体集成程序对真实 3840×2160、约 2.97 秒的录制及其相邻指针 JSONL 执行了项目保存/重载、编辑、取消清理、MP4/M4A 导出和探测，结果成功；对 1080p60 的合成同步素材导出 300 帧、耗时约 1.86 秒，三组音画脉冲偏差均为 20 毫秒，编码器为 `h264_nvenc`，渲染器为 Direct3D 11 GPU。合成素材数据仅用于时间线及封装同步验证，不代表真实采集同步性能。

安装器此前已通过 per-user 安装与卸载检查；正式最终包复测的退出码、HKCU 卸载记录清除及安装文件检查须以 `package-final.json` 状态字段为准。安装器设置 `PrivilegesRequired=lowest`，不安装系统级 VC 运行库。本机已安装 Microsoft Visual C++ 2015–2022 Redistributable (x64)，干净虚拟机仍未验证。源码 ZIP 的元数据回退测试尚待提交最终脚本后，在临时解压副本中运行 `package.ps1 -WithoutFfmpeg -SkipInstaller`；在外部 JSON 明确通过前不标记完成。

本机包随附 FFmpeg 核心仓库源码材料及构建信息，但 Gyan full build 静态包含的全部依赖源码、补丁和构建脚本仍未完整核实。FFmpeg 通过独立子进程调用；这不免除发行 GPLv3 构建所需的材料和义务。公开发行流程保持未启用。
