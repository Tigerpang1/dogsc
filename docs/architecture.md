# FrameTrail Windows 工程架构

FrameTrail 0.2.3 是 DogSC 原生 macOS 工具的 Windows 开发候选，发行包运行验收尚未完成。上游源码用于核对行为与导入字段，固定审计基线为 `upstream/dogsc` 提交 `e207d11b111c5e58152238fb64115b2009850547`，采用 MIT 许可。Windows 工程使用独立产品标识、项目格式和应用资源。

## 技术边界

- **界面和应用框架**：.NET 10 WPF，Windows x64。
- **项目与场景计算**：`FrameTrail.Core` 管理独立的 `FrameTrailProject` v1、时间线、鼠标校准、运镜、开场、效果模型、DogSC 有损导入映射及原子保存/备份恢复。
- **媒体与渲染**：`FrameTrail.Media` 用 FFmpeg 子进程解码和封装；Direct3D 11 shader 合成背景、画布、光标、点击效果、摄像头、贴图、柔化和窗口样机。静帧预览、实时预览和成片使用同一场景评估与 GPU 合成语义。实时预览使用持久 FFmpeg 解码会话，不先生成整段视频代理；有声播放会单独生成混音音频文件。
- **平台采集**：显示器和区域通过 FFmpeg `ddagrab` 调用 DXGI Desktop Duplication，编码器实际探测后按 NVENC/AMF/QSV/Media Foundation/CPU 回退。窗口模式使用 ScreenRecorderLib 的 `WindowRecordingSource`（Windows Graphics Capture）捕获指定 HWND；独立监视任务每100ms检查目标是否仍有效、未最小化且窗口边界可读，不把窗口裁成桌面矩形或每次轮询重新裁切。DWM扩展边界用于窗口选择、输入坐标和有效性检查。系统声音与麦克风使用独立 WASAPI 音轨，摄像头使用 Media Foundation。辅助窗口由所属进程设置 `WDA_EXCLUDEFROMCAPTURE`。
- **本机 FFmpeg**：验证机使用 Gyan 8.1.1 full static GPLv3 build。该构建仅用于本机验证；发布所需的对应源代码与许可审核尚未完成。

依赖版本及许可证见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。VC++ Runtime、Media Foundation、显卡驱动与干净 Windows 环境仍需安装矩阵验证。

## 数据流和持久化

静帧预览由 Core 按输出时间映射到源媒体时间，解码当前帧、加载项目素材并交给 `Scene.hlsl` 合成。实时预览复用同一评估器、素材加载器和 GPU renderer，FFmpeg 解码器在播放期间保持打开；导出按时间线 trim/setpts/concat/fps 生成帧，再逐帧合成并编码。音频根据同一时间线剪辑和重排，主媒体、摄像头、系统声音和麦克风可作为独立轨道混音。当前仍存在 CPU 解码、GPU 上传、staging 回读及编码上传，不宣称零复制。

FrameTrail 项目保存为自有 `.frametrail` JSON 格式，版本 1；它与上游 `.dogscproject` 不等价。DogSC schema v23–v27 可导入到项目副本，已映射字段和缺失项写入 `dogsc-import-report.json`，原项目不被修改。导入不代表效果完全兼容。项目内素材使用相对路径；解析器拒绝路径穿越与已存在的符号链接/重解析点。保存通过临时文件替换并保留上次版本备份。

指针原始记录使用 Stopwatch ticks 和频率，项目事件以秒及 0–1 归一化位置保存。显示器/区域首帧观察锚点来自FFmpeg首帧过滤器的墙钟与进程时钟配对；窗口首帧观察锚点来自WGC source preview回调被应用观察到的Stopwatch时刻，若该回调未到达则使用录制帧回调。两者都不是原生捕获PTS。每段有效结尾由最终帧数和帧率确定，避免将编码器退出等待计入媒体。视频、WASAPI、摄像头和指针分别保存开始/结束及暂停区间；`RecordingTimelineMapper` 按活动区间交集生成音轨分段与 CameraSyncSegments。

