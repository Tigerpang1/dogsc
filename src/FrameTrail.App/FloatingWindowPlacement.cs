using System.IO;
using System.Text.Json;
using System.Windows;

namespace FrameTrail.App;

internal static class FloatingWindowPlacement
{
    private record Position(double Left, double Top);

    public static void Attach(Window window, string key)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", key + "-position.json");
        Position? position = null;
        try { if (File.Exists(file)) position = JsonSerializer.Deserialize<Position>(File.ReadAllText(file)); }
        catch (Exception error) { App.Log(error); }
        if (position is not null && double.IsFinite(position.Left) && double.IsFinite(position.Top))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = position.Left;
            window.Top = position.Top;
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Loaded += (_, _) =>
        {
            var area = SurfaceWindow.WorkArea(window);
            window.Left = Math.Clamp(window.Left, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.ActualWidth - 8));
            window.Top = Math.Clamp(window.Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.ActualHeight - 8));
        };
        window.Closed += (_, _) =>
        {
            if (!double.IsFinite(window.Left) || !double.IsFinite(window.Top)) return;
            try { MemoStorage.SaveText(file, JsonSerializer.Serialize(new Position(window.Left, window.Top))); }
            catch (Exception error) { App.Log(error); }
        };
    }
}
