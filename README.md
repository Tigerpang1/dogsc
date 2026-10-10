# FrameTrail · Windows 录屏与运镜

FrameTrail 是基于 [DogSC](https://github.com/laogou717/dogsc) 的设计与核心算法开发的独立 Windows 迁移项目，不代表原作者的官方 Windows 发行。目标平台为 **Windows 11 x64**。

固定上游提交：`e207d11b111c5e58152238fb64115b2009850547`。开始迁移时核验远端 `main` 与 `v0.1.2` 均为该提交；本轮继续采用此基线。源码子模块在 `upstream/dogsc`，MIT 许可全文在 `licenses/DogSC-MIT.txt`。

## 0.2.5 实现范围

- 显示器、区域录制，暂停/恢复；WGC窗口录制已通过指定目标的遮挡、移动/缩放和结束收尾探针。独立系统声音、麦克风和摄像头素材；鼠标事件、热键标记、备忘录及辅助窗口排除。
- 项目自动保存、备份恢复、最近项目、相对素材路径、缺失素材重新定位；只读导入 DogSC schema 23–27 并生成转换报告。
- 实时预览、逐帧定位、缩略图/波形、分割、裁短、重排、0.25× 起的慢放与加速、恢复剪辑、范围导出；多效果轨道、多选、吸附、悬浮预览、复制和属性粘贴、撤销重做。
- 自动镜头弹簧跟随、手动缩放、自定义 Bezier 和共享弹簧参数；录制光标形态、速度倾斜及四种点击动画；摄像头布局/动画、屏幕 3D 和聚焦虚化；贴图、区域柔化、画布裁切、样机、图片和循环视频背景、三种原版开场及场景预设。
- 共享 D3D11 场景渲染的预览和导出。MP4/M4A，原始、1080p、1440p、4K，30/60fps，编码器回退、取消和重试。

0.2.5 是本机 Windows 11 x64 本地交付候选。源码提交 `e4bfd5ed44deb93272df4d3f52217e3cb8e43420`，DogSC上游基线为 `e207d11b111c5e58152238fb64115b2009850547`。发行包和源码ZIP验证通过，包验收8/8，477个发布目录文件经完整流程后与ZIP逐字节一致；manifest状态为`validated-local-delivery`，产物见 [交付清单](artifacts/release/FrameTrail-0.2.5-manifest.json) 和 [包验收报告](artifacts/validation/package-0.2.5/result.json)。旧7项验收及ContentOnly发现的Demo字段变更保留为历史，样例已恢复；当前最终验收包含隔离播放副本和归档未变检查。包内编辑53项、导出21项、文件锁12项和辅助素材重定位10项通过，App DLL SHA256为 `D46FE2FDBA4393E0D6925EC57598A42EDC9CD388FF30F9FCA624DF86298C6491`。

## 0.2.6 界面候选

0.2.6 统一设置页、开关、滑块、复选框、输入框和滚动条，完善浅深主题；设置按任务分组，全部常用选项在本机 150% DPI 首屏可见。导出进度区与操作按钮保持稳定位置，失败详情可展开、滚动和复制。取消、撤销、时间线平移及真实导出已有本轮证据，导出状态回归 34/34 通过。

本轮交付采用候选清单，范围见 [UI 验收记录](docs/ui-craft-validation.md) 和生成的 `artifacts/release/FrameTrail-0.2.6-candidate-manifest.json`。0.2.5 的历史采集与三档 DPI 结果不替代本轮全矩阵验收；系统高对比模式、模态窗口 Esc 送达及整体 95% 覆盖仍未证明。

## 构建与运行

0.2.5发行包在100%、150%、200%单显示器缩放下全量UIA各44/44、浮层UIA各20/20。全量UIA使用旧合成testsrc2工程；首次启动边界通过只读工作区检查，备忘录重启步骤由harness调用SetWindowPos重新定位。三档指定浮层截图均已人工审阅；150%查看浅色主窗、深色下拉、嵌套菜单及预设确认弹窗。素材重定位v12为10/10通过；PNG贴图导入的撤销/重做、保存重开和工程副本保留已有22项实测；该证据不覆盖所有素材类型或渲染，当前没有工程素材自动清理机制。干净虚拟机、多显示器、真实Mac工程和物理摄像头仍未验收；功能覆盖95%尚未证明。

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

本机为 Windows 11 Pro 26200、AMD Ryzen 7 9850X3D、NVIDIA RTX 5070 Ti。0.2.5包内30秒同包音画探针检出18/18组cue；1800秒同包采集检出1071个flash、1070个tone并匹配1070组（1070/1080名义事件，99.07%），双向覆盖99.9066%，跨1797.866667秒。raw绝对偏差P95为80.9143ms、最大99.2483ms；映射导出P95为43.333ms、最大63.333ms，360次会话租约观察及状态恢复通过。证据见 `artifacts/validation/capture-025-av-30s-v1/` 和 `capture-025-av-1800s-audit-v1.json`。窗口目标探针通过遮挡、移动、缩放、目标关闭/最小化收尾及区域录制回归，见 `capture-025-target-v1/target.target.json`；覆盖本机单显示器测试窗口。公开分发仍需补齐FFmpeg静态依赖源码及许可核验。真实物理摄像头、混合DPI、多显示器、真实Mac工程和干净虚拟机仍未验收，功能覆盖95%尚未证明。

音频优先使用连续设备帧位置；位置与混音帧数持续不符时回退到QPC。当前包内30秒及1800秒同步结果见上文和 [采集验证](docs/capture-validation.md)；长测fixture为系统回环刺激，音频内容完整性不由cue配对证明。窗口目标探针通过指定场景，其他窗口、跨屏和混合DPI仍待验证。

测试原始产物在 `artifacts/validation`，不纳入 Git 或发行包，以免包含真实屏幕内容。合成媒体、算法测试和本机测试不等同于另一台干净机器或所有摄像头/麦克风已验收。

- [源码审计](docs/source-audit.md) · [架构](docs/architecture.md) · [算法差异](docs/algorithm-port.md)
- [功能映射](docs/feature-mapping.md) · [静态视觉依据](docs/visual-spec.md)
- [采集验证](docs/capture-validation.md) · [媒体效果验证](docs/media-effects-validation.md)
- [第三方许可](licenses/THIRD-PARTY-NOTICES.md)
- [Windows 分支吸收与新增验证](docs/windows-port-integration.md)

无遥测、账号、广告或云端媒体上传。本地验证包包含 Gyan FFmpeg GPLv3 构建；公开分发前须完成该构建全部依赖的对应源码与许可材料。Windows 10、ARM64、自动更新和真实 Mac 工程往返兼容未声明支持。
