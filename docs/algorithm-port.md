# 核心算法移植记录

FrameTrail 使用 Windows 自有的 `.frametrail` 项目格式。本文记录了与 `upstream/dogsc/Sources/RecorderCore` 源码逐项核对后的语义，以及仍然存在的差异；这不表示与 macOS 工程格式或全部输出画面完全兼容。

## 源码语义与 Windows 实现

- 时间线映射参考 `ProjectTimeline.swift`。片段按列表顺序拼接，源时长除以播放速度得到输出时长；接点恰好落在后续片段，末尾时间限制在最后片段。FrameTrail 的 `ProjectTimeline` 支持分割、裁短、速度调整、重排及正反映射；重复源区间的反向映射确定地采用首次出现的位置。
- 缩放动画参考 `ZoomAnimation.swift` 的 `ZoomInterpolator`。相邻容差为 `1/120` 秒；相接片段继承前一片段的缩放与焦点；入场时间不超过片段长度；入场、停留、退出阶段共用焦点锚点。C# 已移植默认三次与五次 Bezier 控制点，并按时间 X 反解曲线参数；默认弹簧采用质量 2.4、刚度 210、阻尼 42、时长 0.72 秒的归一化欠阻尼公式。
- 自动缩放参考 `AutoZoomPlanner.swift`。点击按 1.4 秒聚组，再按可见区域拆分焦点；首组在首次点击前 18% 的入场时间开始，连续焦点开始时间至少间隔 0.16 秒；鼠标活动可按 1.4 秒空闲阈值延长停留，并预留最多 0.55 秒稳定时间。FrameTrail实现这些规则，并合并满足同缩放、同曲线且无退出间隙的相邻自动片段；没有复刻Swift旧工程自动轨道修复与版本升级。自动焦点来自全源鼠标坐标，求值时转换到 `Canvas.Crop` 内部归一化空间；鼠标渲染仍使用全源坐标。
- 鼠标采样参考 `PointerTrack.swift`。持久化 `Settings.PointerSmoothing` 有 `Smooth`、`Medium`、`Rapid` 三档，缺少字段的旧 FrameTrail 项目默认 `Smooth`。Smooth 使用与 Swift 相同的质量 2.8、刚度 450、阻尼 66、240 Hz 显式二阶弹簧、0.00035 抖动阈值和 0.12/0.50 秒连续输入判定；点击位置在前后各 0.18 秒内用五次 smoother-step 锚定。Medium 与 Rapid 使用 Swift 的单调 Hermite 切线，档位切线系数分别为 0.78 和 1；超长静默段跳过已稳定的中间样本，连续轨道缓存达到 200 万样本时明确报错，事件时间超过七天时拒绝构建，避免无限制分配。轨道在 `PreparedPointerTrack` 构造时排序和准备，逐帧只做二分查找；鼠标校准先变换采样时间，不改变项目的平滑档位或曲线参数。
- 屏幕运动参考 `ScreenMotion.swift` 的相邻片段继承与缓动处理；相机运动使用同一类相邻状态插值。开场序列参考 `ProjectModel.swift` 和 `FrameScene.swift` 的元素筛选、顺序、错峰和最短时长。`PreparedFrameSceneEvaluator` 根据输出时间映射源时间，再统一求出缩放、指针、开场、屏幕/相机运动、焦点、贴图和模糊场景。GPU预览与导出消费同一 `FrameScene`。
- 独立音频录制对齐使用 `RecordingTimelineMapper`：屏幕首帧估计 tick 作为时间线零点；屏幕与音频各自的 capture 区间减去暂停区间后求 wall-clock 交集。每个交集输出音频 source 起点、持续时间和 screen timeline 起点，允许多次暂停产生多个区间。捕获设备各自产生的实际媒体长度仍需由媒体探测裁尾。

## 已知差异

- C# 缩放、屏幕和摄像头运动支持 Linear、Quadratic、Cubic、Quintic、Spring 和 Custom。Custom 保存四个 Bezier 控制点并反解 X；检查器可拖动控制柄或直接输入数值。项目级弹簧质量、刚度、阻尼供缩放和自动跟随共用。Quadratic 使用 Swift 控制点 `(0.45, 0, 0.55, 1)`；Quintic 对应五次 Bezier 控制点。
- 屏幕运动的相邻继承已支持，但复杂布局锚点与原 Swift 的 `ManualZoomViewportTransition`、动态透视下的像素级 pivot 求解不完全相同。
- 指针位置的 Smooth/Medium/Rapid 采样、慢速移动判定和点击锚点修正已按 Swift 源码对照移植；速度驱动倾斜和点击进度由 `PreparedPointerDynamics` 求值，GPU 支持脉冲、柔光、涟漪和迸发。点击持续时间、范围和不透明度参与渲染。系统光标形态采集与本机 `.cur` 资源加载参考了 Windows 分支，提供跟随录制、固定箭头/手型/文本及圆点/圆环；没有搬运 Apple 平台光标文件。
- 三个原版开场预设、Smooth/Swift/Gentle 曲线、overshoot 和屏幕/摄像头/贴图独立起始姿态已在 `OpeningMotionEvaluator` 按源公式移植。旧 FrameTrail 项目没有 `ElementCurve` 时继续使用原有通用缓动。场景效果时间线以源时间求值；开场从成片输出起点求值。源码公式测试不代表 macOS Metal 渲染器逐像素认证。
- FrameTrail 拒绝未知项目格式版本，且不把 `.dogscproject` 伪装成兼容格式。导入器只映射报告中明确支持的字段；未表达的 Swift 字段会列入损失报告。

## 验证证据与边界

`dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj -c Release` 已通过。测试包含来自 Swift 源码常量的三次/五次曲线与弹簧采样值、Smooth 弹簧首个 240 Hz 积分步的期望值、三档平滑响应差异、精确点击锚点、长静默边界、24 小时稀疏轨道缓存、校准偏移与平滑档位独立性、相邻和重叠 zoom/ScreenMotion/CameraMotion 接缝连续性、短片段入场上限、源时间效果在时间线重排后的映射、自动鼠标焦点穿过 Canvas crop 后的 source 坐标，以及首帧裁切、多次暂停、暂停合并和空交集的音频映射。八种 Opening 预设都有初始姿态、错峰顺序和结束姿态检查。数值测试是基于 Swift 源码公式或明确时钟区间的独立期望值；不是把 Swift 程序运行后与 C# 做差分。本机未安装 Swift，因此没有 Swift 运行时对照结果。

`dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release` 已通过 Direct3D 11 GPU probe。Probe 编译 HLSL，并比较无窗口框与窗口框像素，确认透视及 Z 旋转后控制点仍完整处于标题栏内；同时覆盖裁切、圆角、光标尺寸、场景图层、导出解码和 30/60 fps 分辨率组合。输出的静态与透视图位于该测试项目的 Release 输出目录。GPU probe 验证的是 Windows 渲染结果，不能证明与原 macOS Metal 渲染器逐像素相同。
