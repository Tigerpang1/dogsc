# 第三方组件通知

FrameTrail Windows 迁移工程当前锁定以下直接依赖。安装包应随附本目录的对应许可证文本；随 NuGet 包的传递依赖应在实际集成后重新从 `packages.lock.json`、NuGet 包元数据和发行输出核对。

| 组件 | 版本 | 许可证 | 用途及来源 |
|---|---:|---|---|
| DogSC 上游源代码 | `e207d11b111c5e58152238fb64115b2009850547` | MIT | 迁移参考来源；完整上游许可见 [`DogSC-MIT.txt`](DogSC-MIT.txt)。Windows 程序不沿用 DogSC 品牌或图标。 |
| Alibaba PuHuiTi 3.0 Regular / Medium / SemiBold | 上游锁定 OTF 文件 | 阿里巴巴普惠体使用许可 | 界面字体，未经修改嵌入应用；完整声明见 [`Alibaba-PuHuiTi-3-LICENSE.txt`](Alibaba-PuHuiTi-3-LICENSE.txt)。原文件及校验值保存在 `upstream/dogsc/Resources/Fonts`。 |
| ScreenRecorderLib | 7.0.1 | MIT | 录制依赖；[NuGet 包](https://www.nuget.org/packages/ScreenRecorderLib/7.0.1)，许可文本见 [`ScreenRecorderLib-MIT.txt`](ScreenRecorderLib-MIT.txt)。依赖 Visual C++ Redistributable 与 Windows Media Foundation。 |
| NAudio.Wasapi | 2.2.1 | MIT | WASAPI 系统回环与麦克风独立 WAV 采集；[NuGet 包](https://www.nuget.org/packages/NAudio.Wasapi/2.2.1)，许可文本见 [`NAudio-MIT.txt`](NAudio-MIT.txt)。依赖 Windows Core Audio（WASAPI），不附带本机二进制。 |
| Vortice.Direct3D11 | 3.8.3 | MIT | Direct3D 11 绑定；[NuGet 包](https://www.nuget.org/packages/Vortice.Direct3D11/3.8.3)。许可文本见 [`Vortice.Windows-MIT.txt`](Vortice.Windows-MIT.txt)。 |
| Vortice.D3DCompiler | 3.8.3 | MIT | shader 编译绑定；[NuGet 包](https://www.nuget.org/packages/Vortice.D3DCompiler/3.8.3)，同属 Vortice.Windows 仓库。 |
| Vortice.DXGI / Vortice.DirectX | 3.8.3 | MIT | Vortice 的传递依赖，运行时包由锁文件确认。 |
| Vortice.Mathematics | 2.1.0 | MIT | Vortice 的传递依赖，运行时包由锁文件确认。 |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | Vortice 的传递依赖；NuGet 包声明 MIT。 |
| System.Text.Json / System.IO.Pipelines / System.Text.Encodings.Web | 9.0.1 | MIT | SharpGen 传递依赖；Microsoft .NET 包。 |
| .NET 8 Runtime / Windows Desktop Runtime assets | 随 .NET 8 SDK 发布 | MIT 及各自第三方声明 | 自包含发布随应用目录一起发行；以 `dotnet publish` 输出中的 `LICENSE.txt` 和 `ThirdPartyNotices.txt` 为准。 |
| Microsoft Visual C++ 2015–2022 Redistributable (x64) | 系统预装组件 | Microsoft 许可条款 | ScreenRecorderLib 的运行依赖。本安装包不捆绑、不安装、不更新它；用户从[微软官方页面](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)自行获取。 |
| FFmpeg（Gyan Windows full static build） | 8.1.1，FFmpeg commit `239f2c733d` | GPLv3 build | 仅本机验证包。随发布候选包的许可证和源代码准备见 [`FFmpeg-GPLv3.txt`](FFmpeg-GPLv3.txt)、[`FFmpeg-CORRESPONDING-SOURCE.md`](FFmpeg-CORRESPONDING-SOURCE.md) 与 [`../scripts/fetch-ffmpeg-corresponding-source.ps1`](../scripts/fetch-ffmpeg-corresponding-source.ps1)。公开发布仍须确认完整对应源代码覆盖和 GPL 合规。 |

依赖许可标识以 NuGet 版本页面及各上游仓库许可为审计依据。NuGet 锁文件改动、包升级、构建配置变更或发布介质变化时，必须重新生成并审阅此清单。此文件不是法律意见。
