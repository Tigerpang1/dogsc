using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

internal static class AudioSourceRateProbe
{
    private const int OutputSampleRate = 48_000;
    private const int IntermediateSampleRate = 192_000;

    public static async Task RunAsync(string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Output path must be fresh: {outputDirectory}");
        Directory.CreateDirectory(outputDirectory);

        var source = Path.Combine(outputDirectory, "pulse-train.flac");
        await RunFfmpeg(["-v", "error", "-f", "lavfi", "-i", "aevalsrc=0.8*eq(mod(n\\,240000)\\,0):s=48000:d=1801", "-c:a", "flac", "-y", source]);

        var results = new List<object>();
        foreach (var duration in new[] { 30d, 1800d })
        foreach (var ppm in new[] { 0, 30, -30, 100, -100, 500, -500 })
        {
            var sourceRate = 1 + ppm / 1_000_000d;
            results.Add(await RenderAndMeasure(source, outputDirectory, duration, sourceRate, playbackRate: 1));
        }

        // A user-requested speed change remains pitch-preserving atempo, while the
        // capture-clock correction remains a separate, sub-ppm pitch shift.
        var userRateResult = await RenderAndMeasure(source, outputDirectory, 30, 1.00003, playbackRate: 2);
        results.Add(userRateResult);

        var mappedRateFilter = MediaTools.AudioTrackFilter(
            new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 2 }] },
            [new AudioTrack { MediaPath = source, SourceStart = 0, Duration = 2, SourceRate = .5,
                SyncSegments = [new CameraSyncSegment(0, 0, 2, .5)] }]);
        if (!mappedRateFilter.Contains("atempo=0.5", StringComparison.Ordinal)
            || mappedRateFilter.Contains("asetrate=", StringComparison.Ordinal))
            throw new InvalidOperationException("Meaningful media-map SourceRate must remain pitch-preserving atempo, outside the device-clock correction band.");

        var boundaryChecks = new Dictionary<string, bool>();
        foreach (var (label, rate, shouldCorrectClock) in new[]
        {
            ("inside-positive-bound", 1.001, true),
            ("outside-positive-bound", 1.001001, false),
            ("inside-negative-bound", .999, true),
            ("outside-negative-bound", .998999, false)
        })
        {
            var filter = MediaTools.AudioTrackFilter(
                new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 2 }] },
                [new AudioTrack { MediaPath = source, SourceStart = 0, Duration = 2, SourceRate = rate,
                    SyncSegments = [new CameraSyncSegment(0, 0, 2, rate)] }]);
            var hasResamplingCorrection = filter.Contains("asetrate=", StringComparison.Ordinal);
            var hasPitchPreservingTempo = filter.Contains("atempo=", StringComparison.Ordinal);
            if (hasResamplingCorrection != shouldCorrectClock || hasPitchPreservingTempo == shouldCorrectClock)
                throw new InvalidOperationException($"Unexpected SourceRate routing at {label} ({rate:R}).");
            boundaryChecks[label] = true;
        }

        var unityTimeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 2 }] };
        var unityMainAudioFilter = MediaTools.AudioFilter(unityTimeline);
        if (unityMainAudioFilter.Contains("atempo=", StringComparison.Ordinal))
            throw new InvalidOperationException("Unity-speed main audio must bypass atempo=1.");
        var pitchControl = await VerifyUserTempoKeepsPitch(outputDirectory);

        var report = new
        {
            Type = "source-clock-resampling-pulse-probe",
            Synthetic = true,
            Source = source,
            SourceRateMeaning = "asset seconds consumed per screen-source second",
            UserPlaybackRateMeaning = "timeline source seconds consumed per output second; handled separately by pitch-preserving atempo",
            ClockCorrection = "aresample=192000,asetrate=round(192000*SourceRate),aresample=48000",
            ClockCorrectionBandLimitPpm = 1_000,
            ClockRateQuantizationMaximumResidualPpm = 1_000_000d / (2 * IntermediateSampleRate),
            OutputSampleRate,
            DurationsSeconds = new[] { 30, 1800 },
            RequestedClockOffsetsPpm = new[] { 0, 30, -30, 100, -100, 500, -500 },
            UserRateControl = "SourceRate=1.00003, PlaybackRate=2",
            MeaningfulMappingRateControl = new { SourceRate = .5, UsesPitchPreservingAtempo = true, Filter = mappedRateFilter },
            RoutingBoundaryChecks = boundaryChecks,
            UnityMainAudioBypassesAtempo = true,
            UserTempoPitchControl = pitchControl,
            Results = results
        };
        var reportPath = Path.Combine(outputDirectory, "source-rate-pulse-probe.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"通过：SourceRate 的 30s/1800s 脉冲与帧数检查完成；报告：{reportPath}");
    }

    private static async Task<object> RenderAndMeasure(string source, string outputDirectory, double duration, double sourceRate, double playbackRate)
    {
        var timeline = new ProjectTimeline
        {
            Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = duration, PlaybackRate = playbackRate }]
        };
        var track = new AudioTrack
        {
            MediaPath = source,
            SourceStart = 0,
            Duration = duration,
            OffsetSeconds = 0,
            SourceRate = sourceRate,
            SyncSegments = [new CameraSyncSegment(0, 0, duration, sourceRate)]
        };
        var filter = MediaTools.AudioTrackFilter(timeline, [track]);
        var sourceRateHz = checked((int)Math.Round(IntermediateSampleRate * sourceRate, MidpointRounding.AwayFromZero));
        if (playbackRate == 1 && Math.Abs(sourceRate - 1) < 1e-12)
        {
            if (filter.Contains("atempo=", StringComparison.Ordinal) || filter.Contains("asetrate=", StringComparison.Ordinal))
                throw new InvalidOperationException("Unity source clock and user rate must bypass tempo and rate correction filters.");
        }
        else if (sourceRateHz != IntermediateSampleRate && !filter.Contains($"asetrate={sourceRateHz}", StringComparison.Ordinal))
            throw new InvalidOperationException("Production filter did not apply the expected source-clock rate correction.");

        if (playbackRate != 1 && !filter.Contains("atempo=2", StringComparison.Ordinal))
            throw new InvalidOperationException("User-requested 2x playback must remain pitch-preserving atempo.");

        var script = Path.Combine(outputDirectory, "source-rate-filter.txt");
        await File.WriteAllTextAsync(script, filter);
        var pcmPath = Path.Combine(outputDirectory, "source-rate-render.s16le");
        try
        {
            await RunFfmpeg(["-v", "error", "-i", source, "-filter_complex_script", script, "-map", "[out]", "-ac", "1", "-ar", OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "-y", pcmPath]);
            var frames = new FileInfo(pcmPath).Length / sizeof(short);
            var expectedDuration = duration / playbackRate;
            var durationErrorMs = (frames / (double)OutputSampleRate - expectedDuration) * 1000;
            if (Math.Abs(durationErrorMs) > 1)
                throw new InvalidOperationException($"Rendered duration error {durationErrorMs:0.###}ms exceeds 1ms.");

            var pulseMeasurement = MeasurePulseDeltas(pcmPath, frames, duration, sourceRate, playbackRate);
            var pulseDeltas = pulseMeasurement.Errors;
            var maxPulseErrorMs = pulseDeltas.Count == 0 ? 0 : pulseDeltas.Max(Math.Abs);
            var clockPulseLimitMs = duration >= 1800 ? 5 : 1;
            var intervalErrors = pulseDeltas.Zip(pulseDeltas.Skip(1), (previous, current) => current - previous).ToArray();
            var maxIntervalErrorMs = intervalErrors.Length == 0 ? 0 : intervalErrors.Max(Math.Abs);
            if (pulseMeasurement.MinimumPeakMagnitude <= 0)
                throw new InvalidOperationException("A synthetic pulse window was all zero; the clock probe cannot pass without signal.");
            if (playbackRate == 1 && maxPulseErrorMs > clockPulseLimitMs)
                throw new InvalidOperationException($"Clock-correction pulse error {maxPulseErrorMs:0.###}ms exceeds {clockPulseLimitMs}ms for {duration:0}s at SourceRate={sourceRate:R}.");
            if (playbackRate != 1 && maxIntervalErrorMs > 10)
                throw new InvalidOperationException($"User-tempo pulse interval error {maxIntervalErrorMs:0.###}ms exceeds 10ms; absolute atempo onset offset is recorded separately.");

            return new
            {
                DurationSeconds = duration,
                SourceRate = sourceRate,
                RequestedSourceClockOffsetPpm = (sourceRate - 1) * 1_000_000,
                IntermediateRate = IntermediateSampleRate,
                QuantizedIntermediateRate = sourceRateHz,
                ResidualRatePpm = (sourceRateHz / (double)IntermediateSampleRate / sourceRate - 1) * 1_000_000,
                PlaybackRate = playbackRate,
                OutputFrames = frames,
                OutputDurationSeconds = frames / (double)OutputSampleRate,
                DurationErrorMilliseconds = durationErrorMs,
                PulseCount = pulseDeltas.Count,
                PulsePeakMinimumMagnitude = pulseMeasurement.MinimumPeakMagnitude,
                PulseTimingMaxAbsErrorMilliseconds = maxPulseErrorMs,
                PulseTimingMedianErrorMilliseconds = Median(pulseDeltas),
                PulseTimingFirstErrorMilliseconds = pulseDeltas.FirstOrDefault(),
                PulseTimingLastErrorMilliseconds = pulseDeltas.LastOrDefault(),
                PulseIntervalMaxAbsErrorMilliseconds = maxIntervalErrorMs,
                ClockPulseTimingGateMilliseconds = playbackRate == 1 ? clockPulseLimitMs : (double?)null,
                UserTempoIntervalGateMilliseconds = playbackRate == 1 ? (double?)null : 10,
                AbsoluteUserTempoOnsetIsDiagnosticOnly = playbackRate != 1,
                ProductionFilter = filter
            };
        }
        finally
        {
            if (File.Exists(pcmPath)) File.Delete(pcmPath);
            if (File.Exists(script)) File.Delete(script);
        }
    }

    private sealed record PulseMeasurement(List<double> Errors, int MinimumPeakMagnitude);

    private static async Task<object> VerifyUserTempoKeepsPitch(string outputDirectory)
    {
        var source = Path.Combine(outputDirectory, "user-tempo-440hz.wav");
        var pcmPath = Path.Combine(outputDirectory, "user-tempo-440hz.s16le");
        var script = Path.Combine(outputDirectory, "user-tempo-filter.txt");
        try
        {
            await RunFfmpeg(["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6.1", "-c:a", "pcm_s16le", "-y", source]);
            var timeline = new ProjectTimeline { Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = 6, PlaybackRate = 2 }] };
            var track = new AudioTrack { MediaPath = source, SourceStart = 0, Duration = 6, SourceRate = 1.00003,
                SyncSegments = [new CameraSyncSegment(0, 0, 6, 1.00003)] };
            var filter = MediaTools.AudioTrackFilter(timeline, [track]);
            if (!filter.Contains("atempo=2", StringComparison.Ordinal) || !filter.Contains("asetrate=192006", StringComparison.Ordinal))
                throw new InvalidOperationException("The 2x pitch control must combine the user tempo filter and the separate tiny clock correction.");
            await File.WriteAllTextAsync(script, filter);
            await RunFfmpeg(["-v", "error", "-i", source, "-filter_complex_script", script, "-map", "[out]", "-ac", "1", "-ar", OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "-y", pcmPath]);

            using var stream = File.OpenRead(pcmPath);
            using var reader = new BinaryReader(stream);
            const int startFrame = OutputSampleRate;
            const int count = OutputSampleRate / 2;
            stream.Position = startFrame * sizeof(short);
            var previous = reader.ReadInt16();
            var crossings = 0;
            for (var i = 1; i < count; i++)
            {
                var current = reader.ReadInt16();
                if (previous <= 0 && current > 0) crossings++;
                previous = current;
            }
            var frequency = crossings / .5;
            if (Math.Abs(frequency - 440) > 2)
                throw new InvalidOperationException($"User 2x playback changed the 440Hz control tone to {frequency:0.##}Hz.");
            return new { InputFrequencyHz = 440, MeasuredOutputFrequencyHz = frequency, UserPlaybackRate = 2, SourceRate = 1.00003, Filter = filter };
        }
        finally
        {
            foreach (var path in new[] { source, pcmPath, script })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static PulseMeasurement MeasurePulseDeltas(string path, long frames, double duration, double sourceRate, double playbackRate)
    {
        var deltas = new List<double>();
        var minimumPeakMagnitude = int.MaxValue;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        for (var k = 1; k * 5 < duration * sourceRate - .01; k++)
        {
            var expectedSeconds = k * 5 / sourceRate / playbackRate;
            if (expectedSeconds >= duration / playbackRate - .1) continue;
            var center = (long)Math.Round(expectedSeconds * OutputSampleRate);
            var radius = OutputSampleRate / 20; // +/- 50ms around each known synthetic pulse.
            var start = Math.Max(0, center - radius);
            var end = Math.Min(frames, center + radius + 1);
            if (end <= start) continue;
            stream.Position = start * sizeof(short);
            var maxMagnitude = -1;
            var maxFrame = start;
            for (var frame = start; frame < end; frame++)
            {
                var magnitude = Math.Abs((int)reader.ReadInt16());
                if (magnitude <= maxMagnitude) continue;
                maxMagnitude = magnitude;
                maxFrame = frame;
            }
            if (maxMagnitude <= 0)
                throw new InvalidOperationException($"Expected synthetic pulse {k} at {expectedSeconds:0.###}s is absent from the output.");
            minimumPeakMagnitude = Math.Min(minimumPeakMagnitude, maxMagnitude);
            var observedSeconds = maxFrame / (double)OutputSampleRate;
            deltas.Add((observedSeconds - expectedSeconds) * 1000);
        }
        if (deltas.Count == 0) throw new InvalidOperationException("No synthetic pulses were found in the rendered range.");
        return new(deltas, minimumPeakMagnitude);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static Task RunFfmpeg(IReadOnlyList<string> arguments) => MediaTools.RunAsync(MediaTools.Ffmpeg, arguments);
}
