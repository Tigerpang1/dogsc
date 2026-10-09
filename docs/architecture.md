# FrameTrail Windows 工程架构

FrameTrail 0.2.0 是 DogSC 原生 macOS 工具的 Windows 迁移实现。上游源码用于核对行为与导入字段，固定审计基线为 `upstream/dogsc` 提交 `e207d11b111c5e58152238fb64115b2009850547`，采用 MIT 许可。Windows 工程使用独立产品标识、项目格式和应用资源。

## 技术边界

- **界面和应用框架**：.NET 8 WPF，Windows x64。
- **项目与场景计算**：`FrameTrail.Core` 管理独立的 `FrameTrailProject` v1、时间线、鼠标校准、运镜、开场、效果模型、DogSC 有损导入映射及原子保存/备份恢复。
- **媒体与渲染**：`FrameTrail.Media` 用 FFmpeg 子进程解码和封装；Direct3D 11 shader 合成背景、画布、光标、点击效果、摄像头、贴图、柔化和窗口样机。静帧预览、实时预览和成片使用同一场景评估与 GPU 合成语义。实时预览使用持久 FFmpeg 解码会话，不先生成整段视频代理；有声播放会单独生成混音音频文件。
- **平台采集**：显示器和区域通过 FFmpeg `ddagrab` 调用 DXGI Desktop Duplication，编码器实际探测后按 NVENC/AMF/QSV/Media Foundation/CPU 回退。窗口模式通过 ScreenRecorderLib 跟踪可见桌面矩形；系统声音与麦克风使用独立 WASAPI 音轨，摄像头使用 Media Foundation。辅助窗口由所属进程设置 `WDA_EXCLUDEFROMCAPTURE`。
- **本机 FFmpeg**：验证机使用 Gyan 8.1.1 full static GPLv3 build。该构建仅用于本机验证；发布所需的对应源代码与许可审核尚未完成。

依赖版本及许可证见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。VC++ Runtime、Media Foundation、显卡驱动与干净 Windows 环境仍需安装矩阵验证。

## 数据流和持久化

静帧预览由 Core 按输出时间映射到源媒体时间，解码当前帧、加载项目素材并交给 `Scene.hlsl` 合成。实时预览复用同一评估器、素材加载器和 GPU renderer，FFmpeg 解码器在播放期间保持打开；导出按时间线 trim/setpts/concat/fps 生成帧，再逐帧合成并编码。音频根据同一时间线剪辑和重排，主媒体、摄像头、系统声音和麦克风可作为独立轨道混音。当前仍存在 CPU 解码、GPU 上传、staging 回读及编码上传，不宣称零复制。

FrameTrail 项目保存为自有 `.frametrail` JSON 格式，版本 1；它与上游 `.dogscproject` 不等价。DogSC schema v23–v27 可导入到项目副本，已映射字段和缺失项写入 `dogsc-import-report.json`，原项目不被修改。导入不代表效果完全兼容。项目内素材使用相对路径；解析器拒绝路径穿越与已存在的符号链接/重解析点。保存通过临时文件替换并保留上次版本备份。

指针原始记录使用 Stopwatch ticks 和频率，项目事件以秒及 0–1 归一化位置保存。视频首帧在最早的FFmpeg帧过滤器记录墙钟并换算Stopwatch时钟；每段有效结尾由最终帧数和帧率确定，避免将编码器退出等待计入媒体。视频、WASAPI、摄像头和指针分别保存开始/结束及暂停区间；`RecordingTimelineMapper` 按活动区间交集生成音轨分段与 CameraSyncSegments。单调时钟避免墙钟调整影响相对时间，但用户态观察、呈现和设备缓冲仍引入误差，端到端测量结果以采集报告为准。

## 已验证边界

Core、GPU 合成、实时预览、项目持久化、素材路径校验和范围导出有自动化用例。真实屏幕输入、暂停后编辑导出、30分钟1080p60视频采集、30分钟系统回环以及320×180区域视听同步都有本机报告。窗口与区域有自控探针；物理摄像头/麦克风、多显示器混合DPI、其它GPU和干净机器矩阵仍待设备验收。能力实现和真实硬件验收分别记载，见 `validation-report.md`。

第三方组件参考：[WPF](https://learn.microsoft.com/dotnet/desktop/wpf/overview/)、[Desktop Duplication](https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api)、[Windows Graphics Capture](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)、[WASAPI loopback](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording)、[Vortice](https://github.com/amerkoleci/Vortice.Windows)、[FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html)。

## 发布状态

本地技术预览不等于公开发行包。签名、清洁环境安装与卸载、第三方源码交付、FFmpeg 许可材料、自动更新、维护方署名和发布渠道尚未全部确定。发行信息以 [`build-and-release.md`](build-and-release.md) 为准。
