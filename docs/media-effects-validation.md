# FrameTrail 场景效果与媒体导出验收

本文记录当前 Windows 渲染器的自动化验证范围和已知边界。FrameTrail 项目格式是 Windows 自有格式；这些结果不代表与原版 DogSC 工程完全兼容，也不代表所有视觉效果逐像素复刻原版。

## 自动验收

执行 `dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj -c Release` 可验证源时间效果评估、开场输出时间、屏幕运动连续性、相机负时间线偏移、像素圆形尺寸映射、描边参数和模糊区域等核心规则。

执行 `dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release` 可编译 HLSL、建立 D3D11 渲染器并比较 GPU 输出像素。当前机器报告后端 `Direct3D 11 GPU hardware`；探针包含背景与渐变、网格图案、按 output time 改变的 Aurora 动态背景、裁切缩放、光标和点击环、不同画布尺寸下的光标比例、圆角与按像素宽高比修正的圆形相机、相机像素描边颜色、贴图 alpha 合成及贴图模糊像素差异、浏览器框、Phone/Tablet 独立机身和边框像素断言、开场变换、透视、区域马赛克，以及超出图层容量时的异常断言。画布外框保留原版 0..60 像素单位，相机描边为 0..18 像素；按画布高度缩放到预览或导出尺寸。背景图片模糊以 0..1 强度存储并映射到 shader 采样量，贴图模糊也使用同一GPU场景合成路径，测试分别渲染 Blur=0/1 并断言输出像素确实变化。最近一次 640×360 的 40 帧循环平均渲染加回读约 0.15 ms/帧。该时间只反映当前机器和探针负载，不是所有导出尺寸的性能承诺。

设置 `FRAMETRAIL_FFMPEG_DIR` 指向包含 `ffmpeg.exe` 和 `ffprobe.exe` 的目录后，同一探针会额外生成短测试素材并运行完整 `ExportEngine`。验证场景同时包含 3D 透视、聚焦虚化、区域马赛克、贴图、圆形裁切相机视频、开场缩放/偏移/透明度、图片背景、浏览器样机标题，以及一条独立音轨和主素材音轨。验收检查导出帧数为 36、成片时长约 1.2 秒、音轨存在，并成功解码输出帧。另用无音轨主视频和带音频的相机素材验证分段相机同步音轨的 `AudioOnly` 导出：生成约 1.2 秒的 M4A，且不含视频流，暂停间隔按映射保持静音。测试会在临时目录创建素材并在结束时清理。

渲染器和资源加载器共用 `FrameScene` 与 `FrameLayerInput`，导出已使用这条共享路径。GPU 当前最多接收 8 个前景图层、1 个后置背景图层，以及 4 个独立模糊区域。超过容量会抛出参数异常，不会静默忽略图层。背景图层超过一个也会明确拒绝。预览端需要把 `SceneAssetLoader.LoadAsync` 返回的图层传给 renderer 的图层重载，才能获得与导出相同的素材合成效果。

实时预览解码提供 `PreviewPlaybackSession.CreateAsync` 与 `ReadFrameAsync(outputTime, outputBgra)`。Session 持有 FFmpeg 顺序解码进程、`PreparedFrameSceneEvaluator`、`SceneAssetLoader` 和 GPU renderer；它按编辑后的时间线映射 output time 到 source time，遇到回跳或大幅跳时重启解码进程。调用者提供 BGRA 输出缓冲区，并负责 UI 播放时钟和音频同步。`SeekAsync` 会关闭当前解码器，下一次 `ReadFrameAsync` 按请求时间启动新的解码位置。解码子进程由 .NET `Process` 持有的安全进程句柄管理；取消读取会杀死并等待子进程退出，`DisposeAsync` 会序列化关闭解码器、素材加载器和 GPU 资源。

## 当前实现边界

