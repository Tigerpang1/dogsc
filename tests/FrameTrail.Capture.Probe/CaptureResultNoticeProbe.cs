using System.Text.Json;
using System.Text.Json.Nodes;
using FrameTrail.Capture;

internal static class CaptureResultNoticeProbe
{
    internal static void Run()
    {
        var captureWarning = "Capture backend failure: synthetic failure";
        var audioRequestWarning = "AudioOptions are not muxed by the ddagrab backend.";
        var captureDiagnostic = "Display/region capture uses FFmpeg ddagrab Desktop Duplication.";
        var result = new RecordingResult("screen.mp4", 10, 20, 10, EncoderMode.Unknown,
            "H.264 Media Foundation", 1280, 720, 30, null,
            [captureWarning, audioRequestWarning, "Encoder fallback attempts failed: synthetic fallback"], Diagnostics: [captureDiagnostic]);
        var serialized = JsonSerializer.Serialize(result);
        var roundTrip = JsonSerializer.Deserialize<RecordingResult>(serialized)
            ?? throw new InvalidOperationException("RecordingResult serialization returned null.");
        Require(roundTrip.Warnings.Contains(captureWarning), "actionable capture warning survives serialization");
        Require(roundTrip.Warnings.Contains(audioRequestWarning), "ignored explicit audio request remains an actionable warning");
        Require(roundTrip.Diagnostics?.Contains(captureDiagnostic) == true, "capture diagnostic survives serialization separately");
        Require(roundTrip.Warnings.Count == 3, "diagnostic is not counted as a warning");

        var legacyJson = JsonNode.Parse(serialized)?.AsObject()
            ?? throw new InvalidOperationException("Could not parse current RecordingResult JSON.");
        legacyJson.Remove(nameof(RecordingResult.Diagnostics));
        var legacyResult = JsonSerializer.Deserialize<RecordingResult>(legacyJson.ToJsonString())
            ?? throw new InvalidOperationException("Legacy RecordingResult JSON returned null.");
        Require(legacyResult.Warnings.Contains(captureWarning), "legacy RecordingResult warnings remain readable");
        Require(legacyResult.Diagnostics is null, "legacy RecordingResult JSON without Diagnostics remains readable");

        var handle = new CaptureTrackHandle(Guid.NewGuid(), CaptureTrackKind.SystemAudio, "system.wav", 10, 10);
        var track = new CaptureTrackResult(handle, 20, 0, 0, 0, EncoderMode.Unknown,
            ["Audio capture ended unexpectedly; WAV was finalized with clock-filled silence.",
                "A WASAPI clock reanchor mapped behind the already-written WAV cursor; the new source-clock segment was clamped to the written boundary.",
                "Estimated audio device clock drift may accumulate about 120 ms over 10 seconds."], [],
            Diagnostics: ["WASAPI packet timing: packets=1, timestampErrors=0."]);
        var trackJson = JsonSerializer.Serialize(track);
        var roundTripTrack = JsonSerializer.Deserialize<CaptureTrackResult>(trackJson)
            ?? throw new InvalidOperationException("CaptureTrackResult serialization returned null.");
        Require(roundTripTrack.Warnings.Count == 3
            && roundTripTrack.Warnings[0].Contains("ended unexpectedly", StringComparison.Ordinal)
            && roundTripTrack.Warnings[1].Contains("clamped", StringComparison.Ordinal)
            && roundTripTrack.Warnings[2].Contains("drift", StringComparison.Ordinal),
            "audio end, clock clamp, and drift warnings survive serialization");
        Require(roundTripTrack.Diagnostics?.Count == 1, "audio timing diagnostic remains separate from warnings");

        var legacyTrackJson = JsonNode.Parse(trackJson)?.AsObject()
            ?? throw new InvalidOperationException("Could not parse current CaptureTrackResult JSON.");
        legacyTrackJson.Remove(nameof(CaptureTrackResult.Diagnostics));
        var legacyTrack = JsonSerializer.Deserialize<CaptureTrackResult>(legacyTrackJson.ToJsonString())
            ?? throw new InvalidOperationException("Legacy CaptureTrackResult JSON returned null.");
        Require(legacyTrack.Warnings.Count == 3 && legacyTrack.Diagnostics is null,
            "legacy CaptureTrackResult JSON remains readable without Diagnostics");

        Console.WriteLine("Capture result notice serialization passed: diagnostics stay separate, actionable warnings persist, and legacy JSON remains readable.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Capture result notice probe failed: " + message);
    }
}
