# DogSC 原源码审计

## 审计范围与基线

审计对象为只读子模块 `upstream/dogsc`，基线提交 `e207d11b111c5e58152238fb64115b2009850547`。上游工作区无未提交改动。`README.md` 将 DogSC 描述为面向 Apple Silicon、macOS 14+ 的原生录屏与轻量剪辑应用；`Package.swift` 使用 Swift 6、macOS 14，核心拆为 `RecorderCore` 和 `DogSCApp`，并固定依赖 Sparkle 2.9.4（见 `Package.resolved`）。最初采集验证采用 ScreenRecorderLib 7.0.1；0.2.0 最终方案为 .NET 8 WPF、FFmpeg ddagrab（显示器/区域 Desktop Duplication）、ScreenRecorderLib（窗口可见矩形及摄像头 Media Foundation）、独立 NAudio WASAPI 音轨，以及 Vortice.Direct3D11 3.8.3 GPU 合成。FFmpeg 子进程负责解码、硬件编码与 H.264/AAC 封装，首帧和暂停映射见架构说明。本机 FFmpeg 8.1.1 Gyan GPLv3 full build 仅用于本地验证。此处记录的是方案和审计约束，不代表任何 Windows 功能已实现。

## 关键模块及语义

| 上游文件 | 责任与必须保留的行为 | Windows 移植风险 |
|---|---|---|
| `Sources/RecorderCore/AutoZoomPlanner.swift` | 把左、右键点击映射成自动缩放片段；过滤负数/非有限时间并排序；默认缩放 1.6、组间隙 1.4 秒、安全边距 0.12、进入/退出时长默认 0.7 秒、保持 0.9 秒。相邻点击先按时间分组，再按空间焦点分组；连续的自动片段折叠为单一连续镜头。若点击后指针事件间隔不超过 1.4 秒，则延长末段保持并留 0.55 秒收尾。历史修复只识别已知、未编辑的自动轨道，避免覆盖用户手动改动。 | 不能把算法简化成“每次点击一个缩放”；否则快速连续操作会产生停顿。时钟单位、排序稳定性、片段边界和旧项目修复识别都要一致。`ZoomEasingPreset`、自定义曲线也属于项目语义。 |
| `Sources/RecorderCore/PointerTrack.swift` | 以归一化位置事件构造指针轨道；按事件时间插值，长静默（超过 0.12 秒）会断开移动，只有短距离慢速移动允许最多 0.50 秒间隔。对平滑光标预计算弹簧轨迹并限量缓存；点击锚点校正、点击反馈、光标资产标识、运动造成的轻微旋转均由轨道评估提供。 | 事件稀疏度和休止间隔决定因果性，不能跨长静默插值。弹簧采样时钟、浮点误差、缓存取消与 UI 线程负载都需验证。 |
| `Sources/DogSCApp/PointerEventRecorder.swift` | 使用全局事件 tap，权限不足时有 NSEvent 监视回退；采集鼠标移动、点击、修饰键、光标形状变化。事件以录制起点为零点；暂停期间累计并从时间中扣除。捕获范围可随窗口变化，位置限制并归一化到 [0,1]；Quartz 与 AppKit 的 Y 轴方向不同，源码显式翻转。移动事件按节流策略压缩，非单调时最终排序。 | Windows Raw Input/低级钩子权限和系统光标采集行为不同；DPI 感知、多显示器原点、窗口移动、暂停恢复和事件丢失需单独适配。不可直接照搬 macOS 坐标公式。 |
| `Sources/RecorderCore/ProjectTimeline.swift` | 时间线单一持久化所有者：主素材序列、分段音量覆写、缩放、屏幕运动、摄像运动、马赛克和贴图片段。片段输出起点由顺序累计时长推导，不再持久化第二套易冲突的位置。速度区间 1×–100×。 | Windows 编辑器需让预览、波形、播放、导出均读取同一时间线解释，避免 UI 自己维护第二套时间语义。 |
| `Sources/RecorderCore/FrameScene.swift` | `FrameSceneEvaluator` 在给定项目时间、画布、素材比例及轨道状态下，生成后端无关的不可变画面场景与图层顺序；包括背景、屏幕、摄像头、光标、贴图、柔化/遮挡等。它将项目时间线解释与渲染后端隔开。 | WPF 预览和 FFmpeg 导出必须共享场景评估语义；不同图形后端的坐标、透明度、滤镜顺序和色彩空间可能产生不一致。 |
| `Sources/RecorderCore/CompositionScene.swift` | 评估屏幕/摄像头在画布中的几何、裁切、留白、边框与装饰，输出统一坐标。摄像头布局也有独立评估入口。 | 需定义像素中心、画布坐标原点、宽高比填充、圆角阴影外扩及多 DPI 对应关系。 |
| `Sources/RecorderCore/ScreenProjection.swift` | 通过投影四边形及映射器在屏幕平面和画布间投影/反投影；支持命中测试和语义缩放拖动。透视为零时走轴对齐语义。 | 透视矩阵、退化四边形、病态逆矩阵和屏幕 3D 下光标附着必须一致；图形 API 的 Y 轴转换需集中处理。 |
| `Sources/RecorderCore/ZoomAnimation.swift` | 定义归一化点、点击事件、缩放片段、Bezier 曲线、缓动预设、缩放轨道与采样器。片段含进入/保持/退出时间、进入/退出相位偏移、聚焦效果和首选转场长度；相接片段链式过渡。JSON 解码对旧字段设置默认值。 | 区间端点、动画相位、弹簧曲线和旧值缺省决定项目打开后的观感。将时间线动画按帧率量化保存会累积漂移。 |
| `Sources/RecorderCore/ProjectModel.swift` | `RecorderProject` 及设置/素材模型。项目 schema 最低支持 v23、当前 v27；拒绝旧于 v23 及高于 v27 的项目。解码器有逐字段兼容默认值。 | 不可把未知未来版本降级后回写；旧字段缺省和版本迁移必须保留，并在载入失败时保护原始数据。 |
| `Sources/DogSCApp/ProjectStore.swift`、`ProjectDocument.swift` | `.dogscproject` 是目录包：`project.json` 为项目元数据，`media/`、`events/`、`recovery/` 保存素材、事件和恢复信息。指针事件使用 JSON Lines；资源以包内相对路径引用。项目 JSON 暂存后原子替换，保留上一有效修订供恢复；会话 epoch 和递增 revision 防止关闭/移动后的迟到自动保存覆盖新状态。打开旧 schema 时写迁移备份。 | Windows 路径规范、大小写、原子替换、文件占用、移动中并发写入和外部编辑器都与 macOS 不同。需验证相对路径安全、损坏恢复、崩溃时一致性及 schema 前向拒绝。 |
| `Sources/DogSCApp/ScreenRecorder.swift`、`NativeScreenRecording.swift`、`ScreenCaptureOutput.swift` | 基于 ScreenCaptureKit 录制显示器/窗口/区域，维护启动/暂停/恢复/停止状态，处理流中断、捕获区域更新、帧时间和系统声音。`README.md` 说明辅助窗口排除策略以及录制中可编辑既有项目。 | Windows 捕获 API 的窗口排除、最小化窗口、HDR、独占全屏、多显示器混合 DPI 和系统声音回环行为不同。录制 API 需与 UI 生命周期隔离，并保留故障恢复状态。 |
| `Sources/DogSCApp/CameraRecorder.swift`、`MicrophoneRecorder.swift`、`CoreAudioSystemAudioTap.swift` | 摄像头、麦克风和系统声音可按需录制为独立素材；摄像头带方向、画面黑边分析及时间同步，音频可分别静音、调节音量。 | DirectShow/Media Foundation 与 FFmpeg 的设备时钟和音频时基可能不同。跨轨同步、暂停分段、设备热插拔和无摄像头素材路径均需验证。 |
| `Sources/DogSCApp/SharedFrameRenderer.swift`、`SharedFrameCompositor.swift` | 将 `FrameScene` 降低为 Core Image 图层；背景、屏幕、摄像头、光标、贴图按场景顺序合成。贴附屏幕的光标先并入屏幕层再共同透视，轴对齐路径有兼容快路；背景图案和文字图层带缓存。 | FFmpeg 滤镜图或 Direct2D 需要逐项匹配层序、裁切、透视、模糊、阴影、色彩管理和字体栅格差异。输出至少要对代表性帧做图像差异核验。 |
| `Sources/DogSCApp/DirectExportPipeline.swift`、`TimelineCompositionBuilder.swift`、`VideoExporter.swift` | 直接读取组合媒体并按固定输出帧表评估场景，逐帧渲染合成后编码；音频另队列处理，时间戳按选区偏移重置。切片边界以 60k timescale 比较，避免 VFR 切点误判。包含取消闩锁、进度、阶段、看门狗和短时背压重试；超过 4096 像素边长倾向 HEVC，否则 H.264。 | FFmpeg 输入 seek、VFR 采样、音视频时间戳、编码器能力、颜色元数据、取消收尾和部分文件回滚语义需重新设计。逐帧导出容易成为性能瓶颈。 |
| `Sources/DogSCApp/AudioOnlyExportPipeline.swift` | 音频导出单独处理，README 标明支持 M4A。 | 需验证 FFmpeg 编码器、声道布局、采样率、分段音量/静音、暂停与剪切对齐。 |
| `Sources/DogSCApp/AppUpdateController.swift` | README 中软件更新入口由 Sparkle 提供；正式更新源签名和发布密钥不在源码内。 | Windows 更新安装、签名、回滚和发布通道需单独设计，不能复用 Sparkle 运行时。 |

