# FrameTrail 演示工程

这是一个8秒、1280×720、30fps的可重建演示。`FrameTrail-Demo.frametrail` 使用同目录相对路径；`FrameTrail-Demo.mp4` 是保存并重新打开工程后，由实际导出引擎生成的样片，含合成的440Hz低音量音轨。

屏幕和摄像头画面由FFmpeg `testsrc2` 合成，声音由 `sine` 合成，角标由本项目绘制。工程展示动态海洋背景、圆角、阴影、指针与点击、缩放、屏幕3D和聚焦、独立摄像头及其运动、图像角标、依次开场和独立音轨。角标标记为“SYNTHETIC SAMPLE”。

在源码仓库根目录运行：

```powershell
pwsh -File scripts/create-sample.ps1 -OutputDirectory C:\Temp\FrameTrail-Demo
```

需要.NET 10 SDK、FFmpeg/ffprobe和支持D3D11的Windows图形设备。生成器会保存并重开工程、导出MP4，并核对尺寸、帧率、时长和音轨；媒体生成与导出不打开桌面窗口，也不播放声音。普通生成命令会覆盖同名生成物，保留目录内其他文件；打包脚本始终在全新临时目录生成，只收录固定的八个演示文件。

此演示不含真实桌面、用户录音或DogSC截图。FFmpeg程序及其编解码器的公开分发条件见 `licenses/THIRD-PARTY-NOTICES.md`。
