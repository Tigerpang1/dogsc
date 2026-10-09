# 本机验证报告

验证日期：2026-10-08（用户时区 America/Guatemala）。JSON 和 CSV 诊断中的时间使用 UTC ISO 8601，部分文件名因此为 20261009。

当前结论：**阶段 1 的首个录制—编辑—运镜—导出闭环可以运行；完整迁移、长录制稳定性和所有设备验收未完成。**

## 测试环境

- Windows 11 Pro x64，版本 `10.0.26200`。
- AMD Ryzen 7 9850X3D；NVIDIA GeForce RTX 5070 Ti，驱动 `32.0.16.1088`；物理内存约 30.88GiB。
- 主显示器 3840×2160；系统另有 ToDesk/Oray 虚拟显示适配器，尚未作为多屏通过证据。
- 构建 .NET SDK 8.0.303；应用目标 net8.0-windows / win-x64。
- ScreenRecorderLib 7.0.1；Vortice 3.8.3；FFmpeg 8.1.1 Gyan full build。
- 本机已有 VC++ Runtime 与 Media Foundation。未提供干净虚拟机结果。

## 已执行的自动验证

| 项目 | 实际结果 | 证据 |
|---|---|---|
| Core 算法与存储 | 时间线分割、变速、重排、接点、重复源区间、鼠标校准、自动缩放、备份恢复、未来版本和路径验证通过 | `tests/FrameTrail.Core.Tests` |
| GPU 合成 | 硬件 D3D11；背景、圆角、缩放、裁切外光标隐藏、点击环、不同输出尺寸的光标比例通过 | `tests/FrameTrail.Render.Probe`；40 帧小尺寸平均约 0.19ms，不能外推为完整导出性能 |
| 桌面闭环 | 主程序实际录制 3.519979s，分割、后半段 2 倍速并前移、保存重开，导出 2.63998425s | `artifacts/validation/workflow/workflow-result.json` 与 `desktop-edited.mp4` |
| 固定点击与运镜 | 6s 自制 testsrc2 与固定指针事件生成 2 段自动缩放，项目重载、MP4/M4A 成功 | `artifacts/validation/synthetic-testsrc2-20261009-042721-000/result.json` |
| 预览/成片构图 | 四个精确帧时刻使用相同 GPU 代码生成参考，压缩后 PSNR 25.07–25.85dB、MAE 3.75–4.69；按测试容差通过 | 同上；这是有损编码容差验证，不能理解为逐像素完全相同 |
| 1080p60 导出 | 完整 6 秒、360 帧，实际 `h264_nvenc` 与 D3D11 硬件；约 1.688 秒 | 同上；源图是 320×180 合成素材，输出为 1920×1080，不等价于复杂原生 1080p 素材压力测试 |
| 时间线音画对齐 | 三段重排/变速后输出 5s/300帧；闪屏上升 0.5/2/4s，音频脉冲上升 0.48/1.98/3.98s，三处偏差均 20ms | `artifacts/validation/sync-1080p60-20261009-042653-851/sync-result.json` |
| 取消后再次导出 | 在合成帧阶段取消，检查临时文件消失，再导出成功 | Integration 默认与真实素材两模式 |
| 系统回环素材 | Probe 播放本机生成的 440Hz 音调并录制默认输出，成片含音轨；真实录制再次剪辑导出成功 | `artifacts/validation/real-recording-20261009-042508-244/result.json` |
| 程序界面 | 主程序初始化、录制、编辑后生成实际 WPF 诊断快照 | `artifacts/validation/workflow/editor-project.png` |

桌面闭环样片的鼠标基本静止，只有 1 条位置事件；点击运镜验证来自明确标识的合成事件。未将合成测试当作人工真实点击验收。

## 采集诊断与仍需测量的指标

ScreenRecorderLib 的 Recording 通知相对开始调用约 0.4–0.74 秒，但不公开首帧 PTS 的 Stopwatch 锚点。通知延迟不是已知同步偏差，不能直接将其用于校准。源视频与鼠标事件均保留时钟信息，UI 提供独立毫秒校准。

长录制测试中 private bytes 持续增长：最初约 130 秒达到 990MiB，诊断中止。关闭录制 faststart 后输出文件持续增长，短测仍出现内存增长；碎片 MP4 未直接解决。设置低延迟后的结果继续记录在 `artifacts/validation/long-capture`。未通过 30 分钟稳定性验收，也未测得真实长片漂移。

测试工具记录进程 CPU、工作集、private bytes、线程数、文件增长与可用物理内存。GPU 利用率、驱动级丢帧、真实首帧/采集声音偏差、长期漂移尚未得到完整可重复报告。文件的标称 FPS 不能代表没有重复帧或采集丢帧。

## 复现命令

```powershell
dotnet run --project tests/FrameTrail.Core.Tests/FrameTrail.Core.Tests.csproj -c Release
dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release
dotnet run --project tests/FrameTrail.Integration/FrameTrail.Integration.csproj -c Release
dotnet run --project tests/FrameTrail.Integration/FrameTrail.Integration.csproj -c Release -- --sync
```

主程序诊断参数 `--workflow-smoke <output-directory>` 会实际短录当前桌面、编辑并导出；只在允许录屏的本机会话使用，录制内容保存在指定目录，不上传。`--open <project.frametrail> --smoke <snapshot.png>` 可做指定项目打开与界面快照检查。正常使用无需这些参数。

安装包结果单独见 `package-validation.md`。全部真实素材只保留在本机 `artifacts`，没有公开发布。
