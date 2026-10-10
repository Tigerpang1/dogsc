# Windows 原生录制验证

## 当前候选验收状态（0.2.3开发候选）

0.2.3候选尚未完成发行包运行验收。当前30秒区域实录 `artifacts/validation/capture-aligned-audio-clock-30s-v1/raw-analysis.json` 检出18/18 cue，原始 A−V 中位数−9.7205ms、绝对P95 35.22095ms、最大38.054ms，覆盖率100%。刺激来自独立生成的 `aligned-capture-stimulus-v2`，其解码音视频cue起始差为+0.020833ms；本次实录尚未扣除fixture偏差。现行分析器基线为−15ms，因此该短测按原始值通过；它不是30分钟或实际工程素材验收。音频位置复位边界已修正代码路径并通过纯逻辑回归，麦克风已有一次15秒物理设备采集及一次含暂停实录，细节和限制见下文；物理设备热插拔/复位尚未验证。此前v8长测仅匹配485/1080 cue，明确作为旧版失败记录。旧20秒交互样本为全SILENT，仅作历史诊断；不能据此解释其他版本。没有物理摄像头、第二显示器、真实Mac工程或clean machine验收。

窗口录制已有两类当前证据。`artifacts/validation/wgc-first-frame-matrix-v1/matrix-summary.json` 在同一toolchain下对比未修复与修复版：控制组静态窗口只有1帧raw callback、0个source preview帧，解码网格全黑；修复组静态窗口同样只有1帧raw callback，但有15个source preview帧且解码非黑。两组动态画面均正常，支持首帧修复结论。`artifacts/validation/window-wgc-patched-product-v2/target.target.json` 记录产品WGC窗口目标实录通过：目标被不透明窗口遮挡及移动/缩放后，解码样点仍为目标图案颜色；输出H.264 640×360、30fps、5.933秒/178帧，关闭和最小化时自动收尾也通过。该探针使用PerMonitorV2；较早v1黑帧记录中的探针坐标受DPI虚拟化影响，不能沿用为当前失败结论。产品目标探针实际输入bin为`capture-audio-clock-build-v5`，manifest可对应Recorder DLL SHA `3d366dd95eeddde20bf66b6c0cc8b10e5f9bf7c9489e8d0b5aaee0c838118151`，但报告未直接记录加载程序集哈希；下一版probe应写入assembly SHA。两项结果均未覆盖多显示器。

统一后台回归日志 `artifacts/validation/net10-023-background-regression-v2.log` exit0，包含Capture纯逻辑回归；复位边界覆盖QPC倒退和无效时间戳回退，验证有效重锚tick不会早于已映射样本边界，停止映射帧数保持48000。此前漂移拟合由1.001切换到0.9995时，边界映射从1200480修正为1199760；QPC倒退至99秒或无效时间戳回退到99.5秒、而映射已到100.02秒时，停止帧数不再错误变为96960或72960。上述为源码和纯逻辑测试结果，不是物理设备复位测试。

## 实现与公共 API

`FrameTrail.Capture` 当前目标为 `net10.0-windows`，不依赖 `FrameTrail.Core`。主界面通过 `IFrameCaptureService` 枚举显示器、窗口和音频设备，构造 `RecordingRequest` 后调用 `StartAsync`，由状态事件观察启动、录制、停止和失败，再调用 `StopAsync` 取得 `RecordingResult`。窗口目标丢失会触发 `ErrorOccurred` 并自动正常封装；UI 可订阅 `RecordingCompleted`（`Action<RecordingResult>`）接收自动停止后的素材结果，也可读取 `LatestCompletedResult`。模型位于 `src/FrameTrail.Capture/Models.cs`。

显示器录制可通过 `IFrameCaptureService.CurrentCapturePrivateBytes` 读取当前 FFmpeg 子进程的 Private Bytes；暂停或无活动子进程时为 `null`。建议主UI同时采样主进程和此属性，按每个录制子进程及整个应用设置独立内存停止阈值。中断录制会在 `<输出MP4>.parts/<会话GUID>/manifest.json` 保留已正常封段的媒体段和错误摘要；`FrameCaptureService.FindRecoverableRecordings(directory)` 只读取此清单并跳过符号链接目录，`RecoverReadableRecordingAsync(info, destination)` 可将清单里的完整 MP4 段无损拼接为可编辑 MP4。被强制终止时当前未完成段只作为部分诊断文件保存，不列为可读段。成功完成的正常录制会清理 `.parts` 会话目录。

