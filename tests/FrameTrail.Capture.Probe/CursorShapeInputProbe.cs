using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using FrameTrail.Capture;

internal static class CursorShapeInputProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task RunAsync(string outputPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        outputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".input.jsonl");
        var summaryPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(outputPath) + ".summary.json");
        foreach (var path in new[] { outputPath, inputPath, summaryPath })
            if (File.Exists(path)) throw new IOException($"Refusing to overwrite existing probe output: {path}");

        await using var capture = new FrameCaptureService();
        var displays = await capture.EnumerateDisplaysAsync();
        var display = displays.FirstOrDefault(x => x.IsPrimary) ?? throw new InvalidOperationException("No primary display was found.");
        var fixture = await CursorShapeFixture.StartAsync(display.Bounds);
        RecordingResult? result = null;
        try
        {
            // Put the pointer inside our own topmost fixture before recording so all three shape samples share one point.
            await fixture.SetCursorAsync(Cursors.Arrow, movePointer: true);
            await Task.Delay(350);
            await capture.StartAsync(new RecordingRequest(outputPath,
                new CaptureTarget(CaptureTargetKind.Region, display.Id, fixture.WindowBounds),
                new VideoOptions(FrameRate: 30), new AudioOptions(), new InputTrackingOptions(true, 120, inputPath)));
            await Task.Delay(450);
            await fixture.SetCursorAsync(Cursors.Arrow);
            await Task.Delay(550);
            await fixture.SetCursorAsync(Cursors.Hand);
            await Task.Delay(650);
            await fixture.SetCursorAsync(Cursors.IBeam);
            await Task.Delay(650);
            result = await capture.StopAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cursor shape probe failed: " + ex);
            if (capture.State is CaptureState.Recording or CaptureState.Paused or CaptureState.Faulted)
            {
                try { result = await capture.StopAsync(); }
                catch (Exception stopError) { Console.Error.WriteLine("Stop after probe error: " + stopError); }
            }
            throw;
        }
        finally { await fixture.DisposeAsync(); }

        if (result is null) throw new InvalidOperationException("Capture completed without a RecordingResult.");
        if (result.InputEventsPath is null || !File.Exists(result.InputEventsPath)) throw new InvalidDataException("Input sampler did not create its JSONL output.");
        var events = File.ReadLines(result.InputEventsPath).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<InputEvent>(line, JsonOptions) ?? throw new InvalidDataException("Invalid input event JSONL row."))
            .ToArray();
        var wanted = new[] { "system.arrow", "system.hand", "system.ibeam" };
        var cursorPoint = fixture.CursorPoint;
        var shapeEvents = events.Where(x => x.Kind == InputEventKind.Move && x.X == cursorPoint.X && x.Y == cursorPoint.Y && wanted.Contains(x.CursorAsset, StringComparer.Ordinal)).ToArray();
        var firstByShape = wanted.Select(id => shapeEvents.FirstOrDefault(x => x.CursorAsset == id)
            ?? throw new InvalidOperationException($"The input log did not capture {id}. Events: {string.Join(", ", shapeEvents.Select(x => x.CursorAsset))}"))
            .ToArray();
        if (firstByShape.Select(x => (x.X, x.Y)).Distinct().Count() != 1)
            throw new InvalidOperationException("Cursor shape changes were not recorded at a stationary physical coordinate.");
        if (new FileInfo(outputPath).Length < 1024) throw new InvalidDataException("The region capture did not produce a usable MP4.");
        var mediaInfo = await ProbeVideoAsync(outputPath);
        if (mediaInfo.Width <= 0 || mediaInfo.Height <= 0 || mediaInfo.Duration < 1 || mediaInfo.FrameCount < 1)
            throw new InvalidDataException("ffprobe did not find a decodable video stream in the fixture capture.");
        if (mediaInfo.ColorSpace != "bt709" || mediaInfo.ColorTransfer != "iec61966-2-1" || mediaInfo.ColorPrimaries != "bt709" || mediaInfo.ColorRange != "tv")
            throw new InvalidDataException($"Captured video color tags were unexpected: colorspace={mediaInfo.ColorSpace}, transfer={mediaInfo.ColorTransfer}, primaries={mediaInfo.ColorPrimaries}, range={mediaInfo.ColorRange}.");
        var decodedFrame = await DecodeBgraFrameAsync(outputPath, mediaInfo.Width, mediaInfo.Height, 1.5);
        var colorMeasurements = fixture.ColorCards.Select(card => MeasureColorCard(decodedFrame, mediaInfo.Width, mediaInfo.Height, fixture.WindowBounds, card)).ToArray();
        var maxColorChannelDeviation = colorMeasurements.Max(x => x.MaxChannelDeviation);
        var colorsPassed = colorMeasurements.All(x => x.MaxChannelDeviation <= 8);

        var summary = new
        {
            Passed = colorsPassed,
            ColorCardsPassed = colorsPassed,
            ColorChannelDeviationThreshold = 8,
            MaxColorChannelDeviation = maxColorChannelDeviation,
            ColorMeasurement = "One decoded frame at 1.5s; each RGB value is the rounded average of the center 5x5 BGRA pixels. Deviation is the maximum absolute R/G/B difference from the fixture RGB value, in 0..255 channel levels.",
            ColorCards = colorMeasurements,
            OutputPath = outputPath,
            InputEventsPath = result.InputEventsPath,
            InputEventCount = events.Length,
            StationaryScreenPoint = new { firstByShape[0].X, firstByShape[0].Y },
            ShapeEvents = firstByShape.Select(x => new { x.CursorAsset, x.StopwatchTicks, x.X, x.Y, x.NormalizedX, x.NormalizedY }),
            Video = mediaInfo,
            Recording = new { result.Width, result.Height, result.FrameRate, result.EncoderName, result.FirstFrameNumber },
            FixtureRect = fixture.WindowBounds
        };
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        if (!colorsPassed) throw new InvalidDataException($"Color-card pixel verification failed: maximum channel deviation {maxColorChannelDeviation}/255 exceeds 8/255.");
    }

    private static async Task<VideoInfo> ProbeVideoAsync(string path)
    {
        var executable = ResolveFfprobe() ?? throw new FileNotFoundException("ffprobe.exe was not found on PATH or in the Gyan WinGet package.");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,width,height,nb_read_frames,duration,color_space,color_transfer,color_primaries,color_range", "-of", "json", path }
        }) ?? throw new InvalidOperationException("ffprobe.exe did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidDataException($"ffprobe failed: {stderr}");
        using var document = JsonDocument.Parse(stdout);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
        if (stream.ValueKind != JsonValueKind.Object) throw new InvalidDataException("ffprobe returned no video stream.");
        return new VideoInfo(
            GetInt(stream, "width"), GetInt(stream, "height"), GetDouble(stream, "duration"), GetInt(stream, "nb_read_frames"),
            stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null,
            GetString(stream, "color_space"), GetString(stream, "color_transfer"), GetString(stream, "color_primaries"), GetString(stream, "color_range"));
    }

    private static int GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    private static double GetDouble(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static async Task<byte[]> DecodeBgraFrameAsync(string path, int width, int height, double timeSeconds)
    {
        var ffprobe = ResolveFfprobe() ?? throw new FileNotFoundException("ffprobe.exe was not found on PATH or in the Gyan WinGet package.");
        var configured = Environment.GetEnvironmentVariable("FRAMETRAIL_FFMPEG");
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "ffmpeg.exe")).FirstOrDefault(File.Exists);
        var executable = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured
            : fromPath ?? Path.Combine(Path.GetDirectoryName(ffprobe)!, "ffmpeg.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("ffmpeg.exe was not found on PATH or beside ffprobe.exe.", executable);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-ss", timeSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", path,
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }
        }) ?? throw new InvalidOperationException("ffmpeg.exe did not start.");
        var pixels = new byte[checked(width * height * 4)];
        var stderrTask = process.StandardError.ReadToEndAsync();
        var offset = 0;
        while (offset < pixels.Length)
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(offset));
            if (read == 0) break;
            offset += read;
        }
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || offset != pixels.Length)
            throw new InvalidDataException($"ffmpeg failed to decode a {width}x{height} BGRA frame at {timeSeconds}s (bytes={offset}): {stderr}");
        return pixels;
    }

    private static ColorCardMeasurement MeasureColorCard(byte[] frame, int frameWidth, int frameHeight, PixelRect region, ColorCard card)
    {
        var centerScreenX = card.ScreenBounds.X + card.ScreenBounds.Width / 2d;
        var centerScreenY = card.ScreenBounds.Y + card.ScreenBounds.Height / 2d;
        var centerX = (int)Math.Round((centerScreenX - region.X) * frameWidth / region.Width - .5, MidpointRounding.AwayFromZero);
        var centerY = (int)Math.Round((centerScreenY - region.Y) * frameHeight / region.Height - .5, MidpointRounding.AwayFromZero);
        if (centerX < 2 || centerY < 2 || centerX + 2 >= frameWidth || centerY + 2 >= frameHeight)
            throw new InvalidDataException($"Color card '{card.Name}' center maps outside the captured frame: ({centerX},{centerY}).");
        long red = 0, green = 0, blue = 0;
        for (var y = centerY - 2; y <= centerY + 2; y++)
        for (var x = centerX - 2; x <= centerX + 2; x++)
        {
            var offset = (y * frameWidth + x) * 4;
            blue += frame[offset]; green += frame[offset + 1]; red += frame[offset + 2];
        }
        var actual = Color.FromArgb((int)Math.Round(red / 25d), (int)Math.Round(green / 25d), (int)Math.Round(blue / 25d));
        var deviation = Math.Max(Math.Abs(actual.R - card.Expected.R), Math.Max(Math.Abs(actual.G - card.Expected.G), Math.Abs(actual.B - card.Expected.B)));
        return new ColorCardMeasurement(card.Name, card.Expected.R, card.Expected.G, card.Expected.B, actual.R, actual.G, actual.B,
            Math.Abs(actual.R - card.Expected.R), Math.Abs(actual.G - card.Expected.G), Math.Abs(actual.B - card.Expected.B), deviation,
            new { card.ScreenBounds.X, card.ScreenBounds.Y, card.ScreenBounds.Width, card.ScreenBounds.Height }, new { X = centerX, Y = centerY });
    }

    private static string? ResolveFfprobe()
    {
        var configured = Environment.GetEnvironmentVariable("FRAMETRAIL_FFPROBE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "ffprobe.exe")).FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winget)) return null;
        return Directory.GetDirectories(winget, "Gyan.FFmpeg*").SelectMany(package => Directory.GetDirectories(package, "ffmpeg-*-full_build"))
            .Select(folder => Path.Combine(folder, "bin", "ffprobe.exe")).FirstOrDefault(File.Exists);
    }

    private sealed record VideoInfo(int Width, int Height, double Duration, int FrameCount, string? Codec,
        string? ColorSpace, string? ColorTransfer, string? ColorPrimaries, string? ColorRange);
    internal sealed record ColorCard(string Name, Color Expected, Rectangle ClientBounds, PixelRect ScreenBounds);
    private sealed record ColorCardMeasurement(string Name, int ExpectedR, int ExpectedG, int ExpectedB, int ActualR, int ActualG, int ActualB,
        int RedDeviation, int GreenDeviation, int BlueDeviation, int MaxChannelDeviation, object ScreenRect, object VideoCenter);

    private sealed class CursorShapeFixture : IAsyncDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<CursorShapeFixture> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Form? _form;
        private Panel? _surface;
        private PixelRect _windowBounds = new(0, 0, 0, 0);
        private Point _cursorPoint;
        private ColorCard[] _colorCards = [];

        private CursorShapeFixture(PixelRect displayBounds)
        {
            _thread = new Thread(() => Run(displayBounds)) { IsBackground = true, Name = "FrameTrail controlled cursor fixture" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal PixelRect WindowBounds => _windowBounds;
        internal Point CursorPoint => _cursorPoint;
        internal IReadOnlyList<ColorCard> ColorCards => _colorCards;

        internal static async Task<CursorShapeFixture> StartAsync(PixelRect displayBounds)
        {
            var fixture = new CursorShapeFixture(displayBounds);
            try { return await fixture._ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private void Run(PixelRect displayBounds)
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                _form = new Form
                {
                    Text = "FrameTrail 光标形态采集验证",
                    FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(displayBounds.X + 48, displayBounds.Y + 48, 440, 240),
                    TopMost = true,
                    ShowInTaskbar = false,
                    BackColor = Color.FromArgb(34, 37, 43),
                    Cursor = Cursors.Arrow
                };
                _surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(34, 37, 43), Cursor = Cursors.Arrow };
                var swatches = new (string Name, Color Color)[]
                {
                    ("red", Color.FromArgb(255, 0, 0)), ("green", Color.FromArgb(0, 255, 0)), ("blue", Color.FromArgb(0, 0, 255)),
                    ("gray", Color.FromArgb(128, 128, 128)), ("white", Color.FromArgb(255, 255, 255)),
                    ("skin", Color.FromArgb(224, 178, 146)), ("black", Color.FromArgb(0, 0, 0))
                };
                _surface.Paint += (_, e) =>
                {
                    e.Graphics.DrawString("FrameTrail 受控采集验证 · 光标固定点切换与 SDR 色卡", SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, Brushes.White, 12, 12);
                    foreach (var card in _colorCards)
                    {
                        using var brush = new SolidBrush(card.Expected);
                        e.Graphics.FillRectangle(brush, card.ClientBounds);
                        e.Graphics.DrawRectangle(Pens.White, card.ClientBounds);
                    }
                };
                _form.Controls.Add(_surface);
                _form.Shown += (_, _) =>
                {
                    try
                    {
                        if (!GetWindowRect(_form.Handle, out var rect) || !GetClientRect(_surface.Handle, out var client))
                            throw new InvalidOperationException("Could not read fixture physical bounds.");
                        _windowBounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                        const int swatchWidth = 44, swatchHeight = 30, gap = 10;
                        var startX = Math.Max(8, (_surface.ClientSize.Width - (swatches.Length * swatchWidth + (swatches.Length - 1) * gap)) / 2);
                        var swatchY = Math.Max(8, _surface.ClientSize.Height - swatchHeight - 14);
                        _colorCards = swatches.Select((item, index) => new ColorCard(item.Name, item.Color,
                            new Rectangle(startX + index * (swatchWidth + gap), swatchY, swatchWidth, swatchHeight), new PixelRect(0, 0, 0, 0))).ToArray();
                        var center = new Point(client.Right / 2, client.Bottom / 2);
                        if (!ClientToScreen(_surface.Handle, ref center)) throw new InvalidOperationException("Could not locate the fixture cursor point.");
                        _cursorPoint = center;
                        _colorCards = _colorCards.Select(card =>
                        {
                            var topLeft = new Point(card.ClientBounds.Left, card.ClientBounds.Top);
                            var bottomRight = new Point(card.ClientBounds.Right, card.ClientBounds.Bottom);
                            if (!ClientToScreen(_surface.Handle, ref topLeft) || !ClientToScreen(_surface.Handle, ref bottomRight))
                                throw new InvalidOperationException($"Could not map color card '{card.Name}' to physical screen coordinates.");
                            return card with { ScreenBounds = new PixelRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y) };
                        }).ToArray();
                        _surface.Invalidate();
                        _ready.TrySetResult(this);
                    }
                    catch (Exception ex) { _ready.TrySetException(ex); _form.Close(); }
                };
                _form.FormClosed += (_, _) => _closed.TrySetResult(true);
                Application.Run(_form);
                _closed.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                _closed.TrySetResult(true);
            }
        }

        internal Task SetCursorAsync(Cursor cursor, bool movePointer = false)
        {
            var form = _form ?? throw new InvalidOperationException("Fixture window is not ready.");
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            form.BeginInvoke(new Action(() =>
            {
                try
                {
                    _surface!.Cursor = cursor; _form!.Cursor = cursor; Cursor.Current = cursor;
                    if (movePointer && !SetCursorPos(_cursorPoint.X, _cursorPoint.Y)) throw new InvalidOperationException("SetCursorPos failed.");
                    SetCursor(cursor.Handle);
                    // Ask Windows to re-run hit testing after changing the fixture's cursor; return to the identical physical point before sampling.
                    if (!movePointer)
                    {
                        SetCursorPos(_cursorPoint.X + 1, _cursorPoint.Y);
                        SetCursorPos(_cursorPoint.X, _cursorPoint.Y);
                        SetCursor(cursor.Handle);
                    }
                    completion.TrySetResult(true);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }));
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            if (_form is { IsDisposed: false } form && form.IsHandleCreated)
            {
                try { form.BeginInvoke(new Action(form.Close)); }
                catch (InvalidOperationException) { }
            }
            await _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    }
}
