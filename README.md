# FrameTrail · Windows 录屏与运镜

FrameTrail 是基于 [DogSC](https://github.com/laogou717/dogsc) 的设计与核心算法开发的独立 Windows 迁移项目，不代表原作者的官方 Windows 发行。目标平台为 **Windows 11 x64**。

固定上游提交：`e207d11b111c5e58152238fb64115b2009850547`。开始迁移时核验远端 `main` 与 `v0.1.2` 均为该提交；本轮继续采用此基线。源码子模块在 `upstream/dogsc`，MIT 许可全文在 `licenses/DogSC-MIT.txt`。

## 0.2.3 实现范围

- 显示器、区域录制，暂停/恢复；窗口录制已接入 WGC，跨进程黑帧问题仍在修复。独立系统声音、麦克风和摄像头素材；鼠标事件、热键标记、备忘录及辅助窗口排除。
- 项目自动保存、备份恢复、最近项目、相对素材路径、缺失素材重新定位；只读导入 DogSC schema 23–27 并生成转换报告。
- 实时预览、逐帧定位、缩略图/波形、分割、裁短、重排、0.25× 起的慢放与加速、恢复剪辑、范围导出；多效果轨道、多选、吸附、悬浮预览、复制和属性粘贴、撤销重做。
- 自动镜头弹簧跟随、手动缩放、自定义 Bezier 和共享弹簧参数；录制光标形态、速度倾斜及四种点击动画；摄像头布局/动画、屏幕 3D 和聚焦虚化；贴图、区域柔化、画布裁切、样机、图片和循环视频背景、三种原版开场及场景预设。
- 共享 D3D11 场景渲染的预览和导出。MP4/M4A，原始、1080p、1440p、4K，30/60fps，编码器回退、取消和重试。

0.2.3 是当前开发候选，尚未完成发行验收。功能代码、桌面交互测试、真实设备验证分别记录，不以入口存在代表全部验收。视觉以 DogSC 的美感和流畅度为下限，允许更适合 Windows 的交互；实际限制见 [已知问题](docs/known-issues.md)、[视觉品质合同](docs/visual-quality-contract.md) 和 [迁移验收表](docs/parity-checklist.md)。

## 构建与运行

需要 .NET 10 SDK（仓库 `global.json` 以 10.0.401 为基准）、Windows Media Foundation、Microsoft Visual C++ x64 Runtime、FFmpeg/FFprobe。可用 `FRAMETRAIL_FFMPEG_DIR` 指定媒体工具目录；发行便携包自带 .NET 与 FFmpeg。

```powershell
git submodule update --init --recursive
pwsh -File scripts/build.ps1
pwsh -File scripts/test.ps1
pwsh -File scripts/test.ps1 -IncludeMediaProbe -IncludeIntegration
pwsh -File scripts/test.ps1 -IncludeVisualProbe
pwsh -File scripts/test-release-manifest.ps1
pwsh -File scripts/package.ps1 -Version 0.2.3
```

启动 `src/FrameTrail.App/bin/Release/net10.0-windows/win-x64/FrameTrail.exe`。打包产物在 `artifacts/release`；[使用说明](docs/user-guide.md) 和 [构建与打包](docs/build-and-release.md) 说明操作与依赖。

## 验证证据

本机为 Windows 11 Pro 26200、AMD Ryzen 7 9850X3D、NVIDIA RTX 5070 Ti。当前 .NET 10 候选的完整桌面流程43/43、浮层专项19/19、实际导出面板15/15通过；深色主题正文的继承问题已修正并重新截图核对。音频改用连续设备位置拼接，QPC用于锚定和漂移估算：30秒真实系统回环采集配对18/18个视听事件，原始绝对偏差P95为54.736ms，当前实现的30分钟长测仍待完成。窗口采集已接入WGC，产品服务的受控窗口实录成功，完整遮挡、移动、缩放和窗口消失流程仍在验证。各轮候选、截图与数值边界见 [验证报告](docs/validation-report.md) 和 [迁移验收表](docs/parity-checklist.md)。这些证据尚不足以认定功能迁移95%已达到。

测试原始产物在 `artifacts/validation`，不纳入 Git 或发行包，以免包含真实屏幕内容。合成媒体、算法测试和本机测试不等同于另一台干净机器或所有摄像头/麦克风已验收。

- [源码审计](docs/source-audit.md) · [架构](docs/architecture.md) · [算法差异](docs/algorithm-port.md)
- [功能映射](docs/feature-mapping.md) · [静态视觉依据](docs/visual-spec.md)
- [采集验证](docs/capture-validation.md) · [媒体效果验证](docs/media-effects-validation.md)
- [第三方许可](licenses/THIRD-PARTY-NOTICES.md)
- [Windows 分支吸收与新增验证](docs/windows-port-integration.md)

无遥测、账号、广告或云端媒体上传。本地验证包包含 Gyan FFmpeg GPLv3 构建；公开分发前须完成该构建全部依赖的对应源码与许可材料。Windows 10、ARM64、自动更新和真实 Mac 工程往返兼容未声明支持。
