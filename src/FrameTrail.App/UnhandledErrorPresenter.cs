using System.Windows;

namespace FrameTrail.App;

/// <summary>Keep unexpected UI errors inside the shared shell without recursive error dialogs.</summary>
internal sealed class UnhandledErrorPresenter(Action<Exception> log, Action<Exception> present)
{
    private bool presenting;

    internal void Report(Exception error)
    {
        log(error);
        if (presenting) return;
        presenting = true;
        try { present(error); }
        catch (Exception presentationError) { log(presentationError); }
        finally { presenting = false; }
    }

    internal static Window Create(Window? owner, Exception error)
    {
        var dialog = SurfaceDialog.Create(owner,
            "此次操作未完成。\n\n" + error.Message + "\n\n请关闭提示后检查当前内容，再重试。",
            "操作未完成", MessageBoxButton.OK, _ => { });
        if (owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;
        }
        return dialog;
    }

    internal static void Show(Exception error)
    {
        var windows = Application.Current?.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray() ?? [];
        var owner = windows.FirstOrDefault(w => w.IsActive) ?? windows.FirstOrDefault();
        Create(owner, error).ShowDialog();
    }
}
