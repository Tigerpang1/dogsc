# FrameTrail Windows 工程架构

FrameTrail 是 DogSC 原生 macOS 工具的 Windows 迁移实现，内部产品标识为 FrameTrail，版本 0.1.0。上游源码用于理解行为和项目格式，固定审计基线为 `upstream/dogsc` 提交 `e207d11b111c5e58152238fb64115b2009850547`，采用 MIT 许可。Windows 应用使用自行制作的图标和独立应用身份，不沿用 DogSC 名称、图标或品牌资源。

## 已确定的技术边界

- **界面和应用框架**：.NET 8 WPF，Windows x64。
- **录制输入**：ScreenRecorderLib 7.0.1，使用 Windows DXGI/WGC 捕获、WASAPI 音频和 Media Foundation 编码能力。
- **GPU 合成**：Vortice.Direct3D11 3.8.3；屏幕、摄像头、光标与贴图尽量经 GPU 共享像素进入统一 shader 合成路径。
- **媒体读取和导出**：FFmpeg 作为独立子进程解码与 H.264/AAC 封装。进程边界不等于许可证隔离；随包二进制及其构建选项仍决定再分发义务。
- **本地验证 FFmpeg**：Gyan 8.1.1 full static GPLv3 build，只能用于本地验证；完整对应源代码和发布合规尚未确认。

依赖版本及许可证详见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。ScreenRecorderLib 还依赖 VC++ 运行库及 Windows Media Foundation；安装包只检测并提示，用户通过微软官方渠道自行安装系统运行库。

## 主要模块职责

`FrameTrail.Core` 承载平台无关的项目模型、时间线和运镜评估；`FrameTrail.Capture` 承接 Windows 捕获设备 API；`FrameTrail.Media` 管理媒体进程、帧解码、GPU 合成输出和导出。WPF UI 负责交互和视图状态，不应成为项目文件或导出时间线的第二数据源。

迁移的关键不变量是让编辑预览与导出从相同的项目时间、媒体片段和场景参数生成画面。业务状态先评估为统一场景描述，再由 Direct3D 11 shader 输出；FFmpeg 的输入切片、帧采样及音频时间戳要使用相同的输出时钟。设备捕获专属时间和系统 API 坐标在 Capture 边界规范化，核心模型使用明确的时间单位和 0–1 归一化坐标。

项目保存兼容上游 `.dogscproject` 目录包、JSON 项目模型、媒体相对路径和逐行 JSONL 指针事件。上游 schema 当前 v27，支持 v23–v27；未来版本必须拒绝修改保存。存储层需保留缺省值、迁移备份、原子提交与已知有效快照恢复。不得将上游文件结构当作 Windows 代码许可或品牌资源许可。

## 发布范围

便携包和 per-user 安装器均不要求管理员权限。安装目录为当前用户本地应用程序目录，系统 VC++ Runtime 由用户通过微软官方页面安装，Media Foundation 使用操作系统提供的组件。产品及安装器不静默部署全局运行库。

构建文件由 `scripts/build.ps1`、`scripts/test.ps1`、`scripts/package.ps1` 和 `installer/FrameTrail.iss` 管理。发行依赖清单保存在 `licenses/`，许可证审核状态以 [`build-and-release.md`](build-and-release.md) 为准。所有公开发行操作都必须在准确确认 FFmpeg 构建、第三方源码和许可材料后另行执行。
