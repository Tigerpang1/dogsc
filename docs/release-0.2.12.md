# 0.2.12 录制、预览与画质候选

0.2.12 为录制条增加齿轮菜单，录制浮窗菜单继续由 `.11` 独立 `TrayMenuWindow` 承载；最近项目子菜单独立设置录屏排除。录制完成提示保持简洁，正常采集格式、时钟和 backend 信息写入 `Diagnostics`，真实异常写入 `Warnings`。

本轮还调整时间线预览：悬停帧保持到点击定位、播放或关闭预览；左键按下立即 seek。调度使用一个 active 请求和一个 latest pending 请求，并复用同素材的解码会话。本轮确认的最近三段实录均为4K60；旧预览固定1440分辨率再放大。自动预览现按 viewport 物理像素确定目标，源素材继续原始分辨率解码以保留 zoom 细节；显式预览尺寸可选960、1440、1920。录制默认采用源分辨率，NVENC使用p5/hq/vbr/cq19，1080p60目标约16 Mbps、4K60最大目标64 Mbps。Shader bytecode本地缓存及异步预热入口已经加入。不能凭静态桌面负载下观察到的低码率直接推断实际录制画面模糊。

## 已有后台证据

最终 SDK 10.0.401 构建为0警告、0错误。`artifacts/validation/package-0.2.12/preview-interaction-pipe/result.json` 的12项 MainWindow 探针通过：合成媒体解码与合成、悬停不改播放头、active+latest pending 最终收敛、顺逆向 seek、同帧 Bitmap 复用、播放/项目换代/显式取消时拒绝旧帧、选中效果不污染真实时间线帧；窗口未显示且无 HWND。探针首帧383.28毫秒、顺向134.57毫秒、逆向122.75毫秒；同帧点击5.83毫秒。`preview-sizing-pipe/result.json` 的12项尺寸探针通过，覆盖1×/1.5×/2×物理视口对应1600×900/2400×1350/3200×1800输出、合成4K原分辨率输入、同帧尺寸重合成和与播放会话的采样像素一致。

`artifacts/validation/capture-video-quality-v5/playback-throughput/result-named-pipe.json` 记录持续会话吞吐：4K源保留原始解码、2400×1350@30合成，zoom 1/2连续30帧分别耗时875.76/873.55毫秒（34.26/34.34 fps），steady median为14.85/14.55毫秒、P95为16.85/19.94毫秒；首帧分别438.62/407.19毫秒。此为无 UI 的 Media 离屏吞吐，不能等同于用户实机拖动流畅度。持续解码的 scrub decoder 专项记录在 `artifacts/validation/package-0.2.12/scrub-decoder-pipe/scrub-decoder-report.txt`，覆盖CFR、VFR、PTS偏移、倒退/远跳、取消恢复与索引裁剪。

`artifacts/validation/capture-video-quality-v5/result.json` 使用合成4K60来源做2秒编码比较：新策略编码1.741秒，SSIM 0.988639、平均输出60.15 Mbps；旧6 Mbps策略SSIM 0.827179。该结果只证明合成编码对照，不覆盖真实桌面采集吞吐、实际用户画面观感或硬件兼容。另有本机真实录制证据 `artifacts/validation/package-0.2.12/native-4k60/screen.summary.json`：3840×2160@60，成片8.1秒、486帧，H.264 NVENC，warnings为空；ffmpeg完整解码退出码0。该段无音频，也不代表动态桌面、菜单排除或用户主观画质认可。

Shader 缓存断言见 `artifacts/validation/package-0.2.12/shader-cache/shader-cache-assertions.json`：真实 D3DCompiler bytecode 首次编译一次，第二次读取相同字节，损坏 payload 后自动重新编译，修复后再次命中。统一 SDK 10.0.401 构建已通过，0警告、0错误；Core、Integration、Render、Capture `--capture-result-notice-test`、`--failure-lifecycle-probe`、Visual `--recording-hotkey`（108项）、`--tray-menu-window`（13项）、`--timeline-input`（14项）及以上预览探针记录在 `artifacts/validation/package-0.2.12/`。Core、Integration、Render最终日志分别为 `core-pipe-final.log`、`integration-pipe-final.log`、`render-pipe-final.log`；综合合成夹具结果位于 `artifacts/validation/synthetic-testsrc2-20261011-030850-451/result.json`。Scrub decoder最终报告通过CFR30/60、VFR、5秒PTS偏移、4400帧索引裁剪、取消恢复和进程释放检查，见 `artifacts/validation/package-0.2.12/scrub-decoder-pipe/scrub-decoder-report.txt`。

## 尚待验收与发行信息

FT-07仍待实机确认新录制条菜单，以及提词器、菜单和最近项目子菜单的录屏排除。新的悬停保留、左键立即定位、预览尺寸及录制质量体验也需要用户实机验收；人工验收和包验证不得由无窗口探针代替。便携包/安装器/源码归档尚未生成，包内容、SHA-256、独立源码归档构建、实际安装卸载及版本身份待补。0.2.11的包结果不作为0.2.12发行验收。

保持验收台账28项通过、2项待验、3项延期，分母33；本候选不宣称达到95%。复杂权限恢复按用户要求暂缓；摄像头设备场景和在线更新/公开反馈服务仍延期。Mac工程互通不属于本轮要求。0.2.11既有真实录制、托盘菜单、提词器恢复和重复启动证据见 [`release-0.2.11.md`](release-0.2.11.md)、[`desktop-residency.md`](desktop-residency.md) 及 `artifacts/validation/package-0.2.11/basic-desktop-v1/`。

预览调度和物理像素尺寸的 OpenScreen 参考来自固定源码快照及[固定 commit](https://github.com/getopenscreen/openscreen/commit/2896c1f7b9ad46cf53419e6edd360da2d2d80ac3)；本地只读调研记录在 [`artifacts/reference/openscreen/README.md`](../artifacts/reference/openscreen/README.md)，不作为本项目验收证据。
