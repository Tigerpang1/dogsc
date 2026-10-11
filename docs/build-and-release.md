# DogSC Windows 社区移植版构建与发行

## 品牌与项目参数

本仓库维护 DogSC Windows 社区移植版，沿用原项目名称和品牌素材。此品牌恢复按本轮维护者明确指令执行，优先于本文件中较早的迁移品牌隔离规则；产品对外须注明“Windows 社区移植版”，不表示原作者官方发布或背书。原项目作者为神烦老狗，许可证为 MIT，固定上游提交为 `e207d11b111c5e58152238fb64115b2009850547`。图标来源为 `upstream/dogsc/Resources/AppIconMaster.png`。

- 产品名：DogSC · Windows 社区移植版（兼容性技术标识仍可能使用 FrameTrail）
- 目标版本：0.2.17 Beta 准备版；当前发布状态见 [`release-preparation.md`](release-preparation.md)
- 目标：.NET 10 WPF、`win-x64`、自包含发布目录
- 主项目：`src/FrameTrail.App/FrameTrail.App.csproj`（技术项目路径保留 FrameTrail）
- 0.2.17 发行文件名：`DogSC-0.2.17-win-x64.zip`、`DogSC-Setup-0.2.17-win-x64.exe`、`DogSC-0.2.17-source.zip`
- 输出：便携目录、ZIP、per-user Inno Setup 安装程序

历史发行脚本和清单保留旧版 FrameTrail 路径与命名。当前应用可执行文件和程序集为 `DogSC.exe` 与 `DogSC.dll`；源码项目路径、命名空间和用户数据目录继续使用 FrameTrail 标识。打包脚本使用自包含 `win-x64` 发布，默认把本机 FFmpeg 放入 `app/tools/ffmpeg`。0.2.5 包对应源码提交 `e4bfd5ed44deb93272df4d3f52217e3cb8e43420`，固定 DogSC 上游基线为 `e207d11b111c5e58152238fb64115b2009850547`；其验收记录只属于该历史候选。

## 构建和测试

需要已安装 .NET 10 SDK 10.0.401 或更新的 .NET 10 SDK。仓库 `global.json` 以 SDK `10.0.401` 为起点，`latestFeature` 允许采用兼容的后续 .NET 10 feature band。所有项目统一声明 `win-x64` 恢复目标，使普通测试与发布不会轮流改写NuGet锁文件；发布启用 `RestoreLockedMode`，依赖图与已提交锁文件不一致时即停止。自包含发布目录包含对应的 .NET 10 Runtime 和 Windows Desktop Runtime；发行时应使用当前受支持的补丁版 SDK 重建，避免捆绑过时运行库。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

`build.ps1` 构建 WPF 应用；`test.ps1` 运行 Core 与 DogSC 导入测试，`-IncludeMediaProbe` 运行真实 GPU/编码矩阵及逐片段音量、辅助媒体时钟探针，`-IncludeIntegration` 运行完整剪辑导出集成和合成音画脉冲同步。可传 `-Configuration Debug`。`-IncludeCaptureProbe` 构建采集探针并运行包时钟、诊断分桶、启动/释放并发、故障收尾、参数校验、模拟会话恢复和麦克风时钟段验收规则的八组纯逻辑检查；不会访问设备、开始录制或申请权限。真实采集须按 `capture-validation.md` 的命令另行运行。 `-IncludeVisualProbe` 在不创建桌面窗口、不访问设备的条件下渲染实际WPF界面，验证浅/深主题文字对比度和时间线标签像素；这是可重复的后台回归，不替代真实焦点、弹层、DPI和动效验收。`-IncludeReleaseProbe` 用隔离合成报告验证发行清单会拒绝缺项、失败、重复检查、错误版本、脏工作区与包哈希变化，并检查源码归档不会误用父Git仓库身份。归档结构的17项合成用例覆盖根目录、路径穿越、重复和冲突路径、Windows文件名歧义与元数据类型；不运行应用或安装器。发行规则测试需要Git来创建隔离的测试仓库；源码归档验证也会执行这些检查。

## 本地验证包

源码验证的PowerShell子进程通过显式参数映射运行，并使用UTF-8记录stdout/stderr；`test-utf8-script.ps1` 验证命名参数、开关、特殊字符文本和失败退出码，不改变调用者的控制台代码页。

独立的 `FrameTrail.App.Smoke` 会操作真实鼠标键盘，必须显式传入 `--interactive-desktop`。启动后每次输入前核对前台属于本次测试应用；用户切走时停止输入并释放测试按住的键和鼠标按钮，结果标记未完成，不反复抢回焦点。Windows 拒绝自动激活时，可加 `--wait-for-foreground`，等待180秒内手动切入测试窗口一次。测试前将四个应用偏好文件的原始状态保存到本次临时目录；异常强停后可用 `FrameTrail.App.Smoke.exe --restore-user-data <测试临时目录>` 恢复，先核对该目录确属需要恢复的测试轮次。
PNG贴图生命周期专项可使用已授权的交互桌面运行：

