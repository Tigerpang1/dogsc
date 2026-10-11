<p align="center">
  <img src="docs/assets/DogSC.png" width="112" alt="DogSC 应用图标">
</p>

<h1 align="center">DogSC · Windows 社区移植版</h1>

<p align="center">把录屏、剪辑、运镜与导出带到 Windows。</p>

<p align="center">
  <a href="https://github.com/laogou717/dogsc"><strong>DogSC 原项目</strong></a>
  · <a href="https://github.com/lantiansb/dogsc">Windows 社区仓库</a>
  · <a href="https://github.com/lantiansb/dogsc/issues">Windows 问题反馈</a>
  · <a href="https://github.com/laogou717/dogsc/releases/latest">macOS 版下载</a>
</p>

这是 DogSC 的 Windows 社区移植版，沿用原项目名称与品牌视觉。原项目由作者神烦老狗发布，采用 MIT 许可证；本仓库遵循该许可证并保留来源。Windows 版本由 `lantiansb` 维护，不代表原作者发布或背书。macOS 版本请通过上方原项目链接获取。

本仓库保留 GitHub Fork 关系：默认分支 `codex/windows` 维护 Windows 实现，`main` 保留 Fork 时的原项目代码。贡献流程与自动检查范围见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 下载 Windows Beta

**DogSC 0.2.17 · Windows 11 x64 · 社区测试版**

| 下载 | 用法 |
|---|---|
| **[下载安装器（EXE）](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-Setup-0.2.17-win-x64.exe)** | 安装到当前用户，创建开始菜单入口。 |
| **[下载便携版（ZIP）](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-0.2.17-win-x64.zip)** | 完整解压后运行 `app/DogSC.exe`。 |
| [下载源码（含原 DogSC 快照）](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-0.2.17-source.zip) | 开发与自行构建。 |
| [SHA-256 校验文件](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-0.2.17-SHA256SUMS.txt) | 核对下载文件完整性。 |

主要功能包括显示器、窗口和区域录制，托盘常驻与独立录制浮窗、口播提词器、工程恢复、多轨剪辑、运镜效果，以及 MP4/M4A 导出。安装包自带 .NET 和 FFmpeg。录制还需要系统具备 [Microsoft Visual C++ x64 运行库](https://aka.ms/vc14/vc_redist.x64.exe)。当前包未签名，Windows 可能显示发布者提示。

这是首个 Windows Beta；摄像头、多设备与多显示器/混合 DPI、长时间稳定性仍未完整验收，暂不包含在线自动更新。详细变更、验收范围和已知问题见 [Release 页面](https://github.com/lantiansb/dogsc/releases/tag/v0.2.17-windows-beta.1)和[版本说明](docs/release-0.2.17.md)。

本软件使用 FFmpeg：预览动态库采用 LGPL，独立编码程序采用 GPL；其精确源码、依赖和构建脚本随包提供，也可[单独下载对应源码](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-FFmpeg-8.1.2-sources.zip)。许可及构建说明见 [FFmpeg 来源](third_party/ffmpeg/README.md)。

## 从源码运行

需要 Windows 11 x64、.NET 10 SDK 和 Windows Media Foundation。构建脚本自动取得经固定 SHA-256 核验的媒体依赖。克隆后初始化上游子模块，再按仓库构建说明操作：

```powershell
git clone --branch codex/windows --recurse-submodules https://github.com/lantiansb/dogsc.git
cd dogsc
git submodule update --init --recursive
pwsh -File scripts/build.ps1
```

详细步骤见[构建与发行](docs/build-and-release.md)；功能操作见[使用说明](docs/user-guide.md)。历史开发记录保存在[开发历史](docs/development-history.md)。

## 许可与来源

DogSC 原项目：[github.com/laogou717/dogsc](https://github.com/laogou717/dogsc)，作者：神烦老狗，许可证：MIT。许可证副本见 [`licenses/DogSC-MIT.txt`](licenses/DogSC-MIT.txt)。Windows 移植还包含第三方依赖，各自许可证见 [`licenses/THIRD-PARTY-NOTICES.md`](licenses/THIRD-PARTY-NOTICES.md)。

原项目以只读子模块保存在 `upstream/dogsc`，固定提交为 `e207d11b111c5e58152238fb64115b2009850547`；源码发行包包含该快照。应用图标直接沿用原图，来源、校验值与 Windows ICO 转换方法见 [`licenses/DogSC-ASSETS.md`](licenses/DogSC-ASSETS.md)。Windows 适配另参考 [`xiaoying8666/dogscwindows`](https://github.com/xiaoying8666/dogscwindows)，并保留其独立署名与 MIT 许可。
