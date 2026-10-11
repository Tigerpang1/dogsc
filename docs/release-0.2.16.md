# 0.2.16 播放节拍与缓冲复用

本轮针对暂停恢复和连续播放：原来每帧使用普通定时等待，唤醒可能额外滞后约14 ms；4K播放还反复分配并复制整帧，播放头更新也重画整个时间线。现在使用绝对QPC期限与可取消的Windows高精度计时器，解码写入会话复用的缓冲，时间线通过保留的绘图变换移动播放头。共享场景只计算一次。

暂停保留当前图像和最多一张已准备的后续帧；恢复先提交暂停位置，后续帧按真实媒体时间推进，音频也从暂停位置开始。探针明确校验恢复首帧时间等于暂停位置，避免跳过一帧来制造较短延迟。项目、素材或预览尺寸变更时重建资源；快速连点以最后请求为准。新用户默认60帧预览，已有30/60帧设置继续使用。鼠标悬浮保留点击帧的规则继续有效。

## 开源参考

继续沿用[OpenScreen采用记录](openscreen-adoption.md)中的常驻解码思路，本轮检查了OBS Studio固定提交 `7d98bebe1115b12608837b622164ea5fe6d9483a`：

- [独立视频节拍和有限帧缓存](https://github.com/obsproject/obs-studio/blob/7d98bebe1115b12608837b622164ea5fe6d9483a/libobs/media-io/video-io.c)。FrameTrail采用固定期限和一张待显示帧，落后时跟随媒体时间，避免积压播放请求。
- [Windows截止时间等待](https://github.com/obsproject/obs-studio/blob/7d98bebe1115b12608837b622164ea5fe6d9483a/plugins/win-dshow/virtualcam-module/sleepto.c)。FrameTrail独立实现高精度waitable timer和线程池异步等待，不进行忙等，也不修改系统全局时钟精度；系统不支持时回退普通waitable timer。
- [GPU缓冲池与归还机制](https://github.com/obsproject/obs-studio/blob/7d98bebe1115b12608837b622164ea5fe6d9483a/libobs/obs-video-gpu-encode.c)。本轮落实可复用CPU输入/输出缓冲，GPU读回和WPF位图交付仍存在。没有复制OBS的GPL源码，没有声称达到OBS的性能。

## 后台测量

同一真实3840×2160、60fps录屏，在本机软件libav解码、D3D11硬件合成路径测量。连续吞吐覆盖解码、合成、GPU读回、BitmapSource创建和冻结，各48帧，非物理屏幕帧率。

| 输出与取样 | 0.2.15吞吐 | 0.2.16最终吞吐 | 每帧托管分配变化 |
|---|---:|---:|---:|
| 2560×1440，30fps取样 | 43.30 fps | 79.77 fps | 29.5 MB → 10.0 KB |
| 2560×1440，60fps取样 | 49.29 fps | 81.35 fps | 29.5 MB → 8.2 KB |
| 3840×2160，30fps取样 | 43.83 fps | 72.08 fps | 66.4 MB → 8.2 KB |
| 3840×2160，60fps取样 | 45.13 fps | 77.27 fps | 66.4 MB → 7.9 KB |

原始基线为 `artifacts/validation/playback-throughput-baseline-20261011.json`，最终报告为 `playback-throughput-20261011-062807-145/result.json`，项目与媒体前后SHA匹配。内存数字仅为托管分配，不包含WPF/驱动的原生分配，也不代表进程总内存。

真实MainWindow路径的三轮恢复、每组3秒连续播放：

| 条件 | 恢复准备 | 首个前进帧提交 | 连续提交 / p95间隔 |
|---|---:|---:|---:|
| 真实4K素材，1440×810输出，30fps | 0.58–1.06 ms | 34.07–36.22 ms | 30.00 fps / 33.71 ms |
| 真实4K素材，3840×2160输出，60fps | 0.45–0.94 ms | 17.58–19.47 ms | 59.99 fps / 17.60 ms |
| 合成带音频，1440×810输出，60fps | 0.69–1.25 ms | 17.56–20.42 ms | 60.00 fps / 17.05 ms |

三组均无提交序列跳帧，声音路径的最大音画时钟差约21.4 ms。60/30fps下一帧正常间隔分别约16.7/33.3 ms；首个前进帧由赋图像并更新播放头后的回调计时。窗口未显示、未创建HWND，声音Volume=0且IsMuted=true，未抢占桌面。这些数据不包含DWM和显示器呈现、真实扬声器听感或长时稳定性。原工程SHA前后一致。

首次冷启动仍需初始化或生成混音：本次真实静音素材约326–364 ms，合成带音频约786 ms。原生共享GPU纹理交付尚未实现，旧长GOP素材远距离跳转也仍有解码成本。

## 验证与交付

`scripts/test.ps1 -IncludeMediaProbe -IncludeIntegration` 和 `-IncludeVisualProbe -IncludeReleaseProbe` 已通过；日志在 `artifacts/validation/playback-0.2.16/`。覆盖Core、共享渲染、1080p60同步、跨剪辑音轨范围导出、原生解码11场景、流式非对齐行跨度逐像素一致、取消恢复、时间线悬浮保持和播放头移动不重绘。时钟探针还检查等待中释放、取消后复用、1000次生命周期无句柄净增长。浅深主题、录制条、托盘菜单和包规则的离屏回归通过。

本地输出为便携目录/ZIP、安装器和源码ZIP；包内容与源码独立重建的最终状态见 `artifacts/release/FrameTrail-0.2.16-candidate-manifest.json`。没有重新进行真实采集、安装卸载或用户手感验收，整体95%台账不因本轮性能测试而提升。

核对时旧进程仍来自0.2.14目录。试用前须从托盘或浮窗菜单选择“退出 FrameTrail”，再启动0.2.16；主窗“×”仅隐藏编辑器。已有用户可在设置中选择60帧预览。仅启动新EXE会被单实例机制转交给旧进程。
