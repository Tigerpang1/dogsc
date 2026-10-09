# FrameTrail 核心算法移植记录

FrameTrail 是 Windows 端的新项目格式与渲染实现。下面记录了实现时对照原 `upstream/dogsc/Sources/RecorderCore` 的位置，以及仍然存在的近似行为；本记录不代表与 macOS 工程格式或全部播放结果完全兼容。

## 已对照并采用的语义

- `ProjectTimeline.swift`：片段顺序决定输出顺序，输出位置由累计片段时长推导；片段保存源起点、源时长和播放速度。FrameTrail 的 `ProjectTimeline` 同样按片段顺序映射时间，支持拆分、裁短、调速和重排。输出时间恰好位于剪切接点时选择下一段，最后一段的终点仍映射到最后一帧边界。
- `PointerTrack.swift`：指针事件按时间排序，采样点在相邻事件间平滑插值，长间隔不跨越插值。FrameTrail 使用单调 Hermite/PCHIP 风格的逐轴插值、0.5 秒最大跨段间隔，以及独立的 0.18 秒点击脉冲。`PreparedPointerTrack` 在创建时排序并缓存点击数组，每帧通过二分搜索取样。
- `AutoZoomPlanner.swift`：点击按 1.4 秒时间间隔分组；每个时间组再按缩放后可见范围拆分空间区域。进入时间采用点击前 18% 的提前量，连续空间区域的起点至少相隔 0.16 秒。连续鼠标活动在 1.4 秒空闲间隔规则下延长停留，并在后续区域开始前留出最多 0.55 秒的稳定时间。无退出过渡且接壤、缩放和缓动相同的自动片段会合并。
- `FrameScene.swift` 与 `ZoomAnimation.swift`：渲染端只读使用已求值场景；归一化焦点、裁切范围和进入/回落阶段均由时间采样决定。FrameTrail 的 `PreparedFrameSceneEvaluator` 在 session 开始时准备时间线、鼠标轨道和缩放列表，随后按输出时间映射到源时间并产生 renderer 所需的场景值。

## 近似与未移植部分

- 指针曲线采用单调三次插值，但没有原 `PointerTrack` 的多种 cursor motion 样式、弹簧缓存、速度旋转、长间隔慢速移动判定、点击位置锚定修正或光标皮肤/显隐策略。点击波形也采用简化正弦脉冲。
- 自动缩放会在光标越过当前裁切中心的安全范围后按单帧位置调整焦点。原实现使用近期鼠标轨迹与独立弹簧推进相机焦点；FrameTrail 的焦点响应没有相同的历史状态、弹簧速度或逐帧缓存，因此快速移动时的观感可能不同。
- FrameTrail 不实现原 planner 对旧项目自动轨道的识别、保守修复和缓动升级。FrameTrail 项目使用独立的 `FrameTrailProject` v1 JSON，保存或读取时拒绝未知格式版本。
- `FrameScene` 只覆盖屏幕裁切、背景、边距、圆角、阴影、鼠标位置和点击强度。原场景还包含相机、屏幕装饰、贴纸、马赛克、开场动画、颜色管理和完整图层顺序；这些功能没有在此 renderer 中复刻。
- 时间线目前允许重复使用同一段源素材。源时间映射到输出时间存在多个候选时，`MapSourceToOutput` 确定性地返回首个匹配位置；标记跳转因此会落到第一次播放的位置。

## 验证用例

`dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj` 覆盖了拆分/重排/调速后的源输出映射、接点切换、重复源区间、负鼠标偏移、自动缩放进入与回落连续性、鼠标活动延长 hold、相对素材路径限制、未来项目版本拒绝和备份恢复。

`dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj` 编译独立 HLSL 文件并运行 Direct3D 11 probe，检查圆角边缘、背景、裁切变化、裁切外指针隐藏、圆角内点击环裁切，以及 960×540 与 1920×1080 下光标相对画布尺寸一致。Probe 同时报告 GPU 加 staging readback 的实测帧耗时并保存 PNG。
