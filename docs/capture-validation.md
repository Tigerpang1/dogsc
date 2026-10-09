# Windows 原生录制验证

## 实现与公共 API

`FrameTrail.Capture` 面向 `net8.0-windows`，不依赖 `FrameTrail.Core`。主界面通过 `IFrameCaptureService` 枚举显示器、窗口和音频设备，构造 `RecordingRequest` 后调用 `StartAsync`，由状态事件观察启动、录制、停止和失败，再调用 `StopAsync` 取得 `RecordingResult`。窗口目标丢失会触发 `ErrorOccurred` 并自动正常封装；UI 可订阅 `RecordingCompleted`（`Action<RecordingResult>`）接收自动停止后的素材结果，也可读取 `LatestCompletedResult`。模型位于 `src/FrameTrail.Capture/Models.cs`。

显示器录制可通过 `IFrameCaptureService.CurrentCapturePrivateBytes` 读取当前 FFmpeg 子进程的 Private Bytes；暂停或无活动子进程时为 `null`。建议主UI同时采样主进程和此属性，按每个录制子进程及整个应用设置独立内存停止阈值。中断录制会在 `<输出MP4>.parts/<会话GUID>/manifest.json` 保留已正常封段的媒体段和错误摘要；`FrameCaptureService.FindRecoverableRecordings(directory)` 只读取此清单并跳过符号链接目录，`RecoverReadableRecordingAsync(info, destination)` 可将清单里的完整 MP4 段无损拼接为可编辑 MP4。被强制终止时当前未完成段只作为部分诊断文件保存，不列为可读段。成功完成的正常录制会清理 `.parts` 会话目录。

独立摄像头素材和独立音轨使用 `ICaptureTrackService`。摄像头输出 MP4，系统回环和麦克风各自输出 WAV；音频轨用 NAudio.Wasapi 直接采集 Core Audio 端点，不创建含屏幕画面的临时文件。`CaptureTrackHandle.MediaPath` 是 UI 应转换为项目相对路径的最终素材地址，`StartStopwatchTicks`/`StopwatchFrequency` 与 `CaptureTrackResult.StopStopwatchTicks` 提供单调时钟范围。摄像头 start tick 来自 ScreenRecorderLib Recording 状态回调，音轨 start tick 取 `StartRecording()` 前的单调时钟；二者都不等于实际首个视频帧或音频样本的 PTS。WASAPI DataAvailable 事件也不携带设备采样时间戳，因此音轨由20ms时钟泵以100ms重排窗口将包到达时刻映射到样本时间线，按采样时钟补静音、修剪重叠包，并在停止时填到有效录制时长。包时间仍是回调到达时间估计，结果会明确警告这一限制。暂停/恢复会将停顿作为 `PausedIntervals` 返回并从 WAV 时间线排除；WASAPI 停顿边界精度受实际 packet 大小限制，结果警告会给出观测最大包时长。音频 WAV 使用端点的实际采样率、通道数和 PCM/IEEE-float 格式，UI 不应假定固定的 48kHz PCM16。

显示器和区域使用外部 FFmpeg `ddagrab`（Windows Desktop Duplication API）采集，H.264/MP4 由检测可用的 FFmpeg 编码器输出；窗口目标保留 ScreenRecorderLib，并按 DWM 可见边界动态裁切。光标与点击叠层关闭；输入跟踪另以约 60Hz 采样，输出 JSONL，记录 `Stopwatch` ticks、物理屏幕坐标、目标内归一化坐标、鼠标按键和修饰键，不采集键入文本。输入事件 ticks 与服务状态 ticks 使用同一进程单调时钟域。显示器/区域的 `RecordingResult.FirstFrameObservedStopwatchTicks` 当前由 `ddagrab` 输出首帧时的 FFmpeg 墙钟与进程启动附近的 UTC/Stopwatch 配对换算，属于首帧进入滤镜的观测估计，不是原生捕获 PTS；`ProgressEstimatedFirstFrameStopwatchTicks` 提供旧 progress 反推值供诊断比较，`FirstFrameNumber` 为空。窗口路径的首帧回调保留 ScreenRecorderLib 提供的帧号和墙钟毫秒时间，但该时间不是 Media Foundation sample PTS。两种路径都不能宣称精确曝光时刻或精确 PTS。

