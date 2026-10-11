# 参与贡献

这是 DogSC 的 Windows 社区移植版，由社区独立维护，不代表原作者神烦老狗发布或背书。原项目位于 [laogou717/dogsc](https://github.com/laogou717/dogsc)，采用 MIT 许可证；本移植版保留原项目名称、品牌和来源说明。

## 分支与上游

本仓库是原项目的公开 Fork。`main` 用于保留上游项目，Windows 移植和相关贡献请以 `codex/windows` 为目标分支。请在提交拉取请求前确认目标分支。

## 获取源码

在 Windows 上安装 .NET 10 SDK 和 Git，然后递归克隆仓库及其固定版本的上游子模块：

```powershell
git clone --recurse-submodules https://github.com/lantiansb/dogsc.git
cd dogsc
git switch codex/windows
```

如果已有克隆但尚未初始化子模块：

```powershell
git submodule update --init --recursive
```

## 构建与检查

在仓库根目录运行：

```powershell
dotnet build src/FrameTrail.App/FrameTrail.App.csproj --configuration Release -p:RestoreLockedMode=true
pwsh -File scripts/test.ps1 -Configuration Release -IncludeCaptureProbe -IncludeReleaseProbe
```

这些检查覆盖应用构建、Core 与 DogSC 导入逻辑、采集相关纯逻辑探针，以及发行清单和源码归档策略。CI 不启动录制、不访问麦克风或摄像设备、不验证 GPU 渲染，也不执行真实桌面交互；这些场景需要在具备相应硬件和桌面环境的 Windows 设备上单独验证。CI 不生成发行包或安装器。

## 提交内容

不要提交 `artifacts/`、`bin/`、`obj/` 等构建和验证输出，也不要提交本机真实录屏、录音、项目文件或其他用户素材。不要加入尚未完成许可审查和对应源代码材料的 FFmpeg 二进制。第三方依赖文件须注明来源并保留相应许可证；不要把第三方组件描述成用户采集素材。

报告问题时请先清理日志中的令牌、账号、个人路径和其他隐私内容。不要在 Issue 或拉取请求中附上真实录屏、录音或项目文件。