独立摄像头素材和独立音轨使用 `ICaptureTrackService`。摄像头输出 MP4，系统回环和麦克风各自输出 WAV；音频轨通过 NAudio 的 Core Audio 接口采集端点，不创建含屏幕画面的临时文件。`CaptureTrackHandle.MediaPath` 是 UI 应转换为项目相对路径的最终素材地址，`StartStopwatchTicks`/`StopwatchFrequency` 与 `CaptureTrackResult.StopStopwatchTicks` 提供单调时钟范围。摄像头 start tick 来自 ScreenRecorderLib Recording 状态回调，音轨 start tick 取 `StartRecording()` 前的单调时钟，作为 WAV 的时间零点。

当前映射把 `IAudioCaptureClient.GetBuffer` 的device position视为其文档定义的音频帧计数。`IAudioClock.GetFrequency` 只作诊断记录，不拿来换算GetBuffer位置；两者并无已确认的通用单位对应关系。若GetBuffer位置与本次mix frame count持续不符，映射器经有界检查后切换到QPC fallback：QPC及包FrameCount用于锚定和放置，连续包不会因到达抖动被移动，真实缺口仍保留。首帧QPC（100ns）也用于Stopwatch锚定和暂停边界换算；复位时保存原始计时诊断，同时以不早于已映射边界的有效tick重锚，活动段按有效重锚分段，暂停边界保持冻结。`artifacts/validation/microphone-device-023-v3/default-microphone.wav.diagnostics.json` 的一次实录观察到每包device position约+441而FrameCount为480，AudioClock frequency为384000；模式为`qpc-fallback`，不是把这组frequency误当GetBuffer帧率。该实录支持QPC回退与暂停处理在此端点可用，不证明语音质量、热插拔或普适端点行为。对应逻辑复位测试位于 `artifacts/validation/net10-023-background-regression-v2.log`；它不替代设备复位实测。20ms时钟泵保留100ms重排窗口，按样本时钟补静音、裁掉启动前样本及停止后的尾包；暂停区间按样本位置剪除。暂停期间仍按源时钟排空输入队列，避免录音暂停越久内存越大。包缓冲在同一采集线程的`finally`中释放后才进入应用回调。单位与缓冲规则依据[Microsoft GetBuffer 文档](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)。音频WAV使用端点实际采样率、通道数及PCM/IEEE-float格式，UI不应假定固定48kHz PCM16。音频设备时钟不消除视频首帧观察、播放器和声卡的端到端误差。

麦克风实录于 `artifacts/validation/microphone-device-023-v3/`：15.025秒墙钟录制，包含2.013秒暂停，WAV时长13.013秒，分成2个活动时钟段。诊断中1502个数据包、720954帧、0个timestamp错误/回退包，WAV有非零样本。这个结果只证明本次设备的数值采集、暂停裁切和QPC fallback，不是语音质量、设备热插拔或长期漂移认证。报告中的历史诊断字段名为`DevicePositionFrequency`和`ObservedPositionToMixRateRatio`，前者实际来自AudioClock frequency，后者按Mix/Observed比值计算；新诊断已改为更准确的字段名。

另一次30秒音画短测使用独立fixture `artifacts/validation/aligned-capture-stimulus-v2/`，0.5、2.0、4.0秒各有一个画面/音调cue。分别解码视频和PCM确认fixture cue起始差为0.020833ms。实录结果见 `artifacts/validation/capture-aligned-audio-clock-30s-v1/raw-analysis.json`，18/18匹配，A−V原始P95 35.22095ms、最大38.054ms、中位−9.7205ms；这组数尚未扣除fixture偏差。旧fixture结果 `capture-audio-clock-appsync-30s-v2` 的raw P95 52.5704ms及其派生时钟结果保留为历史；采集代理仍在检查`atempo`派生路径，不能把该历史结果或新独立刺激短测写成现有工程导出音画同步通过。当前30秒测试不代替30分钟长测。

## 历史候选：QPC包时钟长测记录（v8）

2026-10-09 的30分钟候选记录在 `artifacts/validation/capture-qpc-long-final-v8/qpc-av-30min-v2.report.json`。视频完成封装与全片解码：320×180、60/1、1800.383333秒、108023帧；WAV为48kHz双声道float、1800.23925秒。每5秒检查受控播放器是否覆盖录制区域，共360次；录像检测到1065个闪光。采集探针最高private约62.2MiB、编码进程约442.4MiB，采样未显示持续内存增长。

