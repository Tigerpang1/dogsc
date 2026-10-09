# FrameTrail 0.2.1 本机打包验证

本轮交付为 Windows 11 x64 本地开发预览。便携包与 per-user 安装程序均已构建并经过本机运行检查，源码归档要求 Git 工作区干净。每个产物的实际提交、哈希及最后验证结果由包内 `BUILD-INFO.txt`、源码内 `SOURCE-METADATA.json` 和工作区机器可读报告确定。

0.2.1 使用 `scripts/validate-package.ps1 -Version 0.2.1` 和 `scripts/validate-source-package.ps1 -Version 0.2.1 -SourceZip <ZIP>`。最终结果分别写入 `artifacts/validation/package-0.2.1/result.json` 和 `source-validation.json`，必须分别为 `Passed: true`、`status: passed`，并与发布清单中的包哈希对应。下面的 0.2.0 记录作为已完成的历史基线保留。

## 0.2.0 历史包验证

`scripts/validate-package.ps1 -Version 0.2.0` 于2026-10-09完成预检，五项检查全部通过：必要程序/原创样例/许可证齐备；便携版有声播放与暂停恢复；真实屏幕录制、暂停、编辑、重开与导出；安装版样例播放；卸载后的主程序及 HKCU 安装注册清除。预检运行于08:21:13–08:21:59 UTC。

验证进程将 PATH 限制为 Windows 系统目录、将 DOTNET_ROOT 指向不存在的目录，并移除外部 FFmpeg 配置，确认包内自带工具可用。验证仍在已安装 VC++ 运行库的开发机上，不能等同于干净虚拟机。最终产物重新构建后会重复相同验证，结果覆盖于 `artifacts/validation/package-0.2.0/result.json`，具体媒体数据以该报告关联的 workflow JSON 为准。

安装脚本只在无既有 FrameTrail 安装时运行，安装到验证目录；启动自己的卸载程序，并核验该目录内主程序和 HKCU 注册项被清除。用户项目与偏好保留。安装器只支持 Windows 11 x64，不会安装或升级系统级 VC++ 运行库。

## 源码归档验证

`scripts/package-source.ps1` 只归档干净 HEAD，并追加固定 DogSC 源码基线。`scripts/validate-source-package.ps1 -SourceZip <ZIP>` 将归档解压到工作区以外的 TEMP UUID 目录，检查无 `.git`、验证元数据，再运行 Core/导入器测试、App Release build 和 `package.ps1 -WithoutFfmpeg -SkipInstaller`。它检查打包元数据确实使用 `source archive` 回退，而不是误找到外层仓库。

2026-10-09 09:04:34–09:05:00 UTC 的源码归档预验已通过：Core/导入器测试、App Release 构建及重新打包三步退出码均为 0，验证了工作区外解压、无 `.git` 及 source archive 元数据回退。

源码归档的实际验收状态由 `artifacts/validation/package-0.2.0/source-validation.json` 决定；该报告必须为 `status: passed` 才视为此项通过。日志与解压副本会保留，未通过不会自动标记成功。

## 发行边界

包内附带原创8秒合成样例、使用说明、架构、迁移映射、测试报告、MIT及第三方许可。无60秒录制上限；长时性能和同步验收见采集报告。Windows 实录桌面和本地重建的视觉fixture不会随包分发；源码归档包含固定上游快照自身的README示例图片。

当前 Gyan FFmpeg 8.1.1 full static 构建包含 GPLv3 组件。已随包保存 FFmpeg 本体对应源码、构建配置、二进制哈希与 GPLv3 文本；静态依赖的全部对应源码、补丁和构建脚本仍未完整核实。此包用于本地交付，公开发布保持未启用。没有商业代码签名、干净虚拟机或全部物理设备矩阵通过的声明。
