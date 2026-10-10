# FrameTrail · Windows 录屏与运镜

FrameTrail 是基于 [DogSC](https://github.com/laogou717/dogsc) 的设计与核心算法开发的独立 Windows 迁移项目，不代表原作者的官方 Windows 发行。目标平台为 **Windows 11 x64**。

固定上游提交：`e207d11b111c5e58152238fb64115b2009850547`。开始迁移时核验远端 `main` 与 `v0.1.2` 均为该提交；本轮继续采用此基线。源码子模块在 `upstream/dogsc`，MIT 许可全文在 `licenses/DogSC-MIT.txt`。

## 0.2.4 实现范围

- 显示器、区域录制，暂停/恢复；WGC窗口录制已通过指定目标的遮挡、移动/缩放和结束收尾探针。独立系统声音、麦克风和摄像头素材；鼠标事件、热键标记、备忘录及辅助窗口排除。
- 项目自动保存、备份恢复、最近项目、相对素材路径、缺失素材重新定位；只读导入 DogSC schema 23–27 并生成转换报告。
- 实时预览、逐帧定位、缩略图/波形、分割、裁短、重排、0.25× 起的慢放与加速、恢复剪辑、范围导出；多效果轨道、多选、吸附、悬浮预览、复制和属性粘贴、撤销重做。
- 自动镜头弹簧跟随、手动缩放、自定义 Bezier 和共享弹簧参数；录制光标形态、速度倾斜及四种点击动画；摄像头布局/动画、屏幕 3D 和聚焦虚化；贴图、区域柔化、画布裁切、样机、图片和循环视频背景、三种原版开场及场景预设。
- 共享 D3D11 场景渲染的预览和导出。MP4/M4A，原始、1080p、1440p、4K，30/60fps，编码器回退、取消和重试。

0.2.4 是本机已验证的本地交付候选，commit `1a13291115536787f01ceb65ee99efcd64b4d166`。便携和安装包工作流、源码归档重建及桌面UIA均已通过。100%下旧包的Smoke工作区适配测试通过（测试窗由harness移入工作区）；200%下新开发快照的只读启动定位测试20/20通过，实际窗口边界为 `[32,32..3808,2032]`，工作区为 `[0,0..3840,2064]`。旧0.2.4包不含该启动定位修复，新快照也不是0.2.5发行包；完整多DPI交互、多显示器、真实Mac工程和物理摄像头仍未验收。视觉以 DogSC 的美感和流畅度为下限，允许更适合 Windows 的交互；实际限制见 [已知问题](docs/known-issues.md)、[视觉品质合同](docs/visual-quality-contract.md) 和 [迁移验收表](docs/parity-checklist.md)。

## 构建与运行

0.2.5开发候选已修复高DPI主窗超出工作区，以及文件被占用时撤销/重做先改变内存历史的问题。开发快照的150%桌面回归44项、真实文件锁失败/重试12项和跨剪辑双音轨范围导出通过；32秒导出任务的进度采样、取消覆盖保护及同窗口重试21项通过。新版本仍须完成打包与安装后回归，以上不替代0.2.4包的证据。

需要 .NET 10 SDK（仓库 `global.json` 以 10.0.401 为基准）、Windows Media Foundation、Microsoft Visual C++ x64 Runtime、FFmpeg/FFprobe。可用 `FRAMETRAIL_FFMPEG_DIR` 指定媒体工具目录；发行便携包自带 .NET 与 FFmpeg。

```powershell
git submodule update --init --recursive
pwsh -File scripts/build.ps1
pwsh -File scripts/test.ps1
pwsh -File scripts/test.ps1 -IncludeMediaProbe -IncludeIntegration
pwsh -File scripts/test.ps1 -IncludeVisualProbe
pwsh -File scripts/test-release-manifest.ps1
pwsh -File scripts/package.ps1 -Version 0.2.5
```

启动 `src/FrameTrail.App/bin/x64/Release/net10.0-windows/win-x64/FrameTrail.exe`。打包产物在 `artifacts/release`；[使用说明](docs/user-guide.md) 和 [构建与打包](docs/build-and-release.md) 说明操作与依赖。

## 验证证据

本机为 Windows 11 Pro 26200、AMD Ryzen 7 9850X3D、NVIDIA RTX 5070 Ti。0.2.4发行包全量UIA43/43、浮层19/19；包内编辑53/53、导出15/15、录制条虚拟生命周期5项及12张窗口状态图通过。便携版真实录制、暂停、编辑、重开、导出和安装版播放通过，卸载与偏好恢复也通过。源码ZIP重建测试、App Release构建和无FFmpeg打包通过。证据文件见 `artifacts/validation/package-0.2.4/`。新v3离线分析器和Smoke work-area修正未包含在0.2.4源码归档中；100%旧包的Smoke测试窗适配通过，200%主窗启动定位证据来自后续开发快照，未进入0.2.4发行包。这些结果限于本机单显示器，完整多DPI交互和多显示器仍未覆盖。

音频优先使用连续设备帧位置；位置与混音帧数持续不符时回退到QPC。真实麦克风已通过含暂停的15秒采集检查。30秒系统回环实录和保存重开/M4A导出各匹配18/18事件。1800秒音画样本已完成，v3分析原始1070/1080名义cue、P95 74.4448ms，映射成片P95 38.333ms；该轮使用capture v6冻结程序集，0.2.4新增timestamp-errorgap条件未触发，不视为最终包逐字节录制长测。窗口目标样本通过所测遮挡、移动/缩放和关闭场景。100%旧包工作区适配与200%开发快照只读启动定位已有单机证据，但完整DPI交互、多显示器、真实Mac工程和物理摄像头仍未验收。各轮候选与数值边界见 [采集验证](docs/capture-validation.md)、[迁移差距登记](docs/migration-gap-register.md) 和 [迁移验收表](docs/parity-checklist.md)。现有证据尚不足以认定功能迁移95%已达到。

测试原始产物在 `artifacts/validation`，不纳入 Git 或发行包，以免包含真实屏幕内容。合成媒体、算法测试和本机测试不等同于另一台干净机器或所有摄像头/麦克风已验收。

- [源码审计](docs/source-audit.md) · [架构](docs/architecture.md) · [算法差异](docs/algorithm-port.md)
- [功能映射](docs/feature-mapping.md) · [静态视觉依据](docs/visual-spec.md)
- [采集验证](docs/capture-validation.md) · [媒体效果验证](docs/media-effects-validation.md)
- [第三方许可](licenses/THIRD-PARTY-NOTICES.md)
- [Windows 分支吸收与新增验证](docs/windows-port-integration.md)

无遥测、账号、广告或云端媒体上传。本地验证包包含 Gyan FFmpeg GPLv3 构建；公开分发前须完成该构建全部依赖的对应源码与许可材料。Windows 10、ARM64、自动更新和真实 Mac 工程往返兼容未声明支持。
