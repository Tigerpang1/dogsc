using System.IO;
using System.Windows;

namespace FrameTrail.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => { Log(args.Exception); MessageBox.Show(args.Exception.Message, "FrameTrail · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        base.OnStartup(e);
    }
    public static void Log(Exception ex)
    {
        try { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "errors.log"), DateTimeOffset.Now.ToString("O") + " " + ex + Environment.NewLine); } catch { }
    }
}
