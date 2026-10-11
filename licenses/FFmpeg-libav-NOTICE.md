# FFmpeg 共享库告知

DogSC Windows 社区版动态加载 FFmpeg 8.1.2 的五个库：`avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、`swscale-9.dll`、`swresample-6.dll`。它们由固定 FFmpeg 源码 `9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b` 构建，禁用 GPL 与 nonfree，启用 version3，采用 LGPLv3。完整许可见 `FFmpeg-libav-LGPLv3.txt` 与 `FFmpeg-GPLv3.txt`。

动态库位于应用 `tools/libav`，不与 DogSC 程序静态链接。用户可按 LGPL 条款修改、重建并替换兼容 ABI 的动态库；本项目不限制为调试其修改而进行的逆向工程。替换后需要自行核对 ABI 和运行行为。

FFmpeg 原作者与贡献者版权保留于对应源代码。与动态库相关的 zlib 等组件、许可证和构建配置在随包 `libav-corresponding-source` 目录中逐项列出；独立 FFmpeg CLI 使用另一套含 x264 的 GPL 构建，没有把 x264 链接进这五个 LGPL 动态库。

对应源码、构建脚本和组件归档与二进制一起提供，也可从 [同一 GitHub Release](https://github.com/lantiansb/dogsc/releases/tag/v0.2.17-windows-beta.1) 下载 `DogSC-FFmpeg-8.1.2-sources.zip`。精确哈希由 `third_party/ffmpeg/manifest.json` 和 Release 的 SHA-256 文件记录。构建说明见源码仓库 `third_party/ffmpeg/README.md`。

来源：[FFmpeg 固定源码](https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b)，[LGPLv3](https://www.gnu.org/licenses/lgpl-3.0.html)。
