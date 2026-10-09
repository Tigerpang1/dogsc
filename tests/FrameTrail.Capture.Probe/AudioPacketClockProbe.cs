using NAudio.CoreAudioApi;

internal static class AudioPacketClockProbe
{
    internal static void Run()
    {
        const long frequency = 10_000_000;
        var converted = FrameTrail.Capture.AudioPacketClock.Qpc100NanosecondsToStopwatchTicks(123_456_789, frequency);
        Require(converted == 123_456_789, "100 ns QPC conversion at a 10 MHz Stopwatch frequency");
        var otherFrequency = FrameTrail.Capture.AudioPacketClock.Qpc100NanosecondsToStopwatchTicks(100_000_000, 25_000_000);
        Require(otherFrequency == 250_000_000, "100 ns QPC conversion at a 25 MHz Stopwatch frequency");

        var normal = FrameTrail.Capture.AudioPacketClock.Resolve(100_000_000, 100_120_000, 480, 48_000,
            AudioClientBufferFlags.None, frequency);
        Require(!normal.UsedArrivalFallback && normal.StartStopwatchTicks == 100_000_000, "valid packet uses its QPC timestamp");
        Require(Math.Abs(normal.ArrivalLatencyMilliseconds - 2d) < 0.001, "packet arrival latency is measured after packet duration");

        var timestampError = FrameTrail.Capture.AudioPacketClock.Resolve(100_000_000, 100_120_000, 480, 48_000,
            AudioClientBufferFlags.TimestampError, frequency);
        Require(timestampError.UsedArrivalFallback && timestampError.TimestampError && timestampError.StartStopwatchTicks == 100_020_000,
            "timestamp-error packet uses callback arrival estimate");
        var discontinuity = FrameTrail.Capture.AudioPacketClock.Resolve(100_000_000, 100_120_000, 480, 48_000,
            AudioClientBufferFlags.DataDiscontinuity, frequency);
        Require(!discontinuity.UsedArrivalFallback && discontinuity.DataDiscontinuity, "data discontinuity is reported without discarding a valid QPC timestamp");

        var mapped = FrameTrail.Capture.AudioPacketTimeline.MapPacket(95, 35, [(100L, 110L), (120L, 125L)]);
        Require(mapped.Count == 3, "packet crossing two pause boundaries splits into three sample ranges");
        Require(mapped[0] == new FrameTrail.Capture.AudioPacketSegment(0, 5, 95), "pre-pause samples retain their source position");
        Require(mapped[1] == new FrameTrail.Capture.AudioPacketSegment(15, 10, 100), "middle samples remove the first pause duration");
        Require(mapped[2] == new FrameTrail.Capture.AudioPacketSegment(30, 5, 110), "post-pause samples remove both pause durations");

        var openPause = FrameTrail.Capture.AudioPacketTimeline.MapPacket(5, 20, [(10L, long.MaxValue)]);
        Require(openPause.Count == 1 && openPause[0] == new FrameTrail.Capture.AudioPacketSegment(0, 5, 5),
            "samples after an open pause boundary are omitted");

        var packetStartingInsidePause = FrameTrail.Capture.AudioPacketTimeline.MapPacket(15, 10, [(10L, 20L)]);
        Require(packetStartingInsidePause.Count == 1 && packetStartingInsidePause[0] == new FrameTrail.Capture.AudioPacketSegment(5, 5, 10),
            "packet starting inside a pause subtracts the full elapsed pause and keeps the correct input offset");

        var preStartPacket = FrameTrail.Capture.AudioPacketTimeline.MapPacket(-5, 10, []);
        Require(preStartPacket.Count == 1 && preStartPacket[0] == new FrameTrail.Capture.AudioPacketSegment(5, 5, 0),
            "packet samples before recording start are clipped while preserving input offset");

        var stopClipped = FrameTrail.Capture.AudioPacketTimeline.MapPacket(90, 30, [], sourceEndFrame: 100, outputEndFrame: 95);
        Require(stopClipped.Count == 1 && stopClipped[0] == new FrameTrail.Capture.AudioPacketSegment(0, 5, 90),
            "source stop and output active-time limits clip the final packet");
        Console.WriteLine("Audio packet clock tests passed: QPC conversion, fallback flags, arrival latency, pause clipping, pre-start and stop boundaries.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Audio packet clock assertion failed: " + message);
    }
}
