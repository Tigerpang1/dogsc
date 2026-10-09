using System.Diagnostics;
using System.Text.Json;
using FrameTrail.Capture;

internal static class MicrophoneCaptureProbe
{
    private static readonly TimeSpan TotalWallDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirstActiveDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PausedDuration = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(string outputDirectoryArgument)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var outputDirectory = Path.GetFullPath(outputDirectoryArgument);
        var artifactsRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts"));
        if (!outputDirectory.StartsWith(artifactsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Microphone probe output must be a new directory under '{artifactsRoot}'.", nameof(outputDirectoryArgument));
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Refusing to reuse or overwrite microphone probe output: {outputDirectory}");

        await using var tracks = new CaptureTrackService();
        var devices = await tracks.EnumerateAudioDevicesAsync();
        var microphones = devices.Where(device => device.Kind == AudioDeviceKind.Microphone && device.IsDefault).ToArray();
        if (microphones.Length != 1)
            throw new InvalidOperationException($"Expected exactly one enumerated default microphone, found {microphones.Length}. No microphone stream was opened.");

        var microphone = microphones[0];
        Directory.CreateDirectory(outputDirectory);
        var wavPath = Path.Combine(outputDirectory, "default-microphone.wav");
        var summaryPath = Path.Combine(outputDirectory, "microphone-capture.summary.json");
        DateTimeOffset? startedUtc = null;
        Console.WriteLine($"Selected enumerated default microphone: {microphone.Name}; id={microphone.Id}; kind={microphone.Kind}; isDefault={microphone.IsDefault}.");
        Console.WriteLine("Capturing only this microphone for 15 seconds wall time; pause schedule is 5s active, 2s paused, then active through stop. No system loopback or audio playback is used.");

        CaptureTrackHandle? handle = null;
        CaptureTrackResult? result = null;
        var watch = new Stopwatch();
        long? pauseRequestedTicks = null;
        long? resumeRequestedTicks = null;
        try
        {
            handle = await tracks.StartAudioTrackAsync(new AudioTrackRequest(wavPath, AudioDeviceKind.Microphone, microphone.Id));
            startedUtc = DateTimeOffset.UtcNow;
            watch.Start();
            await DelayUntilAsync(watch, FirstActiveDuration);
            pauseRequestedTicks = Stopwatch.GetTimestamp();
            await tracks.PauseTrackAsync(handle.TrackId);
            await Task.Delay(PausedDuration);
            resumeRequestedTicks = Stopwatch.GetTimestamp();
            await tracks.ResumeTrackAsync(handle.TrackId);
            await DelayUntilAsync(watch, TotalWallDuration);
            result = await tracks.StopTrackAsync(handle.TrackId);
            handle = null;

            var wave = ReadWaveStatistics(wavPath);
            var validation = ValidateCapture(result, wave);
            if (!validation.Passed)
                throw new InvalidDataException("Microphone capture media did not meet metadata/duration gates: " +
                    JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true }));
            var capture = new
            {
                Status = "completed",
                PipelinePassed = true,
                SignalObserved = wave.NonZeroSamples > 0,
                SignalStatus = wave.NonZeroSamples > 0 ? "nonzero-samples-observed; signal quality/content not classified" : "all-zero-samples",
                Device = microphone,
                OutputPath = wavPath,
                StartedUtc = startedUtc,
                StoppedUtc = DateTimeOffset.UtcNow,
                WallDurationSeconds = watch.Elapsed.TotalSeconds,
                RequestedWallDurationSeconds = TotalWallDuration.TotalSeconds,
                PauseRequestedStopwatchTicks = pauseRequestedTicks,
                ResumeRequestedStopwatchTicks = resumeRequestedTicks,
                StopwatchFrequency = Stopwatch.Frequency,
                Result = result,
                Wave = wave,
                Validation = validation,
                Notes = new[]
                {
                    "NonzeroSamplesObserved reports numeric non-zero WAV samples only; it does not classify speech or audio content.",
                    "A silent WAV is reported as all-zero-samples and does not change microphone or endpoint mute/gain settings."
                }
            };
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            var failure = new
            {
                Status = "failed",
                Error = ex.ToString(),
                Device = microphone,
                OutputPath = wavPath,
                ExistingWaveFileRetained = File.Exists(wavPath),
                Result = result,
                PauseRequestedStopwatchTicks = pauseRequestedTicks,
                ResumeRequestedStopwatchTicks = resumeRequestedTicks,
                WallDurationSeconds = watch.Elapsed.TotalSeconds
            };
            try { await File.WriteAllTextAsync(Path.Combine(outputDirectory, "microphone-capture.failure.json"), JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception reportException) { Console.Error.WriteLine($"Could not write microphone probe failure report: {reportException.Message}"); }
            throw;
        }
        finally
        {
            if (handle is not null)
            {
                try { await tracks.StopTrackAsync(handle.TrackId); }
                catch (Exception stopException) { Console.Error.WriteLine($"Could not finalize microphone WAV after probe failure: {stopException}"); }
            }
        }
    }

