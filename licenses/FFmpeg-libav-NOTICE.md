# FFmpeg 共享库告知

FrameTrail 的原生帧解码实验使用 FFmpeg `n8.1.2-34-g9b6c8969e0` 的 Windows x64 LGPL shared 构建。程序通过动态链接加载共享 DLL；本应用不修改这些 DLL，也不将它们静态链接进应用程序。

随运行时交付的 DLL 为 `avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、`swscale-9.dll` 和 `swresample-6.dll`。完整 LGPL v3 文本见 `FFmpeg-libav-LGPLv3.txt`。构建包及 SHA-256、配置和文件哈希由 `artifacts/toolchains/libav-8.1-win-x64/provenance.json` 记录。

本地重建对应源材料、BtbN 构建仓库固定版本及依赖源码入口记录在 `artifacts/research/libav-source-materials/README.md`。其中列出的来源链接是追溯入口，不代表已完成第三方组件逐项法律审计，也不应被解释为公开发行合规结论。

上游：

- FFmpeg 源码：[固定 commit](https://github.com/FFmpeg/FFmpeg/tree/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b)
- BtbN Windows 构建：[固定 release](https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-07-31-14-10)
- LGPL v3：[GNU 官方文本](https://www.gnu.org/licenses/lgpl-3.0.txt)
