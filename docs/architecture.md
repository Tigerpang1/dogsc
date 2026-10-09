# FrameTrail Windows 工程架构

FrameTrail 是 DogSC 原生 macOS 工具的 Windows 迁移实现，内部产品标识为 FrameTrail，版本 0.1.0。上游源码用于理解行为和项目格式，固定审计基线为 `upstream/dogsc` 提交 `e207d11b111c5e58152238fb64115b2009850547`，采用 MIT 许可。Windows 应用使用自行制作的图标和独立应用身份，不沿用 DogSC 名称、图标或品牌资源。

## 已确定的技术边界

- **界面和应用框架**：.NET 8 WPF，Windows x64。
- **录制输入**：ScreenRecorderLib 7.0.1，使用 Windows DXGI/WGC 捕获、WASAPI 音频和 Media Foundation 编码能力。
- **GPU 合成**：Vortice.Direct3D11 3.8.3；当前 shader 实现背景、屏幕、圆角、阴影、光标与点击环。摄像头、贴图和 3D 仍属于后续扩展，尚未进入实际场景契约。
- **媒体读取和导出**：FFmpeg 作为独立子进程解码与 H.264/AAC 封装。进程边界不等于许可证隔离；随包二进制及其构建选项仍决定再分发义务。
- **本地验证 FFmpeg**：Gyan 8.1.1 full static GPLv3 build，只能用于本地验证；完整对应源代码和发布合规尚未确认。

依赖版本及许可证详见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。ScreenRecorderLib 还依赖 VC++ 运行库及 Windows Media Foundation；安装包只检测并提示，用户通过微软官方渠道自行安装系统运行库。

## 主要模块职责

`FrameTrail.Core` 承载平台无关的项目模型、时间线和运镜评估；`FrameTrail.Capture` 承接 Windows 捕获设备 API；`FrameTrail.Media` 管理媒体进程、帧解码、GPU 合成输出和导出。WPF UI 负责交互和视图状态，不应成为项目文件或导出时间线的第二数据源。

迁移的关键不变量是让编辑预览与导出从相同的项目时间、媒体片段和场景参数生成画面。业务状态先评估为统一场景描述，再由 Direct3D 11 shader 输出；FFmpeg 的输入切片、帧采样及音频时间戳要使用相同的输出时钟。设备捕获专属时间和系统 API 坐标在 Capture 边界规范化，核心模型使用明确的时间单位和 0–1 归一化坐标。

当前保存为独立的 `FrameTrailProject` v1、`.frametrail` JSON，**不宣称兼容上游 `.dogscproject`**。素材使用项目内相对路径，已实现备份与原子替换；原始鼠标事件另存 JSONL，完成录制后规范化后写入项目。上游 schema v23–v27 的字段、资源路径和版本迁移语义已经审计，后续只读导入需要真实 macOS 项目验证，成功前不得覆盖原项目。

## 决策依据与实际数据流

WPF 便于在本机 .NET 8 工具链上实现文件对话框、键盘交互、DPI 感知窗口和自包含部署。界面不承担屏幕截图录制或截图拼接导出；原生 DXGI/WGC 负责采集，FFmpeg 负责解码、AAC 音轨处理与 H.264 封装。

导出路径为：FFmpeg 按时间线 trim/setpts/concat/fps → BGRA 管道 → Direct3D 11 源纹理上传 → `Scene.hlsl` 合成 → staging 回读 → BGRA 管道 → FFmpeg 编码与音频封装。预览静帧和有声代理视频使用同一渲染器及 shader。此阶段有明确的 CPU/GPU 帧复制，没有宣称零复制；后续优化应以测量为依据，把解码表面、共享纹理和硬件编码器逐步衔接。

源时间、输出时间均以秒表示，所有鼠标原始事件同时保存 Stopwatch ticks 与频率。录制库没有暴露首帧 PTS 对应的单调时钟锚点，因此调用时钟仅是诊断基准，保留用户独立校准；不得将 Recording 通知延迟当作已知偏差。视频与音轨的时间线分别使用相同片段起止与速率，重排后再做封装。

屏幕和窗口输入使用物理像素；采样使用 `GetPhysicalCursorPos`，窗口边界优先使用 DWM 可见框；核心鼠标使用归一化坐标，WPF 控件布局使用 DIP。区域选择用物理鼠标点保存矩形。混合 DPI、负坐标和移动窗口必须实测才能确认支持。

系统声音和麦克风当前由 WASAPI 后端混合进主视频。摄像头计划使用 Media Foundation/原生设备源并保存独立媒体与时钟关系；独立音轨、设备热插拔和摄像头布局仍未完成。Core 保留时间线、活动分组、归一化构图等语义，动画与插值的近似差异在 `algorithm-port.md` 列出。

官方与组件主仓库依据：[WPF](https://learn.microsoft.com/dotnet/desktop/wpf/overview/)、[Desktop Duplication](https://learn.microsoft.com/windows/win32/direct3ddxgi/desktop-dup-api)、[Windows Graphics Capture](https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture)、[WASAPI loopback](https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording)、[窗口排除](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)、[ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)、[Vortice](https://github.com/amerkoleci/Vortice.Windows)、[FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html)。已验证能力与未验证部分以 `validation-report.md` 为准。

## 发布范围

便携包和 per-user 安装器均不要求管理员权限。安装目录为当前用户本地应用程序目录，系统 VC++ Runtime 由用户通过微软官方页面安装，Media Foundation 使用操作系统提供的组件。产品及安装器不静默部署全局运行库。

构建文件由 `scripts/build.ps1`、`scripts/test.ps1`、`scripts/package.ps1` 和 `installer/FrameTrail.iss` 管理。发行依赖清单保存在 `licenses/`，许可证审核状态以 [`build-and-release.md`](build-and-release.md) 为准。所有公开发行操作都必须在准确确认 FFmpeg 构建、第三方源码和许可材料后另行执行。

当前没有自动更新服务；手动安装新版前需保存并退出程序，项目与备忘录放在独立用户数据目录，卸载保留用户素材。未迁入原作者的 Sparkle 更新源、反馈提交接口、赞赏码或收款入口。正式产品名称与图标、Windows 维护方署名、签名更新渠道仍待发布阶段确认。
