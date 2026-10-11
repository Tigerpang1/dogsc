# DogSC Windows 媒体依赖

本目录锁定 Windows 社区版实际使用的 FFmpeg 构建。`manifest.json` 记录二进制包、对应源码包以及运行时文件的 SHA-256；构建脚本拒绝哈希不同的缓存和下载内容。

## 两种构建

- `cli/ffmpeg.exe`、`cli/ffprobe.exe`：静态 GPLv3 构建，包含 x264 软件编码器，通过独立进程完成媒体生成、录制及导出。
- `libav/*.dll`：可替换的 LGPLv3 共享构建，不包含 x264/GPL 组件，用于编辑器内的常驻解码。需要五个 DLL：avcodec 62、avformat 62、avutil 60、swscale 9、swresample 6。

两种构建使用同一 FFmpeg 源码提交 `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`。构建配置、外部组件精确版本、许可证和重建步骤随对应源码归档提供。包中的 `libav/buildconf.txt`、`version.txt`、`provenance.json` 记录共享构建来源。

## 获取与校验

```powershell
./scripts/ensure-native-media.ps1
```

脚本从 [v0.2.17-windows-beta.1](https://github.com/lantiansb/dogsc/releases/tag/v0.2.17-windows-beta.1) 获取 `DogSC-FFmpeg-8.1.2-win-x64.zip`，验证整个归档和内部文件，然后建立 `artifacts/toolchains/dogsc-native-8.1.2` 缓存。现有项目使用的 `artifacts/toolchains/libav-8.1-win-x64` 由同一份已验证 DLL 更新。

离线使用时，把归档保存在缓存目录之外，再运行：

```powershell
./scripts/ensure-native-media.ps1 -ArchivePath C:/Downloads/DogSC-FFmpeg-8.1.2-win-x64.zip -Offline
```

不要手动把 ZIP 放入未完整解压的缓存目录。脚本会拒绝残缺或被修改的缓存，以免混用不同构建。

## 对应源码与许可证

下载同一 Release 的 [DogSC-FFmpeg-8.1.2-sources.zip](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-FFmpeg-8.1.2-sources.zip)。它也以相同字节随媒体包和应用安装包提供，安装后的路径是 `licenses/libav-corresponding-source`。

源码归档包括 FFmpeg、实际参与构建的外部组件、许可证、构建参数和重建脚本。具体版本与哈希以归档内的来源记录为准。开发工具链通过记录的官方来源获取，不要求预装全局 FFmpeg。

LGPL 动态库可由使用者替换为 ABI 兼容版本；调试这些修改所需的逆向工程不受本项目限制。源码材料说明见 [FFmpeg-libav-CORRESPONDING-SOURCE.md](../../licenses/FFmpeg-libav-CORRESPONDING-SOURCE.md) 和 [FFmpeg-CORRESPONDING-SOURCE.md](../../licenses/FFmpeg-CORRESPONDING-SOURCE.md)。
