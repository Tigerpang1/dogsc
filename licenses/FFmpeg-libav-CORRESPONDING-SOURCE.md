# FFmpeg 动态库对应源码

0.2.17 公开 Beta 的动态库来自本项目固定源码构建。发布时提供相同版本的源码归档、依赖归档、构建脚本、配置记录及完整许可证，位于随包 `licenses/libav-corresponding-source`。

单独下载：[DogSC-FFmpeg-8.1.2-sources.zip](https://github.com/lantiansb/dogsc/releases/download/v0.2.17-windows-beta.1/DogSC-FFmpeg-8.1.2-sources.zip)。源码树根的 README 说明所有组件的精确版本与重建方法。

FFmpeg core commit：`9b6c8969e05b4f0b29f0f85cd501be6b3e582e6b`，核心 ZIP SHA-256：`696dc2da356a4d92f8a5d113e426e105896b66915ea54fe1724dd9029ff66fc0`。动态库采用 LGPLv3，禁用 GPL/nonfree、外部自动探测；启用的外部组件均在源码清单中列明。

构建缓存由 `scripts/ensure-native-media.ps1` 逐文件验证；`scripts/fetch-libav-corresponding-source.ps1` 从该已验证缓存收集源码材料。旧 BtbN full LGPL 构建仅属于先前本地验证，不是本次公开 Beta 的运行时来源。
