# Windows 分支吸收记录

参考仓库为 [xiaoying8666/dogscwindows](https://github.com/xiaoying8666/dogscwindows)，固定提交 `3bdcf00d4eb12a107c093e98aac7a894aedd2843`。本次沿用 FrameTrail 的 WPF、D3D11、原生采集和独立项目格式，移植已核实可用的算法及验证方法。参考仓库的 MIT 全文与来源保存在 `licenses/DogSCWindows-MIT.txt`，随源码、便携包和安装包提供。

## 已接入的内容

| 内容 | 参考来源 | FrameTrail 实现 |
|---|---|---|
| 自动镜头连续跟随 | `windows/src/core/pointer.js`、`scene.js` | 120 Hz 弹簧积分、安全区、相邻缩放衔接；跟随坐标经过源时钟和画布裁切映射；稳定静默区间压缩；采样总量有明确上限 |
| 录制光标形态 | `windows/src/main/pointer-recorder.js` | `GetCursorInfo` 区分箭头、手型、文本，即使位置不动也记录形态变化；正常保存及恢复均保留 |
| Windows 光标图像 | `windows/src/renderer/editor/cursors.js` | 读取本机固定 `aero_arrow_xl.cur`、`aero_link_xl.cur` 的 32 位 DIB 与热点；缺少文件时回退矢量；文本光标为矢量；GPU 使用独立纹理槽 |
| 慢放 | `windows/src/core/timeline.js` | 时间线允许 0.25–100×，常用菜单含 0.25/0.5/0.75×；主音轨及独立音轨通过 `atempo` 链保持音调 |
| 色卡验证方法 | `windows/src/main/selftest.js`、`renderer/selftest.html` | 原创色卡逐通道比较，加上失败阈值；分别测共享预览、软件导出、硬件导出和媒体色彩标记 |

本次同时补充了来自原 macOS 基线的自定义 Bezier、共享弹簧参数、三种开场元素曲线和指针倾斜/点击动画。它们与上述 Windows 分支借鉴分别注明来源，不能把该分支的有限功能视为原产品完整迁移。

## 本轮功能补充

- 摄像头轨道增加缩放联动和阴影强度；摄像头运动片段可连续改变布局、几何、圆角和不透明度。基础摄像头可复制，完整粘贴会替换唯一摄像头轨道并可撤销；属性粘贴保留目标素材及时间映射。场景预设仍只保存场景设置，不包含时间线轨道。
- 缩放片段保存进入/退出进度锚点，默认值 `0` 保持旧工程行为。移动或裁剪片段时按对应边界清除锚点，完整复制保留锚点；属性粘贴清零锚点以开始新过渡。
- 贴图可设置背景柔化、柔化是否包含摄像头、显示期间隐藏屏幕或摄像头。渲染器按图层时序计算可见度与柔化，独立 GPU 像素探针及实际素材预览/MP4 导出均通过。
- Spotlight 保留清晰的源画面区域，压暗区域外画面；区域随裁切、缩放和屏幕投影变化。导入保留压暗程度及无转场、线性、顺滑转场，不再将聚光转换成普通模糊。
- 摄像头、麦克风的 `sourceTimeScale` 和 `syncAnchors` 转为分段源时钟映射，移动和裁短保持每段速率；旧工程省略速率时按 1× 读取。主屏幕素材仍遵循原 `sourceSequence` 坐标，不重复应用辅助媒体映射。
- 主剪辑可单独覆写系统声音和麦克风音量或静音；未覆写字段继承全片设置，分割片段保留独立副本，试听与导出共用音频处理路径。

`all-port-final19.log` 已完成Core、导入、完整GPU/媒体、逐片段音量、普通集成及1080p60同步检查。相机分段时钟和重排后的预览/导出颜色、音频分段FFT及Spotlight均有实际媒体证据。新增检查器的两组桌面专项均5/5通过，包含保存重开；证据详见 `validation-report.md`。

## 已取得的新增证据

- `artifacts/validation/color-slow-v2/result.json`：共享预览七色块最大通道偏差 2/255；`libx264` 和 `h264_nvenc` 导出再解码均为 4/255，阈值分别为 4/255、6/255。
- 同一探针将蓝、红、绿三个片段重排，并使用 0.25×、0.5×、2×：60 fps 的 MP4 为 6.25 秒、375 帧；独立音轨 M4A 为 6.25 秒。三个音调的频率比较和预览源时间对应均通过。
- `artifacts/validation/cursor-shapes-v5/cursor-shapes.summary.json`：真实区域采集产生 73 帧、2.433333 秒 H.264；箭头、手型、文本三个事件均处在同一物理坐标 `(268,185)`，并通过七色色卡解码采样。红、绿、蓝、灰、白、肤色、黑七块的最大通道偏差为 2/255，低于 8/255 门槛；该结果属于本机 DDA/NVENC 短样片，不代表其他显卡、显示器或编码器。

SDR 桌面与合成 RGB 保留 sRGB 传递曲线 `iec61966-2-1`，YUV 使用 BT.709 矩阵/基色和 limited (`tv`) 范围。转换和标签必须一致；不将传递曲线与 YUV 矩阵混为同一个字段。滤镜语义依据 [FFmpeg scale](https://ffmpeg.org/ffmpeg-filters.html#scale-1)、[setparams](https://ffmpeg.org/ffmpeg-filters.html#setparams) 与 [atempo](https://ffmpeg.org/ffmpeg-filters.html#atempo)。此轮没有实现 HDR/广色域的全链路色彩管理。

## 验证入口

```powershell
dotnet run --project tests/FrameTrail.Render.Probe -c Release -- --color-slow-motion artifacts/validation/color-slow-local
dotnet run --project tests/FrameTrail.Capture.Probe -c Release -p:Platform=x64 -- --cursor-shapes artifacts/validation/cursor-shapes-local/cursor-shapes.mp4
```

设置 `FRAMETRAIL_FFMPEG_DIR` 为含 `ffmpeg.exe`、`ffprobe.exe` 的目录。采集探针使用真实桌面自己的测试窗口，需空闲的前台测试时段；拒绝覆盖已有样片。测试原始文件只保存在本地 `artifacts`，不随包公开分发。