命令：
dotnet run --project tests/FrameTrail.App.Smoke/FrameTrail.App.Smoke.csproj -c Release -- <DogSC.exe> <project.frametrail> <report.json> --interactive-desktop --png-overlay-undo-only

该模式、测试源码和回归入口是在冻结的0.2.5源码ZIP提交 e4bfd5ed44deb93272df4d3f52217e3cb8e43420 之后新增；冻结源码ZIP不含此模式，不能用该ZIP重现本次UI证据。

```powershell
.\scripts\package.ps1
.\scripts\validate-package.ps1 -InteractiveDesktop
```

脚本执行 `dotnet publish`，生成自包含 `win-x64` 应用目录，把根 `LICENSE`、`README.md`、`docs/`、第三方许可证和 VC 运行库说明一并放入便携包，复制本机 Gyan FFmpeg 8.1.1 的 `ffmpeg.exe`/`ffprobe.exe` 到 `app/tools/ffmpeg`，并通过 `fetch-ffmpeg-corresponding-source.ps1` 归档上游 FFmpeg commit、Gyan README、构建配置、二进制哈希和 GPLv3 文本。随后生成 ZIP 和 Inno Setup 安装程序。

便携版和安装版均随附 `samples/FrameTrail-Demo` 原创合成工程与8秒导出样片。每次打包均调用 `scripts/create-sample.ps1` 在全新临时目录生成，只复制八个固定生成物；这一步需要本机可用的 FFmpeg。不会从开发目录收集残留文件，样例不包含用户桌面或原版截图。样例说明见 `sample-project.md`。

validate-package.ps1 使用隔离 PATH、DOTNET_ROOT子进程验证便携播放/暂停、真实录制/编辑/重开/导出及per-user安装/卸载。0.2.5最终验收8/8通过，包含交互前偏好快照、ZIP内容、便携播放与工作流、安装版播放、卸载、偏好逐字节恢复和结束时477个发布目录文件与原ZIP逐字节一致。早期7项报告和ContentOnly样例副作用保留为历史，Demo已恢复；最终验收使用隔离播放副本。运行验收须显式传入 -InteractiveDesktop；脚本发现已有FrameTrail安装时会拒绝覆盖。-SkipInstallation只跳过安装阶段，结果范围为portable-only且不能生成完整验收清单。源码ZIP验证调用package.ps1 -WithoutFfmpeg -SkipInstaller，不会安装或更改全局依赖。manifest为validated-local-delivery，源码ZIP验证通过。

源码归档命令（工作区必须已全部提交）：

```powershell
.\scripts\package-source.ps1
.\scripts\validate-source-package.ps1 -SourceZip .\artifacts\release\DogSC-0.2.17-source.zip
.\scripts\write-release-manifest.ps1
```

校验清单脚本只有在程序包与源码包的已测SHA256、源码提交均一致时才生成 `DELIVERY-MANIFEST.json` 和 `SHA256SUMS.txt`；同时保留带版本号的 `FrameTrail-0.2.5-manifest.json` 和 `FrameTrail-0.2.5-SHA256SUMS.txt`，便于后续版本交付后继续核对本版。0.2.5最终清单已生成。清单状态表示本机Windows 11 x64本地交付验收通过；它不代表干净虚拟机、其他硬件、完整多DPI/多显示器、真实Mac工程或公开发行许可义务已验证。

没有交互桌面时可先生成后台候选：

```powershell
.\scripts\validate-package.ps1 -Version 0.2.17 -ContentOnly
.\scripts\write-release-manifest.ps1 -Version 0.2.17 -Candidate
```

`-ContentOnly` 不启动应用或安装程序，只检查自包含运行库、样例和许可，逐文件核对便携ZIP与发布目录的路径、长度及SHA256，并保存 `content-result.json`。仍须先完成源码归档独立构建验证。`-Candidate` 只写 `DogSC-0.2.17-candidate-manifest.json` 和 `DogSC-0.2.17-candidate-SHA256SUMS.txt`，状态为 `candidate-awaiting-interactive-validation`；保留上一已验版本的通用清单。候选内容验证不能替代播放、录制、安装卸载或全量UIA。完整清单按稳定编号逐项核对包内容、便携版播放与录制流程、安装版播放、卸载，以及交互前后用户偏好的原始字节保存与恢复；要求每项唯一且通过。`-SkipInstallation` 结果不能生成完整清单。

