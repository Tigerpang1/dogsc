# FFmpeg 原生媒体运行时

`scripts/ensure-libav.ps1` 调用 `ensure-native-media.ps1`，依据 `third_party/ffmpeg/manifest.json` 下载并核验固定媒体归档。归档覆盖独立 FFmpeg CLI、预览所需的五个 libav DLL、对应源码及构建记录；没有采用系统 PATH 中的 FFmpeg 作为发行来源。

```powershell
pwsh -File scripts/ensure-libav.ps1
# 离线使用从同一 Release 下载的媒体归档
pwsh -File scripts/ensure-libav.ps1 -ArchivePath C:\Downloads\DogSC-FFmpeg-8.1.2-win-x64.zip -Offline
```

归档 SHA-256、内部每个文件的 SHA-256、路径与完整文件清单均须匹配后才部署。预览 DLL 输出到 `artifacts/toolchains/libav-8.1-win-x64`，供 `FrameTrail.Media.csproj` 随程序发布；可替换的 DLL 位于应用 `tools/libav`。CLI 输出在固定媒体缓存的 `cli` 子目录，打包到 `tools/ffmpeg`。

预览库禁用 GPL 与 nonfree，采用 LGPLv3。CLI 以独立进程调用，包含 x264，采用 GPL；两种构建配置和对应源码分开记录在 `source-materials`。来源、重建方法和许可证见 [third_party/ffmpeg/README.md](../third_party/ffmpeg/README.md)。

FFmpeg.AutoGen 的动态绑定固定为 NuGet 8.1.0，许可见 `licenses/FFmpeg.AutoGen-MIT.txt`。本仓库固定的源代码快照、下载锁、构建脚本、第三方通知和发行产物哈希应共同更新；更换媒体构建后必须重跑 Core、Integration 与 Render.Probe。
