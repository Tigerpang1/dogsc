# FrameTrail 构建与发行

## 项目参数

- 产品名：FrameTrail（Windows 迁移工程内部代号）
- 版本：0.1.0
- 目标：.NET 8 WPF、`win-x64`、自包含发布目录
- 主项目：`src/FrameTrail.App/FrameTrail.App.csproj`
- 发布目录：`artifacts/release/FrameTrail-0.1.0-win-x64/app`
- 输出：便携目录、ZIP、per-user Inno Setup 安装程序

脚本会在应用 csproj 尚未集成时停止并显示路径错误。应用主程序默认名为 `FrameTrail.App.exe`；集成后若 `AssemblyName` 不同，可在打包时使用 `-ApplicationExeName` 明确指定。

## 构建和测试

需要已安装 .NET 8 SDK。仓库 `global.json` 固定 SDK `8.0.303` 并允许采用相同 feature band 的后续补丁。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

`build.ps1` 构建 WPF 应用；`test.ps1` 运行 `FrameTrail.Core.Tests`。可传 `-Configuration Debug`。`-IncludeCaptureProbe` 只构建设备采集探针，不会启动探针、访问设备、开始录制或申请权限。

## 本地验证包

```powershell
.\scripts\package.ps1
```

脚本执行 `dotnet publish`，生成自包含 `win-x64` 应用目录，将许可证和 VC 运行库说明放进便携包，复制本机 Gyan FFmpeg 8.1.1 的 `ffmpeg.exe`/`ffprobe.exe`，并通过 `fetch-ffmpeg-corresponding-source.ps1` 归档上游精确 FFmpeg commit、Gyan README、构建配置、二进制哈希和 GPLv3 文本。随后生成 ZIP 和 Inno Setup 安装程序。

本机验证版包含 GPLv3 的 Gyan FFmpeg full static build。它可以供本地功能验证使用，生成的 ZIP/安装程序不得直接公开发布。FFmpeg 是独立子进程，通过命令行处理媒体；该技术边界不免除随静态 GPLv3 构建发行时的许可义务。

当前源获取脚本精确下载 FFmpeg 本体 commit `239f2c733d`；它还没有抓取 Gyan full build 静态包含的全部第三方库源码、精确版本、补丁及构建脚本。公开发行前需补齐完整对应源代码材料，核对许可证/NOTICE，并由发行负责人确认 GPLv3 义务。公共发布流程目前没有启用。

可用 `-WithoutFfmpeg` 生成不含该本机 FFmpeg 的打包目录，但依赖 FFmpeg 的导出功能将不可用；这只用于检查其他打包部分。`-SkipInstaller` 用于仅生成发布目录和 ZIP。

## 安装行为与运行依赖

安装器使用 Inno Setup，以 `PrivilegesRequired=lowest` 安装到当前用户的 `%LOCALAPPDATA%\Programs\FrameTrail`，仅创建当前用户开始菜单快捷方式，可选桌面快捷方式，卸载信息写入当前用户范围。脚本不会在运行中自动安装或升级机器级组件。

ScreenRecorderLib 需要 Microsoft Visual C++ 2015–2022 Redistributable (x64) 和 Windows Media Foundation。安装器会检测 64 位 VC 运行库注册项；若缺失会显示微软官方链接及说明，用户可以取消安装。它不会静默下载或安装 VC 运行库。Windows N/KN 版可能需要 Media Feature Pack，Windows Server 需要相应的 Media Foundation 组件。

自包含发布目录减少对 .NET Desktop Runtime 的依赖，但不会代替系统级媒体组件。主程序图标由迁移工程自行制作；禁止复用 DogSC 上游图标、商标和应用身份。

## 安装器编译器

若 PATH 或 Inno Setup 预期位置没有 `ISCC.exe`，`ensure-inno-setup.ps1` 会从 Inno Setup 官方 GitHub 发行页下载签名安装程序，校验 Authenticode 签名发布者后，使用其便携选项将编译器放入 `artifacts/release/_tooling`。该过程只获取项目构建工具，不安装或卸载 FrameTrail，也不写入系统级 Inno Setup 安装。Inno Setup 官方页面对商业使用有单独许可说明；涉及商业发行时须确认所用编译器版本和使用方式符合其许可要求。

## 发行检查

发布材料和第三方组件见 [`../licenses/THIRD-PARTY-NOTICES.md`](../licenses/THIRD-PARTY-NOTICES.md)。每次升级 NuGet 版本、FFmpeg 构建、编译开关或发行文件，都要重新审阅许可证清单和构建配置。只在完成项目集成后运行这些脚本；在本机功能和包结构验证通过前不要创建公开发行物。
