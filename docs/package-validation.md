# 打包与安装验证记录

本记录对应 2026-10-08 本机生成的 FrameTrail 0.1.0 验证包。该包用于检查自包含运行、文件布局和安装器行为，不是公开发行包。

## 验证结果

- `scripts/package.ps1` 成功完成 Release、`win-x64`、自包含发布，并生成便携 ZIP：`artifacts/release/FrameTrail-0.1.0-win-x64.zip`，SHA-256：`498A0F4C386C79141C26E12C3619F0492D23275D09187EAC91A57C541BB33AC3`。
- Inno Setup 6.7.3 成功生成 per-user 安装器：`artifacts/release/FrameTrail-Setup-0.1.0-win-x64.exe`，SHA-256：`7C70711B5C9B75638A3EED137E03073A8290FF9214BAFBB8A37816C203D3FDEC`。编译器报告 `x64` 架构标识已弃用并自动替换为 `x64os`；此警告不影响本次编译结果。
- 从发布目录运行 `FrameTrail.exe --smoke artifacts/validation/packaged-smoke.png`，进程退出码为 0，并生成 84,484 字节的 PNG 截图。进程环境的 `PATH` 只含 `C:\Windows\System32;C:\Windows`，没有开发工具路径；窗口正常显示并自动退出。
- 以验证程序的 `AppContext.BaseDirectory` 指向测试副本目录，并将包内 `tools/ffmpeg` 放在该目录下；清空 `FRAMETRAIL_FFMPEG_DIR`、限制 `PATH` 后，`MediaTools.Ffmpeg` 与 `MediaTools.Ffprobe` 都解析到包内工具，FFmpeg 返回 `8.1.1-full_build-www.gyan.dev`。
- 在测试前确认本机不存在 FrameTrail 的 HKCU/HKLM 卸载注册项。安装到 `artifacts/install-test/app` 后，主程序和 `tools/ffmpeg/ffmpeg.exe` 位于安装目录根，HKCU 卸载记录创建；运行 `unins000.exe /VERYSILENT` 后，程序文件和 HKCU 记录均删除，退出码为 0。临时安装目录随后清除。
- 本机已安装 Microsoft Visual C++ 2015–2022 Redistributable (x64)，因此本次录制依赖检查只验证了提示和不静默安装逻辑；干净虚拟机尚未验证。

## 许可证与版本边界

本机验证包包含 Gyan FFmpeg 8.1.1 GPLv3 构建。包内保留了构建配置、版本信息、GPL 文本和 FFmpeg 核心仓库提交前缀 `239f2c733d` 对应的源码压缩包。Gyan 构建静态外部依赖的完整精确源码、构建脚本与补丁尚未全部收集，因此当前材料不足以宣称满足公开发行要求；该包不得直接公开发布。媒体导出通过独立 FFmpeg 子进程调用，不静态链接 FFmpeg。

FrameTrail 录制功能依赖微软 VC++ x64 运行库。安装程序只检测并提示，不会安装或更新该系统级运行库。当前预览版本每次录制最长 60 秒；发布脚本会在包内写入 `RECORDING-LIMIT.txt`。

## 当前包与最终包的差别

本次打包产物是在 2026-10-08 22:30 左右构建的。`src/FrameTrail.App/MainWindow.xaml.cs` 后续修改时间为 22:38，故本记录列出的包不能代表这些后续界面修改。当前 `scripts/package.ps1` 和 `installer/FrameTrail.iss` 已补充在便携目录及安装目录中放入根 `LICENSE`、`README.md`、`docs/` 和 60 秒录制限制说明，并记录 FrameTrail HEAD 与 DogSC 上游基线；这些新增材料尚未进入本记录所列的 ZIP 与安装器。

源码压缩包由 `scripts/package-source.ps1` 使用 `git archive HEAD` 和上游固定基线 `e207d11b111c5e58152238fb64115b2009850547` 分别生成；该脚本尚未在最终提交状态下执行。最终包需等待代码和文档固定到最终提交后重新生成，并复核 SHA-256、 smoke、安装和卸载结果。当前工作区有未提交修改，`git archive HEAD` 会排除这些修改。

## 本机打包路径修正

当前工作区路径包含撇号，.NET SDK 8.0.303 将仓库内 `artifacts/release` 作为 `dotnet publish` 输出目录时触发 `MSB3094`（目标文件只有 1 项、源文件 481 项）。脚本现先发布到不含撇号的系统临时目录，再复制到发布目录。Gyan README 已提供 FFmpeg 固定提交前缀；源码下载改为直接访问 GitHub codeload，避免依赖受匿名速率限制的 GitHub REST API。
