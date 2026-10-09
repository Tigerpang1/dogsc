# Windows 现代外壳评估

更新时间：2026-10-09。结论针对 FrameTrail 当前 .NET 10 WPF + GPU 预览结构。Windows App SDK 试验只编译和发布，没有启动窗口；它能证明 API 接入与发布管线可构建，不能证明实际外观、焦点处理或桌面集成效果。

## 建议

短期保留 WPF 内容界面，通过 Windows App SDK 和 Win32/DWM API 渐进强化主窗口外壳。先把原生标题栏、系统窗口行为、圆角与阴影作为一个小范围原型，在主编辑窗口做 100%、150%、200% DPI、键盘焦点、UI Automation、拖动缩放、多显示器边界和预览渲染检查。只有桌面验证确认了收益且发布体积可接受后，才扩展到其它窗口。

暂不整体改写为 WinUI 3。微软把 WinUI 3 推荐给新建 Windows 桌面应用，也明确建议现有 WPF 应用可以逐步使用 Windows App SDK。FrameTrail 已有大量 WPF 控件、UI Automation 测试、复杂的画布命中与拖动逻辑；预览渲染器是 D3D11，但当前通过 `BitmapSource` 回到 WPF `ImageSource` 显示。整页切换框架会牵涉键盘路由、控件可访问性、画布坐标和GPU表面互操作的重新验收，当前没有证据表明这种成本能换来足够收益。

短期改进优先级如下：

1. 主窗口优先使用系统支持的非透明窗口框架和原生标题栏行为，检查窗口阴影、圆角、最大化、贴靠、拖动和焦点恢复。可由 `AppWindow` 集成窗口级能力；窗口圆角的系统提示不保证适用于所有窗口状态。
2. 用统一的 WPF 资源与控件模板整理录制条、导出对话框、备忘录、菜单、下拉框和进度浮层：统一文字层级、圆角、阴影、焦点框、悬停、Esc/外部关闭及所有者窗口关系。原生窗口边框之外的透明浮窗仍需自己的圆角裁剪；Windows 不会自动为逐像素透明窗口添加系统圆角。
3. 仅在主窗口的非画布外壳试验 Mica 等系统材质。素材画布、时间线与真实导出画面继续使用明确的不透明表面，避免桌面壁纸混入可编辑或导出内容。
4. 在当前 .NET 10 WPF 上评估 Fluent 样式和 Windows 外壳集成。仓库已锁定 SDK `10.0.401`，项目目标为 `net10.0-windows`；官方支持表将 .NET 10 列为 LTS，运行时 `10.0.12` 于 2026-09-08 发布，支持至 2028-11-14。官方 WPF 文档说明 Fluent 主题和主题模式可用，并指出 .NET 10 继续改进 Fluent 支持；这不等于 FrameTrail 已采用系统 Fluent 模板或完成高对比、焦点、浮层和DWM外观验收。当前主项目 Release 构建为0警告、0错误，隔离的 Core、导入、GPU媒体和合成集成测试通过；GUI视觉和硬件验收仍需单独进行。

## 本机编译与发布试验

隔离工程位于 `artifacts/validation/shell-spike/`，其早期试验使用 .NET 8 WPF、Windows App SDK 2.5.1、`win-x64` 和 `AppWindow` 的 HWND 桥接 API；该版本记录保留为原型历史。主项目随后已迁到 .NET 10。另有隔离 .NET 10 升级结果 `artifacts/validation/net10-upgrade-result.json`：SDK `10.0.401` 本地安装包通过官方哈希校验；主项目 App/Smoke Release 构建均为0警告、0错误，Core、DogSC导入、GPU渲染、音频覆写和合成集成检查通过。自包含 WPF 发布目录为420个文件、188,185,050字节（179.47 MiB），不含FFmpeg。以上验证没有启动WPF窗口、安装Windows App SDK或验证DWM外观。

试验发布目录的大小如下，字节数来自该隔离工程的递归文件统计，不代表 FrameTrail 最终安装包的精确增长：

