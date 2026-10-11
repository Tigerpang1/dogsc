# OpenScreen 对比与 0.2.14 采用结果

调研对象为 OpenScreen 当前仓库 `getopenscreen/openscreen` commit `2896c1f7b9ad46cf53419e6edd360da2d2d80ac3`，以及原仓库 v1.5.0 commit `ffc41a3c8c61a81fc1f1bec1a16c6354e8368cb6`。两版 `LICENSE` 均为 MIT。完整快照和 SHA-256 记录在 `artifacts/research/`；本报告只依据源码，不代表实际 UI 或性能体验。

FrameTrail 本轮目标是改善时间线点击/拖动后的预览首帧延迟。悬停预览不在范围内，摄像头也暂不处理。

## 当前问题

同一段19秒、3840×2160@60录屏，以1440×810预览测量：0.2.13跨位置点击处理需44.65–58.71 ms，新帧提交需223.47–415.98 ms。UI线程同步终止FFmpeg进程树，以及跨关键帧时重新启动解码进程，是两个已测到的等待来源。只把解码启停移到后台后，点击处理降至0.6–3.7 ms，但新帧仍需199–494 ms。

## OpenScreen 机制

当前版播放时让原生解码器 free-run；播放中的 `currentTime` 每帧变化不会触发 seek。暂停后才发送用户离散 seek。原因是每帧 seek 会反复从关键帧回放并与顺序播放抢解码器。[`useNativePlaybackSync.ts`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/src/native/useNativePlaybackSync.ts#L2-L16) [`useNativePlaybackSync.ts`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/src/native/useNativePlaybackSync.ts#L55-L83)

时间线按指针位置立即更新播放头，并立即发送初始 seek；连续 pointermove 通过 `requestAnimationFrame` 合并，拖动结束时再提交最后一个目标。播放头直接写 DOM，减少 React 重渲染等待。[`V4Timeline.tsx`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/src/components/ai-edition/v4/V4Timeline.tsx#L832-L915)

`VirtualPreview` 对 `<video>` demuxer 设置单请求在途：如果正在 seek，只覆盖待处理目标；当前 seek 结束后再执行最新目标，中间位置不排队。源码记录无界 `currentTime` 写入曾导致约 60 次 demuxer seek/秒，并与原生合成器同时解码，引发 Chromium demuxer seek 失败。[`VirtualPreview.tsx`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/src/components/ai-edition/VirtualPreview.tsx#L1409-L1438)

当前原生 `Decoder` 持有打开的 FFmpeg format/codec context、packet 和 frame。精确 seek 调 `av_seek_frame(..., AVSEEK_FLAG_BACKWARD)` 找目标前的关键帧，flush codec，再按 `best_effort_timestamp × stream time_base` 向前解到目标帧。目标等于当前帧时直接复用；目标在当前位置前方 0.5 秒内时继续顺序解码，较远或反向移动时才做完整关键帧 seek。[`pipeline_windows.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/src/pipeline_windows.rs#L927-L997) [`pipeline_windows.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/src/pipeline_windows.rs#L1017-L1032) [`pipeline_windows.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/src/pipeline_windows.rs#L1175-L1175)

Windows H.264 在 GPU 可用时走 D3D11VA，其他格式或 CPU backend 走软件解码；软件路径的 `thread_count=0` 交给 FFmpeg 自动选择线程数。[`pipeline_windows.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/src/pipeline_windows.rs#L840-L865) macOS 同样使用 libav FFI，可选 VideoToolbox 或软件解码，seek 与 PTS 规则一致。[`pipeline_macos.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/src/pipeline_macos.rs#L385-L437)

预览帧有 GPU shared texture 路径：按 frame generation 跳过未变化帧，通过 Electron `sharedTexture` 交付原生纹理，并用 release callback 管理槽位；传输失败时退回 RGBA readback。[`compositorViewService.ts`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/electron/native-bridge/services/compositorViewService.ts#L631-L737) 源码注释称历史 readback 使 renderer 主线程在 30fps 下占用 37–55%；这是 OpenScreen 的记录，未在 FrameTrail 复现。[`compositorViewService.ts`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/electron/native-bridge/services/compositorViewService.ts#L631-L637)

v1.5.0 是常驻 HTML `<video>` 配合 Pixi 的预览。它在 scrub 状态切换时将 Pixi renderer 分辨率降到 1x，静止后恢复设备 DPR，避免每帧调整分辨率导致纹理上传抖动。[`VideoPlayback.tsx`](https://github.com/siddharthvaddem/openscreen/blob/ffc41a3c8c61a81fc1f1bec1a16c6354e8368cb6/src/components/video-editor/VideoPlayback.tsx#L927-L942) 录制端按像素数和帧率选 bitrate，并优先挑 H.264；这些是录制设置，不能单独证明预览 seek 性能。[`useScreenRecorder.ts`](https://github.com/siddharthvaddem/openscreen/blob/ffc41a3c8c61a81fc1f1bec1a16c6354e8368cb6/src/hooks/useScreenRecorder.ts#L27-L31) [`useScreenRecorder.ts`](https://github.com/siddharthvaddem/openscreen/blob/ffc41a3c8c61a81fc1f1bec1a16c6354e8368cb6/src/hooks/useScreenRecorder.ts#L137-L165)

OpenScreen 的原生后端不是 `ffmpeg-next`，而是 bindgen 生成的 libav C FFI。Windows 构建与运行需 FFmpeg 8.x LGPL-shared headers、import libraries 和 DLL；构建还依赖 bindgen/clang。若 FrameTrail 只调用 FFmpeg CLI，照搬原生 context 会新增链接、运行时打包与许可证交付工作；若 Media 已有匹配的共享 libav 运行时，成本会低一些。[`build.rs`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/crates/compositor/build.rs#L17-L74) [`native-compositor.md`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/technical-documentation/architecture/native-compositor.md#L293-L332)

当前检查到的 OpenScreen Windows MF encoder 明确设了平均 bitrate 与 H.264 High profile，未找到 GOP/最大关键帧间隔设置；v1.5.0 的 `MediaRecorder` MIME/bitrate 选项也没有显式 GOP 参数。因此 OpenScreen 源码不能为 FrameTrail 的短 GOP 录制时长或清晰度取值背书。[`mf_encoder.cpp`](https://github.com/getopenscreen/openscreen/blob/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3/electron/native/wgc-capture/src/mf_encoder.cpp#L817-L828)

## FrameTrail 已采用

0.2.14的暂停预览使用常驻libav format/codec会话；向前0.5秒内继续解码，较远或反向移动回到关键帧再解到目标PTS。同一目标可复用96 MiB有界缓存。时间戳来自实际帧，支持VFR、B帧和非零起点。解码、初始化、释放和像素复制离开输入线程，播放头先更新。沿用一个进行中请求和一个最新等待目标；按住拖动合并中间目标，松开提交最终位置。

新增固定BtbN FFmpeg 8.1 shared运行时和FFmpeg.AutoGen 8.1绑定，版本、哈希、许可证及源码获取记录随包交付。缺少运行时、未支持的色彩矩阵或带旋转/翻转元数据的素材，回退到原FFmpeg CLI路径，保留autorotate语义。原生与CLI使用同样的bicubic缩放；预览分辨率、场景和导出计算继续共用现有规则。

独立测量后，显示器/区域FFmpeg录制路径改用0.5秒关键帧间隔，保持原分辨率、帧率和质量参数。合成4K60对照的文件体积增加约5.3%，SSIM从0.988639到0.985825；不能称为像素无损。窗口WGC/ScreenRecorderLib路径不属于这次关键帧修改。旧录屏不重编码，仍受原关键帧间隔影响。

本机D3D11VA未稳定快于常驻软件解码，因此默认使用软件自动线程，硬解只留内部诊断开关。OpenScreen的共享GPU纹理交付本轮未实现。新版不自动降低预览清晰度，也不恢复用户已拒绝的鼠标悬停预览。

## 状态、返回与验证

同素材、同时间点的后台MainWindow探针结果：

| 指标 | 0.2.13 | 0.2.14 |
|---|---:|---:|
| 跨位置点击处理 | 44.65–58.71 ms | 0.46–3.67 ms |
| 跨位置新画面提交 | 223.47–415.98 ms | 58.78–264.04 ms |
| 相邻/近邻新帧 | 10.37–18.74 ms | 9.75–13.88 ms |
| 首次冷帧 | 926.41 ms | 494.18 ms |

0.2.14的五个跨位置样本分别为109.13、135.69、203.77、58.78、264.04 ms；对应旧版分别为282.86、301.28、374.14、223.47、415.98 ms。19秒素材全程一个原生解码会话，零FFmpeg子进程启动；连续四次点击后最后目标帧提交为62.71 ms。4.58秒真实素材的五个随机跳转为115.55–216.80 ms，连续点击最终提交198.44 ms。旧素材GOP为250帧，远跳仍可能需几百毫秒。

计时终点是 `PreviewImage.Source` 赋值，并非显示器实际呈现；探针没有HWND，不抢占桌面，源工程哈希前后相同。证据在 `artifacts/validation/seek-latency-0.2.13/real-19s/`、`artifacts/validation/openscreen-adoption/native-19s/` 和 `native-4s/`。以上是单机固定序列样本，未测OpenScreen实机，不能据此宣称比OpenScreen更快或用户已认可手感。

下一步若继续降低远跳延迟，先用新短GOP录屏测量；再考虑常驻GPU纹理交付和解码/合成拷贝成本。共享纹理应保持同帧像素、尺寸及取消后恢复语义，并单独测量屏幕呈现。

## 固定源码

- 当前分支：[`getopenscreen/openscreen@2896c1f`](https://github.com/getopenscreen/openscreen/tree/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3)，MIT。快照 `artifacts/research/getopenscreen-2896c1f.zip`，SHA-256 `1B50DC9A3917F85651427875D8A1FEFAA31C98A026160EF8781C9128E957BA87`。
- 原仓库发行版：[`siddharthvaddem/openscreen@v1.5.0`](https://github.com/siddharthvaddem/openscreen/tree/ffc41a3c8c61a81fc1f1bec1a16c6354e8368cb6)，MIT。快照 `artifacts/research/siddharth-ffc41a3.zip`，SHA-256 `203C61CD9DC86C301EE92944F6D676AA528AB127FE4C493306B82123B56E51A4`。