**这次长测不能算音画同步通过。** 名义1080个事件只匹配485个（44.9%），最后匹配在994.2秒，跨度993.066667秒，未满足预先设定的95%名义覆盖、90%双向检测覆盖及1700秒跨度。已匹配区间的原始A−V绝对P95为43.424ms、最大71.757ms，回归漂移−1.973ms/min仅适用于该区间，不能外推到30分钟。扣除fixture三个事件的中位偏差只是估计；原始数据和失败判定均保留。

原始PCM逐10秒数值分析发现930–980秒间有全零块，1030秒后持续全零；后474个闪光附近的频谱搜索没有通过1kHz刺激判据。最长RMS候选465ms，未发现大量背景声合并多个刺激导致漏检的证据。现有日志没有按时间记录Silent标志、补零帧数或被覆盖的迟到帧，且探针未保存ffplay stderr，无法区分声源/端点静音、缺包补零和迟包裁切；原因仍未定位。没有播放、转写或上传原始音轨。

该长测使用v8目录中固定的采集运行库，包含QPC放置、暂停裁切及启动/释放串行化；后续增加的通知订阅者异常隔离未包含在该二进制中。报告保留运行库与源码哈希，后续改动不能直接继承这次实采认证。20秒预检的12个闪光与12个音频事件均匹配，原始绝对P95为29.508ms，也不能替代长时覆盖验收。

`polish-capture-pure-final3.log` 记录 Core、导入器与四个采集纯逻辑入口通过：包时钟换算/裁切、诊断分桶、启动与释放并发、故障收尾。并发用例在无设备条件下阻塞启动、请求多次释放，确认所有释放等待同一任务且后续启动被拒绝。故障用例还检查状态及错误事件的第一个订阅者抛异常时，后续订阅者仍能收到通知，异常不会逃出通知派发。启动失败和停止异常均有资源清理路径；这些纯检查不能代替物理摄像头故障或设备热插拔验收。

final8增加与WAV同目录的 `<文件名>.wav.diagnostics.json`，每10秒记录包数、样本数、Silent标志样本、实际写入、迟到重叠裁切，以及时钟泵、包间隙和停止收尾的补零帧数。最多保存2160桶（6小时），超出后淘汰最早桶，并保留全程总计与淘汰数量。分桶和 `LastPacketElapsedMs` 使用回调到达时间，不是最后QPC样本位置。文件不包含原始音频、按键或文本；正常写入不增加界面素材警告，副档失败不会使有效WAV收尾失败。该诊断帮助下一次实录归因，不回填v8缺失证据，也不改变QPC映射。视听探针同时保存ffplay stderr，并在播放器取得前台时中止，避免未观测的播放器快捷键污染刺激。

探针可显式启用 `--sync-playback --interactive-playback`：准备阶段只激活自有播放器一次，以 `GetLastInputInfo` 的活动时间戳变化检测输入干扰，出现变化便中止；它不读取按键或文本。短测的 `--restore-own-playback-session` 要求循环播放且时长不超过30秒；长测使用单独的 `--long-restore-own-playback-session`，固定1800秒，并每5秒核对自有ffplay进程、loopback端点和会话音量。两种模式都要求测试期间无人操作，输入时间戳变化即中止。仅在自有ffplay `-volume 3` 基础上临时解除静音并将该进程会话音量设为1.0，保留约3%的播放器增益；结束时恢复原会话值，不改系统端点音量。默认模式不激活播放器并使用前台检查。所有模式都保留录制区域遮挡校验。未知参数和多余位置参数会在启动设备前报错，避免拼错探针模式时落入默认录制。

之后的20秒独立短测位于 `artifacts/validation/capture-qpc-interactive-v2/run20s/`：`qpc-interactive-20s-v2.mp4` 为320×180、60fps、20.205秒；`.sync-playback.json` 保存录制汇总，同名 `.wav.diagnostics.json` 保存分桶。2020个WASAPI包共969600帧，全部带SILENT标志；实际写入包来源帧966308，时钟泵补零1842，包间隙补零1005，重叠裁切282。时间戳回退和设备位置不匹配均为0，discontinuity为1。录制区域、前台和系统输入时间戳守卫未触发；正常收尾但没有可测音调，不算同步通过。`fixture-analysis.json` 则检测到源刺激的3次闪光与3个1kHz音调，排除fixture本身没有声音这一情况。

