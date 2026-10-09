using FrameTrail;

internal static class RecordingAudioClockSegmentProbe
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var screenPause = RecordingTimelineMapper.MapAudioClockSegments(
            screenOriginTicks: 0,
            screenCapture: new(0, 10_000),
            screenPauses: [new(4_000, 6_000)],
            audioSegments: [new(0, 10_000, 0, 1)],
            ticksFrequency: 1_000,
            sourceDuration: 10,
            timelineDuration: 8);
        Check(screenPause.Count == 2 &&
              screenPause[0] == new MappedAudioClockInterval(0, 4, 0, 1) &&
              screenPause[1] == new MappedAudioClockInterval(6, 4, 4, 1),
            "audio that continues through a screen pause drops the corresponding WAV samples instead of compressing them into the screen timeline");

        var differentPauseBoundaries = RecordingTimelineMapper.MapAudioClockSegments(
            screenOriginTicks: 0,
            screenCapture: new(0, 10_000),
            screenPauses: [new(4_000, 6_000)],
            audioSegments: [new(0, 3_500, 0, 1), new(4_500, 10_000, 3.5, 1.001)],
            ticksFrequency: 1_000,
            sourceDuration: 9.0055,
            timelineDuration: 8);
        Check(differentPauseBoundaries.Count == 2 &&
              differentPauseBoundaries[0] == new MappedAudioClockInterval(0, 3.5, 0, 1) &&
              Math.Abs(differentPauseBoundaries[1].SourceStart - 5.0015) < 1e-9 &&
              Math.Abs(differentPauseBoundaries[1].Duration - 4) < 1e-9 &&
              Math.Abs(differentPauseBoundaries[1].TimelineStart - 4) < 1e-9 &&
              differentPauseBoundaries[1].SourceRate == 1.001,
            "audio pause and screen pause boundaries are intersected independently, with the measured source rate retained");

        var clipped = RecordingTimelineMapper.MapAudioClockSegments(
            screenOriginTicks: 0,
            screenCapture: new(0, 10_000),
            screenPauses: [new(4_000, 6_000)],
            audioSegments: [new(0, 10_000, 0, 1)],
            ticksFrequency: 1_000,
            sourceDuration: 7,
            timelineDuration: 5);
        Check(clipped.Count == 2 && clipped[0].Duration == 4 && clipped[1].TimelineStart == 4 && clipped[1].Duration == 1,
            "source media duration and project duration clip each mapped interval in their own clock domain");

        // Old recordings without per-segment clock metadata continue through MapAudio.
        var legacy = RecordingTimelineMapper.MapAudio(0, new(0, 3_000), [], new(0, 3_000), [], 1_000);
        Check(legacy.Count == 1 && legacy[0] == new MappedAudioInterval(0, 3, 0),
            "legacy capture results without piecewise clock metadata retain the existing continuous mapping path");

        Console.WriteLine("Recording audio clock segment tests passed: screen/audio pause intersection, measured per-segment rate, duration clipping, and legacy mapping.");
    }
}
