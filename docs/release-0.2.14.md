# 0.2.14 预览响应

暂停预览改为常驻libav解码会话：近邻向前位置继续解码，随机跳转从关键帧定位；重复目标使用有界帧缓存。初始化、解码、释放和大帧复制放在后台，点击立即更新播放头。连续拖动只保留最新等待目标，悬浮保持最后点击的画面。

显示器/区域FFmpeg新录制采用0.5秒关键帧间隔，保持原分辨率、帧率及质量参数。窗口ScreenRecorderLib路径不在本次编码参数修改范围。旧素材不重编码。

本机对同一段4K60旧录屏的1440预览，跨位置输入处理由44.65–58.71 ms降至0.46–3.67 ms，画面提交由223.47–415.98 ms降至58.78–264.04 ms。首帧仍有初始化开销，旧长GOP素材远跳仍可达到几百毫秒。数字测到WPF图像赋值，未覆盖物理显示呈现。对比来源、机制和逐项数值见 [OpenScreen采用记录](openscreen-adoption.md)。

## 验证与边界

后台证据在 `artifacts/validation/openscreen-adoption/`：`native-19s`、`native-4s`测量真实素材的相邻、远跳、反向和快速连续点击；源工程哈希未变，探针没有HWND。`native-interaction`通过13项，`native-sizing`通过12项，`native-input`通过15项；覆盖悬浮不触发、最终定位、播放/换项目竞态、预览分辨率与共享场景像素。

`native-semantics-v2-added-cases-final5/result.json`通过11组：CFR30/60、长GOP和B帧、VFR、非零起点、尾帧、缓存、预先取消、连续/反向读取、BT.709 limited/full range缩放，以及90°旋转素材的CLI autorotate回退。原生/回退帧均与对应FFmpeg CLI逐字节一致；CFR60连续/反向48点，VFR连续/反向51点。默认软件解码；硬解与共享GPU纹理没有标为本版已交付能力。Core、Integration、Render.Probe和CLI持续解码检查通过，日志在同一验证目录。

0.5秒GOP合成4K60编码对照见 `artifacts/validation/openscreen-adoption-capture/capture-probe-30fps/result.json`：体积增加约5.3%，SSIM从0.988639到0.985825；30/60fps关键帧间隔均符合设置。该验证未进行真实桌面新录制，不等同于新录屏主观画质验收。

## 本地交付

候选输出为 `artifacts/release/FrameTrail-0.2.14-win-x64/`、同名ZIP、`FrameTrail-Setup-0.2.14-win-x64.exe` 和 `FrameTrail-0.2.14-source.zip`。包内容、源码独立重建和哈希状态以 `FrameTrail-0.2.14-candidate-manifest.json` 及 `artifacts/validation/package-0.2.14/` 为准。新增libav DLL、MIT/LGPL文本和固定对应源码获取材料随包交付。

实机手感、安装卸载和新录制条菜单/辅助窗排除仍待验收；摄像头延期。原有功能台账不因后台性能测试而升级到95%。
