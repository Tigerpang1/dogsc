using System.Diagnostics;
using System.Windows;

namespace FrameTrail.App;

internal static class MicrophoneAccessRecovery
{
    internal const string Title = "无法访问麦克风";
    internal const string SettingsUri = "ms-settings:privacy-microphone";
    internal const string OpenSettingsLabel = "打开麦克风设置";
    internal const string LaterLabel = "稍后";
    internal const string Message = "Windows 拒绝了麦克风访问。\n\n"
        + "请在“设置 → 隐私和安全性 → 麦克风”中检查“麦克风访问”和“允许桌面应用访问麦克风”。\n\n"
        + "调整后返回 FrameTrail，重新开始录制。也可以先关闭麦克风，仅录屏。";

    internal static Window Create(Window? owner, Action<MessageBoxResult> completed) =>
        SurfaceDialog.Create(owner, Message, Title, MessageBoxButton.OKCancel, completed,
            OpenSettingsLabel, LaterLabel, preferCancel: true);

    internal static void Show(Window owner)
    {
        var result = MessageBoxResult.Cancel;
        var window = Create(owner, value => result = value);
        window.ShowDialog();
        if (result != MessageBoxResult.OK) return;
        try
        {
            Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            App.Log(error);
            SurfaceDialog.Show(owner,
                "未能打开 Windows 设置。请手动进入“设置 → 隐私和安全性 → 麦克风”，检查访问权限后返回 FrameTrail 重试。",
                "请手动打开麦克风设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