区域录制要求矩形完整落在单个显示器内。窗口枚举和输入采样使用 DWM 扩展窗口边界及物理像素坐标；窗口目标每100ms检查 HWND 并更新其屏幕裁切，输出尺寸以启动时边界为准。受控窗口移动、区域物理像素裁切和目标最小化/关闭行为的实测结果列在下文。设备枚举不会启用摄像头；本机相机枚举为空，未实际打开相机。

首帧时钟回调 smoke 样片 `first-frame-callback-smoke.mp4` 在预览位图关闭时成功回调 `FrameNumber=1`，Record 调用前单调时钟到首帧回调抵达为 442.811ms，录制状态回调为 441.838ms；样片 ffprobe 检出 H.264、3840×2160、约 30.13fps、150帧、4.9781秒。故 `FirstFrameObservedStopwatchTicks` 比 Record 调用基线更接近首帧提交时刻，但仍不等于捕获时刻或编码 sample PTS；单次 smoke 的约 1ms 状态差不能外推到其他设备或负载。`FirstFrameSystemTimestampUnixMilliseconds` 来自库事件，官方[帧回调实现](https://github.com/sskodje/ScreenRecorderLib/blob/master/ScreenRecorderLibNative/RecordingManager.cpp)当前用 `system_clock::now()` 生成毫秒值，不能直接与 `Stopwatch` ticks 比较，也不能当媒体时钟。

## 官方来源与部署

NuGet 页面标注 ScreenRecorderLib 7.0.1 为 MIT 许可证；对应许可证文本随项目放在 `src/FrameTrail.Capture/ScreenRecorderLib.LICENSE.txt`。官方仓库说明其通过 Windows Media Foundation 实时编码 H.264，并提供 x64 架构程序集。发布产物实测包含 `ScreenRecorderLib.dll` 和许可证文件；本机成功加载该程序集，且 `vcruntime140.dll`、`mfplat.dll`、`mfreadwrite.dll` 可解析。部署机器仍需具备 Windows Media Foundation，N/KN 版本可能需要 Media Feature Pack；不把运行库作为应用自带组件。

独立音轨使用 NAudio.Wasapi 2.2.1（MIT，完整文本位于 `src/FrameTrail.Capture/NAudio.LICENSE.txt`），仅引入 `NAudio.Core` 传递依赖；NuGet 锁文件固定包版本。它通过系统 WASAPI/Core Audio 采集，不需要额外 native DLL。ScreenRecorderLib API 的 `OutputOptions.IsVideoCaptureEnabled=false` 在官方文档中定义为禁用视频输出，但本机 7.0.1 实测生成的 MP4 仍含真实桌面画面；因此废弃该方案，独立音轨改为 WASAPI 直写 WAV，避免桌面内容进入音轨素材或临时文件。

显示器/区域录制调用外部 `ffmpeg.exe`，程序从 `FRAMETRAIL_FFMPEG_DIR`、应用 `tools/ffmpeg`、应用目录、PATH 与 WinGet 安装中查找，不依赖本机绝对安装路径。开发机实际验证使用的 Gyan FFmpeg 8.1.1 full build 含 `--enable-gpl --enable-version3 --enable-libx264`，因此该验证二进制为 GPLv3-or-later 构建；它只用于本机验证，不能直接随专有应用打包分发。正式发布需选择并审核符合分发要求、含 ddagrab/D3D11/NVENC 所需功能的构建并提供对应源码和许可证材料。FFmpeg 官方说明 GPL 组件会使 FFmpeg 构建整体适用 GPL 条款，详见[FFmpeg 法律说明](https://ffmpeg.org/legal.html)。`ddagrab` 返回 D3D11 硬件帧，软件缩放前必须显式 `hwdownload`，见[官方滤镜文档](https://ffmpeg.org/ffmpeg-filters.html#ddagrab)和[官方实现](https://github.com/FFmpeg/FFmpeg/blob/n8.1.1/libavfilter/vsrc_ddagrab.c)。

官方源代码将 `IsMp4FastStartEnabled` 映射到 `MF_MPEG4SINK_MOOV_BEFORE_MDAT`，而 `IsFragmentedMp4Enabled` 选择 `MFCreateFMPEG4MediaSink`。本实现设置 fast-start=false、fragmented=false、fixed-framerate=false、low-latency=true，生成普通 MP4，便于下游媒体工具读取。ScreenRecorderLib 的 [Issue #372](https://github.com/sskodje/ScreenRecorderLib/issues/372) 报告长录制 private bytes 增长；issue 示例也打开 fragmented MP4，所以该设置不能视为已知问题的修复。

参考：[NuGet 包和许可证](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1)、[官方输出管理器](https://github.com/sskodje/ScreenRecorderLib/blob/master/ScreenRecorderLibNative/OutputManager.cpp)、[官方项目](https://github.com/sskodje/ScreenRecorderLib)。

## 本机验证记录

验证机为 Windows x64，输出请求 1920×1080、60fps；默认系统 loopback 为 `扬声器 (2- HECATE G1 PRO GAMING HEADSET)`。Probe 可枚举显示器、窗口、loopback 和麦克风设备；ScreenRecorderLib 与 WASAPI 两套枚举得到一致的端点 ID。旧版音轨 MP4 诊断样片曾用本地生成的 440Hz 音调验证，但它也含有非黑屏幕画面，已明确弃用。WASAPI 替代实测的 WAV 仅有音频流并能测到本地音调；麦克风仅枚举，未打开采集。

样片在工作区 `artifacts/validation/long-capture/`。`frametrail-faststart-disabled-smoke.mp4`（30 秒，3,518,807 bytes）和 `frametrail-fixedfps-disabled-smoke.mp4`（40 秒，4,717,561 bytes）在录制时文件持续增长，结束后检测到 `ftyp`、`moov`、`avc1`、`soun` 标记。碎片 MP4 隔离样片 `frametrail-fragmented-smoke.mp4`（60 秒，7,035,689 bytes）同样可以写出并结束；它的 private bytes 在 10/20/30/40/50/60 秒约为 305/391/495/556/599/634 MiB，工作集约为 136/138/140/143/144/145 MiB。非碎片的 40 秒样片 private bytes 在 10/20/30/40 秒约为 312/415/460/504 MiB。旧配置的 30 秒短测在 10/20 秒约为 219/322 MiB。

以上结果显示短录制时 private bytes 快速增长，且关闭 fast-start、关闭 fixed frame rate、启用 fragmented MP4、移除 native `CurrentFrameNumber` 查询均未证明解决增长。Probe 长测 runner 每 10 秒刷新 CSV 和控制台日志，记录工作集/private bytes、系统可用内存、CPU 和文件大小；private bytes 达到 2 GiB 时请求正常停止。此前被中止的诊断素材及日志标为非完成样本。

启用 `IsLowLatencyEnabled=true` 后进行的 300 秒普通 MP4 + loopback 验证输出 `frametrail-lowlatency-300s.mp4`（33,194,802 bytes）。private bytes 在 10/20/30/40/50/60 秒为 356/476/563/641/778/897 MiB，随后到 130 秒约 1,271 MiB；从 140 秒至 300 秒维持在约 1,272–1,283 MiB。录制正常完成，2 GiB 守护未触发。这个样本显示 private 使用量先上升后平台，支持保留低延迟配置，但 300 秒样本不足以证明 30 分钟稳定。

另做 30 秒同时请求软件编码并关闭音频的 ScreenRecorderLib Probe：private bytes 在 10/20/30 秒为 771/772/775 MiB，工作集约 188–191 MiB；输出经 ffprobe 验证为 1920×1080 H.264、1800 帧、29.999983 秒且没有音轨。这个对照同时改变了编码器请求和音频开关，不能单独归因哪个设置减少内存，也不足以证明软件编码模式长时稳定。

Probe 记录 StartAsync 调用前的 Stopwatch 基线与 `Recording` 状态事件：30/40/60 秒样片状态事件相对调用基线分别晚约 430/400/474 ms。这只是状态回调时刻，不是首帧时间；真实首帧 PTS 无可用 API，不能据此声称视频与输入事件精确同步。输入采样频率约 60Hz，时间精度受采样周期和线程调度影响。

Probe 可通过以下方式执行（`--no-audio --software` 用于 ScreenRecorderLib 对照；`--target-test` 覆盖受控窗口移动/关闭、区域裁切；`--track-test` 覆盖 loopback WASAPI 与独立音轨 pause/resume；`--sync-capture` 做可控窗口脉冲验证；`--sync-playback --loop-playback --region-playback` 循环播放已有音画同步 MP4、录制置顶窗口和默认 WASAPI 回环；`--multi-pause-cycle` 对屏幕与 WASAPI 做三次并行暂停并保留各自区间元数据；`--kill-recovery-test` 实际终止当前 FFmpeg 子进程并调用恢复API）：

```powershell
dotnet build tests/FrameTrail.Capture.Probe/FrameTrail.Capture.Probe.csproj -p:Platform=x64 -c Release
dotnet tests/FrameTrail.Capture.Probe/bin/x64/Release/net8.0-windows/FrameTrail.Capture.Probe.dll --duration 30 --fps60 --width1920 --metrics --output artifacts/validation/long-capture/sample.mp4
```

Probe 会从 PATH、`FRAMETRAIL_FFPROBE` 或本机 WinGet FFmpeg 包目录解析 ffprobe。5 分钟 loopback 样片经 ffprobe 验证为 1920×1080 H.264、17,982 帧、视频时长 300.007033 秒；AAC 音轨为 14,063 帧、时长 300.010646 秒，容器时长 300.010646 秒。流报告时长相差约 3.6ms，但没有用已知音频脉冲或帧 PTS 做重复漂移测试，不能据此保证输入事件和音视频同步。样片的流元数据和帧数已检查，未验证音频样本内容或 FFmpeg 导出。硬件编码请求由库配置，但库不暴露 Media Foundation 最终选择的硬件或软件编码器，结果标记为 `Unknown`。

同步探针以独立受控窗口每10秒发本地440Hz音调并闪白120ms，记录每次刺激的 UTC 与 `Stopwatch` ticks。`analyze-sync.py` 从MP4抽取测试窗口亮度、解码440Hz带通能量，并逐脉冲报告编码流内音频相对视频事件偏差；也可传入独立WASAPI WAV做相同测量。首轮30分钟静态样片检测到120次窗口事件，其中75次在MP4音轨中检测到可辨440Hz脉冲；匹配脉冲的音频相对视频事件中位数约+454ms，绝对偏差P95约1.34秒。由于视频只有4,095帧（约2.27fps），且120ms视觉闪白远短于部分帧间隔，这轮数值只表示当前样片可辨事件的流级差异，不是同步通过或端点延迟精度。独立WASAPI样本的重复脉冲分析待第二轮完成后补充。

```powershell
$ffmpeg = (Get-Command ffmpeg.exe).Source
$ffprobe = (Get-Command ffprobe.exe).Source
py -3 tests/FrameTrail.Capture.Probe/analyze-sync.py $ffmpeg $ffprobe capture.mp4 pulse.csv analysis.csv independent-loopback.wav
```

首轮30分钟 ScreenRecorderLib 样片 `frametrail-long-30m-1080p60-lowlatency.mp4` 请求1920×1080/60fps，含默认loopback；真实ffprobe结果为H.264 4,095帧、视频1800.049秒，AAC 84,378帧、音频1800.064秒，平均画面约2.27fps，不能算60fps验收。文件大小35,483,326 bytes；private bytes长时间先升后平台在约1.28GiB范围，未触发2GiB停止保护。另一次独立 W 30 分钟 WASAPI loopback WAV 为 44.1kHz 双声道 float PCM，时长1800.037188秒，文件635,053,178 bytes；进程private最高约62.6MiB。受控440Hz脉冲分析检测221/221次，音频相对触发点中位数+1.979ms、P95绝对偏差7.671ms，首末脉冲分别+2.238ms与+3.518ms，线性漂移估计0.094ms/min。这只验证本机此端点、此时钟泵设置的一次实验，不能外推到其他设备。

新后端 1080p60 全屏桌面活动负载压测于2026-10-09 06:44:37 UTC启动，并于07:14:37 UTC正常结束。使用本机 FFmpeg 8.1.1 的 Desktop Duplication→`hwdownload`/缩放→NVENC 路径。ffprobe 验证输出 H.264 1920×1080、`r_frame_rate=60/1`、`avg_frame_rate=60/1`、时长1800秒、108000帧，文件103,507,353 bytes；FFmpeg 最终进度为59.99fps、dup_frames=2419、drop_frames=41、speed=1x。CSV 每10秒记录 CPU/private/working set/系统可用内存和文件大小；private bytes 在约140秒后维持约970–975MiB平台，结束前最高约975MiB，系统可用内存约7.5–8GiB。录制期间桌面存在实际变化，但不是受控测试图案；此实验没有音轨，也未测受控音画脉冲。因此它验证本机该负载下30分钟封装与内存平台，不代表其他 GPU、显示器或音轨设备。

后续一轮 DDA/NVENC 4K全屏输入并缩放至1080p60的30分钟测试通过：时长1800秒、108000帧。该测试验证本机4K显示器采集到1080p编码的长时视频路径；没有系统音轨，也没有受控音画同步测量。独立WASAPI loopback另有一轮30分钟测试通过，两项独立结果不代表端到端视听同步。

首帧时钟锚点实验将 `setpts=PTS+0*print(time(0)*1000000*eq(N\,0))` 放在 `ddagrab` 后，滤镜输出首帧时打印 Unix 微秒，再用启动附近成对采样的 UTC/QPC 换算成 Stopwatch ticks；零乘数不改动帧 PTS。表达式在首帧打印非零 epoch 值、后续为0；输出 MP4 首帧 PTS 为0，后续按30fps递增。一次3秒 1280×720@30探针的首帧观测锚相对录制进程基线为314.924ms；同一录制中第一个progress反推锚为348.943ms，progress锚晚34.019ms。该差值只表示本机此轮短测，不能当固定校正值。ffprobe验证输出为30/1 CFR、92帧、3.066667秒。首帧墙钟观察换算不是硬件PTS，UTC/QPC配对和FFmpeg日志回调仍有有限精度。

第二轮端到端同步试验首个长测于2026-10-09 07:23:50 UTC启动；区域路径最初把region bounds误传为display bounds，使 DDA `offset_x/y` 为0。发现后立即终止并保留错位片段作为 invalid-offset 诊断，不纳入结论。已修复为把真实显示器 Bounds 传给 DDA recorder。`(640,300,320,180)`偏移短测中，FFplay无边框窗口实测矩形和 Region目标一致，命令行 DDA偏移为`(640,300)`，视频内检测到闪白，验证非原点区域采集内容。正确长测进程于07:33:22 UTC启动，08:03:23 UTC正常停止：FFplay循环播放已知声画偏差20ms的 fixture，置于主屏左上角`(40,40,320,180)`；DDA只采此区域，WASAPI录默认系统回环。ffprobe确认视频H.264 320×180、60/1 CFR、1800.683333秒、108041帧、3,682,636 bytes；独立WAV为pcm_f32le、44.1kHz双声道、1800.532472秒、635,227,914 bytes。分析器解码整段媒体，以±250ms容忍窗做一一最近邻匹配，1072组检测到的闪白和1072组音调全部匹配。fixture实测5秒有3组cue，30分钟名义1080组，名义覆盖99.26%；另有8组未检出，原因未分类。median audio-minus-video为−45.790ms，P95绝对偏差70.790ms、P99为79.606ms、最大94.123ms，线性漂移+0.02696ms/min；首对−54.123ms、末对−47.457ms。长测100ms目标按P95、P99和最大值均通过；首/中/末三分段P95分别69.123/70.790/69.123ms，最大值85.790/94.123/85.790ms。长测前20秒12组cue的P95为65.623ms、最大67.457ms，未达到短片50ms目标；另一个15秒短预演9组cue、最大47.594ms。WASAPI是系统回环，可能包含FFplay以外的桌面应用声音，按音调最近邻匹配不能完全排除污染。进程Private Bytes采样范围451.8–463.3MB，working set为86.8–192.1MB；系统可用内存采样8.6–13.8GB。CPU计数器约108秒，即30分钟内平均约6.02%的单逻辑处理器负载；10秒采样间峰值11.93%。CSV UTC列覆盖07:33:42–08:03:19，但collector在07:38:41重启后elapsed计数归零。完整机器可读脉冲和性能报告为`artifacts/validation/long-capture/ffplay-sync-correct-region-30m-report.json`。其他证据文件：`artifacts/validation/long-capture/ffplay-sync-correct-region-30m-20261009.mp4`、同名`.wav`、`.sync-playback.json`、`ffplay-sync-correct-region-30m-analysis.csv`和`ffplay-sync-correct-region-30m-metrics.csv`。

独立WAV/屏幕短同步探针在置顶受控窗口上发5次低音量440Hz并闪白120ms，使用同一 Stopwatch 记录触发点，再以 MP4 帧PTS亮度变化和 WAV 带通音调起始点分析。五次均检测到，WAV事件相对软件触发大多约0–6ms；可见画面相对触发约109–127ms，匹配的 audio-minus-video 中位数为−119.32ms，P95绝对偏差125.654ms。该测量未达到50ms同步验收阈值。视觉事件包含UI状态改变、桌面合成及显示刷新延迟，FFmpeg progress 首帧时间也是粗略估计；不能把该偏差作为普遍固定校准值，也不能据此声称音画同步通过。

三次各约0.5秒的屏幕与WASAPI并行暂停实验中，ScreenRecorderLib/FFmpeg视频暂停边界与WASAPI暂停边界并不同步：每次音频暂停早约235–265ms，音频恢复早约471–485ms。视频媒体为60fps、342帧、5.700秒；WAV为44.1kHz双声道、5.937528秒。各轨道按自身 `PausedIntervals` 计算得到的有效持续时间分别与自身文件时长一致。Core/Media现已提供按独立时钟区间求交的分段映射；摄像头分段可按主屏幕source time映射到独立素材source time。Core和合成导出测试覆盖了分段、暂停空档及音轨输出，但这组设备实测的端到端同步仍待新长测结果确认，不能用算法测试替代设备验收。

屏幕 FFmpeg 暂停区间现以每段首帧 setpts 墙钟换算 Stopwatch 观测锚和封段 CFR frame count / fps 估算媒体边界，代替暂停/恢复操作返回时较晚的 Stopwatch 读数。该时钟是采样观测和媒体帧数估计，不是硬件 PTS。

2026-10-09 07:53:13 UTC 新版本三次 pause/resume 实测（1280×720输出，60fps）：四段分别为81帧/1.350000秒、66帧/1.100000秒、64帧/1.066667秒、66帧/1.100000秒，均为 H.264 60/1 CFR。按首帧观测到 Stop media-end ticks 扣除三段 `PausedIntervals` 计算的有效时长为4.6166662秒；四段时长之和和最终合并 MP4 时长均为4.616667秒，差约1微秒。Stop 操作观察时刻比媒体结束锚晚约53.9ms，已由新增 `StopObservedStopwatchTicks` 单独保留。此结果验证本机此路径的 CFR 分段时间映射，不验证其他帧率/硬件的首帧 PTS 精度；末段在暂停后停止，尾部空暂停没有计入媒体映射。

异常恢复探针于2026-10-09 07:17:57 UTC实测：录制并暂停一次使首个分段封装后，探针只终止自己启动的 FFmpeg 子进程；服务在约3秒内发布unexpected-exit错误并结束当前录制，恢复扫描找到 manifest 和 `segment-0000.mp4`，随后 `RecoverReadableRecordingAsync` 生成可读 MP4（135,286 bytes）。恢复扫描拒绝 manifest 或分段文件为 reparse point 的路径，并要求分段是 manifest 目录下的直接子文件。这个验证覆盖一次子进程强制退出，不代表断电场景。

App层 Display-only 恢复回归于2026-10-09 07:43:57 UTC通过：`CaptureTarget(Display, display.Id)` 不携带 bounds，服务在同一个 Per-Monitor-V2 DPI 上下文中枚举显示器，并直接使用匹配 Id 的物理 bounds。Release build成功，0 warning/error。恢复流程输出 UUID 工程`recovered-20261009-014446-971.frametrail`，projectId为`08779c62-6d16-4d18-a8f1-9d3b0c71b133`；`app-recovery.json`记录`Passed=true`，工程引用`media/screen.mp4`，无音轨、标记和指针事件符合探针配置。App媒体`media/screen-recovered.mp4`经ffprobe确认为H.264、1280×720、30/1 fps、2.066667秒、62帧、203,497 bytes。该工程和媒体是App层恢复结果；下述`probe-logs/display-id-regression`是单独的 API 探针，不能将其63帧输出归到这个UUID工程。

长测结束后再次运行独立 API display-only kill recovery，完整stdout和stderr保存在`artifacts/validation/recovery-parts-app-final/probe-logs/display-id-regression/stdout.log`和`stderr.log`，ExitCode=0。其恢复媒体 `probe-logs/display-id-regression/screen-recovered.mp4` 为 H.264 1280×720、30/1 fps、2.100秒、63帧、207,598 bytes；这组 API 输出与前述 App UUID 工程数据不同；原始采集段与manifest都保留在该目录下`screen.mp4.parts/<GUID>/`。

## 窗口录制排除验证

Probe 新增`--exclusion-test`，只在自控的纯红无边框窗口上测试。测试于2026-10-09 08:19:31 UTC在 Windows 10.0.26200.0 主显示器物理ROI `(640,300,320,180)`完成。窗口所属子进程调用`SetWindowDisplayAffinity`并回读：`WDA_NONE (0)`成功且读回0；`WDA_EXCLUDEFROMCAPTURE (0x11)`成功且读回17。FrameTrail 当前 DDA 区域录制解码得到中心红色样本`#FD0000`，设置排除后得到桌面背景样本`#181818`。同一状态下用 FFmpeg `gdigrab` 录桌面固定区域，中心样本从`#FF0000`变为`#181818`。ffprobe确认 DDA 基线片段为 H.264 320×180、30/1 fps、1.100秒/33帧，排除片段1.033秒/31帧。像素样本是按色度格式约束从中心2×2区域解码的第一个 RGB 像素。

这次实测说明本机的 FrameTrail DDA 和 FFmpeg gdigrab 都遵守这个窗口排除设置。可把它用于 FrameTrail 自己拥有的主窗体、录制条或备忘录窗口；跨进程调用失败，因为 Windows 要求目标顶层窗口属于调用进程，所以实际 WPF UI 应由拥有 HWND 的进程设置属性。该 API 依赖 DWM 合成，微软说明它不是安全或 DRM 边界，也不保证其他捕获实现都遵守。微软文档称 Windows 10 版本2004开始支持`WDA_EXCLUDEFROMCAPTURE`；具体可参考[SetWindowDisplayAffinity 官方文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)。FFmpeg 的 gdigrab 是基于 GDI 的 Windows 桌面/区域捕获设备，选项说明见[FFmpeg 官方设备文档](https://ffmpeg.org/ffmpeg-devices.html#gdigrab)。本次只验证当前 Windows、DDA后端和 FFmpeg gdigrab，不代表所有录屏软件、远程桌面、驱动层捕获或外部摄像设备。

像素证据和媒体在`artifacts/validation/window-exclusion-20261009b/`：`affinity.exclusion.json`包含API结果和DDA/GDI采样值；`affinity-affinity-none.mp4`与`affinity-affinity-excluded.mp4`是DDA控制/排除片段。Probe 窗口已关闭。首次测试因尝试从 H.264 4:2:0帧裁成1×1导致 FFmpeg crop filter 拒绝该奇数尺寸，未得到像素数据；正式测试使用2×2中心采样修复后通过。

## 七色色卡与录制光标实测

`artifacts/validation/cursor-shapes-v5/cursor-shapes.summary.json`记录了一次本机 DDA 区域屏幕采集：输出为 H.264、440×240、30fps、73帧、2.433333秒，使用 `h264_nvenc`。探针从录制解码帧中读取七个受控色块的中心像素；红、绿、蓝、灰、白、肤色和黑的最大 RGB 通道偏差依次为1、1、1、0、0、2、0/255，整体最大偏差2/255，低于预先设定的8/255阈值。该短样片验证了本机这一路径的像素保真度，不能推及其他硬件、驱动、分辨率或编码器。样片标签为 BT.709 矩阵/基色、limited (`tv`) 范围及 sRGB `iec61966-2-1` 传递曲线。

同一实测中，InputSampler 在物理点 `(268,185)` 依次记录 Arrow、Hand、IBeam；三种形态切换时位置保持不动，输入 JSONL 仍产生对应形态事件。色卡中心未被指针覆盖。样片和输入记录分别位于 `artifacts/validation/cursor-shapes-v5/cursor-shapes.mp4` 与 `artifacts/validation/cursor-shapes-v5/cursor-shapes.input.jsonl`。

NAudio `RecordingStopped` 收到异常时现在会立即将音轨状态置为 `Faulted` 并发布 `ErrorOccurred`，停止调用仍可封装已收到的数据并按时钟补静音。当前仅构建验证了该分支；真实设备热插拔及注入异常的运行时验证尚未完成。

## 附加验证：独立音轨与窗口目标

独立系统音轨 `frametrail-wasapi-loopback-pause-test-v2.wav` 经 ffprobe 检查仅含 `pcm_f32le` 音频流，44.1kHz 双声道、2.920 秒、1,030,234 bytes；Probe 在约4秒墙钟时间内录制，并暂停约1秒，暂停区未写入 WAV。它捕获本机生成的低音量440Hz音调，`volumedetect` 报告 mean -35.1 dBFS、max -32.0 dBFS。音轨文件直接由 NAudio WASAPI 写入，不生成视频轨或屏幕临时文件。WASAPI 格式由端点决定，本样本为浮点 PCM；麦克风设备已枚举但未实际打开采集。暂停状态按 WASAPI 数据包丢弃，无法从公开回调拿到 packet 内样本的独立时间戳，停顿边界误差受最大 packet 时长限制，结果警告会报告本次测得的上限。

本机 `WindowRecordingSource`（Windows Graphics Capture）对同一受控窗口连续输出全黑帧，既在探针同进程也在独立受控窗口进程复测，抽帧 YAVG 均为16；关闭边框提示选项后结果不变。因此当前 window target 使用 Desktop Duplication 显示器区域动态裁切 DWM 可见外框，100ms轮询 HWND 并通过 ScreenRecorderLib 动态源更新位置和尺寸。受控窗口移动后，末帧仍显示移动后的图案；区域测试 `(2400,1400,640,360)` 输出640×360 H.264、120帧/4秒，能看到测试图案。受控窗口关闭时会发 `CaptureError` 并自动完成素材封装；最小化时同样自动收尾。该回退按可见桌面像素录制：被其他窗口遮挡时会录入遮挡内容，窗口离开单个显示器或最小化时停止；本机只有一个显示器，跨屏行为未实测。关闭/最小化测试只操作本 Probe 自己启动的窗口。

`frametrail-independent-loopback-retry.mp4` 是旧 ScreenRecorderLib 音频实验遗留的诊断文件，虽然有 AAC 音轨，但 ffprobe 确认它也含真实桌面 H.264 画面；它和相邻的失败 `.mp4` 均不得作为独立音轨素材导入或发布。`frametrail-audio-wav-test.wav` 是切换 WASAPI 前 callback 验证的本地样本，不作为最终产品样片。
