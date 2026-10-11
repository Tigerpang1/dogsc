# DogSC 0.2.17 · Windows 社区移植版（Beta 草稿）

[DogSC](https://github.com/laogou717/dogsc) 由 **神烦老狗** 创作，原项目采用 MIT 许可证。本版本在 Windows 上实现录屏与视频编辑，沿用原项目名称、原图标与 Logo，保留原作者署名。Windows 移植由社区维护。原项目的 [Releases](https://github.com/laogou717/dogsc/releases/latest) 提供 **macOS** 版本。

## 本次变化

- 产品名称、主窗口、托盘、录制条、提词器、安装器和发行文件统一使用 DogSC。
- 恢复原应用图标；“关于”页显示原作者、原仓库链接和 MIT 来源。
- 延续此前的播放优化：点击时间线才定位画面，悬停不改变预览；恢复播放复用已准备的画面和解码会话。
- 兼容已有 `.frametrail` 工程、设置、提词器文稿和本地数据。旧版安装升级沿用原安装登记；新装默认目录为 `%LOCALAPPDATA%\Programs\DogSC`。

## 使用范围

Windows 11 x64。主要功能包括显示器、窗口和区域录制，系统声音和麦克风音轨，托盘常驻、独立录制浮窗与提词器，时间线剪辑、自动/手动运镜、光标和画面效果，以及 MP4/M4A 导出。功能台账在限定场景为 28/33 项通过；它不是整体完成率，也不构成 95% 对齐声明。摄像头实机验收和复杂权限恢复暂缓，其他未验收范围见[发布准备说明](release-preparation.md)。

## 候选文件

- `DogSC-Setup-0.2.17-win-x64.exe`：当前用户安装器。
- `DogSC-0.2.17-win-x64.zip`：便携包，解压后启动 `app/DogSC.exe`。
- `DogSC-0.2.17-source.zip`：源码与固定的原 DogSC 上游子模块快照。

文件 SHA-256 与源码提交在生成后的 `DogSC-0.2.17-candidate-manifest.json` 和 `DogSC-0.2.17-candidate-SHA256SUMS.txt` 中记录。

**此页为二进制发布草稿，目前没有 Windows 安装包下载链接。** 含现有 FFmpeg 构建的二进制候选需补齐对应源码与许可材料，最终安装/卸载和交互验收也尚未完成。当前候选未签名；Windows 源码维护于 [lantiansb/dogsc](https://github.com/lantiansb/dogsc)，后续二进制发布使用该 Fork 的 Releases。

## 来源与许可

- 原项目：[laogou717/dogsc](https://github.com/laogou717/dogsc)，固定基线 `e207d11b111c5e58152238fb64115b2009850547`，MIT，`Copyright (c) 2026 神烦老狗`。
- Windows 实现另参考 [xiaoying8666/dogscwindows](https://github.com/xiaoying8666/dogscwindows)，该项目的贡献与 MIT 署名分别保留。
- 图标来源与精确哈希见 [DogSC-ASSETS.md](../licenses/DogSC-ASSETS.md)，组件声明见 [THIRD-PARTY-NOTICES.md](../licenses/THIRD-PARTY-NOTICES.md)。
