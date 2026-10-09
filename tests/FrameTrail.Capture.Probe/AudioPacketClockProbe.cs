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

        var positionMapper = new FrameTrail.Capture.AudioPacketPositionMapper(1_000_000_000, 48_000, frequency);
        var firstPosition = positionMapper.Map(7_000, 480, 1_001_000_000, 1_001_120_000, AudioClientBufferFlags.None);
        var jitteredPosition = positionMapper.Map(7_480, 480, 1_001_100_625, 1_001_220_625, AudioClientBufferFlags.None);
        Require(firstPosition.StartFrame == 4_800 && jitteredPosition.StartFrame == 5_280,
            "non-zero initial device position anchors correctly and contiguous packets ignore per-packet QPC jitter");
        Require(jitteredPosition.QpcResidualMilliseconds is > 0.05 and < 0.07,
            "QPC jitter remains measurable as residual without shifting packet samples");
        var timestampUncertain = positionMapper.Map(7_960, 480, 0, 1_001_320_000,
            AudioClientBufferFlags.TimestampError);
        Require(timestampUncertain.StartFrame == 5_760 && timestampUncertain.Timing.TimestampError &&
                !timestampUncertain.DevicePositionReset,
            "timestamp-error packet retains a continuous device-position sequence while reporting uncertain time");
        var discontinuous = positionMapper.Map(8_440, 480, 1_001_700_000, 1_001_820_000,
            AudioClientBufferFlags.DataDiscontinuity);
        Require(discontinuous.StartFrame == 8_160 && discontinuous.Timing.DataDiscontinuity,
            "data-discontinuity packet reanchors from QPC and keeps its measurable missing-time interval");
        var afterDiscontinuity = positionMapper.Map(8_920, 480, 1_001_800_000, 1_001_920_000,
            AudioClientBufferFlags.None);
        Require(afterDiscontinuity.StartFrame == discontinuous.StartFrame + 480,
            "packets return to device-position placement after a discontinuity reanchor");

        var gapMapper = new FrameTrail.Capture.AudioPacketPositionMapper(1_500_000_000, 48_000, frequency);
        _ = gapMapper.Map(0, 480, 1_500_000_000, 1_501_020_000, AudioClientBufferFlags.None);
        _ = gapMapper.Map(480, 480, 1_500_100_000, 1_500_220_000, AudioClientBufferFlags.None);
        var deviceGap = gapMapper.Map(1_200, 480, 1_502_500_000, 1_502_620_000, AudioClientBufferFlags.None);
        Require(deviceGap.DevicePositionMismatch && !deviceGap.DevicePositionReset && deviceGap.StartFrame == 1_200,
            "a forward device-position jump remains a genuine 240-frame gap");

        var resetMapper = new FrameTrail.Capture.AudioPacketPositionMapper(2_000_000_000, 48_000, frequency);
        _ = resetMapper.Map(5_000, 480, 2_000_000_000, 2_010_200_000, AudioClientBufferFlags.None);
        _ = resetMapper.Map(5_480, 480, 2_010_000_000, 2_020_200_000, AudioClientBufferFlags.None);
        var resetPosition = resetMapper.Map(12, 480, 2_300_000_000, 2_310_200_000, AudioClientBufferFlags.None);
        Require(resetPosition.DevicePositionReset && resetMapper.PositionResetCount == 1 && resetPosition.StartFrame >= 9_600,
            "position reset is counted and reanchored without overwriting earlier samples");

        // Exercise the production mapper and pause packet splitter together. QPC has
        // small packet jitter, the stream position resets at the pause end, and a
        // separate forward position jump after resume must remain a real silent gap.
        const long pauseClockStart = 10_000_000_000;
        var pauseClockMapper = new FrameTrail.Capture.AudioPacketPositionMapper(pauseClockStart, 48_000, frequency);
        var pausePackets = new[]
        {
            pauseClockMapper.Map(5_000, 480, pauseClockStart, pauseClockStart + 120_000, AudioClientBufferFlags.None),
            pauseClockMapper.Map(5_480, 480, pauseClockStart + 100_625, pauseClockStart + 220_625, AudioClientBufferFlags.None),
            pauseClockMapper.Map(5_960, 480, pauseClockStart + 200_000, pauseClockStart + 320_000, AudioClientBufferFlags.None),
            pauseClockMapper.Map(6_440, 480, pauseClockStart + 300_625, pauseClockStart + 420_625, AudioClientBufferFlags.None),
            pauseClockMapper.Map(10, 480, pauseClockStart + 500_000, pauseClockStart + 620_000, AudioClientBufferFlags.None),
            pauseClockMapper.Map(490, 480, pauseClockStart + 600_000, pauseClockStart + 720_000, AudioClientBufferFlags.None),
            pauseClockMapper.Map(1_210, 480, pauseClockStart + 700_000, pauseClockStart + 820_000, AudioClientBufferFlags.None)
        };
        var pauseStartFrame = pauseClockMapper.StopwatchTicksToSourceFrame(pauseClockStart + 200_000);
        var pauseEndFrame = pauseClockMapper.StopwatchTicksToSourceFrame(pauseClockStart + 500_000);
        var capturedPause = (pauseStartFrame, pauseEndFrame);
        var mappedPausePackets = pausePackets.Select(packet => FrameTrail.Capture.AudioPacketTimeline.MapPacket(
            packet.StartFrame, 480, [capturedPause])).ToArray();
        Require(pausePackets[1].StartFrame == 480 && pausePackets[1].DevicePositionMismatch == false
                && pausePackets[1].QpcResidualMilliseconds is > 0.05 and < 0.07,
            "QPC packet jitter before the pause does not move contiguous device-position samples");
        Require(pauseStartFrame == 960 && pauseEndFrame == 2_400
                && mappedPausePackets[2].Count == 0 && mappedPausePackets[3].Count == 0,
            "the pause interval is measured in the source-frame clock and removes packets captured inside it");
        Require(pausePackets[4].DevicePositionReset && pausePackets[4].StartFrame == pauseEndFrame
                && mappedPausePackets[4].Single() == new FrameTrail.Capture.AudioPacketSegment(0, 480, 960)
                && mappedPausePackets[5].Single() == new FrameTrail.Capture.AudioPacketSegment(0, 480, 1_440),
            "a device-position reset exactly at resume reanchors to QPC while the first resumed samples continue after pause removal");
        Require(pausePackets[6].DevicePositionMismatch && !pausePackets[6].DevicePositionReset
                && pausePackets[6].StartFrame == 3_600
                && mappedPausePackets[6].Single() == new FrameTrail.Capture.AudioPacketSegment(0, 480, 2_160),
            "a forward device-position jump after resume remains a 240-frame output gap instead of being deleted with the pause");

        // A reset before the one-second drift threshold must use the current QPC
        // anchor, preserve the nominal frame rate, and produce a finite segment rate.
        const long shortClockStart = 20_000_000_000;
        var shortClockMapper = new FrameTrail.Capture.AudioPacketPositionMapper(shortClockStart, 48_000, frequency);
        FrameTrail.Capture.AudioPacketPlacement shortReset = default;
        for (var index = 0; index < 50; index++)
        {
            var tick = index * 100_000L;
            var position = index * 480L;
            shortReset = shortClockMapper.Map(position, 480, shortClockStart + tick,
                shortClockStart + tick + 120_000, AudioClientBufferFlags.None);
        }
        shortReset = shortClockMapper.Map(0, 480, shortClockStart + 5_000_000,
            shortClockStart + 5_120_000, AudioClientBufferFlags.None);
        var segmentStartFrame = shortReset.StartFrame;
        var segmentEndFrame = shortClockMapper.StopwatchTicksToSourceFrame(shortClockStart + 7_500_000);
        var shortSegmentRate = ((segmentEndFrame - segmentStartFrame) / 48_000d) / .25d;
        Require(shortReset.DevicePositionReset && shortReset.StartFrame == 24_000
                && shortClockMapper.DeviceClockDriftPpm is null && segmentEndFrame == 36_000
                && Math.Abs(shortSegmentRate - 1d) < 1e-12,
            "a sub-second position reset keeps the local QPC anchor and yields a 1x measured segment rate without premature drift correction");

        var driftMapper = new FrameTrail.Capture.AudioPacketPositionMapper(3_000_000_000, 48_000, frequency);
        const int driftPackets = 6_000;
        const double deviceRateScale = 1.001;
        long driftStart = 3_000_000_000;
        for (var index = 0; index < driftPackets; index++)
        {
            var qpc = driftStart + (long)Math.Round(index * 480d / 48_000 / deviceRateScale * frequency);
            var qpc100Ns = qpc;
            var arrival = qpc + 10_000_000;
            driftMapper.Map(20_000 + (long)index * 480, 480, qpc100Ns, arrival, AudioClientBufferFlags.None);
        }
        var pauseBoundary = driftMapper.StopwatchTicksToSourceFrame(driftStart + 30 * frequency);
        Require(driftMapper.DeviceClockDriftPpm is > 900 and < 1_100 && Math.Abs(pauseBoundary - 1_441_440) <= 1,
            "long-baseline QPC drift is measured and pause boundaries map through the measured device clock rate");
        var pauseResumeBoundary = driftMapper.StopwatchTicksToSourceFrame(driftStart + 31 * frequency);
        var pauseMapped = FrameTrail.Capture.AudioPacketTimeline.MapPacket(pauseBoundary - 10,
            checked((int)(pauseResumeBoundary - pauseBoundary + 20)),
            [(pauseBoundary, pauseResumeBoundary)]);
        Require(pauseMapped.Count == 2 && pauseMapped[0].OutputStartFrame == pauseBoundary - 10 &&
                pauseMapped[1].OutputStartFrame == pauseBoundary && pauseMapped[1].InputFrameOffset == pauseResumeBoundary - pauseBoundary + 10,
            "pause clipping uses measured device-clock boundaries at both edges, without cumulative wall-time drift");

        VerifyReanchoredClockEpoch(AudioClientBufferFlags.None, resetDevicePosition: true);
        VerifyReanchoredClockEpoch(AudioClientBufferFlags.DataDiscontinuity, resetDevicePosition: false);
        VerifyBackwardReanchorBoundary(timestampError: false);
        VerifyBackwardReanchorBoundary(timestampError: true);

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
        Console.WriteLine("Audio packet clock tests passed: QPC conversion, device-position placement, jitter residual, timestamp uncertainty, discontinuity/reset/gap handling, per-epoch drift and pause boundaries, segmented SourceRate, fallback flags, and pause clipping.");
    }

    private static void VerifyReanchoredClockEpoch(AudioClientBufferFlags reanchorFlag, bool resetDevicePosition)
    {
        const long frequency = 10_000_000;
        const long startTicks = 30_000_000_000;
        const int sampleRate = 48_000;
        const int packetFrames = 480;
        const double firstEpochRate = 1.001;
        const double secondEpochRate = 0.9995;
        var mapper = new FrameTrail.Capture.AudioPacketPositionMapper(startTicks, sampleRate, frequency);
        var firstPacket = mapper.Map(50_000, packetFrames, startTicks, startTicks + 120_000, AudioClientBufferFlags.None);
        Require(firstPacket.StartFrame == 0, "reset-epoch fixture begins on the initial audio source anchor");

        for (var index = 1; index < 1_500; index++)
        {
            var elapsedTicks = (long)Math.Round(index * packetFrames / (double)sampleRate / firstEpochRate * frequency);
            mapper.Map(50_000 + (long)index * packetFrames, packetFrames, startTicks + elapsedTicks,
                startTicks + elapsedTicks + 120_000, AudioClientBufferFlags.None);
        }
        Require(mapper.DeviceClockDriftPpm is > 900 and < 1_100,
            "the first simulated epoch reaches a stable positive device-clock drift before reanchoring");

        var resetIndex = 1_500;
        var resetElapsedTicks = (long)Math.Round(resetIndex * packetFrames / (double)sampleRate / firstEpochRate * frequency);
        var resetTicks = startTicks + resetElapsedTicks;
        var devicePositionForReset = resetDevicePosition ? 7L : 50_000 + (long)resetIndex * packetFrames;
        var resetPacket = mapper.Map(devicePositionForReset, packetFrames, resetTicks, resetTicks + 120_000, reanchorFlag);
        Require(resetPacket.ClockReanchored && mapper.DeviceClockDriftPpm is null,
            "a stream reset or discontinuity closes the old drift fit and starts a fresh clock epoch");

        var nextEpochDeviceBase = devicePositionForReset + packetFrames;
        var endTicks = resetTicks;
        var endFrame = resetPacket.StartFrame;
        for (var index = 1; index <= 1_000; index++)
        {
            var elapsedTicks = (long)Math.Round(index * packetFrames / (double)sampleRate / secondEpochRate * frequency);
            endTicks = resetTicks + elapsedTicks;
            var placement = mapper.Map(nextEpochDeviceBase + (long)(index - 1) * packetFrames, packetFrames,
                endTicks, endTicks + 120_000, AudioClientBufferFlags.None);
            endFrame = placement.StartFrame + packetFrames;
        }
        // Segment end is the end of the final packet, not its start timestamp.
        endTicks += (long)Math.Round(packetFrames / (double)sampleRate / secondEpochRate * frequency);

        var pauseBoundaryTicks = resetTicks + 10 * frequency;
        var mappedAfterReset = mapper.StopwatchTicksToSourceFrame(pauseBoundaryTicks);
        var expectedAfterReset = resetPacket.StartFrame + (long)Math.Round(10 * sampleRate * secondEpochRate);
        var staleFirstEpochEstimate = resetPacket.StartFrame + (long)Math.Round(10 * sampleRate * firstEpochRate);
        Require(mapper.DeviceClockDriftPpm is > -600 and < -400 && Math.Abs(mappedAfterReset - expectedAfterReset) <= 1,
            "pause/stop boundaries after reset use the new epoch rate instead of extrapolating the previous positive drift");
        Require(Math.Abs(mappedAfterReset - staleFirstEpochEstimate) > 100,
            "the post-reset boundary differs materially from the stale pre-reset clock fit");
        Console.WriteLine($"Clock epoch {reanchorFlag}: resetFrame={resetPacket.StartFrame}, pauseBoundaryActual={mappedAfterReset}, newEpochExpected={expectedAfterReset}, staleOldFitEstimate={staleFirstEpochEstimate}, newEpochDriftPpm={mapper.DeviceClockDriftPpm:R}");

        var firstSegment = FrameTrail.Capture.AudioClockSegmentMath.Create(startTicks, resetTicks,
            firstPacket.StartFrame, resetPacket.StartFrame, sampleRate, frequency);
        var secondSegment = FrameTrail.Capture.AudioClockSegmentMath.Create(resetTicks, endTicks,
            resetPacket.StartFrame, endFrame, sampleRate, frequency);
        Require(firstSegment is { SourceRate: > 1.0008 and < 1.0012 } &&
                secondSegment is { SourceRate: > 0.9993 and < 0.9997 } &&
                firstSegment.SourceEndFrame == secondSegment.SourceStartFrame,
            $"separate active clock segments retain independently measured rates and continuous sample boundary (first={firstSegment?.SourceRate:R}, second={secondSegment?.SourceRate:R}, firstEnd={firstSegment?.SourceEndFrame}, secondStart={secondSegment?.SourceStartFrame})");

        var compressedBoundary = FrameTrail.Capture.AudioPacketTimeline.SourceFrameToActiveOutputFrame(
            12_000, [(4_000L, 6_000L), (9_000L, 10_000L)]);
        Require(compressedBoundary == 9_000,
            "a reanchored source boundary remains in the active WAV frame domain after prior pause intervals are removed");
    }

    private static void VerifyBackwardReanchorBoundary(bool timestampError)
    {
        const long frequency = 10_000_000;
        const long startTicks = 1_000_000_000;
        const int sampleRate = 48_000;
        const int packetFrames = 480;
        var mapper = new FrameTrail.Capture.AudioPacketPositionMapper(startTicks, sampleRate, frequency);
        var first = mapper.Map(0, packetFrames, startTicks, startTicks + 100_000, AudioClientBufferFlags.None);
        var second = mapper.Map(packetFrames, packetFrames, startTicks + 100_000,
            startTicks + 200_000, AudioClientBufferFlags.None);
        var priorEndFrame = second.StartFrame + packetFrames;
        var priorEndTicks = startTicks + 200_000;
        var rawPacketTicks = startTicks - 10_000_000;
        var flags = timestampError
            ? AudioClientBufferFlags.TimestampError
            : AudioClientBufferFlags.None;
        var arrivalTicks = timestampError ? startTicks - 4_900_000 : rawPacketTicks + 100_000;
        var reset = mapper.Map(0, packetFrames, rawPacketTicks, arrivalTicks, flags);
        var stopTicks = startTicks + frequency;
        var stopFrame = mapper.StopwatchTicksToSourceFrame(stopTicks);

        Require(first.StartFrame == 0 && second.StartFrame == packetFrames && reset.ClockReanchored &&
                reset.StartFrame == priorEndFrame,
            "backward timestamp reanchor keeps the prior sample cursor monotonic");
        Require(reset.Timing.StartStopwatchTicks < startTicks && reset.ClockBoundaryStopwatchTicks == priorEndTicks,
            "raw QPC or arrival fallback remains diagnostic while the effective boundary is clamped to prior packet end");
        Require(stopFrame == sampleRate,
            $"stop mapping after backward reanchor has no extra elapsed second (timestampError={timestampError}, stopFrame={stopFrame}, expected={sampleRate})");

        var closedSegment = FrameTrail.Capture.AudioClockSegmentMath.Create(startTicks,
            reset.ClockBoundaryStopwatchTicks, first.StartFrame, reset.StartFrame, sampleRate, frequency);
        var resumedSegment = FrameTrail.Capture.AudioClockSegmentMath.Create(reset.ClockBoundaryStopwatchTicks,
            stopTicks, reset.StartFrame, stopFrame, sampleRate, frequency);
        Require(closedSegment is { SourceRate: > .99 and < 1.01 } &&
                resumedSegment is { SourceRate: > .99 and < 1.01 } &&
                closedSegment.EndStopwatchTicks == resumedSegment.StartStopwatchTicks &&
                closedSegment.SourceEndFrame == resumedSegment.SourceStartFrame,
            "segments split at the monotonic effective boundary without losing duration or overlapping samples");
        Console.WriteLine($"Backward reanchor timestampError={timestampError}: raw={reset.Timing.StartStopwatchTicks}, effective={reset.ClockBoundaryStopwatchTicks}, priorEnd={priorEndTicks}, stopFrame={stopFrame}, old-raw-anchor-stop-frame={reset.StartFrame + sampleRate * (stopTicks - reset.Timing.StartStopwatchTicks) / frequency}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Audio packet clock assertion failed: " + message);
    }
}
