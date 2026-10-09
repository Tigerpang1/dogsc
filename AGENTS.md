# FrameTrail Windows 迁移协作规则

- 用户输出与交付文档使用简体中文，代码、路径、命令和原始日志保持原样。
- 所有子代理必须显式使用 `gpt-6-luna`，`fork_turns` 使用 `none` 或正整数，继续委派时传递本规则。Luna 不可用时由主代理继续。
- `upstream/dogsc` 是只读、固定提交的上游子模块；本次基线为 `e207d11b111c5e58152238fb64115b2009850547`，MIT。
- 主程序在 `src/FrameTrail.App`，纯算法在 Core，原生录制在 Capture，共享 GPU 渲染与 FFmpeg 管线在 Media。预览和导出必须共享场景计算和渲染语义。
- 修改媒体或项目存储后运行 Core tests 与 Integration；修改渲染后运行 Render.Probe。原生采集必须有实际媒体证据，不能以编译代替验收。
- `artifacts` 不提交：其中可能包含用户真实屏幕、录音、构建与临时诊断文件。不得公开上传。
- 硬件、DPI、长录制、干净机器和 macOS 兼容未经实测不得标为通过。
- 不得覆盖原始 `.dogscproject`。当前格式为 `FrameTrailProject` v1，扩展名 `.frametrail`。
- 正式品牌、公开发布、签名凭据与付费上线由用户确认；本地源码、测试、便携包和安装器可自主推进。
