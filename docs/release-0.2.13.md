# 0.2.13 时间线点击定位

鼠标未按左键时，悬浮和离开时间线都不改变预览画面。左键按下立即定位播放头；在标尺或空白轨道按住左键拖动时持续更新，松开后停在最终位置。范围操作和新粘贴内容使用当前播放头。

本版移除灰色悬浮线、悬浮预览事件和工具条的预览开关。拖动时让当前解码完成，只保留最新等待位置，避免连续取消导致迟迟不出图；新的单击可以中断不同位置的旧请求。

## 验证

固定 SDK 10.0.401 构建通过，0警告、0错误。证据位于 `artifacts/validation/package-0.2.13/`：

- `timeline-input-final/result.json`：15项通过，包括悬浮无请求、立即单击、拖动最终位置、拖回起点、末帧边界、变速映射、多选和片段编辑。
- `preview-interaction-v5/result.json`：13项通过。真实时间线点击经 MainWindow 订阅完成媒体解码；悬浮后播放头、Bitmap 和队列不变；快速拖动不取消正在处理的帧，最终收敛到最新位置。保留播放、换项目及取消竞态检查。
- `preview-sizing/result.json`：12项通过，覆盖物理视口、4K源细节与共享播放场景采样像素对照。
- `theme-final/result.json`：111项通过，检查浅深主题、控件布局、文字及焦点样式。已查看离屏布局图片，预览开关已移除。
- `core.log`、`integration.log`、`render.log`：Core、合成媒体 Integration 和 Render.Probe 通过；Integration 结果为 `artifacts/validation/synthetic-testsrc2-20261011-033350-056/result.json`。

测试过程发现：即使不调用 Application.Run，Dispatcher.PushFrame 也会执行 WPF 排队的 App 启动回调。离屏探针现用内部构造参数隔离常驻、托盘与 IPC 入口；正常启动构造路径保持开启。前几次探针失败作为诊断保留，最终 v5 明确检查只有测试窗口且无 HWND。这些结果支持后台回归，不代表用户已认可新版实机体验。

## 本地交付

产物使用 `artifacts/release/FrameTrail-0.2.13-win-x64/`、同名 ZIP、`FrameTrail-Setup-0.2.13-win-x64.exe` 和 `FrameTrail-0.2.13-source.zip`。交付状态、哈希和归档验证以 `FrameTrail-0.2.13-candidate-manifest.json`、`artifacts/validation/package-0.2.13/content-result.json`、`source-validation.json` 为准。安装卸载与新版手动操作验收仍待完成。

FT-07新菜单和辅助窗口录屏排除继续待实机验证；摄像头仍按用户要求延期。整体台账仍为28项通过、2项待验、3项延期，分母33，不据本次修正宣称整体已达95%。0.2.12悬浮预览行为是已被最新反馈取代的历史实现。