历史只读端点元数据 `capture-qpc-interactive-v2/audio-endpoint-metadata.json` 显示当时默认输出 `耳机 (Realtek USB Audio)` 未静音、主音量1.0。另一次5秒自有播放器会话诊断 `loopback-session-diagnostic-20261009-170820-retry/audio-session-diagnostic.json` 中，36次采样均为 `Mute=true`、`Volume=0.0`，路由为该默认loopback端点。该会话观测与旧20秒全静音采样不是同一次完整状态记录，不能确认静音是其原因，更不能解释后续30秒样本或其他版本。v8长测也没有会话状态记录，不能追溯断定后段全零音频原因。当前长测恢复选项按精确PID做会话租约并读回状态；历史诊断没有改变系统端点音量。

短测通过 `--restore-own-playback-session` 使用会话音量租约；仅当同时启用 `--sync-playback --interactive-playback --loop-playback` 且时长不超过30秒时可用。它只匹配本轮ffplay精确PID的唯一音频会话，并确认路由等于所选loopback端点；ffplay自身 `-volume 3`，进程会话临时取消静音并设为1.0（100%），从而保留约3%实际播放器增益。收尾时先恢复原会话值再结束自有播放器，不操作端点主音量。报告区分请求值和实际读回值；Apply异常保留回滚结果，恢复错误或读回不一致使本次测试失败。1800秒长测需要显式 `--long-restore-own-playback-session`，每5秒监视租约，要求全程无人输入并在任一输入变化时中止。默认不启用这些选项。

`polish-capture-pure-final4.log` 记录Core、导入器及六项采集纯测通过，Release构建0警告0错误。新增模拟会话测试覆盖正常/异常恢复、幂等、读写故障、无/多会话、其他PID和路由不匹配，未调用设备。`ffplay-volume-numeric-20261009-172159/attenuation-comparison.json` 对fixture进行离线幅度缩放；在线性×0.0009模型下，原分析阈值仍识别并配对3/3个音调，最小目标频带功率占比0.9492，高于0.85。该结果只验证离线检测余量，不证明Windows实际增益、系统回环中的信噪比或实录成功。

`--audio-packet-clock-test` 覆盖10MHz及非10MHz计数器、错误时间戳回退、跨暂停包、暂停内部开始的包、负起点和停止上限。三次15秒区域录制使用同一 `sync-export.mp4` 循环刺激，原始 A−V 绝对P95分别17.155、29.543、28.308ms；配对分别9、9、8组。检测闪帧数分别10、10、10，音频事件分别9、9、9，第三次有1组音频未配对，保留检测覆盖限制。证据在 `artifacts/validation/capture-qpc-candidate/sync-qpc-0*-analysis.json` 及逐事件CSV。

fixture本身三个事件测得A−V为−25、−35、−35ms。扣除其中位数−35ms后的估计P95分别45.225、51.124、40.441ms，最大值49.225、53.790、41.608ms。这是中位偏差估计，不是逐cue校准或绝对同步精度证明；一次略超50ms，不能报告短测全部通过。原始与估计两组数据都保留，不把估计偏差写成产品中的固定补偿。

另一次约30秒独立loopback测量含约10秒暂停，WAV为20.34175秒、48kHz双声道float。采样队列在暂停前、暂停中、恢复后的最大值均17包，中位数均15包，暂停期间没有持续增长；244次采样保存在 `audio-pause-queue-30s.csv`。对应结果有3014包，时间戳回退/错误均0、discontinuity为1、device position mismatch为0。可用 `tests/FrameTrail.Capture.Probe/audio-pause-queue-test.ps1` 复测。

初始化失败清理覆盖音频client初始化、WAV创建、session注册和开始事件。WAV采用 `CreateNew`，启动失败只清理本次创建的文件。`--audio-writer-failure-test` 使用占位目录触发实际 `UnauthorizedAccessException`，同一service随后成功录制并收尾有效WAV；证据为 `artifacts/validation/audio-writer-failure-v2/probe.log`。下文0.2.0的30分钟WASAPI指标属于旧到达时刻实现；v8也属于旧QPC候选，均不能替代0.2.3开发候选的长测。

