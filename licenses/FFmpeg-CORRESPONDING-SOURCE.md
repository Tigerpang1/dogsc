# 独立 FFmpeg 编码程序对应源码

DogSC 0.2.17 Windows Beta 随附从固定源码构建的 `ffmpeg.exe` 与 `ffprobe.exe`，通过独立进程处理媒体。编码程序启用 x264 和 GPL，采用 GPLv3；完整许可见 `FFmpeg-GPLv3.txt`，x264 的原始版权和许可保留于其源码归档。

与本次二进制匹配的 FFmpeg、x264、zlib、硬件接口头文件及构建配方一起提供在 `licenses/libav-corresponding-source`。可在同一 Release [下载对应源码](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-FFmpeg-8.1.2-sources.zip)，按其中 README 重建。文件哈希和来源锁见源码仓库 `third_party/ffmpeg/manifest.json`。

动态链接进 DogSC 的预览库使用另一套禁用 GPL 的 LGPLv3 配置；独立 CLI 的 GPL 构建没有替换预览 DLL。两者共享固定 FFmpeg 核心源码，分别保留实际 configure 参数和版本输出。

此前本地验证包使用 Gyan FFmpeg 8.1.1 full build，其材料没有覆盖全部静态外部组件，因此没有作为公开 Release 上传。本次公开 Beta 使用上述自行构建的媒体依赖。历史 `fetch-ffmpeg-corresponding-source.ps1` 保留供复核旧记录，当前打包流程不再调用它。
