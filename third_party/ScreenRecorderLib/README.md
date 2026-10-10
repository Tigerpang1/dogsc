# ScreenRecorderLib 7.0.1（本地补丁构建）

本目录保存 ScreenRecorderLib `v7.0.1` 的固定上游源码、MIT 许可证、一个 WGC 首帧补丁和已构建的 x64 托管 DLL。上游标签提交为 `646383234187efd3854e6b1069612b80c3ab9981`；源码 ZIP 的 SHA-256 为 `31435BB4F5D159D5D943AE5E31BD4DE6A9C5E8476AFA9691E7DC8E51A5FA27E0`。`provenance.json` 记录了补丁、源文件和 DLL 的 SHA-256。

补丁修复静态窗口在首个窗口尺寸变化时丢弃唯一有效帧的问题。它保留当前已经取得的帧，并将复制区域限制在帧内容、目标纹理和来源纹理三者都有效的范围内。补丁文件使用标准 unified diff；`scripts/rebuild-screenrecorderlib.ps1` 会先核对源码、补丁和许可证哈希，再在临时目录解压源码、应用补丁并构建。重建 DLL 的 PE 哈希可能随工具链变化，脚本会记录本次实际哈希，不声称二进制可逐字节复现。

`src/FrameTrail.Capture/FrameTrail.Capture.csproj` 直接引用本目录预构建 DLL，并在编译前校验其 SHA-256；该依赖不再从 NuGet 缓存复制。重建需要 Windows、Git、Visual Studio 2022 的 MSBuild、v143 C++ 工具链 `14.44.35207`、ATL 头文件、Windows SDK `10.0.26100.0`，以及 .NET Framework 4.8 Developer Pack。默认输出写入 `artifacts/validation/`，不会改系统 PATH 或安装运行库。运行时仍需 Microsoft Visual C++ x64 Redistributable；应用安装流程应明确检测并引导用户使用微软渠道安装。

固定预构建 DLL 的 SHA-256 是 `3D366DD95EEDDDE20BF66B6C0CC8B10E5F9BF7C9489E8D0B5AAEE0C838118151`。它与 `artifacts/validation/capture-audio-clock-build-v5/snapshot-provenance.json` 中记录的产品目标测试 DLL 相同。`artifacts/validation/wgc-first-frame-matrix-v1/matrix-summary.json` 显示：同工具链的未补丁静态窗口视频解码为全黑，补丁静态窗口有非黑画面；两组动态窗口均正常。产品目标报告 `artifacts/validation/window-wgc-patched-product-v2/target.target.json` 记录了自有窗口被遮挡时三色色块仍可见、窗口移动和调整尺寸、关闭与最小化自动收尾、关闭窗口拒绝录制，以及 DDA 区域录制回归。该目标测试使用的工作树快照包含当时未提交的音频/DPI变更，不是最终发行构建或广泛设备认证。

脚本在隔离输出中另行完成了一次从固定源码和补丁的 Release x64 重建；该次 DLL SHA-256 为 `3650538CC7F8F90F88DB653191FA658A3B09630CF65BBC008C97BB656ED235AF`。这不是随包预构建 DLL，PE 哈希可能因工具链或环境差异而变化。重建日志还记录上游 `AudioManager.cpp:313` 的 C4018 有符号/无符号比较警告；WGC 补丁编译单元没有警告。

此处二进制是对应上述补丁的本地构建，不应标成未经修改的 NuGet 原始二进制。上游许可全文见本目录 `LICENSE`；FrameTrail 的通知文件还应列出该 MIT 依赖及其来源。