屏幕倾斜使用像素着色器的投影变换矩阵近似平面 3D 透视，没有真实网格深度、遮挡关系或光照。聚焦虚化及区域模糊/马赛克由 GPU 着色器近似实现，参数和采样结果不保证与原版 Swift 实现完全相同。动态背景提供 Aurora、Ocean、Sunset 三种程序化预设，按输出时间和速度驱动；图案背景提供网格和点阵。开场支持 Custom、ZoomUp、Fade、SlideUp、RotateIn、Converge、SideSlide、Light3D 共8种预设，可在 `OpeningSettings` 上选择。相机支持 RoundedRectangle、Circle 及归一化源裁切。样机框支持窗口、浏览器、Phone 和 Tablet；手机/平板采用独立金属机身渐变、均匀边框和设备圆角，Render.Probe 对两种外观和边框宽度执行 GPU 像素断言。设备框没有传感器缺口或真实材质纹理。预览 session 的 seek 和编辑时间映射由短素材探针验证，长项目解码吞吐、UI 音画同步和不同显卡/WARP 设备仍需验证。集成探针不替代长视频压力测试或人工视觉验收。

## 最近结果

- Capture.Probe `cursor-shapes-v5` 实际录制七色色卡：DDA/NVENC H.264 样片为440×240、30fps、73帧、2.433333秒；解码七个色块中心像素的最大RGB通道偏差为2/255，低于8/255门槛。该项只覆盖本机短样片和当前编码路径，不构成跨设备色彩保证。
- `FrameTrail.Core.Tests`：通过。
- `FrameTrail.Render.Probe`：通过，实际后端为硬件 D3D11；Phone 与 Tablet 的独立设备框像素测试通过。
- FFmpeg 综合导出探针：通过，36 帧、独立音轨、输出帧解码均通过。
- 图层和模糊区域容量：超限时明确抛出异常。
- 无主素材音轨的 `AudioOnly`：仅独立音轨导出通过。
- 持久预览 session：跨 source discontinuity 映射、seek 重启和合成帧输出通过。
- 1080p 素材到 960×540 预览，静态与 `testsrc2` 动态源各连续读取 60 帧，场景含网格背景、浏览器标题、开场、3D 运动、聚焦虚化、贴图、相机、光标和模糊区域。最近运行两路平均每帧约 1.6 ms，p95 为 1.98 ms / 1.74 ms，低于 30 fps 的 33.33 ms 预算。
- 解码器标准输出暂停约 0.8 秒时，静态源 FFmpeg Private Bytes 从 179 MiB 到 179 MiB，动态源从 181 MiB 到 181 MiB；取消 seek 后进程已退出，下一次 seek 可恢复。
- 长时采集/音画同步结果见 `validation-report.md` 和 `capture-validation.md`：本机正确区域30分钟试验P95绝对偏差70.790ms、最大94.123ms，达到长时100ms阈值；启动前20秒P95为65.623ms，未达到短时50ms阈值。系统回环音频可能混入桌面声源，不能视作纯净的独立输入。

## 2026-10-09 音频时钟校正复核

有声预览由 `MainWindow.Playback.cs` 生成 AudioOnly 代理，代理与正式导出都经 `ExportEngine`，因此共用 `MediaTools.AudioFilter`、`MediaTools.AudioTrackFilter` 和 `AudioTrack.SyncSegments/SourceRate` 映射；预览再由 WPF `MediaElement` 播放代理并按其播放位置校正画面时钟。这里没有另一条 NAudio 预览音频滤镜链。单位速率不再插入 `atempo=1`；设备时钟 `SourceRate` 在 ±1000 ppm 内经 192 kHz 中间采样率重采样校正，超出此范围按素材映射速率保留音高使用 `atempo`，用户时间线速度始终独立处理。

在固定代码构建上，用 30 秒受控真实采集素材完成了保存、重载、AudioOnly M4A 导出和同一分析器的派生分析。原始 WAV 与导出 M4A 都配对 18/18 个事件；原始 P95 绝对 A−V 为 35.221 ms、最大 38.054 ms，工程映射成片 P95 为 32.917 ms、最大 40.000 ms。fixture 三个脉冲测得的中位偏差为 −15 ms，扣除后的 P95 估计分别为 20.695 ms 和 23.583 ms；这是短样本的估计，不能证明绝对物理同步精度，也不替代正在进行的长测。

同一固定构建的滤镜脉冲探针覆盖 15 种速率与时长组合：时钟校正最大脉冲误差在 30 秒为 0.042 ms、1800 秒为 2.253 ms；用户 2× 速度保持 440 Hz 音高，单独记录其内容相关起点偏差，不套用时钟校正门槛。结果文件位于 `artifacts/validation/capture-aligned-audio-clock-30s-v1/project-output-source-rate-v1/validation-summary.json`。这些数据适用于该构建和本机探针素材，不代表其他采集设备或新的长时样本。
