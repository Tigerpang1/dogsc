# DogSC 0.2.17 · Windows 社区 Beta

下载与完整文件清单位于 [v0.2.17-windows-beta.1](https://github.com/lantiansb/dogsc/releases/tag/v0.2.17-windows-beta.1)。Windows 版由 `lantiansb` 维护；原 [DogSC](https://github.com/laogou717/dogsc) 由 **神烦老狗** 创作并以 MIT 开源。本版沿用原名称、图标和 Logo，保留来源，不代表原作者官方 Windows 发行。

## 安装与使用

- 安装器：`DogSC-Setup-0.2.17-win-x64.exe`，当前用户安装，无需预装 .NET。
- 便携版：`DogSC-0.2.17-win-x64.zip`，完整解压并启动 `app/DogSC.exe`。
- 源码：`DogSC-0.2.17-source.zip`，包含固定的原 DogSC 上游快照。
- `DogSC-0.2.17-SHA256SUMS.txt` 与 `DogSC-0.2.17-manifest.json` 记录文件哈希、源码提交和验收范围。

仅支持 Windows 11 x64。录制依赖 Windows Media Foundation 和 Microsoft Visual C++ 2015–2022 x64 运行库；安装器会提示缺少运行库的情况。Windows N/KN 需系统媒体功能。包未做代码签名，Windows 可能提示发布者未知。

主窗口关闭后应用继续在托盘运行；从托盘可以重新打开编辑器、录制浮窗与口播提词器，或完全退出。使用说明见 [user-guide.md](user-guide.md)。

## 本次交付

- 显示器、窗口和区域录制，系统声音与麦克风音轨，工程保存及恢复。
- 托盘常驻，独立录制浮窗，口播提词器及文稿/外观保存。
- 时间线剪辑、自动/手动运镜、光标和画面效果，MP4/M4A 导出。
- 时间线仅在点击或拖动时定位；悬停保留当前画面。恢复播放复用已准备的帧和解码会话。
- 恢复原 DogSC 名称与图标；“关于”、安装器、文档保留原作者和 MIT 署名。
- 保留已有 `.frametrail` 工程、设置、提词器文稿与用户数据。
- 媒体依赖从固定源码重新构建，随附对应源码、许可证、构建脚本及哈希；安装后可直接使用，无需自行安装 FFmpeg。

## Beta 范围

功能台账为 33 项，其中 28 项在限定场景通过、2 项待验、3 项延期。这不是整体功能完成率，也不代表达到 95% 对齐。[验收台账](acceptance-ledger.md)保留每项证据和边界。

尚未完整覆盖物理摄像头、多设备同时采集、多显示器/混合 DPI、长时间稳定性、干净系统及所有 GPU/驱动组合。复杂麦克风权限拒绝恢复按维护者要求暂缓；新录制条菜单与辅助浮窗排除的完整实机复核仍在后续清单。Mac 工程互通不属于本轮范围。当前无自动更新，请从本仓库 Releases 获取新版本。

随包编码程序包含 NVIDIA NVENC、AMD AMF、Windows Media Foundation 和 x264；实际可用性取决于设备及驱动。本版未编译 Intel QSV，Intel 设备可尝试系统 Media Foundation 录制，导出使用 x264 软件编码。

预览采用可替换的 LGPL FFmpeg 动态库，独立 FFmpeg 编码程序包含 GPL 的 x264；二者的配置分别记录在随包材料中。对应源码可从同一 Release 的 `DogSC-FFmpeg-8.1.2-sources.zip` 获取，详情见 [FFmpeg 构建来源](../third_party/ffmpeg/README.md)。

问题反馈：[lantiansb/dogsc/issues](https://github.com/lantiansb/dogsc/issues)。提交问题时请说明版本、系统和复现步骤；避免附带私人的录屏、录音或工程素材。
