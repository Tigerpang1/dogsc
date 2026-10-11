using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class MainWindowProbeLifetime
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Close(MainWindow? window)
    {
        if (window is null) return;
        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(() => Close(window));
            return;
        }

        var dispatcher = window.Dispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        List<Exception>? errors = null;
        try
        {
            Try(() => typeof(MainWindow).GetMethod("ClosePlaybackAudio", Private)?.Invoke(window, null));
            Try(() =>
            {
                if (typeof(MainWindow).GetMethod("DisposePlaybackResourcesAsync", Private)?.Invoke(window, null) is not Task release)
                    return;
                if (!release.IsCompleted)
                {
                    var frame = new DispatcherFrame();
                    _ = release.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
                        new Action(() => frame.Continue = false)));
                    Dispatcher.PushFrame(frame);
                }
                release.GetAwaiter().GetResult();
            });
            Try(() =>
            {
                var closeReady = typeof(MainWindow).GetField("closeReady", Private)
                    ?? throw new MissingFieldException(typeof(MainWindow).FullName, "closeReady");
                closeReady.SetValue(window, true);
            });
            Try(window.Close);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        if (errors is { Count: > 0 }) throw new AggregateException("MainWindow probe cleanup failed.", errors);

        void Try(Action action)
        {
            try { action(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
    }
}
