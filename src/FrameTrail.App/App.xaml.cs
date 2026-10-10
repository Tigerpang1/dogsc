using System.IO;
using System.Windows;

namespace FrameTrail.App;
public partial class App : Application
{
    private readonly UnhandledErrorPresenter unhandledErrors = new(Log, UnhandledErrorPresenter.Show);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            unhandledErrors.Report(args.Exception);
        };
        base.OnStartup(e);
    }
    public static void Log(Exception ex)
    {
        try { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "logs"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "errors.log"), DateTimeOffset.Now.ToString("O") + " " + ex + Environment.NewLine); } catch { }
    }
}
