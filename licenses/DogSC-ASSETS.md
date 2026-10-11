# DogSC 名称、图标与上游署名

本项目使用 **DogSC** 名称与原应用图标，标注为 **Windows 社区移植版**。原项目：[laogou717/dogsc](https://github.com/laogou717/dogsc)，原作者：**神烦老狗**。这不表示原作者参与或官方发布了此 Windows 移植版。

- 固定上游提交：`e207d11b111c5e58152238fb64115b2009850547`。
- 原图：`upstream/dogsc/Resources/AppIconMaster.png`，1024 × 1024 RGBA。
- 原图 SHA-256：`1F1A16C58905C0901D408F3D3DF39CCCA5070C34C820A8BD6B0255230E554AF8`。
- 原仓库 MIT 版权声明：`Copyright (c) 2026 神烦老狗`，完整文本见 [`DogSC-MIT.txt`](DogSC-MIT.txt)。
- Windows 图标：`src/FrameTrail.App/Assets/DogSC.ico`，使用同一图案生成 Windows 所需尺寸；应用内 Logo 使用原 PNG。README 图标副本 `docs/assets/DogSC.png` 与原 PNG 的字节和哈希完全一致，可在 GitHub 和离线发行文档中显示。
- 转换命令：安装 Pillow 后运行 `python scripts/sync-dogsc-icon.py`。脚本先验证原图哈希，再生成 ICO。

界面字体 Alibaba PuHuiTi 3.0 的许可单独保留在 [`Alibaba-PuHuiTi-3-LICENSE.txt`](Alibaba-PuHuiTi-3-LICENSE.txt)，不将字体许可混同于上游代码许可。第三方 Windows 适配参考另见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

`FrameTrail.*` 代码命名空间、`.frametrail` 工程格式和历史用户数据目录作为兼容标识保留；它们不再作为产品品牌。
