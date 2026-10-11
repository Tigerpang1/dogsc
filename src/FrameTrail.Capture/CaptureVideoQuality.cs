using System.Globalization;

namespace FrameTrail.Capture;

/// <summary>Resolution and frame-rate based bitrate defaults for desktop video capture.</summary>
internal static class CaptureVideoQuality
{
    internal const int BaselineWidth = 1920;
    internal const int BaselineHeight = 1080;
    internal const int BaselineFrameRate = 60;
    internal const int BaselineBitrate = 16_000_000;
    internal const int MinimumBitrate = 2_000_000;
    internal const int NvencConstantQuality = 19;

    internal static int ResolveBitrate(int width, int height, int frameRate, int? requestedBitrate = null)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (requestedBitrate is <= 0) throw new ArgumentOutOfRangeException(nameof(requestedBitrate));
        if (requestedBitrate is int requested) return requested;

        var scale = (double)width * height * frameRate / (BaselineWidth * (double)BaselineHeight * BaselineFrameRate);
        var bitrate = Math.Round(BaselineBitrate * scale, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(bitrate, MinimumBitrate, int.MaxValue);
    }

    internal static IReadOnlyList<string> EncoderArguments(string encoder, VideoOptions video, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);
        ArgumentNullException.ThrowIfNull(video);
        var bitrate = ResolveBitrate(width, height, video.FrameRate, video.Bitrate);
        var bitrateText = bitrate.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-c:v", encoder };
        switch (encoder)
        {
            case "h264_nvenc":
                var maxRate = bitrate;
                var bufferSize = Math.Min((long)int.MaxValue, (long)maxRate * 2);
                args.AddRange([
                    "-preset", "p5", "-tune", "hq",
                    "-rc", "vbr", "-cq", NvencConstantQuality.ToString(CultureInfo.InvariantCulture),
                    "-b:v", bitrateText,
                    "-maxrate", maxRate.ToString(CultureInfo.InvariantCulture),
                    "-bufsize", bufferSize.ToString(CultureInfo.InvariantCulture)
                ]);
                break;
            case "h264_amf":
                args.AddRange(["-usage", "ultralowlatency", "-b:v", bitrateText]);
                break;
            case "h264_qsv":
                args.AddRange(["-preset", "veryfast", "-b:v", bitrateText]);
                break;
            case "h264_mf":
                args.AddRange(["-b:v", bitrateText]);
                break;
            default:
                args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-b:v", bitrateText]);
                break;
        }
        return args;
    }
}
