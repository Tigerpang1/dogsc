using System.Diagnostics;

namespace FrameTrail.Media;

public sealed record ExportOptions(int Width = 1920, int Height = 1080, int FrameRate = 30, bool HardwarePreferred = true, bool AudioOnly = false, string? ProjectFilePath = null);
public sealed record ExportProgress(string Stage, double Fraction, TimeSpan Elapsed, TimeSpan? Remaining);
public sealed record ExportResult(string Path, string Encoder, string Renderer, int FrameCount, TimeSpan Elapsed);

public sealed class ExportEngine
{
    public async Task<ExportResult> ExportAsync(FrameTrailProject project, string sourcePath, string outputPath, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken cancellation = default)
    {
        project.Timeline.Validate();
        var requiredExtension = options.AudioOnly ? ".m4a" : ".mp4";
        if (!Path.GetExtension(outputPath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"输出文件扩展名必须为 {requiredExtension}，不能覆盖项目或备份文件。");
        if (options.Width is < 16 or > 7680 || options.Height is < 16 or > 4320 || options.Width % 2 != 0 || options.Height % 2 != 0 || options.FrameRate is not (30 or 60)) throw new ArgumentException("输出尺寸必须是偶数，帧率须为 30 或 60。");
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) throw new IOException("导出文件不能覆盖原始素材。");
        var watch = Stopwatch.StartNew();
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!; Directory.CreateDirectory(folder);
        var stem = ".frametrail-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(folder, stem + (options.AudioOnly ? ".m4a" : ".mp4"));
        var audioPath = Path.Combine(folder, stem + "-audio.m4a");
        var mainAudioPath = Path.Combine(folder, stem + "-main-audio.m4a");
        var tracksAudioPath = Path.Combine(folder, stem + "-tracks-audio.m4a");
        var timelineFile = Path.Combine(folder, stem + "-video.txt");
        var audioFile = Path.Combine(folder, stem + "-audio.txt");
        var tracksAudioFile = Path.Combine(folder, stem + "-tracks-audio.txt");
        Process? decoder = null; Process? encoder = null;
        try
        {
            var info = await MediaTools.ProbeAsync(sourcePath, cancellation);
            progress?.Report(new("准备音轨", 0, watch.Elapsed, null));
            var projectFile = options.ProjectFilePath is { Length: > 0 } ? Path.GetFullPath(options.ProjectFilePath) : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "project.frametrail");
            double? cameraAspectRatio = options.AudioOnly ? null : await SceneAssetLoader.ProbeCameraAspectRatioAsync(project, projectFile, cancellation);
            var audioTracks = project.AudioTracks.ToList();
            if (project.Camera is { Muted: false, Volume: > 0 } cameraAudio)
            {
                var cameraPath = FrameTrailProjectStore.ResolveAssetPath(projectFile, cameraAudio.MediaPath);
                var cameraInfo = await MediaTools.ProbeAsync(cameraPath, cancellation);
                if (cameraInfo.HasAudio)
                {
                    IReadOnlyList<CameraSyncSegment> cameraSegments = cameraAudio.SyncSegments is { Count: > 0 } mappedCamera
                        ? mappedCamera
                        : [new CameraSyncSegment(cameraAudio.SourceStart, cameraAudio.TimelineStart, cameraAudio.Duration)];
                    audioTracks.Add(new AudioTrack
                    {
                        MediaPath = cameraAudio.MediaPath, SourceStart = cameraAudio.SourceStart, Duration = cameraAudio.Duration,
                        OffsetSeconds = cameraAudio.TimelineStart, Volume = cameraAudio.Volume,
                        SourceRate = cameraSegments[0].SourceRate, SyncSegments = cameraSegments.ToList(), Source = AudioTrackSource.Camera
                    });
                }
            }
            var extraTracks = audioTracks.Where(track => MediaTools.HasAudibleAudioTrack(project.Timeline, track, project.Settings.MainAudioVolume, project.Settings.MainAudioMuted)).ToArray();
            var extraPaths = extraTracks.Select(x => FrameTrailProjectStore.ResolveAssetPath(projectFile, x.MediaPath)).ToArray();
            var hasMainAudio = info.HasAudio && MediaTools.HasAudibleMainAudio(project.Timeline, project.Settings.MainAudioVolume, project.Settings.MainAudioMuted);
            if (hasMainAudio)
            {
                await File.WriteAllTextAsync(audioFile, MediaTools.AudioFilter(project.Timeline, project.Settings.MainAudioVolume, project.Settings.MainAudioMuted), cancellation);
                await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", sourcePath, "-filter_complex_script", audioFile, "-map", "[out]", "-vn", "-c:a", "aac", "-b:a", "192k", "-y", mainAudioPath], cancellation);
            }
            var hasExtraAudio = extraTracks.Length > 0;
            if (hasExtraAudio)
            {
                await File.WriteAllTextAsync(tracksAudioFile, MediaTools.AudioTrackFilter(project.Timeline, extraTracks, project.Settings.MainAudioVolume, project.Settings.MainAudioMuted), cancellation);
                var trackArgs = new List<string> { "-v", "error" };
                foreach (var path in extraPaths) trackArgs.AddRange(["-i", path]);
                trackArgs.AddRange(["-filter_complex_script", tracksAudioFile, "-map", "[out]", "-vn", "-c:a", "aac", "-b:a", "192k", "-y", tracksAudioPath]);
                await MediaTools.RunAsync(MediaTools.Ffmpeg, trackArgs, cancellation);
            }
            var hasAudio = hasMainAudio || hasExtraAudio;
            if (hasMainAudio && extraTracks.Length > 0)
                await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-i", mainAudioPath, "-i", tracksAudioPath, "-filter_complex", $"[0:a][1:a]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.98,apad=pad_dur={MediaTools.Number(project.Timeline.Duration)},atrim=duration={MediaTools.Number(project.Timeline.Duration)}[out]", "-map", "[out]", "-c:a", "aac", "-b:a", "192k", "-y", audioPath], cancellation);
            else if (hasMainAudio) File.Move(mainAudioPath, audioPath, true);
            else if (extraTracks.Length > 0) File.Move(tracksAudioPath, audioPath, true);
            else if (options.AudioOnly) throw new InvalidOperationException("项目没有可导出的音轨。");
            if (options.AudioOnly)
            {
                File.Move(audioPath, temporary); await Verify(temporary, project.Timeline.Duration, options.FrameRate, cancellation);
                cancellation.ThrowIfCancellationRequested(); File.Move(temporary, outputPath, true);
                progress?.Report(new("已完成", 1, watch.Elapsed, TimeSpan.Zero));
                return new(outputPath, "aac", "无视频", 0, watch.Elapsed);
            }
            var chosenEncoder = await ChooseEncoder(options.HardwarePreferred, cancellation, options.Width, options.Height);
            // Decode at original dimensions up to output size. This CPU/GPU copy is explicit and measured.
            var decodeWidth = Math.Min(info.Width, options.Width); var decodeHeight = Math.Max(2, (int)Math.Round(info.Height * (double)decodeWidth / info.Width));
            await File.WriteAllTextAsync(timelineFile, MediaTools.VideoFilter(project.Timeline, decodeWidth, decodeHeight, options.FrameRate), cancellation);
            var decArgs = new[] { "-v", "error", "-i", sourcePath, "-filter_complex_script", timelineFile, "-map", "[out]", "-an", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
            decoder = MediaTools.Start(MediaTools.Ffmpeg, decArgs, output: true);
            var decoderErrors = decoder.StandardError.ReadToEndAsync();
            var args = new List<string> { "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{options.Width}x{options.Height}", "-r", options.FrameRate.ToString(), "-i", "pipe:0" };
            if (hasAudio) args.AddRange(["-i", audioPath, "-map", "0:v", "-map", "1:a", "-c:a", "copy"]);
            // RGB scene pixels use an explicit BT.709 limited-range YUV transform.
            // Write matching tags so external players do not infer a different matrix.
            args.AddRange(["-vf", "scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p,setparams=range=limited:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709",
                "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "iec61966-2-1", "-color_primaries", "bt709",
                "-c:v", chosenEncoder, "-pix_fmt", "yuv420p"]);
            if (chosenEncoder == "libx264") args.AddRange(["-preset", "fast", "-crf", "20"]);
            else args.AddRange(["-b:v", "16M"]);
            // The rawvideo input is a finite stream containing exactly the CFR frame count
            // selected below. Do not apply an output -t here: FFmpeg can discard the final
            // frame when its packet extends past a fractional timeline duration, even though
            // that frame's timestamp is still inside the requested interval.
            args.AddRange(["-movflags", "+faststart", "-y", temporary]);
            encoder = MediaTools.Start(MediaTools.Ffmpeg, args, input: true);
            var encoderErrors = encoder.StandardError.ReadToEndAsync();
            using var reg = cancellation.Register(() => { MediaTools.Kill(decoder); MediaTools.Kill(encoder); });
            using var renderer = new GpuFrameRenderer(decodeWidth, decodeHeight, options.Width, options.Height);
            var input = new byte[checked(decodeWidth * decodeHeight * 4)]; var output = new byte[checked(options.Width * options.Height * 4)];
            var total = checked((int)Math.Ceiling(project.Timeline.Duration * options.FrameRate - 1e-7)); var frames = 0;
            var frameWatch = Stopwatch.StartNew(); var evaluator = new PreparedFrameSceneEvaluator(project, cameraAspectRatio);
            using var assetLoader = new SceneAssetLoader(projectFile, options.Width, options.Height);
            while (frames < total)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = 0;
                while (read < input.Length) { var count = await decoder.StandardOutput.BaseStream.ReadAsync(input.AsMemory(read), cancellation); if (count == 0) break; read += count; }
                if (read != input.Length)
                {
                    await decoder.WaitForExitAsync(cancellation);
                    var decodeError = await decoderErrors;
                    if (decoder.ExitCode != 0) throw new IOException("视频解码失败：" + MediaTools.Tail(decodeError));
                    // The CFR filter can round the final fractional frame down. Duplicate only the last complete frame.
                    if (read != 0 || frames == 0 || total - frames > 1) throw new IOException("视频解码提前结束：" + decodeError);
                }
                var scene = evaluator.Evaluate(Math.Min(frames / (double)options.FrameRate, project.Timeline.Duration));
                var layers = await assetLoader.LoadAsync(scene, cancellation);
                renderer.Render(input, scene, output, layers);
                await encoder.StandardInput.BaseStream.WriteAsync(output, cancellation); frames++;
                if (frames % 5 == 0 || frames == total)
                {
                    var remaining = frameWatch.Elapsed.TotalSeconds > 3 ? TimeSpan.FromSeconds(frameWatch.Elapsed.TotalSeconds / frames * (total - frames)) : (TimeSpan?)null;
                    progress?.Report(new($"合成与编码 · {chosenEncoder}", .05 + .9 * frames / total, watch.Elapsed, remaining));
                }
            }
            encoder.StandardInput.Close();
            await decoder.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellation);
            await decoder.WaitForExitAsync(cancellation);
            var finalDecodeError = await decoderErrors;
            if (decoder.ExitCode != 0) throw new IOException("视频解码失败：" + MediaTools.Tail(finalDecodeError));
            await encoder.WaitForExitAsync(cancellation); var error = await encoderErrors;
            if (encoder.ExitCode != 0) throw new IOException("视频编码失败：" + MediaTools.Tail(error));
            progress?.Report(new("核验媒体与封装", .98, watch.Elapsed, null));
            await Verify(temporary, project.Timeline.Duration, options.FrameRate, cancellation);
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, outputPath, true);
            progress?.Report(new("已完成", 1, watch.Elapsed, TimeSpan.Zero));
            return new(outputPath, chosenEncoder, renderer.Backend, frames, watch.Elapsed);
        }
        catch (Exception ex) when (cancellation.IsCancellationRequested && ex is not OperationCanceledException) { throw new OperationCanceledException("导出已取消。", ex, cancellation); }
        finally
        {
            if (decoder != null) { await MediaTools.StopAsync(decoder); decoder.Dispose(); } if (encoder != null) { await MediaTools.StopAsync(encoder); encoder.Dispose(); }
            foreach (var file in new[] { temporary, audioPath, mainAudioPath, tracksAudioPath, timelineFile, audioFile, tracksAudioFile }) if (File.Exists(file)) File.Delete(file);
        }
    }
    private static async Task Verify(string path, double expected, int fps, CancellationToken token)
    {
        var info = await MediaTools.ProbeAsync(path, token);
        if (new FileInfo(path).Length < 512 || Math.Abs(info.Duration - expected) > Math.Max(.12, 2d/fps)) throw new IOException($"成片核验失败：预期 {expected:0.000}s，实际 {info.Duration:0.000}s。");
    }
    public static async Task<string> ChooseEncoder(bool hardware, CancellationToken cancellation, int width = 320, int height = 180)
    {
        foreach (var candidate in hardware ? new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" } : ["libx264"])
        {
            try { await MediaTools.RunAsync(MediaTools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"color=size={width}x{height}:rate=30", "-frames:v", "1", "-c:v", candidate, "-f", "null", "-"], cancellation); return candidate; }
            catch (IOException) { }
        }
        throw new IOException("没有通过实际编码探测的 H.264 编码器。");
    }
}
