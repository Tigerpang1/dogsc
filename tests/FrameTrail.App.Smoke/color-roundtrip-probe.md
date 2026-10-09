# 颜色往返探针

颜色往返验证由 `tests/FrameTrail.Render.Probe/ColorAndSlowMotionProbe.cs` 实现，从 Render Probe 入口运行：

```powershell
dotnet run --project tests/FrameTrail.Render.Probe/FrameTrail.Render.Probe.csproj -c Release -- --color-slow-motion artifacts/validation/color-slow-v2
```

探针生成七色色块视频和项目 fixture，读取预览帧，并分别使用软件编码器和硬件编码器导出 MP4；再解码输出帧检查色块通道误差和视频色彩元数据。当前证据位于 `artifacts/validation/color-slow-v2/result.json`：`passed` 为 `true`，预览最大通道误差为 2/255，软件与硬件导出均为 4/255，低于各自 4/255 与 6/255 阈值；输出标记为 BT.709 primaries/matrix、sRGB transfer (`iec61966-2-1`) 和 limited range (`tv`)。

这项探针覆盖渲染预览到编码输出的像素往返，不覆盖编辑器颜色字段的 UI 输入及项目 JSON 字段持久化，也不代表显示器或操作系统色彩管理验收。慢放音调与预览/导出时间映射同由此入口验证，结果也记录在同一 JSON 中。