独立 WASAPI 音轨按 `IAudioCaptureClient.GetBuffer` 返回的设备帧位置连续放置样本；包首帧 QPC（100 ns）用于把设备样本时钟锚到当前进程的 Stopwatch ticks、估算漂移并映射暂停边界。普通连续包的位置不随包级QPC抖动移动；只有QPC无效时才用回调到达时间减包时长作为锚点回退，设备位置复位或数据断续也会重建映射锚点。20 ms 时钟泵保留 100 ms 重排窗口，按样本时钟补静音并裁掉启动前、停止后的样本；暂停区间按样本位置从输出中剪除。设备时钟/位置复位边界已修正代码路径，并通过纯逻辑回归；物理设备复位尚未实测。这不代表端到端同步的设备验收已完成。单调时钟避免墙钟调整影响相对时间，但用户态观察、呈现和设备缓冲仍引入误差，测量结果按对应报告及实现版本解释。

效果片段有独立于主视频段的源时间分割操作：缩放、屏幕运动、摄像头运动、贴图和柔化分别保留片段参数并创建右段 ID；应用先将播放头输出时间映射回源时间，再针对所选效果修改工程快照，以支持撤销/重做。运动分割会采样切点状态以衔接两侧；摄像头和音轨本身不由此操作分割，界面会说明当前不支持的选择。

每个 `SceneAssetLoader` 为图片与窗口/浏览器标题图层共用最多 256 MiB 的字节 LRU 缓存。超预算单张图仍可供当前帧使用，但不会保留在缓存中；这个额度仅限制缓存持有的像素数组，不是整个进程或渲染帧的内存上限。实时预览以 gate 串行加载素材、合成和替换 renderer；关闭窗口时先取消预览，再等待预览 gate 释放后销毁 loader 和 renderer。

## 已验证边界

0.2.3后台统一回归 `artifacts/validation/net10-023-background-regression-v2.log` exit0，Core、DogSC构造fixture导入、GPU/Render、时间线资产20项、逐片段音量、合成媒体Integration、VisualProbe、Capture纯逻辑、发行清单16项和源码身份6项检查通过。WPF离屏主题与时间线像素探针15/15，窗口未Show且没有HWND，PNG证据已保存；新版时间线没有新的实机主窗口截图。实机浮层v6为19/19。复位边界修正有纯逻辑回归，物理设备复位仍待测。

0.2.2 的贴图背景处理将画布/屏幕、摄像头分别保留在 GPU 中间纹理；各自执行水平和垂直高斯柔化后再叠加清晰贴图。隐藏屏幕保留项目背景，隐藏摄像头连同其阴影一起淡出。四个中间纹理按需创建复用，最终只回读一次；未启用这类处理时沿用单次绘制。shader 字节码在进程内缓存，编辑和导出重建 renderer 不会重复编译。

Core、GPU 合成、实时预览、项目持久化、素材路径校验和范围导出有自动化用例。显示器采集、暂停后编辑导出和历史30分钟媒体测量有本机报告；当前QPC实现有30秒18/18短测，当前版本长时同步仍待测。窗口探针在同进程自有窗口与子进程目标间有不一致结果，且条件混杂，尚不能归因边界；同条件raw WGC/SLR矩阵未做。物理摄像头/麦克风、多显示器混合DPI、其它GPU、真实Mac工程和干净机器矩阵仍待验。见 `validation-report.md` 与 `capture-validation.md`。旧版本长测不自动继承到0.2.3。

第三方组件参考：[WPF](https://learn.microsoft.com/dotnet/desktop/wpf/overview/)、[Desktop Duplication](https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api)、[Windows Graphics Capture](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)、[WASAPI loopback](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording)、[Vortice](https://github.com/amerkoleci/Vortice.Windows)、[FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html)。

## 发布状态

本地技术预览不等于公开发行包。签名、清洁环境安装与卸载、第三方源码交付、FFmpeg 许可材料、自动更新、维护方署名和发布渠道尚未全部确定。发行信息以 [`build-and-release.md`](build-and-release.md) 为准。
