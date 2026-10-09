using System.Text.Json;
using FrameTrail.Capture;

internal static class AudioWriterFailureProbe
{
    internal static async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var blockedOutput = Path.Combine(outputDirectory, "writer-must-fail.wav");
        var recoveredOutput = Path.Combine(outputDirectory, "writer-recovered.wav");
        if (File.Exists(blockedOutput) || Directory.Exists(blockedOutput) || File.Exists(recoveredOutput))
            throw new IOException("Probe output already exists; use a fresh validation directory.");

        await using var tracks = new CaptureTrackService();
        var loopback = (await tracks.EnumerateAudioDevicesAsync())
            .FirstOrDefault(device => device.Kind == AudioDeviceKind.SystemLoopback && device.IsDefault)
            ?? throw new InvalidOperationException("No default loopback device was enumerated.");

        Directory.CreateDirectory(blockedOutput);
        Exception? startupFailure = null;
        try
        {
            await tracks.StartAudioTrackAsync(new AudioTrackRequest(blockedOutput, AudioDeviceKind.SystemLoopback, loopback.Id));
        }
        catch (Exception ex) { startupFailure = ex; }
        finally { Directory.Delete(blockedOutput); }

        if (startupFailure is null)
            throw new InvalidOperationException("Writer creation unexpectedly succeeded for a directory path.");
        if (File.Exists(blockedOutput))
            throw new InvalidOperationException("Failed writer startup left an output file behind.");

        var handle = await tracks.StartAudioTrackAsync(new AudioTrackRequest(recoveredOutput, AudioDeviceKind.SystemLoopback, loopback.Id));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var result = await tracks.StopTrackAsync(handle.TrackId);
        var bytes = File.ReadAllBytes(recoveredOutput);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Recovery recording is not a finalized RIFF/WAVE file.");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            blockedOutput,
            startupFailure = startupFailure.GetType().FullName,
            startupFailure.Message,
            recoveredOutput,
            bytes = bytes.Length,
            result.Warnings,
            result.PausedIntervals
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