| 模式 | 文件数 | 目录总大小 | 说明 |
|---|---:|---:|---|
| Windows App SDK 框架依赖，.NET 也为框架依赖 | 48 | 80,388,435 字节（76.7 MiB） | 仍需目标机具备 Windows App SDK 运行时或另行部署；试验中的投影/组件依赖仍占明显体积。 |
| Windows App SDK 自包含，.NET 为框架依赖 | 330 | 156,595,363 字节（149.3 MiB） | 本机发布输出增加约72.6 MiB，可目录复制部署；应用必须主动跟进 SDK 安全和服务更新并重发。 |

构建输出包含 `Microsoft.UI.Xaml`、`Microsoft.WindowsAppRuntime`、WinUI 投影和 AI/ONNX 相关运行库。该测量使用完整 `Microsoft.WindowsAppSDK` 元包，不是经过裁剪的最终产品依赖集。测试未检查目标机运行时探测、应用冷启动、内存、DWM实际效果、主题、高对比、UI Automation、窗口间焦点切换、WPF `Popup` 与原生菜单混用或 FFmpeg/D3D 运行。因此当前只证明“可接入并发布”，不证明“适合直接合入产品”。

## 部署与许可

Windows App SDK 官方提供框架依赖和自包含两种部署。框架依赖体积较小，但非打包应用需要目标机安装运行时或由安装流程部署。自包含可以随应用目录复制，不要求额外安装运行时，但包更大，服务更新需由本项目重建并重新发布。FrameTrail 当前便携 ZIP 与 Inno Setup 工作流采用自包含应用目录；若引入 SDK，必须在隔离路径、安装器、卸载器和升级路径重新验证运行时文件、快捷方式及文件关联行为。

本地 NuGet 缓存的 `Microsoft.WindowsAppSDK` 2.5.1 包含 `license.txt` 和 `NOTICE.txt`。该包许可证允许分发随 SDK NuGet 放置的可再发行代码，但同时规定显著主要功能、下游及最终用户条款保护、对分发相关索赔的赔偿义务，并禁止移除 Microsoft/供应方标识或暗示背书。自包含试验输出没有自动附带包根目录的这两份文件。正式采用前须审查完整包与传递依赖许可证，决定许可证/NOTICE 随包放置方式，并由发行负责人确认终端用户条款和赔偿义务是否可接受；本机构建成功不等于许可审查完成。

## 决策门槛

继续采用 WPF + Windows App SDK 原型，前提是：主窗口实测有明确的视觉或系统集成提升；窗口拖动、最大化和Windows贴靠稳定；现有 UI Automation 与快捷键不退化；GPU画布帧率和输入延迟没有回归；部署体积、更新责任和SDK许可均可接受。未满足时，先使用 WPF 控件模板、系统窗口API和统一弹层管理改进视觉，不为追求框架名称引入部署负担。

若未来新建独立功能页，可对 WinUI 3 或 WPF/WinUI XAML Islands 做单独原型。完整迁移需要以真实的预览画面、浮窗、弹出菜单、键盘导航、辅助技术和发行包为验收对象；框架文档的推荐不替代产品证据。

## 参考资料

- [Windows App SDK 发布通道](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)：截至2026-10-09查阅，Stable 为 2.5.1。
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/)：微软面向新 Windows 桌面应用的推荐 UI 框架。
- [在现有项目中使用 Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project)：说明既有 WPF 项目可渐进接入以及未打包应用的初始化要求。
- [Windows App SDK 部署概述](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview)：比较框架依赖与自包含部署的安装、体积和服务责任。
- [未打包应用分发](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)：说明目录复制分发及运行时依赖。
- [Windows 11 桌面应用圆角](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)：自定义窗口框架、透明窗口及系统圆角的限制。
- [Win32 桌面应用 Mica 材质](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-mica-win32)：Mica 对系统主题及运行时初始化的要求。
- [.NET 10 WPF 新功能](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)：Fluent 样式改进仍在完善中。
- [.NET 官方支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)：截至2026-09-08，.NET 10 LTS支持至2028-11-14；该文档随版本更新，应在后续发行时重新核对。
