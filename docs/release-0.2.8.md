# 0.2.8 本地候选交付

源码提交：`48fcdcfabd8a12668b96c888e41c85893d4e6bd1`。三个包均来自该干净提交；包后的验收文档提交不改变包的源码身份。上游仍为固定 DogSC `e207d11b111c5e58152238fb64115b2009850547`。

| 产物 | 路径 |
|---|---|
| 便携包 | `artifacts/release/FrameTrail-0.2.8-win-x64.zip` |
| 安装器 | `artifacts/release/FrameTrail-Setup-0.2.8-win-x64.exe` |
| 源码包 | `artifacts/release/FrameTrail-0.2.8-source.zip` |
| 清单和 SHA-256 | `artifacts/release/FrameTrail-0.2.8-candidate-manifest.json`、同前缀 `SHA256SUMS.txt` |

本版包含录制标记热键冲突提示及备用键、窗口来源过滤、非整帧区间导出末帧修复、麦克风被拒绝时的应用内恢复说明和设置入口。摄像头仍按用户要求延期。

打包后发现：便携版的“版本与手动更新说明”和“源码与许可证”按安装版目录查找，无法找到上一级随包材料。源码已修复两种布局，并验证带尾随分隔符的实际程序目录、空许可证目录和缺失文件场景；11项纯检查见 `artifacts/validation/bundled-resource-locator-v2-1d111d2b233647cdb93cb85fd0e0aa14/result.json`。此修复尚未进入本页列出的0.2.8包，后续候选将包含修复。

后台验收通过：

- 便携包487文件与发布目录逐字节一致。
- 源码在仓库外解压，纯算法/采集逻辑/离屏界面/发行策略测试、App构建、再次打包通过；App构建0警告0错误。
- 本机静默安装与卸载退出码均0；426程序文件与便携包匹配，注册项、程序和快捷方式已清理。
- 实际加载包内Core/Media，对同一真实录制短区间导出381帧；有/无标记逐帧一致，原录制12文件哈希不变。
- 直接引用包内App/Core/Capture/Media编译恢复提示探针，4份运行时程序集哈希和40项离屏检查通过。

证据在 `artifacts/validation/package-0.2.8/`。安装报告为 `artifacts/validation/install-028-084b58957afe443f8b3f3cb724ee65af/result.json`。测试夹具失败及修正、外层安装脚本退出码误判均保留在 `verification-history.md`，未覆盖旧日志。源码验收的206份细粒度产物已保留到外部日志目录，0.68GiB临时解包/构建副本已清理。

仍待验：当前包完整桌面流程，真实麦克风拒绝/重新授权录制，备忘录字号/透明度/位置重启与录屏排除。干净机器、多显示器混合DPI没有本轮实测；摄像头两项、在线更新及公开反馈服务延期。本版未签名、未公开发布。台账28/33项通过，不代表95%迁移已完成。