## 项目 JSON 与兼容要求

主项目文件为 `.dogscproject/project.json`，序列化为 JSON；当前 `ProjectSchema.currentVersion == 27`，读取范围 v23–v27。未知未来版本会拒绝打开以避免旧程序覆盖新字段。模型大量使用 `decodeIfPresent` 提供默认值，因此 Windows 端要逐模型对照 CodingKeys 和解码器，不能只按当前对象结构一次性反序列化。尤其需要保留 UUID、原始枚举字符串、可选字段、Double 时间值、相位偏移及相对路径。`ProjectTimeline` 中 `sourceSequence` 缺失按完整录制处理，其余各轨缺失按空数组；分段速度缺失按 1×。

指针文件是 `events/pointer.jsonl`：每行一个 `PointerEventRecord` JSON 对象，而非整体 JSON 数组。事件缺少 modifiers 时按空数组读取，光标资产 ID 可缺失。录制素材在 `media/`，恢复清单在 `recovery/manifest.json`。具体相对路径和可选媒体成员以 `ProjectMediaModel.swift`、`ProjectStore.swift` 为准。打开时对旧项目保存迁移备份，并可在主项目损坏或解码/校验失败时回退到上一已知有效修订。未来版本拒绝、迁移备份和回退快照共同构成数据保护边界。

