# DogSC Windows 迁移协作规则

- 用户输出与交付文档使用简体中文，代码、路径、命令和原始日志保持原样。
- 所有子代理必须显式使用 `gpt-6-luna`，`fork_turns` 使用 `none` 或正整数，继续委派时传递本规则。Luna 不可用时由主代理继续。
- `upstream/dogsc` 是只读、固定提交的上游子模块；本次基线为 `e207d11b111c5e58152238fb64115b2009850547`，MIT。
- 主程序在 `src/FrameTrail.App`，纯算法在 Core，原生录制在 Capture，共享 GPU 渲染与 FFmpeg 管线在 Media。预览和导出必须共享场景计算和渲染语义。
- 修改媒体或项目存储后运行 Core tests 与 Integration；修改渲染后运行 Render.Probe。原生采集必须有实际媒体证据，不能以编译代替验收。
- `artifacts` 不提交：其中可能包含用户真实屏幕、录音、构建与临时诊断文件。不得公开上传。
- 硬件、DPI、长录制、干净机器和 macOS 兼容未经实测不得标为通过。
- 不得覆盖原始 `.dogscproject`。当前格式为 `FrameTrailProject` v1，扩展名 `.frametrail`。
- 用户已明确采用原项目 DogSC 名称、图标与 Logo；README、应用内和发行材料须保留神烦老狗、原仓库及 MIT 署名，并注明 Windows 社区移植版，不暗示原作者官方发布 Windows 版本。
- `FrameTrail.*` 内部命名、`.frametrail` 格式、历史用户数据目录及单实例身份保留兼容性，不作无必要的破坏性重命名。
- 公开发布目标、签名凭据与付费上线由用户确认；本地源码、测试、便携包和安装器可自主推进。
- 用户已授权使用 `lantiansb` 账户维护 GitHub 源码仓库 `lantiansb/dogsc`；该仓库是 `laogou717/dogsc` 的 Fork，Windows 默认开发分支为 `codex/windows`，`main` 保留上游代码。`origin` 指向个人 Fork，`upstream` 指向原项目。含 FFmpeg 的本地候选二进制在分发材料和验收未齐备时不上传。
