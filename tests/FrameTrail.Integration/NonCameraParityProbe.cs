using System.Text.Json;
using FrameTrail;
using FrameTrail.Media;

internal static class NonCameraParityProbe
{
    private const int LandscapeWidth = 320;
    private const int LandscapeHeight = 180;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string repoRoot, string? requestedOutput)
    {
        if (requestedOutput is null) throw new ArgumentException("--noncamera-parity 必须指定 --out 新目录。");
        var outputRoot = Path.GetFullPath(requestedOutput);
        var validationRoot = Path.GetFullPath(Path.Combine(repoRoot, "artifacts", "validation")) + Path.DirectorySeparatorChar;
        if (!outputRoot.StartsWith(validationRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"专项输出必须位于验证目录内：{validationRoot}");

        if (File.Exists(outputRoot)) throw new IOException($"专项输出路径不是目录：{outputRoot}");
        if (Directory.Exists(outputRoot))
        {
            var existingReport = Path.Combine(outputRoot, "result.json");
            if (!File.Exists(existingReport)) throw new IOException($"拒绝覆盖无专项失败报告的已有目录：{outputRoot}");
            using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(existingReport));
            var root = previous.RootElement;
            if (!root.TryGetProperty("GeneratedFixturesOnly", out var generated) || generated.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("OutputDirectory", out var previousPath)
                || !string.Equals(previousPath.GetString(), outputRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"拒绝覆盖非本专项生成的已有目录：{outputRoot}");
        }

        Directory.CreateDirectory(outputRoot);
        var report = new Dictionary<string, object?>
        {
            ["Passed"] = false,
            ["GeneratedFixturesOnly"] = true,
            ["CaptureDevicesUsed"] = false,
            ["OutputDirectory"] = outputRoot,
            ["Ffmpeg"] = MediaTools.Ffmpeg,
            ["Ffprobe"] = MediaTools.Ffprobe,
            ["Checks"] = new Dictionary<string, object?>()
        };

        try
        {
            var checks = (Dictionary<string, object?>)report["Checks"]!;
            var cursorSource = Path.Combine(outputRoot, "cursor-source.mp4");
            await CreateSolidVideoAsync(cursorSource, "0x78899A", 3.2);
            var cursorInfo = await MediaTools.ProbeAsync(cursorSource);
            Require(cursorInfo.Width == LandscapeWidth && cursorInfo.Height == LandscapeHeight && cursorInfo.FrameRate == 30 && cursorInfo.Duration >= 3,
                "指针固定素材元数据不符。");

            var pointerEvents = new List<PointerEvent>
            {
                new(.15, new(.25, .5)),
                new(.4, new(.5, .5), PointerEventKind.LeftClick),
                new(.55, new(.5, .5))
            };
            const int cursorOutputWidth = 640, cursorOutputHeight = 360;
            var baseSettings = CreateSettings(cursorOutputWidth, cursorOutputHeight, new CursorSettings
            {
                Visible = true, Style = CursorStyle.FollowRecorded, Scale = 2,
                ClickEffectsEnabled = false, ClickEffect = ClickEffectStyle.None,
                HideWhenIdle = false, MotionTiltEnabled = false
            });

            var baseline = await ExportPointerCaseAsync("cursor-visible", pointerEvents, baseSettings, cursorSource, outputRoot);
            var clickSettings = CopySettings(baseSettings);
            clickSettings.Cursor!.ClickEffectsEnabled = true;
            clickSettings.Cursor.ClickEffect = ClickEffectStyle.Ring;
            clickSettings.Cursor.ClickDuration = .5;
            clickSettings.Cursor.ClickScale = 3;
            clickSettings.Cursor.ClickOpacity = 1;
            var click = await ExportPointerCaseAsync("cursor-click-ring", pointerEvents, clickSettings, cursorSource, outputRoot);
            var idleSettings = CopySettings(baseSettings);
            idleSettings.Cursor!.HideWhenIdle = true;
            idleSettings.Cursor.IdleSeconds = .2;
            var idle = await ExportPointerCaseAsync("cursor-hide-idle", pointerEvents, idleSettings, cursorSource, outputRoot);
            var hiddenSettings = CopySettings(baseSettings);
            hiddenSettings.Cursor!.Visible = false;
            var hidden = await ExportPointerCaseAsync("cursor-always-hidden", pointerEvents, hiddenSettings, cursorSource, outputRoot);

            var baselineClickFrame = await DecodeAndSaveAsync(baseline.Path, .6, cursorOutputWidth, cursorOutputHeight, "cursor-visible-click-time.png", outputRoot);
            var ringFrame = await DecodeAndSaveAsync(click.Path, .6, cursorOutputWidth, cursorOutputHeight, "cursor-click-ring-frame.png", outputRoot);
            var baselineAfterClickFrame = await DecodeAndSaveAsync(baseline.Path, 1.1, cursorOutputWidth, cursorOutputHeight, "cursor-visible-after-click.png", outputRoot);
            var ringAfterClickFrame = await DecodeAndSaveAsync(click.Path, 1.1, cursorOutputWidth, cursorOutputHeight, "cursor-click-ring-after-window.png", outputRoot);
            var visibleFrame = await DecodeAndSaveAsync(baseline.Path, .9, cursorOutputWidth, cursorOutputHeight, "cursor-visible-idle-time.png", outputRoot);
            var idleFrame = await DecodeAndSaveAsync(idle.Path, .9, cursorOutputWidth, cursorOutputHeight, "cursor-hide-idle-frame.png", outputRoot);
            var hiddenFrame = await DecodeAndSaveAsync(hidden.Path, .9, cursorOutputWidth, cursorOutputHeight, "cursor-always-hidden-frame.png", outputRoot);

            var clickDifference = CountDifferentPixels(baselineClickFrame, ringFrame, cursorOutputWidth, cursorOutputHeight, 320, 180, 50);
            Require(clickDifference > 8, $"点击环配置没有在点击窗口的成片像素中形成可见差异（变化像素 {clickDifference}）。");
            var ringAfterClickDifference = CountDifferentPixels(baselineAfterClickFrame, ringAfterClickFrame, cursorOutputWidth, cursorOutputHeight, 320, 180, int.MaxValue);
            Require(ringAfterClickDifference <= 2, $"点击环结束后仍与普通指针帧不一致（差异像素 {ringAfterClickDifference}）。");
            var visibleCursorDifference = CountDifferentPixels(visibleFrame, hiddenFrame, cursorOutputWidth, cursorOutputHeight, 320, 180, 45);
            Require(visibleCursorDifference > 8, "可见指针与始终隐藏导出在指针区域没有形成可见差异。");
            var idleHiddenDifference = CountDifferentPixels(idleFrame, hiddenFrame, cursorOutputWidth, cursorOutputHeight, 0, 0, int.MaxValue);
            Require(idleHiddenDifference <= 2, "静止隐藏样本与始终隐藏样本成片像素不一致。");
            checks["FT21"] = new
            {
                Passed = true,
                Fixture = "同一固定静态源和同一指针轨迹；分别导出普通指针、Ring点击反馈、静止隐藏、始终隐藏。",
                ClickRingChangedPixelsAt06Seconds = clickDifference,
                ClickRingAfterWindowChangedPixelsAt11Seconds = ringAfterClickDifference,
                VisibleVsAlwaysHiddenChangedPixelsAt09SecondsNearPointer = visibleCursorDifference,
                IdleVsAlwaysHiddenChangedPixelsAt09Seconds = idleHiddenDifference,
                Frames = new[] { "cursor-visible-click-time.png", "cursor-click-ring-frame.png", "cursor-visible-after-click.png", "cursor-click-ring-after-window.png", "cursor-visible-idle-time.png", "cursor-hide-idle-frame.png", "cursor-always-hidden-frame.png" }
            };

            var cutEvents = new List<PointerEvent>
            {
                new(.4, new(.2, .5)),
                new(.7, new(.2, .5)),
                new(.9, new(.8, .5)),
                new(1.7, new(.8, .5)),
                new(1.9, new(.8, .5)),
                new(2.1, new(.2, .5))
            };
            var cutProject = CreateProject("pointer-cut-calibrated", Path.GetFileName(cursorSource), cursorInfo, cursorOutputWidth, cursorOutputHeight,
                new ProjectTimeline
                {
                    Segments = [new() { SourceStart = 0, SourceDuration = 1 }, new() { SourceStart = 2, SourceDuration = 1 }]
                }, CreateSettings(320, 180, new CursorSettings
                {
                    Visible = true, Style = CursorStyle.FollowRecorded, Scale = 1,
                    ClickEffectsEnabled = false, ClickEffect = ClickEffectStyle.None,
                    HideWhenIdle = false, MotionTiltEnabled = false
                }));
            cutProject.PointerEvents = cutEvents;
            cutProject.MouseClock = new(.2, 1);
            var cutExport = await ExportAsync("pointer-cut-calibrated", cutProject, cursorSource, outputRoot, cursorOutputWidth, cursorOutputHeight);
            var uncalibratedCutProject = CopyProject(cutProject);
            uncalibratedCutProject.DisplayName = "pointer-cut-uncalibrated-control";
            uncalibratedCutProject.MouseClock = new();
            var uncalibratedCutExport = await ExportAsync("pointer-cut-uncalibrated-control", uncalibratedCutProject, cursorSource, outputRoot, cursorOutputWidth, cursorOutputHeight);
            var cutBefore = await DecodeAndSaveAsync(cutExport.Path, .9, cursorOutputWidth, cursorOutputHeight, "pointer-cut-before.png", outputRoot);
            var cutAfter = await DecodeAndSaveAsync(cutExport.Path, 1.1, cursorOutputWidth, cursorOutputHeight, "pointer-cut-after.png", outputRoot);
            var uncalibratedBefore = await DecodeAndSaveAsync(uncalibratedCutExport.Path, .9, cursorOutputWidth, cursorOutputHeight, "pointer-cut-uncalibrated-before.png", outputRoot);
            var uncalibratedAfter = await DecodeAndSaveAsync(uncalibratedCutExport.Path, 1.1, cursorOutputWidth, cursorOutputHeight, "pointer-cut-uncalibrated-after.png", outputRoot);
            var blank = await MediaTools.DecodeFrameAsync(hidden.Path, .9, cursorOutputWidth, cursorOutputHeight, CancellationToken.None);
            var leftCursorPixels = CountDifferentPixels(cutBefore, blank, cursorOutputWidth, cursorOutputHeight, 128, 180, 40);
            var rightCursorPixels = CountDifferentPixels(cutAfter, blank, cursorOutputWidth, cursorOutputHeight, 512, 180, 40);
            Require(leftCursorPixels > 8 && rightCursorPixels > 8,
                "剪切前后解码帧未在预期的左右指针位置呈现光标像素。");
            Require(CountDifferentPixels(cutBefore, blank, cursorOutputWidth, cursorOutputHeight, 512, 180, 40) < leftCursorPixels
                    && CountDifferentPixels(cutAfter, blank, cursorOutputWidth, cursorOutputHeight, 128, 180, 40) < rightCursorPixels,
                "剪切前后的光标像素没有从预期左侧位置切换到右侧位置。");
            var uncalibratedBeforeRightPixels = CountDifferentPixels(uncalibratedBefore, blank, cursorOutputWidth, cursorOutputHeight, 512, 180, 40);
            var uncalibratedAfterLeftPixels = CountDifferentPixels(uncalibratedAfter, blank, cursorOutputWidth, cursorOutputHeight, 128, 180, 40);
            var calibrationChangedBeforeCut = CountDifferentPixels(cutBefore, uncalibratedBefore, cursorOutputWidth, cursorOutputHeight, 320, 180, int.MaxValue);
            var calibrationChangedAfterCut = CountDifferentPixels(cutAfter, uncalibratedAfter, cursorOutputWidth, cursorOutputHeight, 320, 180, int.MaxValue);
            Require(uncalibratedBeforeRightPixels > leftCursorPixels && uncalibratedAfterLeftPixels > rightCursorPixels
                    && calibrationChangedBeforeCut > 8 && calibrationChangedAfterCut > 8,
                "同一导出时刻的未校准对照没有呈现不同指针位置，无法证明偏移参数生效。");

            var uncutProject = CopyProject(cutProject);
            uncutProject.Timeline = new ProjectTimeline { Segments = [new() { SourceStart = 0, SourceDuration = 3 }] };
            uncutProject.MouseClock = new();
            var uncalibratedEvaluator = new PreparedFrameSceneEvaluator(uncutProject);
            var calibratedEvaluator = new PreparedFrameSceneEvaluator(cutProject);
            var smoothingBefore = uncalibratedEvaluator.Evaluate(.7).Cursor;
            var smoothingBeforeCalibrated = calibratedEvaluator.Evaluate(.9).Cursor;
            var smoothingAfter = uncalibratedEvaluator.Evaluate(1.9).Cursor;
            var smoothingAfterCalibrated = calibratedEvaluator.Evaluate(1.1).Cursor;
            Require(smoothingBefore == smoothingBeforeCalibrated && smoothingAfter == smoothingAfterCalibrated
                    && cutProject.Settings.PointerSmoothing == uncutProject.Settings.PointerSmoothing,
                "校准后的剪切两侧样本没有保留未校准时相同的指针平滑结果。");
            checks["FT22"] = new
            {
                Passed = true,
                Calibration = cutProject.MouseClock,
                PointerSmoothing = cutProject.Settings.PointerSmoothing.ToString(),
                BeforeCut = new { OutputTime = .9, ExpectedPosition = new NormalizedPoint(.2, .5), ChangedPixelsNearExpected = leftCursorPixels, Frame = "pointer-cut-before.png" },
                AfterCut = new { OutputTime = 1.1, ExpectedPosition = new NormalizedPoint(.8, .5), ChangedPixelsNearExpected = rightCursorPixels, Frame = "pointer-cut-after.png" },
                UncalibratedSameOutputTimeControl = new
                {
                    BeforeCutExpectedPosition = new NormalizedPoint(.8, .5), BeforeCutChangedPixels = uncalibratedBeforeRightPixels,
                    AfterCutExpectedPosition = new NormalizedPoint(.2, .5), AfterCutChangedPixels = uncalibratedAfterLeftPixels,
                    CalibrationDifferencePixels = new[] { calibrationChangedBeforeCut, calibrationChangedAfterCut },
                    Frames = new[] { "pointer-cut-uncalibrated-before.png", "pointer-cut-uncalibrated-after.png" }
                },
                CalibrationPreservedSmoothingSamples = true,
                Basis = "位置断言取自实际导出 MP4 解码像素；平滑保持由同一事件轨迹的校准/未校准评价器对应采样点逐值比较。"
            };

            var quadrantSource = Path.Combine(outputRoot, "quadrants.ppm");
            await CreateQuadrantSourceAsync(quadrantSource);
            var quadrantVideo = Path.Combine(outputRoot, "quadrants.mp4");
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
            [
                "-v", "error", "-loop", "1", "-framerate", "30", "-i", quadrantSource,
                "-t", "1.2", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "yuv420p", "-y", quadrantVideo
            ]);
            var quadrantInfo = await MediaTools.ProbeAsync(quadrantVideo);
            Require(quadrantInfo.Width == LandscapeWidth && quadrantInfo.Height == LandscapeHeight && quadrantInfo.Duration >= 1,
                "9:16 裁切素材元数据不符。");
            var cropSettings = CreateSettings(360, 640, new CursorSettings { Visible = false });
            cropSettings.Padding = .1;
            cropSettings.Shadow = 0;
            cropSettings.CornerRadius = 0;
            const double cropX = .58, cropY = .1, cropWidth = .32, cropHeight = .8, padding = .1, contentScale = 1;
            const double contentX = .5, contentY = .5;
            cropSettings.Canvas = new CanvasSettings
            {
                AspectWidth = 9, AspectHeight = 16,
                Crop = new(cropX, cropY, cropWidth, cropHeight),
                ContentPosition = new(contentX, contentY),
                ContentScale = contentScale
            };
            cropSettings.Padding = padding;
            var cropProject = CreateProject("portrait-noncenter-crop", Path.GetFileName(quadrantVideo), quadrantInfo,
                360, 640, new ProjectTimeline { Segments = [new() { SourceDuration = 1.2 }] }, cropSettings);
            var cropExport = await ExportAsync("portrait-noncenter-crop", cropProject, quadrantVideo, outputRoot, 360, 640);
            var cropInfo = await MediaTools.ProbeAsync(cropExport.Path);
            Require(cropInfo.Width == 360 && cropInfo.Height == 640 && cropInfo.FrameRate == 30,
                $"9:16 导出尺寸/帧率不符：{cropInfo.Width}x{cropInfo.Height}@{cropInfo.FrameRate}。");
            var cropFrame = await DecodeAndSaveAsync(cropExport.Path, .5, 360, 640, "portrait-noncenter-crop-frame.png", outputRoot);
            var expectedRect = CalculateExpectedContentRect(360, 640, LandscapeWidth, LandscapeHeight, padding, contentScale, contentX, contentY);
            var topLeft = Pixel(cropFrame, (int)Math.Round(expectedRect.Left + expectedRect.Width * .22), (int)Math.Round(expectedRect.Top + expectedRect.Height * .2), 360);
            var topRight = Pixel(cropFrame, (int)Math.Round(expectedRect.Left + expectedRect.Width * .78), (int)Math.Round(expectedRect.Top + expectedRect.Height * .2), 360);
            var bottomLeft = Pixel(cropFrame, (int)Math.Round(expectedRect.Left + expectedRect.Width * .22), (int)Math.Round(expectedRect.Top + expectedRect.Height * .8), 360);
            var bottomRight = Pixel(cropFrame, (int)Math.Round(expectedRect.Left + expectedRect.Width * .78), (int)Math.Round(expectedRect.Top + expectedRect.Height * .8), 360);
            var corners = new[] { Pixel(cropFrame, 4, 4, 360), Pixel(cropFrame, 355, 4, 360), Pixel(cropFrame, 4, 635, 360), Pixel(cropFrame, 355, 635, 360) };
            Require(IsRed(topLeft) && IsBlue(topRight) && IsGreen(bottomLeft) && IsYellow(bottomRight),
                $"9:16 非居中裁切四个内容样点不符：TL={topLeft}, TR={topRight}, BL={bottomLeft}, BR={bottomRight}。");
            var background = new PixelRgb(0x18, 0x23, 0x30);
            Require(corners.All(cornerPixel => ColorDistance(cornerPixel, background) < 42),
                $"9:16 四角留白没有保持背景色：{string.Join(";", corners)}。");
            var boundaryLeftOutside = (int)Math.Floor(expectedRect.Left) - 2;
            var boundaryLeftInside = (int)Math.Ceiling(expectedRect.Left) + 2;
            var boundaryRightInside = (int)Math.Floor(expectedRect.Right) - 2;
            var boundaryRightOutside = (int)Math.Ceiling(expectedRect.Right) + 2;
            var boundaryTopOutside = (int)Math.Floor(expectedRect.Top) - 2;
            var boundaryTopInside = (int)Math.Ceiling(expectedRect.Top) + 2;
            var boundaryBottomInside = (int)Math.Floor(expectedRect.Bottom) - 2;
            var boundaryBottomOutside = (int)Math.Ceiling(expectedRect.Bottom) + 2;
            var edgeMidX = (int)Math.Round((expectedRect.Left + expectedRect.Right) * .5);
            var edgeMidY = (int)Math.Round((expectedRect.Top + expectedRect.Bottom) * .5);
            var edgeSamples = new
            {
                LeftOutside = Pixel(cropFrame, boundaryLeftOutside, edgeMidY, 360), LeftInside = Pixel(cropFrame, boundaryLeftInside, edgeMidY, 360),
                RightInside = Pixel(cropFrame, boundaryRightInside, edgeMidY, 360), RightOutside = Pixel(cropFrame, boundaryRightOutside, edgeMidY, 360),
                TopOutside = Pixel(cropFrame, edgeMidX, boundaryTopOutside, 360), TopInside = Pixel(cropFrame, edgeMidX, boundaryTopInside, 360),
                BottomInside = Pixel(cropFrame, edgeMidX, boundaryBottomInside, 360), BottomOutside = Pixel(cropFrame, edgeMidX, boundaryBottomOutside, 360)
            };
            Require(ColorDistance(edgeSamples.LeftOutside, background) < 42 && IsGreen(edgeSamples.LeftInside)
                    && IsYellow(edgeSamples.RightInside) && ColorDistance(edgeSamples.RightOutside, background) < 42
                    && ColorDistance(edgeSamples.TopOutside, background) < 42 && IsRed(edgeSamples.TopInside)
                    && IsGreen(edgeSamples.BottomInside) && ColorDistance(edgeSamples.BottomOutside, background) < 42,
                $"9:16 四条内容边界内外样点不符合独立几何预测：{JsonSerializer.Serialize(edgeSamples)}。");

            // A separate coordinate-coded source makes the configured source crop observable.
            // The full-crop export is a negative control at the same output time and canvas geometry.
            var cropChartSource = Path.Combine(outputRoot, "crop-coordinate-chart.ppm");
            await CreateCropCoordinateSourceAsync(cropChartSource);
            var cropChartVideo = Path.Combine(outputRoot, "crop-coordinate-chart.mp4");
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
            [
                "-v", "error", "-loop", "1", "-framerate", "30", "-i", cropChartSource,
                "-t", "1.2", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "yuv420p", "-y", cropChartVideo
            ]);
            var chartInfo = await MediaTools.ProbeAsync(cropChartVideo);
            var cropChartProject = CopyProject(cropProject);
            cropChartProject.DisplayName = "crop-coordinate-proof";
            cropChartProject.MediaPath = Path.GetFileName(cropChartVideo);
            cropChartProject.Media = new() { Width = chartInfo.Width, Height = chartInfo.Height, Duration = chartInfo.Duration, FrameRate = chartInfo.FrameRate };
            var cropChartExport = await ExportAsync("crop-coordinate-proof", cropChartProject, cropChartVideo, outputRoot, 360, 640);
            var fullCropProject = CopyProject(cropChartProject);
            fullCropProject.DisplayName = "full-crop-negative-control";
            fullCropProject.Settings.Canvas!.Crop = NormalizedRect.Full;
            var fullCropExport = await ExportAsync("full-crop-negative-control", fullCropProject, cropChartVideo, outputRoot, 360, 640);
            var cropChartFrame = await DecodeAndSaveAsync(cropChartExport.Path, .5, 360, 640, "crop-coordinate-proof-frame.png", outputRoot);
            var fullCropFrame = await DecodeAndSaveAsync(fullCropExport.Path, .5, 360, 640, "full-crop-negative-control-frame.png", outputRoot);
            var cropSamples = new[]
            {
                VerifyCropSample("left source edge", .1, .5),
                VerifyCropSample("right source edge", .9, .5),
                VerifyCropSample("top source edge", .5, .05),
                VerifyCropSample("bottom source edge", .5, .95)
            };
            Require(cropSamples.All(sample => sample.CropMatchesExpected && sample.FullMatchesExpected && sample.DiffersFromFull),
                $"裁切成片未呈现独立计算的源坐标，或完整裁切负对照未形成差异：{JsonSerializer.Serialize(cropSamples)}。");
            checks["FT23"] = new
            {
                Passed = true,
                Output = new { Width = cropInfo.Width, Height = cropInfo.Height, FrameRate = cropInfo.FrameRate, Duration = cropInfo.Duration, Path = Path.GetFileName(cropExport.Path) },
                Canvas = new { Aspect = "9:16", Crop = cropSettings.Canvas!.Crop, cropSettings.Padding, cropSettings.Canvas.ContentPosition, cropSettings.Canvas.ContentScale },
                ExpectedScreenRectFromIndependentGeometry = expectedRect,
                DecodedPixels = new
                {
                    TopLeft = new { topLeft.R, topLeft.G, topLeft.B },
                    TopRight = new { topRight.R, topRight.G, topRight.B },
                    BottomLeft = new { bottomLeft.R, bottomLeft.G, bottomLeft.B },
                    BottomRight = new { bottomRight.R, bottomRight.G, bottomRight.B },
                    Corners = corners.Select(corner => new { corner.R, corner.G, corner.B }),
                    Edges = edgeSamples
                },
                CropCoordinateNegativeControl = new
                {
                    CropFrame = "crop-coordinate-proof-frame.png",
                    FullCropFrame = "full-crop-negative-control-frame.png",
                    SameOutputTimeSeconds = .5,
                    Samples = cropSamples,
                    Basis = "由独立源坐标图案计算裁切与 Full 两种映射，再核对实际成片解码 RGB。忽略 Crop 会使四个样点的预期灰度或相对 Full 差异断言失败。"
                },
                Frame = "portrait-noncenter-crop-frame.png",
                Basis = "输出 MP4 的 ffprobe 尺寸/帧率及实际解码帧四象限颜色、留白边界和独立源坐标裁切对照。"
            };

            CropSample VerifyCropSample(string edge, double localX, double localY)
            {
                var x = (int)Math.Round(expectedRect.Left + expectedRect.Width * localX);
                var y = (int)Math.Round(expectedRect.Top + expectedRect.Height * localY);
                var crop = cropChartProject.Settings.Canvas!.Crop;
                var croppedSourceX = crop.X + localX * crop.Width;
                var croppedSourceY = crop.Y + localY * crop.Height;
                var cropExpected = CropChartGray(croppedSourceX, croppedSourceY);
                var fullExpected = CropChartGray(localX, localY);
                var croppedPixel = Pixel(cropChartFrame, x, y, 360);
                var fullPixel = Pixel(fullCropFrame, x, y, 360);
                var cropMatches = ColorDistance(croppedPixel, new(cropExpected, cropExpected, cropExpected)) < 34;
                var fullMatches = ColorDistance(fullPixel, new(fullExpected, fullExpected, fullExpected)) < 34;
                var differsFromFull = ColorDistance(croppedPixel, fullPixel) >= 14;
                return new(edge, localX, localY, new(x, y), new(croppedSourceX, croppedSourceY), new(localX, localY),
                    cropExpected, fullExpected, croppedPixel, fullPixel, cropMatches, fullMatches, differsFromFull,
                    ColorDistance(croppedPixel, fullPixel));
            }

            report["Passed"] = true;
            report["CompletedUtc"] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            report["Error"] = ex.ToString();
            report["FailedUtc"] = DateTimeOffset.UtcNow;
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "result.json"), JsonSerializer.Serialize(report, JsonOptions));
            Console.Error.WriteLine(ex);
            return 1;
        }

        await File.WriteAllTextAsync(Path.Combine(outputRoot, "result.json"), JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"通过：FT21–FT23 非摄像头实际导出专项；{outputRoot}");
        return 0;
    }

    private static FrameTrailSettings CreateSettings(int width, int height, CursorSettings cursor) => new()
    {
        CanvasWidth = width,
        CanvasHeight = height,
        Background = "#182330",
        Padding = 0,
        CornerRadius = 0,
        Shadow = 0,
        CursorVisible = true,
        AutoZoom = false,
        PointerSmoothing = PointerSmoothing.Rapid,
        Cursor = cursor
    };

    private static FrameTrailProject CreateProject(string name, string mediaName, MediaInfo media, int width, int height,
        ProjectTimeline timeline, FrameTrailSettings settings) => new()
    {
        DisplayName = name,
        MediaPath = mediaName,
        Media = new() { Width = media.Width, Height = media.Height, Duration = media.Duration, FrameRate = media.FrameRate },
        Timeline = timeline,
        Settings = settings
    };

    private static async Task<(string Path, ExportResult Result, MediaInfo Probe)> ExportPointerCaseAsync(string name,
        List<PointerEvent> events, FrameTrailSettings settings, string source, string outputRoot)
    {
        var info = await MediaTools.ProbeAsync(source);
        var project = CreateProject(name, Path.GetFileName(source), info, settings.CanvasWidth, settings.CanvasHeight,
            new ProjectTimeline { Segments = [new() { SourceDuration = 1.6 }] }, settings);
        project.PointerEvents = events.ToList();
        return await ExportAsync(name, project, source, outputRoot, settings.CanvasWidth, settings.CanvasHeight);
    }

    private static async Task<(string Path, ExportResult Result, MediaInfo Probe)> ExportAsync(string name,
        FrameTrailProject project, string source, string outputRoot, int width, int height)
    {
        var projectPath = Path.Combine(outputRoot, name + ".frametrail");
        var outputPath = Path.Combine(outputRoot, name + ".mp4");
        FrameTrailProjectStore.Save(projectPath, project);
        var result = await new ExportEngine().ExportAsync(project, source, outputPath,
            new ExportOptions(width, height, 30, HardwarePreferred: false, ProjectFilePath: projectPath));
        var probe = await MediaTools.ProbeAsync(outputPath);
        Require(probe.Width == width && probe.Height == height && probe.FrameRate == 30 && probe.Duration > 0,
            $"导出成片元数据不符：{outputPath}；实际 {probe.Width}x{probe.Height}@{probe.FrameRate}，{probe.Duration:0.###}s。");
        return (outputPath, result, probe);
    }

    private static async Task<byte[]> DecodeAndSaveAsync(string path, double time, int width, int height, string fileName, string outputRoot)
    {
        var pixels = await MediaTools.DecodeFrameAsync(path, time, width, height, CancellationToken.None);
        var framePath = Path.Combine(outputRoot, fileName);
        await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-ss", MediaTools.Number(time), "-i", path, "-frames:v", "1", "-y", framePath]);
        return pixels;
    }

    private static async Task CreateSolidVideoAsync(string path, string color, double duration) =>
        _ = await MediaTools.RunAsync(MediaTools.Ffmpeg,
            ["-v", "error", "-f", "lavfi", "-i", $"color=c={color}:s={LandscapeWidth}x{LandscapeHeight}:r=30:d={MediaTools.Number(duration)}",
                "-an", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "0", "-pix_fmt", "yuv420p", "-y", path]);

    private static async Task CreateQuadrantSourceAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{LandscapeWidth} {LandscapeHeight}\n255\n");
        await stream.WriteAsync(header);
        var row = new byte[LandscapeWidth * 3];
        for (var y = 0; y < LandscapeHeight; y++)
        {
            for (var x = 0; x < LandscapeWidth; x++)
            {
                var top = y < LandscapeHeight / 2;
                var leftSection = x < LandscapeWidth * .75;
                var color = (top, leftSection) switch
                {
                    (true, true) => (R: (byte)230, G: (byte)32, B: (byte)32),
                    (true, false) => (R: (byte)32, G: (byte)32, B: (byte)230),
                    (false, true) => (R: (byte)32, G: (byte)210, B: (byte)32),
                    _ => (R: (byte)230, G: (byte)210, B: (byte)32)
                };
                var offset = x * 3;
                row[offset] = color.R; row[offset + 1] = color.G; row[offset + 2] = color.B;
            }
            await stream.WriteAsync(row);
        }
    }

    private static async Task CreateCropCoordinateSourceAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{LandscapeWidth} {LandscapeHeight}\n255\n");
        await stream.WriteAsync(header);
        var row = new byte[LandscapeWidth * 3];
        for (var y = 0; y < LandscapeHeight; y++)
        {
            var rowBand = Math.Min(31, y * 32 / LandscapeHeight);
            for (var x = 0; x < LandscapeWidth; x++)
            {
                var columnBand = Math.Min(15, x * 16 / LandscapeWidth);
                var gray = (byte)((columnBand * 24 + rowBand * 5) & 0xff);
                var offset = x * 3;
                row[offset] = gray; row[offset + 1] = gray; row[offset + 2] = gray;
            }
            await stream.WriteAsync(row);
        }
    }

    private static byte CropChartGray(double sourceX, double sourceY)
    {
        var columnBand = Math.Clamp((int)Math.Floor(sourceX * 16), 0, 15);
        var rowBand = Math.Clamp((int)Math.Floor(sourceY * 32), 0, 31);
        return (byte)((columnBand * 24 + rowBand * 5) & 0xff);
    }

    private static FrameTrailSettings CopySettings(FrameTrailSettings settings) =>
        JsonSerializer.Deserialize<FrameTrailSettings>(JsonSerializer.Serialize(settings))!;

    private static FrameTrailProject CopyProject(FrameTrailProject project) =>
        JsonSerializer.Deserialize<FrameTrailProject>(JsonSerializer.Serialize(project))!;

    private static int CountDifferentPixels(byte[] first, byte[] second, int width, int height, int centerX, int centerY, int radius)
    {
        var count = 0;
        var left = radius == int.MaxValue ? 0 : Math.Max(0, centerX - radius);
        var right = radius == int.MaxValue ? width : Math.Min(width, centerX + radius + 1);
        var top = radius == int.MaxValue ? 0 : Math.Max(0, centerY - radius);
        var bottom = radius == int.MaxValue ? height : Math.Min(height, centerY + radius + 1);
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var offset = (y * width + x) * 4;
            if (Math.Abs(first[offset] - second[offset]) > 24 || Math.Abs(first[offset + 1] - second[offset + 1]) > 24
                || Math.Abs(first[offset + 2] - second[offset + 2]) > 24) count++;
        }
        return count;
    }

    private static PixelRgb Pixel(byte[] pixels, int x, int y, int width)
    {
        var offset = (y * width + x) * 4;
        return new(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static bool IsRed(PixelRgb pixel) => pixel.R > pixel.G + 45 && pixel.R > pixel.B + 45;
    private static bool IsGreen(PixelRgb pixel) => pixel.G > pixel.R + 45 && pixel.G > pixel.B + 45;
    private static bool IsBlue(PixelRgb pixel) => pixel.B > pixel.R + 45 && pixel.B > pixel.G + 45;
    private static bool IsYellow(PixelRgb pixel) => pixel.R > pixel.B + 45 && pixel.G > pixel.B + 45 && Math.Abs(pixel.R - pixel.G) < 70;
    private static int ColorDistance(PixelRgb left, PixelRgb right) =>
        Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);

    private static ExpectedScreenRect CalculateExpectedContentRect(int outputWidth, int outputHeight, int sourceWidth, int sourceHeight,
        double padding, double contentScale, double positionX, double positionY)
    {
        var availableWidth = outputWidth * (1 - 2 * padding);
        var availableHeight = outputHeight * (1 - 2 * padding);
        var fit = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
        var width = sourceWidth * fit * contentScale;
        var height = sourceHeight * fit * contentScale;
        var centerX = outputWidth * positionX;
        var centerY = outputHeight * positionY;
        return new(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2, width, height);
    }

    private sealed record ExpectedScreenRect(double Left, double Top, double Right, double Bottom, double Width, double Height);
    private sealed record PixelPoint(int X, int Y);
    private sealed record SourcePoint(double X, double Y);
    private sealed record PixelRgb(byte R, byte G, byte B);
    private sealed record CropSample(string Edge, double LocalX, double LocalY, PixelPoint OutputPixel,
        SourcePoint CroppedSource, SourcePoint FullSource, byte CropExpectedGray, byte FullExpectedGray,
        PixelRgb DecodedCropped, PixelRgb DecodedFull, bool CropMatchesExpected, bool FullMatchesExpected,
        bool DiffersFromFull, int DifferenceFromFull);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
