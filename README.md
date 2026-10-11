# FrameTrail · Windows 录屏与运镜

FrameTrail 是基于 [DogSC](https://github.com/laogou717/dogsc) 的设计与核心算法开发的独立 Windows 迁移项目，不代表原作者的官方 Windows 发行。目标平台为 **Windows 11 x64**。

固定上游提交：`e207d11b111c5e58152238fb64115b2009850547`。开始迁移时核验远端 `main` 与 `v0.1.2` 均为该提交；本轮继续采用此基线。源码子模块在 `upstream/dogsc`，MIT 许可全文在 `licenses/DogSC-MIT.txt`。

## 当前：0.2.12 录制、预览与画质候选

0.2.12 在录制条增加齿轮菜单，录制浮窗菜单沿用独立 `TrayMenuWindow`，最近项目子菜单单独设置录屏排除；录制完成提示简洁显示，采集格式、时钟和 backend 信息进入诊断，真实异常仍进入警告。新菜单和提词器、菜单及最近项目子菜单的录屏排除仍待实机验收。

时间线悬停预览会保留到点击定位、开始播放或关闭预览；左键按下立即定位播放头。预览解码器按一个 active 请求加一个 latest pending 请求处理快速拖动，并保持同素材解码器会话。本轮确认的最近三段实录均为4K60；旧预览固定1440分辨率再放大，本轮改为按视口物理像素决定自动预览目标，同时保持源素材原始分辨率解码以保留缩放细节；用户也可选 960、1440 或 1920 预览尺寸。录制默认使用源分辨率，NVENC 使用 p5/hq/vbr/cq19，1080p60 目标约16 Mbps、4K60 最高目标64 Mbps。Shader bytecode 本地缓存和后台预热入口已接入。

0.2.12通过固定 SDK 10.0.401 的最终构建（0警告、0错误）。编码媒体 MainWindow 预览交互与尺寸探针各12项通过；物理视口、4K源分辨率解码和共享播放场景像素对照通过。独立 `PreviewPlaybackSession` 保留4K源解码、合成到2400×1350@30时，zoom 1/2连续30帧分别约34.3 fps，稳态中位数14.85/14.55ms，首帧438.62/407.19ms；这是无窗口的离屏吞吐，不代表用户实机拖动体验。证据见 [`playback-throughput`](artifacts/validation/capture-video-quality-v5/playback-throughput/result-named-pipe.json)、[`preview-interaction-pipe`](artifacts/validation/package-0.2.12/preview-interaction-pipe/result.json) 和 [`preview-sizing-pipe`](artifacts/validation/package-0.2.12/preview-sizing-pipe/result.json)。

质量策略对照使用合成4K60素材，2秒编码1.741秒，SSIM从旧6 Mbps档的0.827179升至0.988639，平均输出约60.15 Mbps；这不是实采画质证据。另有8.1秒、3840×2160@60、486帧的真实本机录制，H.264 NVENC成片完整解码且没有警告，但无音频，不能代表动态桌面、菜单排除或主观画质认可。新录制条菜单及浮窗排除仍待实机确认；预览操作和录制画质也需用户实机验收。便携包、安装器和源码归档尚待生成与核验，本版仍是候选。

口播提词器可编辑或阅读文稿，自动滚动默认停止；可调整速度、暂停/继续和回到开头。背景不透明度只作用于背景，文字保持清晰。正文自动保存，模式和外观设置在收起或关闭时保存；保存失败时窗口与文字会保留，可修复文件访问后重试。具体操作见[使用说明](docs/user-guide.md)。0.2.9已由用户确认主窗关闭后的浮窗独立、托盘找回和提词滚动；0.2.11进一步确认托盘菜单稳定、托盘退出、提词器文稿和外观重启恢复、重复启动召回及录制中关闭主窗后仍可结束录制。

当前功能台账为28项通过、2项待验、3项延期，分母仍为33项。FT-07仅剩新录制条菜单及提词器、菜单和子菜单的录屏排除实机验证；权限拒绝后的复杂恢复按用户要求暂缓。Mac 工程互通不属于本轮要求。该状态不代表达到95%门槛。下列0.2.5～0.2.8段落保留历史范围与证据，版本身份不能混用。

0.2.11菜单稳定、托盘退出、提词器文稿与外观恢复、重复启动召回及录制中关闭编辑器的用户确认，证据见 [桌面常驻记录](docs/desktop-residency.md)。0.2.12菜单、预览调度/尺寸、shader 缓存、录制质量策略及后台构建已有候选证据；人工实机验收和发行包验证仍待完成。具体范围见 [0.2.12记录](docs/release-0.2.12.md)。

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

## 0.2.7 交互收尾候选

时间线和全局控件改用统一的实线焦点提示，中文输入环境下的编辑快捷键可正常使用。文本右键菜单统一为中文圆角样式，设置错误就地提示，导出成功后的退出按钮显示“完成”。素材重定位按事务提交，坏素材可重选，取消会清理本轮副本；保存失败后控件恢复到已保存值。

0.2.7本轮有限证据见 [UI验收记录](docs/ui-craft-validation.md)：v3覆盖素材定位取消/成功重开及轨道显隐、吸附、悬浮预览；v4覆盖变速失败重试/撤销、主片段菜单浅深主题及首尾重排禁用，并核对19.3秒实机成片的顺序和源时间；v5覆盖FT-21至FT-23实际媒体帧，以及FT-17开场和FT-18自动/手动缩放的实机配置、保存重开和实际预览/导出帧核对。偏好应用像素对照使用相同固定 `ExportOptions` 直接调用 `ExportEngine`，不代表真实导出设置窗口路径。当时有限场景记录为28项通过、2项待验、3项延期；FT-03仅覆盖单显示器、当前DPI下人工拖选并确认区域四边，不能据此声称绝对坐标/原始Bounds核验、多DPI或最高1080p缩放验证。FT-04仅覆盖单机显示器/窗口/麦克风菜单枚举和可用状态，自身窗口过滤在19d19f1候选验证，不代表所有外部窗口都能成功采集或正式包已验证。FT-08备用热键实机补验、窗口过滤和短区间尾帧修复均已进入本机0.2.8源码候选，未进入正式交付包。FT-03区域证据使用0.2.8测试候选，媒体754×188、60 FPS、10.2秒、612帧完整解码，App SHA为 `A5F24645D51087E895C9A111B0A562AD1931EFF2AC222627C8678F295BBF4532`；正常完成后恢复记录已删除，旧媒体检查结果中的 `Passed: false` 保留。摄像头延期，Mac工程互通不属于要求。0.2.7为正式本地交付候选；对应源码以包内 BUILD-INFO / SOURCE-METADATA 为准，验证结果见交付清单。分母仍为33项，95%门槛为32/33，该阶段28项通过，尚未达标。

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
pwsh -File scripts/package.ps1 -Version 0.2.12
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
