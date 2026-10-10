# 麦克风访问恢复

2026-10-10：0.2.8 源码修复。当前仅完成后台检查，FT-06 仍待实机拒绝权限与恢复录制验收。

WASAPI 初始化或启动麦克风时，Windows 可能返回 `E_ACCESSDENIED (0x80070005)`。.NET 可能以 `COMException` 或 `UnauthorizedAccessException` 表示该错误。映射只包围麦克风设备 API；系统回环、文件创建、取消和其他 HRESULT 不归入此提示。内部异常保留在日志中，正常采集、时间戳和包处理不变。

提示沿用应用浮窗外壳和明暗主题，默认焦点为“稍后”。关闭和取消都返回应用，仅明确激活“打开麦克风设置”才启动 `ms-settings:privacy-microphone`。调整后由用户重新开始录制，不自动修改权限或启动采集；打开设置失败时显示手动路径。

后台证据在 `artifacts/validation/microphone-access-recovery/`：`capture-probe.log` 为纯错误映射检查，`offscreen-v2/result.json` 为40项离屏布局/动作/默认按钮检查，两张 PNG 记录明暗主题。初次夹具试图给未显示窗口指定 Owner，WPF 拒绝；`offscreen-v1.log` 保留失败。修正后离屏窗口不指定 Owner，不据此声明实机所有权、激活和焦点切换验证。

微软说明桌面应用使用单独的麦克风访问开关，且部分桌面应用可能不完全受隐私设置控制：[麦克风设置](https://support.microsoft.com/en-us/windows/privacy/turn-on-app-permissions-for-your-microphone-in-windows)、[桌面应用和隐私](https://support.microsoft.com/en-gb/windows/privacy/windows-desktop-apps-and-privacy?nochrome=true)。[COM 错误说明](https://learn.microsoft.com/en-us/windows/win32/learnwin32/error-handling-in-com) 将 `E_ACCESSDENIED` 定义为一般访问拒绝，因此提示要求检查设置，不把开关认定为唯一原因。
