# .NET 10.0.12 与 SharpGen 许可证来源

范围：以下许可增补仅进入当前源码目录及后续打包输入；它们没有写入已冻结的 0.2.5 ZIP 或安装器。0.2.5 runtimeconfig 用于确认目标运行时版本，不代表这些许可文本已包含在该版本成品中。文件来自与该 runtimeconfig/NuGet 锁定项一致的包缓存或上游固定提交。它记录材料来源与哈希，不是法律意见。

## .NET Runtime 与 Windows Desktop Runtime

0.2.5 `FrameTrail.runtimeconfig.json` 的 `includedFrameworks` 为 `Microsoft.NETCore.App 10.0.12` 和 `Microsoft.WindowsDesktop.App 10.0.12`。对应的 x64 Runtime NuGet 包来自 `https://api.nuget.org/v3/index.json`：

| NuGet 包 | 版本 | `.nupkg` SHA-256 | 提取的许可材料 | 材料 SHA-256 |
|---|---|---|---|---|
| `Microsoft.NETCore.App.Runtime.win-x64` | `10.0.12` | `0F63DEE7CA4383CB16848966F81E439399E2058E1671EBE27E5EAF4B200E8691` | `LICENSE.TXT` → [`Microsoft.NETCore.App-Runtime-10.0.12-LICENSE.txt`](Microsoft.NETCore.App-Runtime-10.0.12-LICENSE.txt) | `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B` |
| `Microsoft.NETCore.App.Runtime.win-x64` | `10.0.12` | 同上 | `THIRD-PARTY-NOTICES.TXT` → [`Microsoft.NETCore.App-Runtime-10.0.12-THIRD-PARTY-NOTICES.txt`](Microsoft.NETCore.App-Runtime-10.0.12-THIRD-PARTY-NOTICES.txt) | `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21` |
| `Microsoft.WindowsDesktop.App.Runtime.win-x64` | `10.0.12` | `68BCE56D2402969D82AC77FBD2B2A54F55FDA86EA060366E6DDB832353DAB80A` | `LICENSE` → [`Microsoft.WindowsDesktop.App-Runtime-10.0.12-LICENSE.txt`](Microsoft.WindowsDesktop.App-Runtime-10.0.12-LICENSE.txt) | `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB` |

官方包地址：

- <https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/10.0.12/microsoft.netcore.app.runtime.win-x64.10.0.12.nupkg>
- <https://api.nuget.org/v3-flatcontainer/microsoft.windowsdesktop.app.runtime.win-x64/10.0.12/microsoft.windowsdesktop.app.runtime.win-x64.10.0.12.nupkg>

对缓存中的官方 `Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12` `.nupkg` 归档逐项检查后，名称含 `license`、`notice` 或 `third-party` 的条目只有 `LICENSE`，未发现独立 `THIRD-PARTY-NOTICES`。因此本记录只附该包实际提供的 WindowsDesktop `LICENSE`，不声称 .NETCore Runtime 的 notices 文件覆盖或代表 WPF/WindowsDesktop 的第三方材料。

`System.IO.Pipelines.dll`、`System.Text.Encodings.Web.dll` 和 `System.Text.Json.dll` 在发行输出的 `FrameTrail.deps.json` 中是 `Microsoft.NETCore.App` 框架程序集，文件版本为 `10.0.1226.42308`。0.2.5 的锁文件未将 `System.Text.* 9.0.1` 作为独立 NuGet 依赖；运行时版权通知应以同版本 Runtime 包提供的 `THIRD-PARTY-NOTICES.TXT` 为准。

## SharpGen.Runtime

`FrameTrail.Media/packages.lock.json` 锁定 `SharpGen.Runtime` 和 `SharpGen.Runtime.COM` 为 `2.4.2-beta`。两个包的 NuGet 元数据均声明 MIT，并指向 `SharpGenTools/SharpGenTools` 提交 `6990bcafe124a4c22515ad19cee5a081da8db67b`。固定上游 `LICENSE.txt` 已保存为 [`SharpGenTools-MIT-6990bcafe124a4c22515ad19cee5a081da8db67b.txt`](SharpGenTools-MIT-6990bcafe124a4c22515ad19cee5a081da8db67b.txt)，SHA-256 为 `1CC7ABD1E97575838535484BEF6E4AB14E81681DDF7837672ACEDE5FCA1D4E30`。

- 上游固定许可文件：<https://raw.githubusercontent.com/SharpGenTools/SharpGenTools/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt>
- `SharpGen.Runtime` NuGet 包 SHA-256：`5BDCEBEE0BDC3C15DC6E9B73F82FAA98575EA749B92D6AE30106FEB44BD23F18`
- `SharpGen.Runtime.COM` NuGet 包 SHA-256：`32A81AEC51F9FFEE3376A29DB7F9A2447E250CF1CCCD352EA02FC4DC49FD046C`
