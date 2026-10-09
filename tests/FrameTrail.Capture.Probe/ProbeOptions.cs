using System.Globalization;

internal sealed record ProbeOptions(string? OutputPath, int DurationSeconds, int FrameRate, int? Width, int? Height, bool Metrics, bool NoAudio, bool Software)
{
    internal static ProbeOptions Parse(string[] args)
    {
        string? output = null; var duration = 5; var fps = 30; int? width = null, height = null; var metrics = false; var noAudio = false; var software = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--metrics") { metrics = true; continue; }
            if (arg == "--no-audio") { noAudio = true; continue; }
            if (arg == "--software") { software = true; continue; }
            if (TryInlineInt(arg, "--duration", out var inlineDuration)) { duration = inlineDuration; continue; }
            if (TryInlineInt(arg, "--fps", out var inlineFps)) { fps = inlineFps; continue; }
            if (TryInlineInt(arg, "--width", out var inlineWidth)) { width = inlineWidth; continue; }
            if (TryInlineInt(arg, "--height", out var inlineHeight)) { height = inlineHeight; continue; }
            if (arg.StartsWith("--fps", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(5), out var shorthandFps)) { fps = shorthandFps; continue; }
            if (arg.StartsWith("--width", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(7), out var shorthandWidth)) { width = shorthandWidth; continue; }
            if (arg.StartsWith("--height", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(8), out var shorthandHeight)) { height = shorthandHeight; continue; }
            if (arg == "--output") { output = ReadNext(args, ref i, arg); continue; }
            if (arg == "--duration") { duration = ParseNext(args, ref i, arg); continue; }
            if (arg == "--fps") { fps = ParseNext(args, ref i, arg); continue; }
            if (arg == "--width") { width = ParseNext(args, ref i, arg); continue; }
            if (arg == "--height") { height = ParseNext(args, ref i, arg); continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal) && output is null) output = arg;
        }
        if (duration <= 0 || fps is < 1 or > 120 || width is <= 0 || height is <= 0) throw new ArgumentOutOfRangeException(nameof(args), "Duration, frame rate, and output dimensions must be positive; frame rate is capped at 120.");
        return new ProbeOptions(output is null ? null : Path.GetFullPath(output), duration, fps, width, height, metrics, noAudio, software);
    }
    private static bool TryInlineInt(string value, string prefix, out int number)
    {
        number = 0;
        if (!value.StartsWith(prefix + "=", StringComparison.Ordinal)) return false;
        return int.TryParse(value.AsSpan(prefix.Length + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }
    private static int ParseNext(string[] args, ref int index, string flag) => int.Parse(ReadNext(args, ref index, flag), CultureInfo.InvariantCulture);
    private static string ReadNext(string[] args, ref int index, string flag)
    {
        if (++index >= args.Length) throw new ArgumentException($"Missing value after {flag}.");
        return args[index];
    }
}
