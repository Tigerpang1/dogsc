# FFmpeg 原生解码运行时

`scripts/ensure-libav.ps1` 校验固定 BtbN archive 的 SHA-256、构建版本、shared 配置和 LGPL v3 报告，再将原生解码需要的五个 DLL 及 `LICENSE.txt`、`provenance.json`、`buildconf.txt`、`version.txt` 放入 `artifacts/toolchains/libav-8.1-win-x64`。脚本不执行下载包内的构建脚本；它只运行经哈希校验的 `ffmpeg.exe` 查询版本、配置和许可证。

默认复用 `artifacts/toolchains/ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared/ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip`。首次准备可运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ensure-libav.ps1
```

离线构建可传入本地 archive 并加 `-Offline`。脚本按固定清单复制运行时 DLL；应用构建/安装包应从该输出目录复制这五个 DLL，并将 `LICENSE.txt`、本 NOTICE 和完整 `licenses/FFmpeg-libav-LGPLv3.txt` 随包放入许可目录。`provenance.json`、`buildconf.txt`、`version.txt` 可一并保留用于诊断和供应链追溯。

`FFmpeg.AutoGen.Bindings.DynamicallyLoaded` 与传递依赖 `FFmpeg.AutoGen.Abstractions` 固定为 NuGet 8.1.0；其 MIT 文本和版本来源见 `licenses/FFmpeg.AutoGen-MIT.txt` 与 `licenses/FFmpeg.AutoGen-NOTICE.md`。该许可说明只针对 8.1.0。

运行 `scripts/fetch-libav-corresponding-source.ps1` 可按固定 URL 和 SHA-256 获取 FFmpeg 源码 ZIP 与 BtbN 构建仓库快照，README 模板和依赖源码 URL 清单由受版本控制的 `licenses/FFmpeg-libav-CORRESPONDING-SOURCE.md` 提供。传入 `-PackageDirectory <artifacts下的包暂存目录>` 会把完整 `source-materials` 目录复制到该暂存目录；已有目录只有在逐文件一致时才可重复运行。源码发布包应包含这些对应源材料或提供明确可用的获取路径。材料记录不等于完整法律审计。现有 Gyan GPL FFmpeg 打包材料仍服务于原有用途，本方案不替换或删除它们。
