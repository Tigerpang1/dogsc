using System.Text.Json;
using NAudio.CoreAudioApi;
using FrameTrail.Capture;

internal static class AudioPacketQpcJitterDiagnosisProbe
{
    internal static void Run()
    {
        var rates = new[] { DiagnoseRate(44_100), DiagnoseRate(48_000) };
        var timestampFault = AudioPacketClock.Resolve(2_000_000, 12_010_000, 480, 48_000,
            AudioClientBufferFlags.TimestampError, 10_000_000);
        Require(timestampFault.TimestampError && timestampFault.UsedArrivalFallback,
            "timestamp faults remain observable and take the existing arrival-time fallback");

        var pause = Simulate([0L, 480, 960, 1_440, 1_920, 2_400], 480, [(960L, 1_920L)]);
        Require(pause.GapFrames == 0 && pause.OverlapFrames == 0 && pause.OutputTimelineFrames == 1_920,
            "a pause range is removed from the output timeline without an insertion or overlap");

        var realGap = Simulate([0L, 480, 960, 1_680, 2_160], 480, []);
        Require(realGap.GapFrames == 240 && realGap.OverlapFrames == 0,
            "a genuine 240-frame device-position jump remains represented as a gap");

        var report = new
        {
            Diagnostic = "Synthetic-only comparison; does not claim that captured per-packet QPC jitter was measured.",
            PacketRate = "100 packets/s, 10 ms packets, 30 s sequence",
            PositionIsContinuous = true,
            Rates = rates,
            Pause = pause,
            TrueDeviceGap = realGap,
            TimestampFault = new
            {
                timestampFault.TimestampError,
                timestampFault.UsedArrivalFallback,
                FallbackStartTicks = timestampFault.StartStopwatchTicks
            },
            Result = "passed"
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static RateResult DiagnoseRate(int sampleRate)
    {
        const long frequency = 10_000_000;
        var packetFrames = sampleRate / 100; // 10 ms packets.
        const int packetCount = 3_000; // 30 seconds at 100 packets/s.
        const long recordingStartTicks = 1_000_000_000;
        var packetTicks = checked((long)((decimal)packetFrames * frequency / sampleRate));
        var jitterTicks = checked((long)Math.Ceiling((decimal)3 * frequency / sampleRate));
        var qpcStarts = new long[packetCount];
        var devicePositionStarts = new long[packetCount];
        for (var index = 0; index < packetCount; index++)
        {
            var jitter = index % 2 == 0 ? jitterTicks : -jitterTicks;
            var qpcStartTicks = checked(recordingStartTicks + index * packetTicks + jitter);
            var qpc100Nanoseconds = checked((long)((decimal)qpcStartTicks * AudioPacketClock.QpcUnitsPerSecond / frequency));
            var arrivalTicks = checked(qpcStartTicks + packetTicks + frequency / 500); // 2 ms after nominal packet end.
            var timing = AudioPacketClock.Resolve(qpc100Nanoseconds, arrivalTicks, packetFrames, sampleRate,
                AudioClientBufferFlags.None, frequency);
            qpcStarts[index] = checked((long)((decimal)(timing.StartStopwatchTicks - recordingStartTicks) * sampleRate / frequency));
            devicePositionStarts[index] = checked((long)index * packetFrames);
        }

        var qpcPlaced = Simulate(qpcStarts, packetFrames, []);
        var devicePositionPlaced = Simulate(devicePositionStarts, packetFrames, []);
        var qpcTone = RenderTone(qpcStarts, packetFrames, sampleRate, 1_000);
        var devicePositionTone = RenderTone(devicePositionStarts, packetFrames, sampleRate, 1_000);
        var cleanTone = RenderTone(Enumerable.Range(0, packetCount).Select(index => (long)index * packetFrames).ToArray(),
            packetFrames, sampleRate, 1_000);
        var qpcToneFraction = TonePowerFraction(qpcTone, sampleRate);
        var devicePositionToneFraction = TonePowerFraction(devicePositionTone, sampleRate);
        var cleanToneFraction = TonePowerFraction(cleanTone, sampleRate);
        Require(qpcPlaced.GapFrames > 0 && qpcPlaced.OverlapFrames > 0,
            "small alternating QPC offsets reproduce both artificial gap-fill and overlap-drop in QPC-positioned packets");
        Require(devicePositionPlaced.GapFrames == 0 && devicePositionPlaced.OverlapFrames == 0 &&
                devicePositionPlaced.OutputTimelineFrames == (long)packetCount * packetFrames,
            "continuous device positions preserve all samples despite the same QPC offsets");
        Require(cleanToneFraction > 0.999 && devicePositionToneFraction > 0.999,
            "clean and device-position placement preserve the continuous 1 kHz tone");
        Require(qpcToneFraction < (sampleRate == 44_100 ? 0.85 : 0.90),
            "QPC jitter alone measurably broadens the synthetic 1 kHz classification spectrum");

        return new RateResult(sampleRate, packetFrames, packetCount, jitterTicks,
            qpcPlaced.GapFrames, qpcPlaced.OverlapFrames,
            devicePositionPlaced.GapFrames, devicePositionPlaced.OverlapFrames, devicePositionPlaced.OutputTimelineFrames,
            cleanToneFraction, qpcToneFraction, devicePositionToneFraction);
    }

    private static float[] RenderTone(IReadOnlyList<long> packetStarts, int packetFrames, int sampleRate, double toneHz)
    {
        var output = new List<float>(packetStarts.Count * packetFrames);
        for (var index = 0; index < packetStarts.Count; index++)
        {
            var packet = new float[packetFrames];
            var deviceStart = (long)index * packetFrames;
            for (var frame = 0; frame < packetFrames; frame++)
                packet[frame] = (float)Math.Sin(2 * Math.PI * toneHz * (deviceStart + frame) / sampleRate);

            foreach (var segment in AudioPacketTimeline.MapPacket(packetStarts[index], packetFrames, []))
            {
                var start = segment.OutputStartFrame;
                if (start > output.Count)
                    output.AddRange(Enumerable.Repeat(0f, checked((int)(start - output.Count))));
                var overlap = Math.Max(0, output.Count - start);
                var drop = Math.Min(overlap, segment.FrameCount);
                var inputOffset = segment.InputFrameOffset + drop;
                var copied = segment.FrameCount - drop;
                for (var frame = 0; frame < copied; frame++) output.Add(packet[inputOffset + frame]);
            }
        }

        return output.ToArray();
    }

    private static double TonePowerFraction(float[] signal, int sampleRate)
    {
        const int windowMilliseconds = 60;
        var count = sampleRate * windowMilliseconds / 1_000;
        var start = 5 * sampleRate;
        if (signal.Length < start + count) throw new InvalidOperationException("Synthetic tone is shorter than analysis window.");
        double target = 0;
        double total = 0;
        for (var bin = 0; bin <= count / 2; bin++)
        {
            var frequency = (double)bin * sampleRate / count;
            if (frequency is < 600 or > 1_400) continue;
            double real = 0;
            double imaginary = 0;
            for (var sample = 0; sample < count; sample++)
            {
                var phase = 2 * Math.PI * bin * sample / count;
                var value = signal[start + sample];
                real += value * Math.Cos(phase);
                imaginary -= value * Math.Sin(phase);
            }
            var power = real * real + imaginary * imaginary;
            total += power;
            if (frequency is >= 980 and <= 1_020) target += power;
        }
        return total > 0 ? target / total : 0;
    }

    private static TimelineResult Simulate(IReadOnlyList<long> packetStarts, int packetFrames,
        IReadOnlyList<(long StartFrame, long EndFrame)> pausedRanges)
    {
        long writtenFrames = 0;
        long gapFrames = 0;
        long overlapFramesTotal = 0;
        long packetFramesWritten = 0;
        foreach (var packetStart in packetStarts)
        {
            foreach (var segment in AudioPacketTimeline.MapPacket(packetStart, packetFrames, pausedRanges))
            {
                if (segment.OutputStartFrame > writtenFrames)
                {
                    gapFrames += segment.OutputStartFrame - writtenFrames;
                    writtenFrames = segment.OutputStartFrame;
                }
                var overlap = Math.Max(0, writtenFrames - segment.OutputStartFrame);
                var dropped = Math.Min(overlap, segment.FrameCount);
                overlapFramesTotal += dropped;
                var packetFramesCopied = segment.FrameCount - dropped;
                packetFramesWritten += packetFramesCopied;
                writtenFrames += packetFramesCopied;
            }
        }
        var inputFrames = checked((long)packetStarts.Count * packetFrames);
        var packetFramesNotCopied = inputFrames - packetFramesWritten;
        return new TimelineResult(packetStarts.Count, packetFrames, gapFrames, overlapFramesTotal,
            writtenFrames, packetFramesNotCopied);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Audio packet QPC-jitter diagnostic failed: " + message);
    }

    private sealed record RateResult(int SampleRate, int PacketFrames, int PacketCount, long JitterTicks,
        long QpcPlacedGapFrames, long QpcPlacedOverlapFrames,
        long DevicePositionGapFrames, long DevicePositionOverlapFrames, long DevicePositionWrittenFrames,
        double Clean1kHzTargetPowerFraction, double QpcJittered1kHzTargetPowerFraction,
        double DevicePosition1kHzTargetPowerFraction);

    private sealed record TimelineResult(int PacketCount, int PacketFrames, long GapFrames, long OverlapFrames,
        long OutputTimelineFrames, long PacketFramesNotCopied);
}
