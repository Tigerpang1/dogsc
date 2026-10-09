# Windows 原生录制验证

## 实现与公共 API

`FrameTrail.Capture` 面向 `net8.0-windows`，不依赖 `FrameTrail.Core`。主界面通过 `IFrameCaptureService` 枚举显示器、窗口和音频设备，构造 `RecordingRequest` 后调用 `StartAsync`，由状态事件观察启动、录制、停止和失败，再调用 `StopAsync` 取得 `RecordingResult`。模型位于 `src/FrameTrail.Capture/Models.cs`。

录制后端使用 ScreenRecorderLib 7.0.1：屏幕和区域采用 Desktop Duplication，窗口采用库的 Window Recording Source，H.264/MP4 由 Media Foundation 输出。光标与点击叠层关闭；输入跟踪另以约 60Hz 采样，输出 JSONL，记录 `Stopwatch` ticks、物理屏幕坐标、目标内归一化坐标、鼠标按键和修饰键，不采集键入文本。输入事件 ticks 与服务状态 ticks 使用同一进程单调时钟域，UI 可据此和录制事件对齐，但视频真实首帧 PTS 不由 ScreenRecorderLib API 暴露。

区域录制要求矩形完整落在单个显示器内。窗口枚举和输入采样使用扩展窗口边界及物理像素坐标；窗口目标尺寸在启动时用于设置源，移动窗口时的实时裁切、边框对齐和跨显示器行为尚未以真实窗口录制验证。设备枚举不会启用摄像头。

## 官方来源与部署

NuGet 页面标注 ScreenRecorderLib 7.0.1 为 MIT 许可证；对应许可证文本随项目放在 `src/FrameTrail.Capture/ScreenRecorderLib.LICENSE.txt`。官方仓库说明其通过 Windows Media Foundation 实时编码 H.264，并提供 x64 架构程序集。发布产物实测包含 `ScreenRecorderLib.dll` 和许可证文件；本机成功加载该程序集，且 `vcruntime140.dll`、`mfplat.dll`、`mfreadwrite.dll` 可解析。部署机器仍需具备 Windows Media Foundation，N/KN 版本可能需要 Media Feature Pack；不把运行库作为应用自带组件。

官方源代码将 `IsMp4FastStartEnabled` 映射到 `MF_MPEG4SINK_MOOV_BEFORE_MDAT`，而 `IsFragmentedMp4Enabled` 选择 `MFCreateFMPEG4MediaSink`。本实现设置 fast-start=false、fragmented=false、fixed-framerate=false，生成普通 MP4，便于下游媒体工具读取。ScreenRecorderLib 的 [Issue #372](https://github.com/sskodje/ScreenRecorderLib/issues/372) 报告长录制 private bytes 增长；issue 示例也打开 fragmented MP4，所以该设置不能视为已知问题的修复。

参考：[NuGet 包和许可证](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1)、[官方输出管理器](https://github.com/sskodje/ScreenRecorderLib/blob/master/ScreenRecorderLibNative/OutputManager.cpp)、[官方项目](https://github.com/sskodje/ScreenRecorderLib)。

## 本机验证记录

验证机为 Windows x64，输出请求 1920×1080、60fps；默认系统 loopback 为 `扬声器 (2- HECATE G1 PRO GAMING HEADSET)`。Probe 可枚举显示器、窗口和 loopback/麦克风设备，已验证录制两次屏幕、重复录制、取消/无效窗口句柄失败处理，以及用本地生成的 440Hz 音调验证 loopback 轨道标记。音频标记只证明 MP4 中有音轨结构，不代表已做声学内容或漂移分析；麦克风仅枚举，未实际录制。

样片在工作区 `artifacts/validation/long-capture/`。`frametrail-faststart-disabled-smoke.mp4`（30 秒，3,518,807 bytes）和 `frametrail-fixedfps-disabled-smoke.mp4`（40 秒，4,717,561 bytes）在录制时文件持续增长，结束后检测到 `ftyp`、`moov`、`avc1`、`soun` 标记。碎片 MP4 隔离样片 `frametrail-fragmented-smoke.mp4`（60 秒，7,035,689 bytes）同样可以写出并结束；它的 private bytes 在 10/20/30/40/50/60 秒约为 305/391/495/556/599/634 MiB，工作集约为 136/138/140/143/144/145 MiB。非碎片的 40 秒样片 private bytes 在 10/20/30/40 秒约为 312/415/460/504 MiB。旧配置的 30 秒短测在 10/20 秒约为 219/322 MiB。

以上结果显示短录制时 private bytes 快速增长，且关闭 fast-start、关闭 fixed frame rate、启用 fragmented MP4、移除 native `CurrentFrameNumber` 查询均未证明解决增长。基于风险保护，本轮没有再运行 30 分钟；Probe 长测 runner 已提供每 10 秒刷新 CSV 和控制台日志、记录工作集/private bytes、系统可用内存、CPU、文件大小，并在 private bytes 达到 2 GiB 时请求正常停止。被中止的早期诊断素材及日志注明为不健康/非完成样本，不能当作长时成功证据。长录制性能和内存稳定性目前不通过验收，建议在定位上游 native 内存增长前，不把 ScreenRecorderLib 作为无限时长录制的已验证实现。

Probe 记录 StartAsync 调用前的 Stopwatch 基线与 `Recording` 状态事件：30/40/60 秒样片状态事件相对调用基线分别晚约 430/400/474 ms。这只是状态回调时刻，不是首帧时间；真实首帧 PTS 无可用 API，不能据此声称视频与输入事件精确同步。输入采样频率约 60Hz，时间精度受采样周期和线程调度影响，未进行可重复的音视频漂移测量。

Probe 可通过以下方式执行：

```powershell
dotnet build tests/FrameTrail.Capture.Probe/FrameTrail.Capture.Probe.csproj -p:Platform=x64 -c Release
dotnet tests/FrameTrail.Capture.Probe/bin/x64/Release/net8.0-windows/FrameTrail.Capture.Probe.dll --duration 30 --fps60 --width1920 --metrics --output artifacts/validation/long-capture/sample.mp4
```

本机未安装 `ffprobe`，因此只检查 MP4 box 标记，未验证完整解码、帧率统计、精确时长、音频内容或 FFmpeg 导出。硬件编码请求由库配置，但库不暴露 Media Foundation 最终选择的硬件或软件编码器，结果标记为 `Unknown`。Windows 窗口/区域实际样片、摄像头、麦克风录制、系统版本差异及长时同步仍需补测。
