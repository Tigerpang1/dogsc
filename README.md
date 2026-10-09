# FrameTrail · Windows 录屏与运镜技术预览

这是基于 [DogSC](https://github.com/laogou717/dogsc) 设计与核心算法开发的独立 Windows 迁移项目。**当前交付为阶段 1 技术验证及阶段 2 的基础闭环，完整迁移尚未完成。** FrameTrail 是临时代号，正式名称待确认；本项目不代表 DogSC 原作者的官方 Windows 发行。

固定上游基线：`e207d11b111c5e58152238fb64115b2009850547`。开始时核验远端 `main` 与最新 `v0.1.2` 均为该提交。源码保存在 `upstream/dogsc` 子模块，MIT 全文位于 `licenses/DogSC-MIT.txt`。

## 当前可以运行的闭环

Windows 原生录屏 → 独立鼠标事件与标记 → 保存本机项目 → 分割、裁短、重排、变速 → 点击自动缩放及手动构图 → GPU 合成 → MP4/M4A 导出 → 重新打开项目继续编辑。

界面还提供来源选择、区域框选、系统声音/麦克风开关、备忘录、缩略图与波形、边界吸附、撤销重做、纯色背景、留白、圆角、阴影、光标校准和导出取消。部分入口仍需对应设备及真实使用验证，见功能表。

## 构建与运行

最低目标为 **Windows 11 x64**。本机验证为 Windows 11 Pro 26200、AMD Ryzen 7 9850X3D、NVIDIA RTX 5070 Ti。Windows 10、ARM64 和其他机器没有被声明为已支持。

需要 .NET 8 SDK、Windows Media Foundation、Microsoft Visual C++ x64 Runtime；导出还需要 FFmpeg/FFprobe。可通过 `FRAMETRAIL_FFMPEG_DIR` 指定二者所在目录。便携包自带 .NET 运行时与用于本地验证的 FFmpeg。

```powershell
git submodule update --init --recursive
pwsh -File scripts/build.ps1
dotnet run --project src/FrameTrail.App/FrameTrail.App.csproj -c Release
pwsh -File scripts/test.ps1
dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release
dotnet run --project tests/FrameTrail.Integration/FrameTrail.Integration.csproj -c Release -- --sync
pwsh -File scripts/package.ps1
```

包输出目录为 `artifacts/release`。详细步骤见 [构建与打包](docs/build-and-release.md)、[使用说明](docs/user-guide.md)。本机实际验证产物位于 `artifacts/validation`，未纳入 Git，以避免提交真实屏幕内容。

## 验证与限制

已跑通真实桌面短录制、项目重载、剪辑/变速/重排后导出、合成点击自动缩放、共享 GPU 构图对照、导出取消后重试、1080p60 NVENC 编码及 M4A。合成音画脉冲的三处偏差为 20ms；该结果验证时间线与封装，不代表采集首帧对齐精度。

原生库的录制状态通知与首帧 PTS 之间没有公开的精确对应关系，保留手动鼠标校准。长录制内存增长正在单独验证。摄像头、独立多路音轨、3D、模糊、贴图、多效果轨道、完整快捷键和原 macOS 项目导入尚未完成。首次有声播放会生成预览代理视频，长素材的即时播放性能仍需改进。

- [源码审计](docs/source-audit.md)
- [架构决策](docs/architecture.md)
- [算法移植与差异](docs/algorithm-port.md)
- [功能迁移表](docs/feature-mapping.md)
- [验证报告](docs/validation-report.md)
- [已知问题与下一阶段](docs/known-issues.md)
- [第三方许可](licenses/THIRD-PARTY-NOTICES.md)

当前包含 Gyan FFmpeg GPLv3 的构建只用于本地验证，公开分发前仍须完成其全部对应依赖源码与许可审核。无遥测、账号、广告或云端媒体上传。
