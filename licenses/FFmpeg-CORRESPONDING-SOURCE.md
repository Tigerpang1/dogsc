# FFmpeg 8.1.1 Gyan full build 对应源材料

本机验证使用 `ffmpeg-8.1.1-full_build-www.gyan.dev`。随该构建附带的 `README.txt` 标出 FFmpeg 源提交 `239f2c733d`、GPL v3 和 full build configuration；该文件及 `ffmpeg -buildconf` 输出由 `scripts/fetch-ffmpeg-corresponding-source.ps1` 一并归档。脚本按 README 中的精确 commit 从 FFmpeg 上游 codeload 下载源代码压缩包，并记录二进制版本与 SHA-256。

`FFmpeg-GPLv3.txt` 是构建目录随附的许可证全文副本。Gyan 构建还静态包含 README 所列的第三方库。当前脚本只自动抓取 FFmpeg 本体对应 commit，并保存构建商 README、build configuration 与运行时 `-buildconf`；它尚未自动收集全部第三方库的精确源码、构建脚本及补丁。因而这些材料满足本地工程追溯需要，不能单独证明已经满足发布所需的完整对应源代码义务。

若将来拟公开分发该 GPLv3 静态构建，发布审核必须先补齐并验证所有实际链接组件的精确源代码、版本、构建脚本/补丁和获取途径，随二进制提供 GPLv3 全文及要求的书面源码要约或等效获取方式，并由负责发行的人确认分发模式下的完整义务。没有完成此项前，公开发行包不得包含此验证用 FFmpeg。

上游来源：

- FFmpeg 源代码：[commit `239f2c733d`](https://github.com/FFmpeg/FFmpeg/commit/239f2c733d)
- Gyan Windows builds 与授权说明：[gyan.dev FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/)
- FFmpeg 上游 GPLv3：[COPYING.GPLv3](https://github.com/FFmpeg/FFmpeg/blob/239f2c733d/COPYING.GPLv3)
- Gyan build support repository：[GyanD/codexffmpeg](https://github.com/GyanD/codexffmpeg)
