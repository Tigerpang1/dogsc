using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FrameTrail.Media;

namespace FrameTrail.App;

public sealed record ThumbnailFrame(double SourceTime, BitmapSource Image);
public sealed record TimelineAssetData(IReadOnlyList<ThumbnailFrame> Thumbnails, IReadOnlyList<double> Peaks, double Duration);

/// <summary>Creates small, disposable timeline assets without loading decoded audio into memory.</summary>
public static class TimelineAssets
{
    private const int ThumbnailCount = 8;
    private const int ThumbnailWidth = 160;
    private const int WaveformSampleRate = 8000;
    private const int MaximumPeakColumns = 1000;
    private const int ReadBufferSize = 64 * 1024;

    public static Task<TimelineAssetData> BuildAsync(string sourcePath, string cacheDirectory, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        cancellation.ThrowIfCancellationRequested();
        return Task.Run(() => BuildCoreAsync(sourcePath, cacheDirectory, cancellation), cancellation);
    }

    private static async Task<TimelineAssetData> BuildCoreAsync(string sourcePath, string cacheDirectory, CancellationToken cancellation)
    {
        var media = await MediaTools.ProbeAsync(sourcePath, cancellation).ConfigureAwait(false);
        if (!double.IsFinite(media.Duration) || media.Duration <= 0)
            throw new InvalidDataException("素材时长无效，无法生成时间线资源。");

        var root = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(root);
        var workDirectory = Path.Combine(root, "frametrail-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            var thumbnails = media.Width > 0 && media.Height > 0
                ? await BuildThumbnailsAsync(sourcePath, workDirectory, media.Duration, cancellation).ConfigureAwait(false)
                : Array.Empty<ThumbnailFrame>();
            var peaks = media.HasAudio
                ? await BuildPeaksAsync(sourcePath, media.Duration, cancellation).ConfigureAwait(false)
                : Array.Empty<double>();
            cancellation.ThrowIfCancellationRequested();
            return new TimelineAssetData(thumbnails, peaks, media.Duration);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static async Task<IReadOnlyList<ThumbnailFrame>> BuildThumbnailsAsync(string sourcePath, string workDirectory, double duration, CancellationToken cancellation)
    {
        var pattern = Path.Combine(workDirectory, "thumb-%02d.png");
        var filter = $"fps={MediaTools.Number(ThumbnailCount / duration)},scale={ThumbnailWidth}:-1";
        var args = new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-an", "-vf", filter, "-frames:v", ThumbnailCount.ToString(CultureInfo.InvariantCulture), "-start_number", "1", "-y", pattern };
        await RunProcessAsync(MediaTools.Ffmpeg, args, cancellation).ConfigureAwait(false);

        var frames = new List<ThumbnailFrame>(ThumbnailCount);
        for (var index = 1; index <= ThumbnailCount; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = Path.Combine(workDirectory, $"thumb-{index:00}.png");
            if (!File.Exists(path)) break;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new System.Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            // fps samples begin at source time 0 and advance by duration / 8.
            frames.Add(new ThumbnailFrame((index - 1) * duration / ThumbnailCount, image));
        }
        return frames;
    }

    private static async Task<IReadOnlyList<double>> BuildPeaksAsync(string sourcePath, double duration, CancellationToken cancellation)
    {
        var columns = Math.Clamp((int)Math.Ceiling(duration * 20), 1, MaximumPeakColumns);
        var peaks = new double[columns];
        var expectedSamples = Math.Max(1L, checked((long)Math.Ceiling(duration * WaveformSampleRate)));
        var args = new[] { "-hide_banner", "-loglevel", "error", "-i", sourcePath, "-vn", "-ac", "1", "-ar", WaveformSampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1" };
        using var process = MediaTools.Start(MediaTools.Ffmpeg, args, output: true);
        var errors = process.StandardError.ReadToEndAsync();
        using var registration = cancellation.Register(() => MediaTools.Kill(process));
        var buffer = new byte[ReadBufferSize];
        long sampleIndex = 0;
        var pendingLowByte = -1;
        try
        {
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false);
                if (read == 0) break;
                var position = 0;
                if (pendingLowByte >= 0)
                {
                    AddSample((short)(pendingLowByte | (buffer[0] << 8)));
                    pendingLowByte = -1;
                    position = 1;
                }
                var evenEnd = position + ((read - position) & ~1);
                for (; position < evenEnd; position += 2)
                    AddSample((short)(buffer[position] | (buffer[position + 1] << 8)));
                if (position < read) pendingLowByte = buffer[position];
            }

            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"FFmpeg 波形解码失败：{MediaTools.Tail(error)}");
        }
        finally
        {
            MediaTools.Kill(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            // Reading stderr to completion prevents a redirected pipe from keeping FFmpeg alive.
            try { await errors.ConfigureAwait(false); } catch (IOException) { }
        }
        return peaks;

        void AddSample(short raw)
        {
            var sample = Math.Abs((int)raw) / 32768d;
            var column = Math.Min(columns - 1, (int)Math.Min(columns - 1L, sampleIndex * columns / expectedSamples));
            peaks[column] = Math.Max(peaks[column], sample);
            sampleIndex++;
        }
    }

    private static async Task RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        using var process = MediaTools.Start(executable, arguments, output: true);
        var errors = process.StandardError.ReadToEndAsync();
        using var registration = cancellation.Register(() => MediaTools.Kill(process));
        try
        {
            // Image output goes to files; drain stdout anyway so process wrappers stay safe if FFmpeg emits diagnostics there.
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var error = await errors.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"FFmpeg 缩略图生成失败：{MediaTools.Tail(error)}");
        }
        finally
        {
            MediaTools.Kill(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await errors.ConfigureAwait(false); } catch (IOException) { }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