    private static async Task DelayUntilAsync(Stopwatch watch, TimeSpan deadline)
    {
        var remaining = deadline - watch.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    private static CaptureValidation ValidateCapture(CaptureTrackResult result, WaveStatistics wave)
    {
        var pauses = result.PausedIntervals;
        var segments = result.AudioClockSegments ?? [];
        var frequency = result.Handle.StopwatchFrequency;
        var wallDuration = frequency > 0
            ? (result.StopStopwatchTicks - result.Handle.StartStopwatchTicks) / (double)frequency
            : double.NaN;
        var pauseDuration = frequency > 0
            ? pauses.Sum(pause => Math.Max(0, pause.ResumeStopwatchTicks - pause.PauseStartStopwatchTicks)) / (double)frequency
            : double.NaN;
        var activeWallDuration = wallDuration - pauseDuration;
        var clockSegmentWallDuration = frequency > 0
            ? segments.Sum(segment => Math.Max(0, segment.EndStopwatchTicks - segment.StartStopwatchTicks)) / (double)frequency
            : double.NaN;
        var frameDifference = result.AudioFramesWritten is long captureFrames ? wave.Frames - captureFrames : (long?)null;
        var expectedPauseDuration = pauses.Count == 1 && frequency > 0
            ? (pauses[0].ResumeStopwatchTicks - pauses[0].PauseStartStopwatchTicks) / (double)frequency
            : (double?)null;
        var failures = new List<string>();
        if (wave.DataBytes <= 0 || wave.Frames <= 0) failures.Add("WAV has no sample data.");
        if (result.AudioSampleRate is not int sampleRate || sampleRate != wave.SampleRate)
            failures.Add($"WAV sample rate {wave.SampleRate} does not match CaptureTrackResult.AudioSampleRate {result.AudioSampleRate}.");
        if (result.AudioFramesWritten is not long || frameDifference != 0)
            failures.Add($"WAV frame count {wave.Frames} does not match CaptureTrackResult.AudioFramesWritten {result.AudioFramesWritten}.");
        if (segments.Count != 2) failures.Add($"Expected exactly two active AudioClockSegments around the pause, got {segments.Count}.");
        if (pauses.Count != 1 || expectedPauseDuration is not double pausedSeconds || pausedSeconds is < 1.5 or > 2.5)
            failures.Add($"Expected one measured pause near 2 seconds, got count={pauses.Count}, duration={expectedPauseDuration}.");
        if (!double.IsFinite(wallDuration) || wallDuration is < 14.7 or > 16.0)
            failures.Add($"Measured capture wall duration was outside 14.7..16.0 seconds: {wallDuration:F3}s.");
        if (!double.IsFinite(activeWallDuration) || activeWallDuration is < 12.3 or > 13.6)
            failures.Add($"Measured active wall duration was outside 12.3..13.6 seconds: {activeWallDuration:F3}s.");
        if (!double.IsFinite(wave.DurationSeconds) || wave.DurationSeconds is < 12.3 or > 13.6)
            failures.Add($"WAV sample duration was outside 12.3..13.6 seconds: {wave.DurationSeconds:F3}s.");
        if (!double.IsFinite(clockSegmentWallDuration) || Math.Abs(clockSegmentWallDuration - activeWallDuration) > 0.25)
            failures.Add($"AudioClockSegments wall span {clockSegmentWallDuration:F3}s did not match active capture wall time {activeWallDuration:F3}s.");
        if (Math.Abs(wave.DurationSeconds - activeWallDuration) > 0.5)
            failures.Add($"WAV duration {wave.DurationSeconds:F3}s differed from active wall duration {activeWallDuration:F3}s by more than 0.5s.");
        if (segments.Any(segment => segment.SampleRate != wave.SampleRate || segment.SourceEndFrame <= segment.SourceStartFrame || segment.SourceRate <= 0 || !double.IsFinite(segment.SourceRate)))
            failures.Add("One or more AudioClockSegments have invalid sample rate, frame bounds, or source rate.");

        return new CaptureValidation(failures.Count == 0, failures, wallDuration, pauseDuration, activeWallDuration,
            clockSegmentWallDuration, wave.DurationSeconds, frameDifference, pauses.Count, segments.Count,
            expectedPauseDuration);
    }

    private static WaveStatistics ReadWaveStatistics(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Microphone output is not a RIFF/WAVE file.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Microphone output is not a WAVE file.");

        ushort formatTag = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0, blockAlign = 0;
        long dataOffset = -1, dataLength = -1;
        Guid? extensibleSubFormat = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;
            var chunkEnd = checked(chunkStart + chunkSize);
            if (chunkEnd > stream.Length) throw new InvalidDataException($"WAV chunk '{chunkId}' extends beyond end of file.");
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) throw new InvalidDataException("WAV fmt chunk is too short.");
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = checked((int)reader.ReadUInt32());
                _ = reader.ReadUInt32(); // byte rate
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                if (formatTag == 0xfffe && chunkSize >= 40)
                {
                    stream.Position = chunkStart + 24;
                    extensibleSubFormat = new Guid(reader.ReadBytes(16));
                }
            }
            else if (chunkId == "data" && dataOffset < 0)
            {
                dataOffset = chunkStart;
                dataLength = chunkSize;
            }
            stream.Position = chunkEnd + (chunkSize & 1);
        }
        if (dataOffset < 0 || channels == 0 || sampleRate <= 0 || blockAlign <= 0 || bitsPerSample <= 0)
            throw new InvalidDataException("WAV format/data metadata is incomplete.");
        var effectiveFormat = formatTag == 0xfffe
            ? extensibleSubFormat switch
            {
                { } subFormat when subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") => (ushort)1,
                { } subFormat when subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") => (ushort)3,
                _ => (ushort)0
            }
            : formatTag;
        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample * channels > blockAlign || bitsPerSample % 8 != 0)
            throw new InvalidDataException("WAV sample layout is unsupported.");
        if (effectiveFormat == 1 && bitsPerSample is not (8 or 16 or 24 or 32) || effectiveFormat == 3 && bitsPerSample is not (32 or 64) || effectiveFormat is not (1 or 3))
            throw new InvalidDataException($"Unsupported microphone WAV encoding: formatTag=0x{formatTag:X4}, bitsPerSample={bitsPerSample}.");

        var frameCount = dataLength / blockAlign;
        var alignedDataLength = frameCount * blockAlign;
        if (alignedDataLength > 256L * 1024 * 1024)
            throw new InvalidDataException("WAV data exceeds the microphone probe's 256 MiB analysis bound.");
        stream.Position = dataOffset;
        var buffer = new byte[checked((int)alignedDataLength)];
        stream.ReadExactly(buffer);
        double sumSquares = 0;
        double peak = 0;
        long nonZeroSamples = 0;
        var totalSamples = checked(frameCount * channels);
        for (long frame = 0; frame < frameCount; frame++)
        {
            var frameStart = checked((int)(frame * blockAlign));
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = frameStart + channel * bytesPerSample;
                var value = DecodeSample(buffer.AsSpan(offset, bytesPerSample), effectiveFormat, bitsPerSample);
                if (!double.IsFinite(value)) throw new InvalidDataException("WAV contains a non-finite sample value.");
                var absolute = Math.Abs(value);
                if (absolute > peak) peak = absolute;
                if (value != 0d) nonZeroSamples++;
                sumSquares += value * value;
            }
        }
        var rms = totalSamples == 0 ? 0 : Math.Sqrt(sumSquares / totalSamples);
        return new WaveStatistics(formatTag, effectiveFormat, channels, sampleRate, bitsPerSample, blockAlign,
            dataLength, frameCount, frameCount / (double)sampleRate, totalSamples, nonZeroSamples,
            totalSamples == 0 ? 1 : 1 - nonZeroSamples / (double)totalSamples, peak, rms);
    }

    private static double DecodeSample(ReadOnlySpan<byte> sample, ushort format, ushort bits) => (format, bits) switch
    {
        (1, 8) => (sample[0] - 128) / 128d,
        (1, 16) => BitConverter.ToInt16(sample) / 32768d,
        (1, 24) => DecodePcm24(sample) / 8388608d,
        (1, 32) => BitConverter.ToInt32(sample) / 2147483648d,
        (3, 32) => BitConverter.ToSingle(sample),
        (3, 64) => BitConverter.ToDouble(sample),
        _ => throw new InvalidDataException($"Unsupported sample encoding {format}/{bits}.")
    };

    private static int DecodePcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xff000000) : value;
    }

    private sealed record WaveStatistics(ushort FormatTag, ushort EffectiveFormatTag, ushort Channels,
        int SampleRate, ushort BitsPerSample, int BlockAlign, long DataBytes, long Frames,
        double DurationSeconds, long TotalSamples, long NonZeroSamples, double ExactZeroSampleRatio,
        double PeakAbsoluteNormalized, double RmsNormalized);

    private sealed record CaptureValidation(bool Passed, IReadOnlyList<string> Failures,
        double WallDurationSeconds, double PausedDurationSeconds, double ActiveWallDurationSeconds,
        double ClockSegmentWallDurationSeconds, double WavDurationSeconds, long? FrameDifference,
        int PauseIntervalCount, int AudioClockSegmentCount, double? MeasuredPauseSeconds);
}
