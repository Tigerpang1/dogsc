# DogSC Windows 社区移植版：发布准备说明

本文说明 0.2.17 品牌恢复后的 Beta 准备状态。DogSC 原项目由作者神烦老狗发布，采用 MIT 许可证；本 Windows 版本是社区移植，由本仓库维护，不表示原作者官方发布或背书。原 Mac 项目和 Windows 社区版的下载渠道必须分别标注。

## 品牌与版本

0.2.17 恢复 DogSC 产品名称与上游图标，并以“DogSC · Windows 社区移植版”对外介绍。源码项目路径和命名空间、用户数据目录以及既有 `.frametrail` 工程格式继续使用 FrameTrail 兼容标识。当前包装配置使用 `DogSC.exe`/`DogSC.dll`，分发产物预期文件名为 `DogSC-0.2.17-win-x64.zip`、`DogSC-Setup-0.2.17-win-x64.exe` 和 `DogSC-0.2.17-source.zip`。MIT 来源与许可证副本见根 README 和 `licenses/DogSC-MIT.txt`。

原项目主页为 [github.com/laogou717/dogsc](https://github.com/laogou717/dogsc)，其 Releases 页面是 macOS 版下载入口：[下载 DogSC（macOS）](https://github.com/laogou717/dogsc/releases/latest)。Windows 源码维护在原项目的 Fork [lantiansb/dogsc](https://github.com/lantiansb/dogsc)，默认分支为 `codex/windows`，反馈入口为该仓库的 [Issues](https://github.com/lantiansb/dogsc/issues)。Windows 安装包尚未公开发布，原 Mac Releases 链接不是 Windows 下载入口。

## 功能范围

当前功能验收台账为 33 项：28 项在其有限验收场景通过，2 项待验，3 项延期。主要链路已经实现，涵盖 Windows 录制、工程保存和恢复、时间线编辑、画面运镜与合成、MP4/M4A 导出。该数字表示有限场景验收状态，不是功能覆盖率；不得据此宣称达到 95% 或全部功能完成。

待验项包括 FT-06 的完整真实权限拒绝与恢复流程，以及 FT-07 的新录制条菜单和辅助窗口录屏排除实机确认。按用户要求，复杂权限场景暂缓。延期项包括 FT-05 摄像头、麦克风和系统声音同会话采集，FT-20 真实摄像头布局/动画验收，以及 FT-33 在线更新和公开反馈服务。摄像头场景缺少物理设备实测；Mac 工程兼容不属于本轮要求，也没有真实 Mac 工程互通证据。

具体条目和有限验收场景见 [`acceptance-ledger.md`](acceptance-ledger.md)。硬件、DPI、长录制、干净系统等未实测范围，不应写成已通过。

## Beta 已知问题与限制

- 0.2.17 本地候选已生成，源码提交为 `b1fdbff6ecd8908a935c31c611b40a53042396f8`，源码包独立重建、后台回归及 525 个便携文件的 ZIP 一致性核对通过；状态仍为 `candidate-awaiting-interactive-validation`。随后新增的 GitHub 维护文档与 CI 不改变该候选对应的提交。
- 当前公开的是 Windows 源码仓库，尚无 Windows 安装包下载。源码维护与二进制发布分开记录。
- 本机候选曾使用 Gyan FFmpeg GPLv3 静态构建。当前收集流程尚未取得该构建中全部第三方组件的精确版本、补丁、构建脚本及对应源码，公开分发材料因此仍不齐备。
- 现有验收以 Windows 11 x64 单机为主。物理摄像头、多设备同录、多显示器和混合 DPI、干净机器、长时稳定性及不同 GPU/编码器组合不能泛化为已验收。
- 未完成真实 Mac DogSC 工程互通验证。构造 schema fixture 的导入测试不能代表真实工程完整兼容。

## 发布前动作

1. Windows 源码仓库已确定为 `lantiansb/dogsc`，保留与原项目的 Fork 关系。后续二进制发布使用本 Fork 的 Releases，满足其余门槛前不上传本地候选包。
2. 完成最终候选的交互播放、录制、编辑、导出，以及安装/卸载验收。补验 FT-07；如权限场景仍按要求暂缓，应在 Beta 说明中保留边界。代码变更后的发行包需按实际提交重新生成并核对清单。
3. 为随包 FFmpeg 收集完整对应源代码、精确组件清单、补丁与构建脚本，核对许可证和 NOTICE，并据此完成 GPLv3 分发材料审查。材料补齐前不公开分发含该构建的安装包或便携包。
4. 重新核对 DogSC MIT 许可副本、作者署名、上游图标来源和第三方许可证随包内容；公开页面清楚标注“Windows 社区移植版”，并将 Mac 下载单独标注为 macOS。
5. 决定是否为最终 Windows 包签名并配置发布者身份。签名是减少系统信任提示的发布质量事项；它并非绝对发布前提，也不能替代 FFmpeg 许可审查或包验收。
6. 二进制发布前确认具体文件、SHA-256、版本说明和已知问题，再由维护者决定是否公开发布。源码仓库的存在不表示当前候选安装包已满足公开分发条件。