当前命令生成 `DogSC-0.2.17-source.zip`，其中仍把 FrameTrail `git archive HEAD` 和 `upstream/dogsc` 固定基线 `e207d11b111c5e58152238fb64115b2009850547` 一并归档；上游快照位于 `FrameTrail/upstream/dogsc/`。压缩包内 `FrameTrail/SOURCE-METADATA.json` 记录源码提交、上游基线和打包时工作区状态。历史 `FrameTrail-0.2.5-source.zip` 的证据和文件名保持原样。Git 归档只收录提交中的文件，不含未提交修改，也不会打包 `artifacts/`、`bin/`、`obj/` 或真实录制素材。

从源码 ZIP 构建时，上游快照已经包含在 `FrameTrail/upstream/dogsc/`，不需要运行 `git submodule update`。从 Git 克隆仓库时，才需要按仓库说明初始化上游 submodule。源码 ZIP 中没有 `.git` 元数据；`package-source.ps1` 只在发行者重新生成源码 ZIP 时需要 Git。 `package.ps1` 仅当源码目录拥有自己的 Git 元数据且Git根目录匹配时读取提交与上游基线；源码 ZIP 构建读取 `SOURCE-METADATA.json`，即使解压在其他Git仓库内部，也不继承父仓库的提交。无 Git 源码归档路径的验证状态以 `artifacts/validation/package-0.2.5/source-validation.json` 为准。

0.2.5本机验证包包含 GPLv3 的 Gyan FFmpeg full static build。现有 ZIP 和安装程序属于旧版本本地验证产物，不可作为 0.2.17 Beta 发布材料。FFmpeg 是独立子进程，通过命令行处理媒体；该技术边界不免除随静态 GPLv3 构建发行时的许可义务。

当前源获取脚本精确下载 FFmpeg 本体 commit `239f2c733d`；它还没有抓取 Gyan full build 静态包含的全部第三方库源码、精确版本、补丁及构建脚本。公开发行前需补齐完整对应源代码材料，核对许可证/NOTICE，并由发行负责人确认 GPLv3 义务。该材料是当前含 FFmpeg 候选包的公开分发阻塞项。是否签名是单独的信任提示与发布者身份选择，不作为绝对发布前提。

可用 `-WithoutFfmpeg` 生成不含该本机 FFmpeg 的打包目录，但依赖 FFmpeg 的导出功能将不可用；这只用于检查其他打包部分。`-SkipInstaller` 用于仅生成发布目录和 ZIP。

## 安装行为与运行依赖

安装器限制 Windows 11 build 22000 及以上的原生 x64 系统，采用 [Inno Setup 的 MinVersion](https://jrsoftware.org/ishelp/topic_setup_minversion.htm) 与 [x64os 架构标识](https://jrsoftware.org/ishelp/topic_archidentifiers.htm)。

安装器使用 Inno Setup，以 `PrivilegesRequired=lowest` 安装到当前用户的 `%LOCALAPPDATA%\Programs\DogSC`，仅创建当前用户开始菜单快捷方式，可选桌面快捷方式，卸载信息写入当前用户范围。脚本不会在运行中自动安装或升级机器级组件。

ScreenRecorderLib 需要 Microsoft Visual C++ 2015–2022 Redistributable (x64) 和 Windows Media Foundation。安装器会检测 64 位 VC 运行库注册项；若缺失会显示微软官方链接及说明，用户可以取消安装。它不会静默下载或安装 VC 运行库。Windows N/KN 版可能需要 Media Feature Pack，Windows Server 需要相应的 Media Foundation 组件。

自包含发布目录减少对 .NET Desktop Runtime 的依赖，但不会代替系统级媒体组件。当前可执行文件/程序集和安装目录使用 DogSC 名称；源码项目路径与命名空间、`.frametrail` 工程格式、用户数据目录及历史交付路径继续保留 FrameTrail 兼容标识。Windows 版本由社区维护，必须与原项目 macOS 发行区分。

## 安装器编译器

若 PATH 或 Inno Setup 预期位置没有 `ISCC.exe`，`ensure-inno-setup.ps1` 会从 Inno Setup 官方 GitHub 发行页下载签名安装程序，校验 Authenticode 签名发布者后，使用其便携选项将编译器放入 `artifacts/release/_tooling`。该过程只获取项目构建工具，不安装或卸载 FrameTrail，也不写入系统级 Inno Setup 安装。Inno Setup 官方页面对商业使用有单独许可说明；涉及商业发行时须确认所用编译器版本和使用方式符合其许可要求。

## 发行检查

发布材料和第三方组件见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。每次升级 NuGet 版本、FFmpeg 构建、编译开关或发行文件，都要重新审阅许可证清单和构建配置。当前脚本可构建本地验证包；含该 FFmpeg 构建的公开包须等对应源代码与许可证材料补齐、审查完成后再发行；不含 FFmpeg 的构建会失去依赖该工具的录制/导出能力，不能作为等价成品宣传。
