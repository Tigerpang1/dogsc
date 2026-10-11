# 第三方组件通知

DogSC Windows 社区移植工程当前锁定以下直接依赖。安装包应随附本目录的对应许可证文本；随 NuGet 包的传递依赖应在实际集成后重新从 `packages.lock.json`、NuGet 包元数据和发行输出核对。

| 组件 | 版本 | 许可证 | 用途及来源 |
|---|---:|---|---|
| DogSC 上游源代码与原应用图标 | `e207d11b111c5e58152238fb64115b2009850547` | MIT | 原项目 [laogou717/dogsc](https://github.com/laogou717/dogsc)，原作者神烦老狗。沿用 DogSC 名称和原版图标，明确标注 Windows 社区移植版；完整上游许可见 [`DogSC-MIT.txt`](DogSC-MIT.txt)，图标原文件、哈希和转换方法见 [`DogSC-ASSETS.md`](DogSC-ASSETS.md)。不表示原作者官方发布此 Windows 版本。 |
| dogscwindows Windows 适配参考 | `3bdcf00d4eb12a107c093e98aac7a894aedd2843` | MIT | 参考并移植自动镜头弹簧跟随、系统光标形态/热点处理，参考颜色往返测量方法；来源 [xiaoying8666/dogscwindows](https://github.com/xiaoying8666/dogscwindows)，完整原仓库许可及署名见 [`DogSCWindows-MIT.txt`](DogSCWindows-MIT.txt)。Windows 适配由该仓库维护者贡献，与原 DogSC 作者的贡献分别署名。 |
| Alibaba PuHuiTi 3.0 Regular / Medium / SemiBold | 上游锁定 OTF 文件 | 阿里巴巴普惠体使用许可 | 界面字体，未经修改嵌入应用；完整声明见 [`Alibaba-PuHuiTi-3-LICENSE.txt`](Alibaba-PuHuiTi-3-LICENSE.txt)。原文件及校验值保存在 `upstream/dogsc/Resources/Fonts`。 |
| ScreenRecorderLib | 7.0.1，固定上游提交 `646383234187efd3854e6b1069612b80c3ab9981`，含 WGC 首帧补丁 | MIT | 录制依赖。实际使用本仓库 [`third_party/ScreenRecorderLib/prebuilt/ScreenRecorderLib.dll`](../third_party/ScreenRecorderLib/prebuilt/ScreenRecorderLib.dll)，SHA-256 `3D366DD95EEDDDE20BF66B6C0CC8B10E5F9BF7C9489E8D0B5AAEE0C838118151`；源码 ZIP、统一补丁和重建脚本见 [`third_party/ScreenRecorderLib/README.md`](../third_party/ScreenRecorderLib/README.md)。许可文本见 [`ScreenRecorderLib-MIT.txt`](ScreenRecorderLib-MIT.txt)。依赖 Visual C++ Redistributable 与 Windows Media Foundation。 |
| NAudio.Wasapi | 2.2.1 | MIT | WASAPI 系统回环与麦克风独立 WAV 采集；[NuGet 包](https://www.nuget.org/packages/NAudio.Wasapi/2.2.1)，许可文本见 [`NAudio-MIT.txt`](NAudio-MIT.txt)。依赖 Windows Core Audio（WASAPI），不附带本机二进制。 |
| FFmpeg.AutoGen.Bindings.DynamicallyLoaded | 8.1.0 | MIT | FFmpeg C# 动态加载绑定；[NuGet 包](https://www.nuget.org/packages/FFmpeg.AutoGen.Bindings.DynamicallyLoaded/8.1.0)。准确到 8.1.0 的许可文本与来源见 [`FFmpeg.AutoGen-MIT.txt`](FFmpeg.AutoGen-MIT.txt) 和 [`FFmpeg.AutoGen-NOTICE.md`](FFmpeg.AutoGen-NOTICE.md)。 |
| FFmpeg libav shared runtime | 8.1.2，BtbN 固定构建 `n8.1.2-34-g9b6c8969e0` | LGPLv3 | Windows x64 动态链接运行时；[固定 BtbN release](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-07-31-14-10)。许可文本、来源和构建记录见 [`FFmpeg-libav-LGPLv3.txt`](FFmpeg-libav-LGPLv3.txt)、[`FFmpeg-libav-NOTICE.md`](FFmpeg-libav-NOTICE.md) 与 [`../docs/libav-runtime.md`](../docs/libav-runtime.md)。 |
| Vortice.Direct3D11 | 3.8.3 | MIT | Direct3D 11 绑定；[NuGet 包](https://www.nuget.org/packages/Vortice.Direct3D11/3.8.3)。许可文本见 [`Vortice.Windows-MIT.txt`](Vortice.Windows-MIT.txt)。 |
| Vortice.D3DCompiler | 3.8.3 | MIT | shader 编译绑定；[NuGet 包](https://www.nuget.org/packages/Vortice.D3DCompiler/3.8.3)，同属 Vortice.Windows 仓库。 |
| Vortice.DXGI / Vortice.DirectX | 3.8.3 | MIT | Vortice 的传递依赖，运行时包由锁文件确认。 |
| Vortice.Mathematics | 2.1.0 | MIT | Vortice 的传递依赖，运行时包由锁文件确认。 |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | Vortice 的传递依赖；两个 NuGet 包均指向 SharpGenTools/SharpGenTools 提交 `6990bcafe124a4c22515ad19cee5a081da8db67b`。完整许可见 [`SharpGenTools-MIT-6990bcafe124a4c22515ad19cee5a081da8db67b.txt`](SharpGenTools-MIT-6990bcafe124a4c22515ad19cee5a081da8db67b.txt)，来源及哈希见 [`Microsoft-NET10.0.12-SOURCE.md`](Microsoft-NET10.0.12-SOURCE.md)。 |
| .NET 10 Runtime / Windows Desktop Runtime | `Microsoft.NETCore.App 10.0.12`、`Microsoft.WindowsDesktop.App 10.0.12` | MIT 与 Runtime 第三方声明 | 以 0.2.5 `FrameTrail.runtimeconfig.json` 为准。对应许可及哈希见 [`Microsoft-NET10.0.12-SOURCE.md`](Microsoft-NET10.0.12-SOURCE.md) 和同目录 `Microsoft.NETCore.App-Runtime-10.0.12-*`、`Microsoft.WindowsDesktop.App-Runtime-10.0.12-LICENSE.txt`。`System.IO.Pipelines.dll`、`System.Text.Encodings.Web.dll`、`System.Text.Json.dll` 来自该 .NET Runtime 10.0.12 框架，不是本版本单独锁定的 9.0.1 NuGet 包。 |
| Microsoft Visual C++ 2015–2022 Redistributable (x64) | 系统预装组件 | Microsoft 许可条款 | ScreenRecorderLib 的运行依赖。本安装包不捆绑、不安装、不更新它；用户从[微软官方页面](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)自行获取。 |
| FFmpeg（Gyan Windows full static build） | 8.1.1，FFmpeg commit `239f2c733d` | GPLv3 build | 仅本机验证包。随发布候选包的许可证和源代码准备见 [`FFmpeg-GPLv3.txt`](FFmpeg-GPLv3.txt)、[`FFmpeg-CORRESPONDING-SOURCE.md`](FFmpeg-CORRESPONDING-SOURCE.md) 与 [`../scripts/fetch-ffmpeg-corresponding-source.ps1`](../scripts/fetch-ffmpeg-corresponding-source.ps1)。公开发布仍须确认完整对应源代码覆盖和 GPL 合规。 |

NuGet 锁文件列出实际通过 NuGet 引入的包；ScreenRecorderLib 的版本、源码提交、补丁及本地 DLL 哈希以 [`third_party/ScreenRecorderLib/provenance.json`](../third_party/ScreenRecorderLib/provenance.json) 为准。锁文件、供应来源、构建配置或发布介质变化时，必须重新审阅此清单。此文件不是法律意见。
