# DogSC Windows Beta 发布状态

当前版本为 **0.2.17**，发行 tag 为 `v0.2.17-windows-beta.1`。安装器、便携包、含原项目快照的源码包、媒体对应源码与校验文件均从 [lantiansb/dogsc Releases](https://github.com/lantiansb/dogsc/releases/tag/v0.2.17-windows-beta.1) 获取。使用和限制见[版本说明](release-0.2.17.md)。

## 来源与品牌

原 [DogSC](https://github.com/laogou717/dogsc) 由神烦老狗创作并采用 MIT 许可；本仓库保留 Fork 关系和完整署名。Windows 社区版由 `lantiansb` 维护，不表示原作者官方发布或背书。原项目的 Releases 提供 macOS 版本。

公开品牌为 DogSC。内部 `FrameTrail.*` 命名、`.frametrail` 工程与用户数据目录保留兼容，既有项目、设置及提词器文稿可继续使用。

## 发布材料与验收

0.2.17 最初的本地候选采用 Gyan full build，因为缺少全部外部组件的对应源码材料，没有上传公开发行区。本次公开包重新构建 FFmpeg：预览使用 LGPL 动态库，独立编码使用含 x264 的 GPL 程序。精确源码、第三方组件、构建配方、许可证和文件哈希一起发布；详见 [FFmpeg 来源](../third_party/ffmpeg/README.md)。

发行清单必须同时满足便携文件逐项一致、播放与暂停恢复、录制/编辑/重开/导出、安装版播放、安装/卸载、用户偏好恢复以及独立源码归档重建。公开 Beta 还要求媒体分发清单与对应源码哈希匹配。文件与源码提交记录在 Release 附件 `DogSC-0.2.17-manifest.json`，校验值记录在 `DogSC-0.2.17-SHA256SUMS.txt`。

本机验证不等同于干净机器或完整硬件矩阵。GitHub 托管 CI 是否运行不替代本机发行验收；远程状态可在仓库 Actions 查看。安装器与应用未做代码签名。

## 未完成范围

功能台账仍为 33 项，28 项在限定场景通过、2 项待验、3 项延期；这不是整体功能完成率，不能据此宣称 95% 或全部完成。

- FT-06：完整真实权限拒绝与恢复，复杂权限场景按维护者要求暂缓。
- FT-07：新录制条菜单与辅助浮窗录屏排除的完整实机复核。
- FT-05、FT-20：摄像头、多设备同录、真实摄像头布局/动画。
- FT-33：在线更新；公开反馈入口已在 GitHub Issues 提供，应用内完整更新/反馈服务仍延期。

多显示器和混合 DPI、长时间稳定性、干净系统、全部 GPU/驱动组合未完整覆盖。Mac 工程互通不属于本轮要求。逐项状态和证据见 [acceptance-ledger.md](acceptance-ledger.md)。
