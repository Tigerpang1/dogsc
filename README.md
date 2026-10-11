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

这是 DogSC 的 Windows 社区移植版，沿用原项目名称与品牌视觉。原项目由作者神烦老狗发布，采用 MIT 许可证；本仓库遵循该许可证并保留来源。Windows 版本由 `lantiansb` 维护，不代表原作者发布或背书。请通过上方原项目链接获取 macOS 版本；本仓库目前没有可供下载的 Windows Beta 安装包。

本仓库保留 GitHub Fork 关系：默认分支 `codex/windows` 维护 Windows 实现，`main` 保留 Fork 时的原项目代码。贡献流程与自动检查范围见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## Windows Beta 准备

当前开发版本为 0.2.17，目标环境是 Windows 11 x64。主要功能包括显示器、窗口和区域录制，项目恢复与素材管理，多轨剪辑和运镜效果，以及 MP4/M4A 导出。功能台账有 33 项，其中 28 项在限定场景通过、2 项待验、3 项延期；这不等于全部功能完成或整体达到 95%。发布准备状态、已知问题和验收边界见[发布准备说明](docs/release-preparation.md)。

目前没有 Windows 下载地址。发布前还需补齐随包 FFmpeg 构建的完整对应源代码和许可材料，并完成候选包交互验收。请勿将此仓库的本地候选包当作公开发行版。

## 从源码运行

需要 Windows 11 x64、.NET 10 SDK、Windows Media Foundation 和可用的 FFmpeg。克隆后初始化上游子模块，再按仓库构建说明操作：

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