## 资源授权与非代码交付物

根 `LICENSE` 是 MIT，完整文本已复制到 [`licenses/DogSC-MIT.txt`](../licenses/DogSC-MIT.txt)。字体位于 `Resources/Fonts/`：Alibaba PuHuiTi 3.0 Regular、Medium、SemiBold 三个官方 OTF，说明其免费商业使用许可；禁止字体文件单独定价出售/出租/转让、修改法律声明及逆向工程。随附 `Resources/Fonts/LICENSE.txt` 中英文法律声明原文及 `README.md` 来源、SHA256 校验文件，迁移时若继续捆绑字体，需原样保留声明并核对哈希；也可改用系统字体，但要确认中文排版一致。字体许可不是 MIT/OFL。

源码包还含有 `Resources/Onboarding/SoftLight.png`、`Resources/Onboarding/SOURCE.json`、`Resources/Support/AppreciationCode.jpg`、应用图标、DMG 背景和文档截图。当前仓库文件清单/README 未给这些图像逐项附独立许可文本；在复制到 Windows 发行包前应查清来源和再分发权。`Package.resolved` 锁定 Sparkle 2.9.4，属外部依赖，需随 Windows 方案替换更新机制。

Windows 依赖初审：NuGet 的 [ScreenRecorderLib 7.0.1 页面](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1)标示 MIT；其[上游仓库许可证](https://github.com/sskodje/ScreenRecorderLib/blob/master/LICENSE)也为 MIT。该包页面说明需要 Visual C++ Redistributable 与 Media Foundation；Windows N/KN 和 Server 需要额外媒体组件。NuGet 的 [Vortice.Direct3D11 3.8.3 页面](https://www.nuget.org/packages/Vortice.Direct3D11/3.8.3)标示 MIT，并列出 net8.0 兼容目标。发布前仍须锁定最终包及传递依赖版本并归档对应许可证文本，尤其确认 SharpGen.Runtime 等传递项的声明。

本机 FFmpeg 8.1.1 Gyan full build 仅用于本地验证；[Gyan 构建说明](https://www.gyan.dev/ffmpeg/builds/)说明其 Windows 静态构建为 GPLv3。若发行包继续使用 GPL 构建，须按实际构建履行 GPL 源码获取/提供及对应组件义务，并评估应用分发方式与 GPL 义务是否兼容。改用 LGPL 构建也需确认实际编译选项、链接/调用方式和专利要求，不能仅凭“FFmpeg”名称判断。FFmpeg 是独立子进程的技术事实不能代替针对实际构建和分发方式的许可证审查。系统壁纸、用户录制和项目素材明确不随源码发行。

## 更新反馈与捐赠入口

README 明确把「关于」窗口作为版本、更新、源码和反馈入口，且「请杯咖啡」显示自愿赞赏码，导出完成后也可以打开。源码资源中有 `Resources/Support/AppreciationCode.jpg`。迁移时应在功能映射中保留反馈和更新入口，并评估捐赠码图片的所有权、支付平台适用性与 Windows 版本是否继续展示；不能把自愿支持入口改成付费门槛。原仓库的 Issues 地址为 `https://github.com/laogou717/dogsc/issues`。

## 主要迁移风险

1. **媒体时钟和时间线一致性**：录制、指针、摄像头、音频和动画分别有时间基准；暂停时指针时间需扣除暂停段，编辑速度变化只改变输出时长，运动轨道按输出时间采样。
2. **平台采集差异**：窗口捕获、权限提示、系统音频回录、摄像头枚举、事件监听、光标形状、窗口排除功能没有完全等价的 Windows 对应物。
3. **预览/导出一致性**：原项目明确以共享场景对象连接预览和导出。任何两套几何或效果实现都会导致导出与预览不同。
4. **项目数据安全**：schema v27、旧项目缺省、未来版本拒绝、原子保存、上一修订恢复、包内资源路径和 JSONL 事件均属于兼容合同。
5. **资源与发行许可**：MIT 仅覆盖代码许可证声明；字体、Sparkle、图像资源及未来 FFmpeg 发行依赖有各自条件。

审计依据：上游 `README.md`、`LICENSE`、`Package.swift`、`Package.resolved`、字体 README/许可，以及上述核心实现文件；基线由 `git -C upstream/dogsc rev-parse HEAD` 确认。