显示器和区域使用外部 FFmpeg `ddagrab`（Windows Desktop Duplication API）采集，H.264/MP4由检测可用的FFmpeg编码器输出。当前窗口模式由ScreenRecorderLib `WindowRecordingSource` 使用Windows Graphics Capture捕获指定HWND；每100ms的监视任务只检查窗口有效性、最小化状态和DWM扩展边界，不从桌面重新裁切。WGC source自身跟踪HWND移动/尺寸变化，输出画布尺寸按录制启动时设置。窗口选取及输入坐标使用DWM边界和物理像素。光标与点击叠层关闭；输入跟踪另以约60Hz采样，输出JSONL记录 `Stopwatch` ticks、物理屏幕坐标、目标内归一化坐标、鼠标按键和修饰键，不采集键入文本。输入事件ticks与服务状态ticks使用同一进程单调时钟域。显示器/区域的 `RecordingResult.FirstFrameObservedStopwatchTicks` 当前由 `ddagrab` 首帧滤镜墙钟与进程启动附近的UTC/Stopwatch配对换算，属于首帧进入滤镜的观测估计，不是原生捕获PTS；`ProgressEstimatedFirstFrameStopwatchTicks` 是独立的progress反推诊断值，`FirstFrameNumber`为空。窗口路径优先以WGC source preview callback抵达时的Stopwatch时刻作为首图观察锚点，并读取该回调帧作诊断采样；录制帧回调可作为观察锚点回退，但帧号/墙钟回调时间不等于媒体PTS。两种路径都不能宣称精确曝光时刻或精确PTS。

区域录制要求矩形完整落在单个显示器内。窗口枚举和输入采样使用DWM扩展窗口边界及物理像素坐标；窗口目标每100ms检查HWND和边界状态，但不因此改写WGC source或重新裁切桌面。当前固定启动时的输出宽高；所选窗口的WGC源由ScreenRecorderLib跟踪目标。受控窗口移动、区域物理像素裁切和目标最小化/关闭行为的历史探针列在下文。设备枚举不会启用摄像头；本机相机枚举为空，未实际打开相机。

