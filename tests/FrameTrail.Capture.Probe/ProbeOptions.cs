using System.Globalization;

internal sealed record ProbeOptions(string? OutputPath, int DurationSeconds, int FrameRate, int? Width, int? Height,
    bool Metrics, bool NoAudio, bool Software, bool FixedFrameRate, bool SyncStimulus, bool SyncCapture, bool SyncPlayback, bool LoopPlayback, bool InteractivePlayback, bool RestoreOwnPlaybackSession, bool RegionPlayback, int RegionX, int RegionY, string? FixturePath, bool TargetTest, bool TrackTest, bool PauseCycle, bool MultiPauseCycle, bool KillRecoveryTest, bool EnumerateInputs, bool LongLoopback, bool AnimateWindow, bool ExclusionTest)
{
    internal static ProbeOptions Parse(string[] args)
    {
        string? output = null; var duration = 5; var fps = 30; int? width = null, height = null; var metrics = false;
        var noAudio = false; var software = false; var fixedFrameRate = false; var syncStimulus = false; var syncCapture = false; var syncPlayback = false; var loopPlayback = false; var interactivePlayback = false; var restoreOwnPlaybackSession = false; var regionPlayback = false; var regionX = 40; var regionY = 40; string? fixture = null; var targetTest = false;
        var trackTest = false; var pauseCycle = false; var multiPauseCycle = false; var killRecoveryTest = false; var enumerateInputs = false; var longLoopback = false; var animateWindow = false; var exclusionTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--metrics") { metrics = true; continue; }
            if (arg == "--no-audio") { noAudio = true; continue; }
            if (arg == "--software") { software = true; continue; }
            if (arg == "--fixed-framerate") { fixedFrameRate = true; continue; }
            if (arg == "--sync-stimulus") { syncStimulus = true; continue; }
            if (arg == "--sync-capture") { syncCapture = true; continue; }
            if (arg == "--sync-playback") { syncPlayback = true; continue; }
            if (arg == "--loop-playback") { loopPlayback = true; continue; }
            if (arg == "--interactive-playback") { interactivePlayback = true; continue; }
            if (arg == "--restore-own-playback-session") { restoreOwnPlaybackSession = true; continue; }
            if (arg == "--region-playback") { regionPlayback = true; continue; }
            if (arg == "--target-test") { targetTest = true; continue; }
            if (arg == "--track-test") { trackTest = true; continue; }
            if (arg == "--pause-cycle") { pauseCycle = true; continue; }
            if (arg == "--multi-pause-cycle") { multiPauseCycle = true; continue; }
            if (arg == "--kill-recovery-test") { killRecoveryTest = true; continue; }
            if (arg == "--enumerate-inputs") { enumerateInputs = true; continue; }
            if (arg == "--long-loopback") { longLoopback = true; continue; }
            if (arg == "--animate-window") { animateWindow = true; continue; }
            if (arg == "--exclusion-test") { exclusionTest = true; continue; }
            if (TryInlineInt(arg, "--duration", out var inlineDuration)) { duration = inlineDuration; continue; }
            if (TryInlineInt(arg, "--fps", out var inlineFps)) { fps = inlineFps; continue; }
            if (TryInlineInt(arg, "--width", out var inlineWidth)) { width = inlineWidth; continue; }
            if (TryInlineInt(arg, "--height", out var inlineHeight)) { height = inlineHeight; continue; }
            if (TryInlineInt(arg, "--region-x", out var inlineRegionX)) { regionX = inlineRegionX; continue; }
            if (TryInlineInt(arg, "--region-y", out var inlineRegionY)) { regionY = inlineRegionY; continue; }
            if (arg.StartsWith("--fps", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(5), out var shorthandFps)) { fps = shorthandFps; continue; }
            if (arg.StartsWith("--width", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(7), out var shorthandWidth)) { width = shorthandWidth; continue; }
            if (arg.StartsWith("--height", StringComparison.Ordinal) && int.TryParse(arg.AsSpan(8), out var shorthandHeight)) { height = shorthandHeight; continue; }
            if (arg == "--output")
            {
                if (output is not null) throw new ArgumentException("Only one output path may be supplied.", nameof(args));
                output = ReadNext(args, ref i, arg); continue;
            }
            if (arg == "--fixture") { fixture = ReadNext(args, ref i, arg); continue; }
            if (arg == "--duration") { duration = ParseNext(args, ref i, arg); continue; }
            if (arg == "--fps") { fps = ParseNext(args, ref i, arg); continue; }
            if (arg == "--width") { width = ParseNext(args, ref i, arg); continue; }
            if (arg == "--height") { height = ParseNext(args, ref i, arg); continue; }
            if (arg == "--region-x") { regionX = ParseNext(args, ref i, arg); continue; }
            if (arg == "--region-y") { regionY = ParseNext(args, ref i, arg); continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown capture probe option: {arg}.", nameof(args));
            if (output is not null) throw new ArgumentException("Only one positional output path may be supplied.", nameof(args));
            output = arg;
        }
        if (duration <= 0 || fps is < 1 or > 120 || width is <= 0 || height is <= 0) throw new ArgumentOutOfRangeException(nameof(args), "Duration, frame rate, and output dimensions must be positive; frame rate is capped at 120.");
        if (interactivePlayback && !syncPlayback) throw new ArgumentException("--interactive-playback requires --sync-playback.", nameof(args));
        if (restoreOwnPlaybackSession && (!interactivePlayback || !syncPlayback || !loopPlayback)) throw new ArgumentException("--restore-own-playback-session requires --sync-playback, --interactive-playback, and --loop-playback.", nameof(args));
        if (restoreOwnPlaybackSession && duration > 30) throw new ArgumentOutOfRangeException(nameof(args), "--restore-own-playback-session is limited to playback samples of 30 seconds or less.");
        return new ProbeOptions(output is null ? null : Path.GetFullPath(output), duration, fps, width, height,
            metrics, noAudio, software, fixedFrameRate, syncStimulus, syncCapture, syncPlayback, loopPlayback, interactivePlayback, restoreOwnPlaybackSession, regionPlayback, regionX, regionY, fixture is null ? null : Path.GetFullPath(fixture), targetTest, trackTest, pauseCycle, multiPauseCycle, killRecoveryTest, enumerateInputs, longLoopback, animateWindow, exclusionTest);
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
        if (args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value after {flag}; next argument is another option.");
        return args[index];
    }
}
