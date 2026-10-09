using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FrameTrail.Capture;

internal sealed class InputSampler : IAsyncDisposable
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly int _rate;
    private readonly Func<PixelRect> _boundsProvider;
    private readonly Action<InputEvent> _onEvent;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _stop = new();
    private readonly StreamWriter? _writer;
    private Task? _worker;
    private readonly HashSet<int> _down = [];
    private static readonly (int Code, string Name)[] Modifiers = [(0x10, "Shift"), (0x11, "Control"), (0x12, "Alt"), (0x5B, "Windows"), (0x5C, "Windows")];
    private static readonly (int Code, MouseButton Button)[] Buttons = [(0x01, MouseButton.Left), (0x02, MouseButton.Right), (0x04, MouseButton.Middle), (0x05, MouseButton.X1), (0x06, MouseButton.X2)];

    internal InputSampler(InputTrackingOptions options, Func<PixelRect> boundsProvider, Action<InputEvent> onEvent, Action<Exception> onError)
    {
        _rate = Math.Clamp(options.SampleRate, 1, 240); _boundsProvider = boundsProvider; _onEvent = onEvent; _onError=onError;
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var fullPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            OutputPath = fullPath;
        }
    }
    internal string? OutputPath { get; }
    internal void Start() => _worker = Task.Run(RunAsync);
    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(1d / _rate);
        var oldPoint = new WindowsNative.Point { X = int.MinValue, Y = int.MinValue };
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                WindowsNative.GetPhysicalCursorPos(out var point);
                var bounds = _boundsProvider();
                if (point.X != oldPoint.X || point.Y != oldPoint.Y)
                {
                    Emit(InputEventKind.Move, point.X, point.Y, MouseButton.None, null, bounds);
                    oldPoint = point;
                }
                foreach (var (code, button) in Buttons) Check(code, () => Emit(_down.Contains(code) ? InputEventKind.MouseDown : InputEventKind.MouseUp, point.X, point.Y, button, null, bounds));
                foreach (var (code, name) in Modifiers) Check(code, () => Emit(_down.Contains(code) ? InputEventKind.KeyDown : InputEventKind.KeyUp, point.X, point.Y, MouseButton.None, name, bounds));
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch(Exception ex) { _onError(ex); }
    }
    private void Check(int code, Action changed)
    {
        var pressed = (GetAsyncKeyState(code) & 0x8000) != 0;
        if (pressed == _down.Contains(code)) return;
        if (pressed) _down.Add(code); else _down.Remove(code);
        changed();
    }
    private void Emit(InputEventKind kind, int x, int y, MouseButton button, string? key, PixelRect bounds)
    {
        var evt = new InputEvent(Stopwatch.GetTimestamp(), kind, x, y,
            bounds.Width <= 1 ? 0 : (double)(x - bounds.X) / bounds.Width,
            bounds.Height <= 1 ? 0 : (double)(y - bounds.Y) / bounds.Height,
            button, key,
            (GetAsyncKeyState(0x10) & 0x8000) != 0, (GetAsyncKeyState(0x11) & 0x8000) != 0,
            (GetAsyncKeyState(0x12) & 0x8000) != 0, (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0);
        _writer?.WriteLine(JsonSerializer.Serialize(evt));
        _onEvent(evt);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_worker is not null) await _worker.ConfigureAwait(false); }
        finally { _writer?.Dispose(); _stop.Dispose(); }
    }
}
