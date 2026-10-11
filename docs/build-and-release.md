# DogSC Windows 社区版构建与发行

## 环境

Windows 11 x64，.NET 10 SDK（`global.json` 固定起点 10.0.401），PowerShell 7，Git。录制依赖 Windows Media Foundation 与 Visual C++ x64 运行库。项目使用 WPF、D3D11、Windows Graphics Capture 和 WASAPI。

原项目以只读子模块固定在 `upstream/dogsc`，提交 `e207d11b111c5e58152238fb64115b2009850547`。从 Git 克隆需初始化子模块；Release 的源码 ZIP 已包含上游快照，无需再次下载。

```powershell
git clone --branch codex/windows --recurse-submodules https://github.com/lantiansb/dogsc.git
cd dogsc
pwsh -File scripts/build.ps1
pwsh -File scripts/test.ps1 -IncludeCaptureProbe -IncludeReleaseProbe
```

构建脚本从本仓库 Release 下载固定的媒体归档，按 SHA-256 与文件清单核验后部署。首次构建需要联网；离线可给 `scripts/ensure-libav.ps1` 传入 `-ArchivePath <媒体ZIP> -Offline`。来源和原生组件重建见 [FFmpeg 说明](../third_party/ffmpeg/README.md)。

应用主程序为 `DogSC.exe`；`FrameTrail.*` 内部命名、`.frametrail` 工程和历史用户目录保留兼容。产品、README、安装器均保留原 DogSC 图标、神烦老狗与 MIT 署名。

## 检查

- `test.ps1`：Core 与 DogSC 只读导入测试。
- `-IncludeCaptureProbe`：采集时钟、参数和错误恢复的纯逻辑检查，不访问设备。
- `-IncludeMediaProbe -IncludeIntegration`：GPU 渲染、原生解码、帧缓存、真实编码、音轨与导出测试。
- `-IncludeVisualProbe`：离屏 WPF 视觉与后台交互调度，不代表真实桌面验收。
- `-IncludeReleaseProbe`：发行清单、源码归档身份、ZIP 结构和 UTF-8 执行检查。

媒体测试应使用随包 CLI，可将 `FRAMETRAIL_FFMPEG_DIR` 指向 `artifacts/toolchains/dogsc-native-8.1.2/cli`。不得用另一套本机 FFmpeg 的成功结果替代发行依赖验证。

## 打包

```powershell
pwsh -File scripts/package.ps1 -Version 0.2.17
pwsh -File scripts/package-source.ps1 -Version 0.2.17
pwsh -File scripts/validate-source-package.ps1 -Version 0.2.17 -SourceZip artifacts/release/DogSC-0.2.17-source.zip
```

打包前必须提交代码，固定干净的源码提交。自包含程序、媒体依赖、对应源材料、许可证、文档和合成样例进入便携包与 Inno Setup 当前用户安装器。样例从固定生成流程产生，不收集开发目录或真实用户录屏。源码 ZIP 仅含本仓库已提交内容、固定上游快照及来源元数据，不包含 `artifacts`、用户工程和验证录像。

`package.ps1 -WithoutFfmpeg -SkipInstaller` 仅供源码归档构建检查；它不提供完整录制/导出能力，不作为公开成品。原生媒体包必须与 `third_party/ffmpeg/manifest.json` 匹配，旧 Gyan 本地验证包不能混入公开发行。

## 最终包验收

```powershell
# 会启动真实窗口、播放声音、运行短录制和安装/卸载；需在桌面可用于验收时执行
pwsh -File scripts/validate-package.ps1 -Version 0.2.17 -InteractiveDesktop
pwsh -File scripts/write-release-manifest.ps1 -Version 0.2.17 -PublicBeta
```

完整验收覆盖便携版播放/暂停恢复、录制/编辑/重开/导出、安装版播放、卸载、四个固定用户偏好文件的字节恢复，以及结束时便携目录与 ZIP 的逐文件一致性。安装检查使用唯一验证目录；发现已有相同 AppId 的安装时拒绝覆盖。异常后应依据报告中的偏好恢复指令处理。

`-ContentOnly` 只做后台内容验证；`-SkipInstallation` 只做便携运行验证。二者都不能生成公开 Beta 清单。公开清单还核验媒体二进制与对应源码资产的固定哈希。

## GitHub 发布

使用 `lantiansb`，目标仓库 `lantiansb/dogsc`，默认开发分支 `codex/windows`。本地仓库应单独设置该账户的 GitHub noreply 提交邮箱，不能只检查 gh 登录账户。

0.2.17 的 tag 为 `v0.2.17-windows-beta.1`，指向已打包和验证的同一源码提交。先创建草稿 Release，按清单上传以下资产，核对服务器哈希后再设为公开 prerelease：

- `DogSC-Setup-0.2.17-win-x64.exe`
- `DogSC-0.2.17-win-x64.zip`
- `DogSC-0.2.17-source.zip`
- `DogSC-FFmpeg-8.1.2-win-x64.zip`
- `DogSC-FFmpeg-8.1.2-sources.zip`
- `DogSC-0.2.17-manifest.json`
- `DogSC-0.2.17-SHA256SUMS.txt`

仅上传清单中的公开产物，不能上传 `artifacts/validation`、用户录屏、录音、偏好快照或开发日志。记录验收边界，不能把本机通过等同于干净机器、所有硬件或 95% 功能完成。GitHub Actions 不生成公开安装包；发布前需完成上述本机验收。

## 安装行为

安装器针对 Windows 11 build 22000 及以上原生 x64，以最低权限安装到当前用户 `%LOCALAPPDATA%\Programs\DogSC`。创建开始菜单入口，可选桌面快捷方式；卸载保留用户工程和设置。固定 AppId 支持旧 FrameTrail 安装升级，升级只清理精确列出的旧启动文件和快捷方式。

安装器不安装或修改机器级运行库。若缺少 VC x64 运行库，会显示微软官方链接与说明。当前包未签名；签名状态与功能验证分别记录。
