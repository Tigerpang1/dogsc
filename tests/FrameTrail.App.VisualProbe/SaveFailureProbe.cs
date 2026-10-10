using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FrameTrail;
using FrameTrail.App;
using FrameTrail.Media;

namespace FrameTrail.App.VisualProbe;

internal static class SaveFailureProbe
{
    public static void Verify(MainWindow window, Action<bool, string, object?> check)
    {
        var closingHandler = typeof(MainWindow).GetMethod("WindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        window.Closing -= (CancelEventHandler)closingHandler.CreateDelegate(typeof(CancelEventHandler), window);
        var directory = Path.Combine(Path.GetTempPath(), "frametrail-save-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "project.frametrail");
            var project = new FrameTrailProject
            {
                MediaPath = "media/source.mp4",
                Media = new() { Width = 640, Height = 360, Duration = 4, FrameRate = 30 },
                Timeline = new() { Segments = [new() { SourceDuration = 4 }] },
                Settings = new() { Background = "#F7F7F5", CanvasWidth = 640, CanvasHeight = 360, Padding = .055 }
            };
            FrameTrailProjectStore.Save(file, project);
            var originalBytes = File.ReadAllBytes(file);
            var history = new UndoRedoHistory<FrameTrailProject>(project,
                value => JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(value))!);
            SetField(window, "project", project);
            SetField(window, "projectFile", file);
            SetField(window, "history", history);
            Invoke(window, "RefreshProject");

            var padding = GetField<Slider>(window, "PaddingSlider");
            var background = GetField<TextBox>(window, "BackgroundColor");
            var savedPadding = project.Settings.Padding;
            padding.Value = savedPadding + .02;
            background.Text = "#AA00BB";

            Exception? saveFailure = null;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { Invoke(window, "Edit", (Action<FrameTrailProject>)(next => next.Settings.Padding = padding.Value)); }
                catch (TargetInvocationException ex) { saveFailure = ex.InnerException ?? ex; }

                check(saveFailure is IOException
                    && saveFailure.Message.Contains("本次编辑未应用", StringComparison.Ordinal)
                    && saveFailure.Message.Contains("重新执行该操作", StringComparison.Ordinal),
                    "locked project reports that the edit was not applied and must be repeated",
                    saveFailure?.Message);
                check(ReferenceEquals(GetField<FrameTrailProject>(window, "project"), project)
                    && project.Settings.Padding == savedPadding
                    && Math.Abs(padding.Value - savedPadding) < 1e-9
                    && background.Text == "#F7F7F5",
                    "failed edit leaves model and refreshed inspector controls at the saved values",
                    new { ModelPadding = project.Settings.Padding, ControlPadding = padding.Value, Background = background.Text });
                check(!history.CanUndo && !history.CanRedo && history.Current.Settings.Padding == savedPadding,
                    "failed edit leaves undo and redo history unchanged", null);
                check(File.ReadAllBytes(file).SequenceEqual(originalBytes)
                    && FrameTrailProjectStore.Load(file).Settings.Padding == savedPadding,
                    "failed edit leaves project file bytes and persisted model unchanged", null);
            }

            var warningProjectDirectory = Path.Combine(directory, "open-warning-project");
            Directory.CreateDirectory(Path.Combine(warningProjectDirectory, "media"));
            var shortAudio = Path.Combine(warningProjectDirectory, "media", "short.wav");
            MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:a", "pcm_s16le", "-y", shortAudio])
                .GetAwaiter().GetResult();
            var warningProject = new FrameTrailProject();
            warningProject.AudioTracks.Add(new AudioTrack { MediaPath = "media/short.wav", Duration = 2 });
            var loadAuxiliary = typeof(MainWindow).GetMethod("LoadAuxiliaryDurations", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loadTask = (Task<(Dictionary<string, double> Durations, bool Changed, string? Warning)>)loadAuxiliary.Invoke(
                window, [warningProject, Path.Combine(warningProjectDirectory, "project.frametrail")])!;
            var auxiliaryLoad = loadTask.GetAwaiter().GetResult();
            check(auxiliaryLoad.Changed && auxiliaryLoad.Warning?.Contains("已按实际可用范围裁短", StringComparison.Ordinal) == true,
                "auxiliary validation returns its automatic-trim warning instead of setting transient UI state",
                new { auxiliaryLoad.Changed, auxiliaryLoad.Warning, TrimmedDuration = warningProject.AudioTracks[0].Duration });
            var formatOpenStatus = typeof(MainWindow).GetMethod("ProjectOpenedStatus", BindingFlags.Static | BindingFlags.NonPublic)!;
            var openedStatus = (string)formatOpenStatus.Invoke(null, [Path.Combine(warningProjectDirectory, "project.frametrail"), auxiliaryLoad.Warning])!;
            check(openedStatus.Contains("项目已打开", StringComparison.Ordinal)
                    && openedStatus.Contains(auxiliaryLoad.Warning!, StringComparison.Ordinal)
                    && !openedStatus.Contains("处理已取消", StringComparison.Ordinal),
                "successful project-open status clears prior cancellation text while preserving current trim warning",
                openedStatus);
            check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "save-failure probe does not show the window or create a native HWND", null);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static void Invoke(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);

    private static T GetField<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void SetField(MainWindow window, string name, object value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
}