历史首帧回调样片 `first-frame-callback-smoke.mp4` 关闭预览位图后仍收到 `FrameNumber=1`；Record调用到帧回调抵达为442.811ms，录制状态回调为441.838ms，ffprobe检出H.264、3840×2160、约30.13fps、150帧、4.9781秒。它证明库回调在该次测试可用作fallback观察时刻，不代表当前WGC preview优先的窗口时钟路径，也不等于捕获时刻或编码sample PTS。`FirstFrameSystemTimestampUnixMilliseconds` 来自库事件，官方[帧回调实现](https://github.com/sskodje/ScreenRecorderLib/blob/master/ScreenRecorderLibNative/RecordingManager.cpp)使用 `system_clock::now()` 生成毫秒值，不能直接与 `Stopwatch` ticks比较，也不能当媒体时钟。

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
dotnet tests/FrameTrail.Capture.Probe/bin/x64/Release/net10.0-windows/FrameTrail.Capture.Probe.dll --duration 30 --fps60 --width1920 --metrics --output artifacts/validation/long-capture/sample.mp4
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

历史候选的一轮 DDA/NVENC 4K全屏输入并缩放至1080p60的30分钟测试通过：时长1800秒、108000帧。该测试验证当时本机4K显示器采集到1080p编码的长时视频路径；没有系统音轨，也没有受控音画同步测量。独立WASAPI loopback另有一轮30分钟测试通过。两项独立结果均不代表0.2.3当前候选的端到端视听同步；当前候选长测尚未完成。

首帧时钟锚点实验将 `setpts=PTS+0*print(time(0)*1000000*eq(N\,0))` 放在 `ddagrab` 后，滤镜输出首帧时打印 Unix 微秒，再用启动附近成对采样的 UTC/QPC 换算成 Stopwatch ticks；零乘数不改动帧 PTS。表达式在首帧打印非零 epoch 值、后续为0；输出 MP4 首帧 PTS 为0，后续按30fps递增。一次3秒 1280×720@30探针的首帧观测锚相对录制进程基线为314.924ms；同一录制中第一个progress反推锚为348.943ms，progress锚晚34.019ms。该差值只表示本机此轮短测，不能当固定校正值。ffprobe验证输出为30/1 CFR、92帧、3.066667秒。首帧墙钟观察换算不是硬件PTS，UTC/QPC配对和FFmpeg日志回调仍有有限精度。

历史候选的第二轮端到端同步试验首个长测于2026-10-09 07:23:50 UTC启动；区域路径最初把region bounds误传为display bounds，使 DDA `offset_x/y` 为0。发现后立即终止并保留错位片段作为 invalid-offset 诊断，不纳入结论。随后修复为把真实显示器 Bounds 传给 DDA recorder。`(640,300,320,180)`偏移短测中，FFplay无边框窗口实测矩形和 Region目标一致，命令行 DDA偏移为`(640,300)`，视频内检测到闪白，验证非原点区域采集内容。正确长测进程于07:33:22 UTC启动，08:03:23 UTC正常停止：FFplay循环播放已知声画偏差20ms的 fixture，置于主屏左上角`(40,40,320,180)`；DDA只采此区域，WASAPI录默认系统回环。ffprobe确认视频H.264 320×180、60/1 CFR、1800.683333秒、108041帧、3,682,636 bytes；独立WAV为pcm_f32le、44.1kHz双声道、1800.532472秒、635,227,914 bytes。分析器解码整段媒体，以±250ms容忍窗做一一最近邻匹配，1072组检测到的闪白和1072组音调全部匹配。fixture实测5秒有3组cue，30分钟名义1080组，名义覆盖99.26%；另有8组未检出，原因未分类。median audio-minus-video为−45.790ms，P95绝对偏差70.790ms、P99为79.606ms、最大94.123ms，线性漂移+0.02696ms/min；首对−54.123ms、末对−47.457ms。该历史样本的100ms长测目标按P95、P99和最大值均通过；首/中/末三分段P95分别69.123/70.790/69.123ms，最大值85.790/94.123/85.790ms。长测前20秒12组cue的P95为65.623ms、最大67.457ms，未达到短片50ms目标；另一个15秒短预演9组cue、最大47.594ms。WASAPI是系统回环，可能包含FFplay以外的桌面应用声音，按音调最近邻匹配不能完全排除污染。进程Private Bytes采样范围451.8–463.3MB，working set为86.8–192.1MB；系统可用内存采样8.6–13.8GB。CPU计数器约108秒，即30分钟内平均约6.02%的单逻辑处理器负载；10秒采样间峰值11.93%。CSV UTC列覆盖07:33:42–08:03:19，但collector在07:38:41重启后elapsed计数归零。完整机器可读脉冲和性能报告为`artifacts/validation/long-capture/ffplay-sync-correct-region-30m-report.json`。其他证据文件：`artifacts/validation/long-capture/ffplay-sync-correct-region-30m-20261009.mp4`、同名`.wav`、`.sync-playback.json`、`ffplay-sync-correct-region-30m-analysis.csv`和`ffplay-sync-correct-region-30m-metrics.csv`。

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

独立系统音轨 `frametrail-wasapi-loopback-pause-test-v2.wav` 经 ffprobe 检查仅含 `pcm_f32le` 音频流，44.1kHz 双声道、2.920秒、1,030,234 bytes；Probe在约4秒墙钟时间内录制，并暂停约1秒，暂停区未写入WAV。它捕获本机生成的低音量440Hz音调，`volumedetect`报告mean -35.1dBFS、max -32.0dBFS。音轨文件直接由NAudio WASAPI写入，不生成视频轨或屏幕临时文件。WASAPI格式由端点决定，本样本为浮点PCM。该段最初记录时麦克风尚未实际打开；麦克风设备已在后续 `microphone-device-023-v3` 做15秒含暂停实录，详见当前候选状态段。暂停状态按WASAPI数据包丢弃，无法从公开回调拿到packet内样本的独立时间戳，停顿边界误差受最大packet时长限制，结果警告会报告本次测得的上限。

早期窗口探针的黑帧报告属于旧实现/旧探针。`wgc-first-frame-matrix-v1` 的同toolchain矩阵及 `window-wgc-patched-product-v2` 的PerMonitorV2产品探针见当前候选状态段，已证明该目标窗口在本次遮挡和移动/缩放场景下仍有正确画面，并在关闭/最小化时自动收尾。早期v1探针按物理坐标启动却未声明PerMonitorV2，坐标被再次DPI缩放，导致窗口刺激位置错误；v2已显式初始化PerMonitorV2。上述实测不覆盖多显示器和跨DPI显示器行为。所有窗口关闭/最小化操作仅针对探针自建窗口。

`frametrail-independent-loopback-retry.mp4` 是旧 ScreenRecorderLib 音频实验遗留的诊断文件，虽然有 AAC 音轨，但 ffprobe 确认它也含真实桌面 H.264 画面；它和相邻的失败 `.mp4` 均不得作为独立音轨素材导入或发布。`frametrail-audio-wav-test.wav` 是切换 WASAPI 前 callback 验证的本地样本，不作为最终产品样片。
