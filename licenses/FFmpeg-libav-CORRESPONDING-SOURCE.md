# FFmpeg 8.1.2 本地源码与构建来源记录

这些材料用于本地重建和追踪固定二进制；它们不是完整法律审计，也不表示已满足所有再分发义务。

## 固定输入

- 运行时包：`ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip`
- BtbN 发布标签：`autobuild-2026-07-31-14-10`
- 经 `git ls-remote` 核对的 BtbN 仓库提交：`a99e8230eae00d1cee38f23076a7a1f55cd984e2`
- BtbN 构建仓库快照：`BtbN-FFmpeg-Builds-a99e8230eae00d1cee38f23076a7a1f55cd984e2.zip`，SHA-256 `32bd407c060e53737d5db008e75f52e5b8d63adfe98490be3c044fa5e3628ccd`
- FFmpeg 源码提交：`9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`（构建标记 `g9b6c8969e0` 的完整提交号）
- FFmpeg 源码包：`FFmpeg-9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b-source.zip`，SHA-256 `696dc2da356a4d92f8a5d113e426e105896b66915ea54fe1724dd9029ff66fc0`
- 固定运行时包 SHA-256：`c222a490dde4e7059f45495deef6bfb98dbcacc2b43df5b607546252037aa95c`
- 运行时包地址：`https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-07-31-14-10/ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip`
- FFmpeg 源码地址：`https://github.com/FFmpeg/FFmpeg/archive/9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b.zip`
- BtbN 源码地址：`https://github.com/BtbN/FFmpeg-Builds/archive/a99e8230eae00d1cee38f23076a7a1f55cd984e2.zip`

## 构建配方

固定 BtbN 仓库快照中包含构建工作流与脚本，相关入口为 `build.sh`、`makeimage.sh` 和 `scripts.d/`。运行时目录中的 `buildconf.txt` 保存此包报告的完整 configure 参数。重建时应固定 FFmpeg 与依赖版本，使用相同 Windows x64 目标，并比较生成的功能配置；仅版本字符串一致不能证明构建可复现。

## 依赖源码地址索引

以下链接对应捕获到的 `-buildconf` 中启用的库，是上游源码入口。BtbN 固定构建配方及其构建输入才定义实际依赖版本和补丁；此处没有逐项审计这些内容。

| configure 组件 | 源码入口 |
| --- | --- |
| zlib | https://github.com/madler/zlib |
| libxml2 | https://gitlab.gnome.org/GNOME/libxml2 |
| libvmaf | https://github.com/Netflix/vmaf |
| fontconfig | https://gitlab.freedesktop.org/fontconfig/fontconfig |
| harfbuzz | https://github.com/harfbuzz/harfbuzz |
| freetype | https://gitlab.freedesktop.org/freetype/freetype |
| fribidi | https://github.com/fribidi/fribidi |
| shaderc | https://github.com/google/shaderc |
| libvorbis | https://github.com/xiph/vorbis |
| GMP | https://gmplib.org/ |
| lzma | https://github.com/tukaani-project/xz |
| libaom | https://aomedia.googlesource.com/aom |
| libaribb24 | https://github.com/nkoriyama/aribb24 |
| chromaprint | https://github.com/acoustid/chromaprint |
| libdav1d | https://code.videolan.org/videolan/dav1d |
| nv-codec-headers | https://github.com/FFmpeg/nv-codec-headers |
| libgme | https://github.com/libgme/game-music-emu |
| libkvazaar | https://github.com/ultravideo/kvazaar |
| libaribcaption | https://github.com/xqq/libaribcaption |
| libass | https://github.com/libass/libass |
| libbluray | https://code.videolan.org/videolan/libbluray |
| libjxl | https://github.com/libjxl/libjxl |
| libmp3lame | https://sourceforge.net/projects/lame/ |
| libopus | https://github.com/xiph/opus |
| libplacebo | https://github.com/haasn/libplacebo |
| librist | https://code.videolan.org/rist/librist |
| libssh | https://www.libssh.org/ |
| libtheora | https://github.com/xiph/theora |
| libvpx | https://chromium.googlesource.com/webm/libvpx |
| libwebp | https://chromium.googlesource.com/webm/libwebp |
| libzmq | https://github.com/zeromq/libzmq |
| LV2 | https://github.com/lv2/lv2 |
| libvpl | https://github.com/intel/libvpl |
| OpenAL | https://github.com/kcat/openal-soft |
| liboapv | https://github.com/Onion-Audio/oapv |
| OpenCORE AMR-NB/WB | https://sourceforge.net/projects/opencore-amr/ |
| OpenH264 | https://github.com/cisco/openh264 |
| OpenJPEG | https://github.com/uclouvain/openjpeg |
| libopenmpt | https://github.com/OpenMPT/openmpt |
| rav1e | https://github.com/xiph/rav1e |
| zimg | https://github.com/sekrit-twc/zimg |
| zvbi | https://github.com/zapping-vbi/zvbi |
| SDL2 | https://github.com/libsdl-org/SDL |
| snappy | https://github.com/google/snappy |
| soxr | https://github.com/chirlu/soxr |
| SRT | https://github.com/Haivision/srt |
| SVT-AV1 | https://gitlab.com/AOMediaCodec/SVT-AV1 |
| twolame | https://github.com/njh/twolame |
| uavs3d | https://github.com/uavs3/uavs3d |
| vvenc | https://github.com/fraunhoferhhi/vvenc |

此索引依据包内 configure 输出整理，不是 BtbN 构建环境中所有工具链、系统库、生成文件及传递依赖的完整清单。
