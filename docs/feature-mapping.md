# DogSC 功能迁移映射

目标方案为 **.NET 8 WPF + ScreenRecorderLib 7.0.1（DXGI/WGC、WASAPI、Media Foundation 录制）+ Vortice.Direct3D11 3.8.3（GPU 共享像素和 shader 合成）+ FFmpeg 子进程（解码与 H.264/AAC 封装）**。本机 FFmpeg 8.1.1 Gyan GPLv3 full build 只用于本地验证；发布用构建及其许可证说明仍须审计。状态仅用“待实现”或“待验证”；本表描述目标功能和应验收的原行为，不表示已完成。

| 原功能 | 原行为依据 | Windows 迁移关注点 | 状态 |
|---|---|---|---|
| 首次使用引导与权限检查 | `PermissionTourFlow.swift`、`FirstUseTour.swift`；引导用户开启录屏与辅助功能，摄像头/麦克风按需授权 | 首次运行说明、权限检测及系统设置跳转 | 待实现 |
| 显示器、窗口、区域录制 | `ScreenRecorder.swift`、`CaptureSources.swift`、`CaptureAreaSelection.swift` | ScreenRecorderLib 7.0.1 的 DXGI/WGC 后端、窗口切换、区域选择、多显示器和 DPI | 待实现 |
| 录制条、浮动控制与录制中项目编辑 | `RecorderViews.swift`、`ScreenRecorder.swift`；README 描述既有项目可并行编辑及辅助窗口排除策略 | 录制 UI 生命周期、窗口排除能力和录制画面反馈 | 待验证 |
| 摄像头录制、预览、裁黑边与布局 | `CameraRecorder.swift`、`CameraLetterboxAnalysis.swift`、`EditorTimelineCameraSync.swift` | Media Foundation 设备热插拔、方向、黑边检测和时钟同步 | 待实现 |
| 麦克风和系统声音录制 | `MicrophoneRecorder.swift`、`CoreAudioSystemAudioTap.swift` | WASAPI 输入/回环采集、独立轨和静音 | 待实现 |
| 暂停、继续、停止及失败恢复 | `ScreenRecorder.swift`、`RecordingRun.swift`、`RecordingRecoveryJournal.swift` | 设备中断、暂停后时间轴连续性、崩溃恢复 | 待实现 |
| 指针事件、点击与修饰键记录 | `PointerEventRecorder.swift`、`ZoomAnimation.swift` | 鼠标事件权限、DPI/坐标换算、事件节流及暂停校正 | 待实现 |
| 光标外观、平滑、点击反馈、隐藏与校准 | `PointerTrack.swift`、`CursorAssetLibrary.swift`、`CursorRenderGeometry.swift` | 系统光标形状采集、资产授权、轨道插值和点击动效 | 待实现 |
| 自动缩放与手动缩放动画 | `AutoZoomPlanner.swift`、`ZoomAnimation.swift`、`EditorZoomViews.swift` | 点击分组、自动保持、片段相接、曲线和焦点编辑 | 待实现 |
| 开场动画和屏幕 3D 运镜 | `FrameScene.swift`、`ScreenProjection.swift`、`ScreenMotion.swift` | 透视投影、倾斜、深度、边界和动画预览 | 待实现 |
| 摄像头运动与布局动画 | `CameraMotion.swift`、`CompositionScene.swift` | 摄像头素材时间对齐、位置/尺寸/角度过渡 | 待实现 |
| 时间线分割、裁短、重排、删除、恢复 | `ProjectTimelineEditing.swift`、`ProjectTimelineRetime.swift`、`EditorTimelinePrimaryEditing.swift` | 非破坏编辑、波纹变更、边界吸附、恢复剪辑 | 待实现 |
| 1×–100×片段变速、分段声音覆写 | `ProjectTimeline.swift`、`ProjectTimelineEditingTransforms.swift` | 时间线推导和音视频重采样/混音 | 待实现 |
| 时间线缩略图、胶片条、波形、吸附和播放控制 | `EditorTimelineFilmstrip.swift`、`EditorTimelineMergedWaveform.swift`、`EditorTimelineMagneticSnap.swift` | 长素材异步生成、缓存和交互性能 | 待实现 |
| 录制标记与快捷键 | `RecordingMarker.swift`、`RecordingMarkers.swift`；README 为 ⌃⌥M，标记不进入成片 | Windows 快捷键映射、标记随剪辑移动及被剪片段定位 | 待实现 |
| 画布比例、裁切、留白、圆角、阴影和背景 | `FrameScene.swift`、`CompositionScene.swift`、`CanvasCustomAspectRatio.swift` | Direct3D 11 shader 合成、画布尺寸限制、图片/视频背景、色彩和滤镜 | 待实现 |
| 场景预设和摄像头布局预设 | `EditorScenePresets.swift`、`EditorStylePresetStore.swift` | 预设持久化、项目素材复制、旧预设默认值 | 待实现 |
| 贴图、贴图动画与图层顺序 | `StickerTransition.swift`、`FrameScene.swift`、`ProjectStore.swift` | 静态图导入、项目内复制、图层顺序和时长编辑 | 待实现 |
| 录屏画面柔化/马赛克与遮挡 | `FocusEffect.swift`、`ProjectTimeline.swift`、`FrameScene.swift` | 效果范围、强度、实时预览与导出一致性 | 待实现 |
| 样机外框、标题/网址与背景效果 | `EditorScenePresets.swift`、`SharedFrameRenderer.swift` | 装饰绘制、文字排版、缓存和授权字体 | 待实现 |
| 悬浮备忘录 | `RecorderMemo.swift`；README 说明本机自动保存且不录入 DogSC 自身画面 | 独立窗口、持久化、录制排除 | 待实现 |
| 项目包、自动保存、最近项目、迁移备份和恢复 | `ProjectStore.swift`、`ProjectDocument.swift`；`.dogscproject` 包结构见审计文档 | v23–v27 JSON、JSONL、原子替换、并发屏障和安全资源路径 | 待实现 |
| MP4 视频导出及画质/帧率选项 | `DirectExportPipeline.swift`、`VideoExporter.swift`；README 列出原始分辨率/1080p/1440p/4K 与 30/60 FPS | FFmpeg 子进程解码、H.264/AAC 封装、VFR 读取、色彩元数据和发布构建许可证 | 待实现 |
| M4A 音频导出 | `AudioOnlyExportPipeline.swift`、README | 音频编码器、声道、采样率和混音 | 待实现 |
| 导出进度、预计剩余、阶段状态与取消 | `ExportProgressRelay.swift`、`ExportActivityWatchdog.swift`、`DirectExportPipeline.swift` | 取消后终止子进程、清理临时文件、进度可信度 | 待实现 |
| 中英界面本地化、快捷键和可访问性 | `Resources/zh-Hans.lproj/Localizable.strings`、`Resources/en.lproj/Localizable.strings`、`RecorderAccessibility.swift` | WPF 本地化、键盘导航和屏幕阅读器支持 | 待实现 |
| 关于、版本更新、源码和问题反馈入口 | `AppAboutSettingsView.swift`、`AppUpdateController.swift`、README | Windows 更新包签名与反馈地址 | 待实现 |
| 自愿赞赏码 | `AppSupportSheet.swift`、`Resources/Support/AppreciationCode.jpg`；README 说明导出后也可打开 | 资源再分发权、Windows 支付/展示适用性；不得成为使用门槛 | 待验证 |

## 验收时优先验证的互操作点

- 用原始 v23、v24、v27 项目包做读取；逐字段确认旧值默认和未来版本拒绝，不要通过打开再保存丢弃未知字段。
- 指针 JSONL 要逐行读取；检查归一化坐标、点击/修饰键、暂停区间、光标资产 ID、非单调时间排序和窗口捕获范围变化。
- 对自动缩放准备密集点击、间隔恰好 1.4 秒、点击后持续移动、源录制提前结束和已手工编辑轨道样例，比较片段数、焦点、进入/保持/退出和相接行为。
- 让 WPF 预览和 FFmpeg 导出对相同时间点使用相同 `FrameScene` 语义；核对摄像头、投影屏幕、贴附光标、圆角阴影、马赛克、贴图、透明度及音频剪辑边界。
- 对录制暂停恢复、设备断连、崩溃恢复、跨盘移动项目、中文路径和项目包内外资源分别验证数据安全。
