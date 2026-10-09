using System.Text;
using System.Text.Json;

namespace FrameTrail;

/// <summary>
/// Reads a bounded subset of the DogSC project package and creates a separate
/// FrameTrail project directory. The source package is never modified.
/// </summary>
public static class DogScProjectImporter
{
    public const int MinimumDogScSchemaVersion = 23;
    public const int MaximumDogScSchemaVersion = 27;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 96
    };

    /// <summary>
    /// Imports a recorded .dogscproject folder. Callers must probe the original
    /// screen recording first and supply its dimensions and duration. The
    /// destination folder must not exist.
    /// </summary>
    public static DogScProjectImportResult Import(
        string sourcePackageDirectory,
        string destinationDirectory,
        MediaAsset screenMedia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePackageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(screenMedia);

        var sourceRoot = Path.GetFullPath(sourcePackageDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"找不到 DogSC 项目包：{sourceRoot}");
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("拒绝通过链接或重解析点导入 DogSC 项目根目录。");
        if (!sourceRoot.EndsWith(".dogscproject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入源必须是 .dogscproject 项目目录。");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"导入目标已存在，未覆盖任何文件：{destination}");
        if (IsSameOrChildPath(sourceRoot, destination))
            throw new InvalidDataException("导入目标不能位于原项目包内部。");
        if (IsSameOrChildPath(destination, sourceRoot))
            throw new InvalidDataException("导入目标不能覆盖原项目包或其父目录。");
        if (!ValidMediaInfo(screenMedia))
            throw new InvalidDataException("主屏幕视频的媒体信息无效。");

        var manifestPath = Path.Combine(sourceRoot, "project.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("DogSC 项目包缺少 project.json。", manifestPath);
        RejectReparsePoints(sourceRoot, manifestPath);
        if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("DogSC project.json 超过 16 MiB 安全上限。");

        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), DocumentOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("DogSC project.json 根节点不是 JSON 对象。");
        var version = Int(root, "version", 0);
        if (version is < MinimumDogScSchemaVersion or > MaximumDogScSchemaVersion)
            throw new InvalidDataException(
                $"仅接受 DogSC 项目 schema {MinimumDogScSchemaVersion}–{MaximumDogScSchemaVersion}；发现版本 {version}。原项目未修改。"
            );

        var media = Get(root, "media");
        var screen = Get(media, "screen");
        var screenPath = String(screen, "relativePath");
        if (string.IsNullOrWhiteSpace(screenPath))
            throw new InvalidDataException("此 DogSC 项目没有可导入的屏幕录制素材。");

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("导入目标路径无效。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".frametrail-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var warnings = new List<string>();
            var displayName = String(root, "title").Trim();
            if (displayName.Length > 240)
            {
                displayName = displayName[..240];
                warnings.Add("DogSC 项目标题超过 FrameTrail 名称长度上限；导入名称已截短至 240 个字符。");
            }
            var project = new FrameTrailProject
            {
                DisplayName = displayName.Length == 0 ? Path.GetFileNameWithoutExtension(sourceRoot) : displayName,
                Media = screenMedia,
                Settings = new FrameTrailSettings()
            };

            project.MediaPath = CopyProjectAsset(sourceRoot, staging, screenPath, "media/screen", "", warnings);
            MapTimeline(root, sourceRoot, staging, project, screenMedia.Duration, warnings);
            ValidateImportedTimeline(project, screenMedia.Duration);
            MapPointerEvents(sourceRoot, staging, media, project, warnings);
            MapMarkers(root, project, warnings);
            MapCanvas(root, sourceRoot, staging, project, warnings);
            MapOpening(root, project, warnings);
            MapCursor(root, project, warnings);
            MapCameraAndAudio(root, sourceRoot, staging, media, project, screenMedia.Duration, warnings);

            var projectFile = Path.Combine(staging, "project.frametrail");
            FrameTrailProjectStore.Save(projectFile, project);
            var report = new DogScProjectImportReport(
                SourcePackage: sourceRoot,
                SourceSchemaVersion: version,
                DestinationProject: Path.Combine(destination, "project.frametrail"),
                ImportedMedia: EnumerateImportedMedia(project),
                Warnings: warnings.Distinct(StringComparer.Ordinal).ToArray(),
                PreservationNote: "原 .dogscproject 未改写；目标目录是独立副本。仅报告列出的字段进入 FrameTrail 模型，未映射字段不会伪装成兼容。"
            );
            File.WriteAllText(
                Path.Combine(staging, "dogsc-import-report.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false)
            );

            Directory.Move(staging, destination);
            return new DogScProjectImportResult(
                Path.Combine(destination, "project.frametrail"),
                project,
                report
            );
        }
        catch
        {
            TryDeleteOwnedDirectory(staging, parent);
            throw;
        }
    }

    private static void MapTimeline(JsonElement root, string sourceRoot, string staging, FrameTrailProject project, double mediaDuration, List<string> warnings)
    {
        var timeline = Get(root, "timeline");
        var sequence = Get(timeline, "sourceSequence");
        var kind = String(sequence, "kind");
        if (kind.Equals("edited", StringComparison.OrdinalIgnoreCase))
        {
            var segments = Get(sequence, "segments");
            if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() == 0)
                throw new InvalidDataException("DogSC edited sourceSequence 缺少片段。");
            project.Timeline.Segments = segments.EnumerateArray().Select(segment => new TimelineSegment
            {
                Id = GuidOrNew(segment, "id"),
                SourceStart = Number(segment, "sourceStart", 0),
                SourceDuration = Number(segment, "sourceDuration", 0),
                PlaybackRate = Number(segment, "playbackRate", 1)
            }).ToList();
        }
        else if (kind.Equals("fullRecording", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(kind))
        {
            project.Timeline.Segments = [new TimelineSegment { SourceStart = 0, SourceDuration = mediaDuration }];
        }
        else
        {
            throw new InvalidDataException($"不支持的 DogSC sourceSequence 类型：{kind}");
        }

        var zooms = Get(timeline, "zoomClips");
        if (zooms.ValueKind == JsonValueKind.Array)
        {
            foreach (var zoom in zooms.EnumerateArray())
            {
                var focus = Get(zoom, "focus");
                var origin = String(zoom, "origin");
                var easing = String(zoom, "easing");
                project.ZoomClips.Add(new ZoomClip
                {
                    Id = GuidOrNew(zoom, "id"),
                    Start = Number(zoom, "startTime", 0),
                    End = Number(zoom, "endTime", 0),
                    Scale = Number(zoom, "scale", 1.6),
                    Focus = Point(focus, new(.5, .5)),
                    EnterDuration = Number(zoom, "enterDuration", .55),
                    ExitDuration = Number(zoom, "exitDuration", .55),
                    Easing = MapEasing(easing),
                    Automatic = origin.Equals("automatic", StringComparison.OrdinalIgnoreCase)
                });
                if (Get(zoom, "focusEffect").ValueKind == JsonValueKind.Object)
                    warnings.Add("缩放片段上的 focusEffect 没有目标字段；仅缩放焦点、倍率、时段和缓动已导入。");
                if (easing.Equals("自定义曲线", StringComparison.OrdinalIgnoreCase) || easing.Equals("custom", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("自定义缩放贝塞尔曲线被映射为三次缓动；原控制点未导入。");
            }
        }

        MapScreenMotion(Get(timeline, "screenMotionClips"), project, warnings);
        MapCameraMotion(Get(timeline, "cameraMotionClips"), project, warnings);
        MapMosaics(Get(timeline, "mosaicClips"), project, warnings);
        MapStickers(Get(timeline, "stickerClips"), sourceRoot, staging, project, warnings);
        var audioOverrides = Get(timeline, "primarySegmentAudioOverrides");
        if (audioOverrides.ValueKind == JsonValueKind.Object && audioOverrides.EnumerateObject().Any())
            warnings.Add("各剪辑的 system/microphone 音量覆写未导入；项目级系统音量和独立麦克风轨道已导入。");
    }

    private static void MapScreenMotion(JsonElement clips, FrameTrailProject project, List<string> warnings)
    {
        if (clips.ValueKind != JsonValueKind.Array) return;
        if (clips.GetArrayLength() > 10_000) throw new InvalidDataException("DogSC 屏幕 3D 片段超过 10,000 条上限。");
        foreach (var clip in clips.EnumerateArray())
        {
            var timing = Get(clip, "timing"); var target = Get(clip, "target");
            var start = Number(timing, "startTime", double.NaN); var duration = Number(timing, "duration", double.NaN);
            var position = Get(target, "position");
            var state = new ScreenMotionState
            {
                Position = SafePoint(position, new(.5, .5)),
                Scale = Math.Clamp(Number(target, "scale", 1), .2, 4),
                RotationX = Math.Clamp(Number(target, "rotationX", 0), -80, 80),
                RotationY = Math.Clamp(Number(target, "rotationY", 0), -80, 80),
                RotationZ = Math.Clamp(Number(target, "rotationZ", 0), -360, 360),
                Perspective = Math.Clamp(Number(target, "perspective", .65), 0, 2)
            };
            var enter = Number(timing, "leadInDuration", duration);
            if (!double.IsFinite(start + duration + enter) || start < 0 || duration <= 0 || !state.Position.IsFinite)
            {
                warnings.Add("无效的 DogSC 屏幕 3D 片段已跳过。"); continue;
            }
            project.ScreenMotions.Add(new ScreenMotionClip
            {
                Id = GuidOrNew(clip, "id"), Start = start, Duration = duration,
                EnterDuration = Math.Clamp(enter, 0, duration),
                ReturnDuration = Math.Clamp(Number(timing, "returnDuration", 0), 0, 5),
                Easing = MapEasing(String(timing, "easing")), Target = state,
                GroupId = Guid.TryParse(String(clip, "groupID"), out var group) ? group : null,
                Focus = Get(clip, "focusEffect").ValueKind == JsonValueKind.Object
                    ? MapFocusEffect(Get(clip, "focusEffect"), warnings, "屏幕 3D") : null
            });
            if (Get(timing, "customCurve").ValueKind == JsonValueKind.Object)
                warnings.Add("屏幕 3D 自定义曲线控制点未导入；已映射为最接近的标准缓动。");
        }
    }

    private static void MapCameraMotion(JsonElement clips, FrameTrailProject project, List<string> warnings)
    {
        if (clips.ValueKind != JsonValueKind.Array) return;
        if (clips.GetArrayLength() > 10_000) throw new InvalidDataException("DogSC 摄像头运动片段超过 10,000 条上限。");
        foreach (var clip in clips.EnumerateArray())
        {
            var timing = Get(clip, "timing"); var target = Get(clip, "target");
            var start = Number(timing, "startTime", double.NaN); var duration = Number(timing, "duration", double.NaN);
            var position = SafePoint(Get(target, "position"), new(.8, .8));
            if (!double.IsFinite(start + duration) || start < 0 || duration <= 0 || !position.IsFinite)
            { warnings.Add("无效的 DogSC 摄像头运动片段已跳过。"); continue; }
            var layout = Get(target, "layout");
            if (String(layout, "kind").Equals("fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("DogSC 全屏摄像头运动无法由当前摄像头插图轨道表达；该运动片段已跳过。"); continue;
            }
            project.CameraMotions.Add(new CameraMotionClip
            {
                Id = GuidOrNew(clip, "id"), Start = start, Duration = duration,
                EnterDuration = Math.Clamp(Number(timing, "leadInDuration", duration), 0, duration),
                ReturnDuration = Math.Clamp(Number(timing, "returnDuration", 0), 0, 5),
                Easing = MapEasing(String(timing, "easing")), TargetPosition = position,
                TargetSize = Math.Clamp(Number(target, "size", .28), .02, 1)
            });
            if (Number(target, "opacity", 1) != 1 || Number(target, "roundness", .5) != .5
                || String(layout, "kind").Equals("shape", StringComparison.OrdinalIgnoreCase))
                warnings.Add("摄像头运动的透明度、圆角和形状过渡没有目标字段；位置和尺寸已保留。");
        }
    }

    private static void MapMosaics(JsonElement clips, FrameTrailProject project, List<string> warnings)
    {
        if (clips.ValueKind != JsonValueKind.Array) return;
        if (clips.GetArrayLength() > 10_000) throw new InvalidDataException("DogSC 区域柔化片段超过 10,000 条上限。");
        foreach (var clip in clips.EnumerateArray())
        {
            var timing = Get(clip, "timing"); var rect = Get(clip, "sourceRect");
            var start = Number(timing, "startTime", double.NaN); var duration = Number(timing, "duration", double.NaN);
            var normalized = new NormalizedRect(Number(rect, "x", double.NaN), Number(rect, "y", double.NaN), Number(rect, "width", double.NaN), Number(rect, "height", double.NaN));
            var clippedRect = normalized.Clamp();
            if (!double.IsFinite(start + duration) || start < 0 || duration <= 0 || !normalized.IsFinite || clippedRect.Width <= 0 || clippedRect.Height <= 0)
            { warnings.Add("无效的 DogSC 区域柔化片段已跳过。"); continue; }
            var style = String(clip, "style");
            var transition = String(clip, "transitionStyle");
            project.BlurClips.Add(new BlurClip
            {
                Id = GuidOrNew(clip, "id"), Start = start, Duration = duration, Rect = clippedRect,
                Strength = Math.Clamp(Number(clip, "intensity", .55), 0, 1),
                Kind = BlurKind.Blur,
                CornerRadius = Math.Clamp(Number(clip, "cornerRadius", .08), 0, .5),
                EnterDuration = transition.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                    : Math.Clamp(Number(clip, "transitionInDuration", .3), 0, duration),
                ExitDuration = transition.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0
                    : Math.Clamp(Number(clip, "transitionOutDuration", .3), 0, duration)
            });
            if (style.Equals("spotlight", StringComparison.OrdinalIgnoreCase))
                warnings.Add("DogSC spotlight 已保留其区域和过渡时间并映射为普通柔化；区域外压暗及清晰焦点效果不受支持。");
            if (!string.IsNullOrWhiteSpace(transition) && !transition.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !transition.Equals("swift", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"DogSC 区域柔化过渡曲线 {transition} 未单独迁移；仅保留过渡时长并使用 FrameTrail 标准缓动。");
            if (Number(clip, "spotlightDimming", 0) > 0 && !style.Equals("spotlight", StringComparison.OrdinalIgnoreCase))
                warnings.Add("普通区域柔化中的 spotlightDimming 没有目标效果，已忽略。");
        }
    }

    private static void MapStickers(JsonElement clips, string sourceRoot, string staging, FrameTrailProject project, List<string> warnings)
    {
        if (clips.ValueKind != JsonValueKind.Array) return;
        if (clips.GetArrayLength() > 10_000) throw new InvalidDataException("DogSC 贴图片段超过 10,000 条上限。");
        var canvasAspect = project.Settings.CanvasWidth / (double)project.Settings.CanvasHeight;
        var shortEdge = Math.Min(project.Settings.CanvasWidth, project.Settings.CanvasHeight);
        foreach (var clip in clips.EnumerateArray())
        {
            var relativePath = String(clip, "relativePath");
            if (string.IsNullOrWhiteSpace(relativePath)) { warnings.Add("缺少相对素材路径的 DogSC 贴图片段已跳过。"); continue; }
            var timing = Get(clip, "timing");
            var start = Number(timing, "startTime", double.NaN); var duration = Number(timing, "duration", double.NaN);
            var width = Number(clip, "width", .38);
            if (!double.IsFinite(start + duration + width) || start < 0 || duration <= 0 || width <= 0)
            { warnings.Add("无效的 DogSC 贴图片段已跳过。"); continue; }
            var source = ResolvePackageFile(sourceRoot, relativePath);
            if (!File.Exists(source)) throw new FileNotFoundException($"DogSC 项目引用的贴图素材不存在：{relativePath}", source);
            RejectReparsePoints(sourceRoot, source);
            var imageAspect = ReadImageAspect(source);
            if (imageAspect is null) warnings.Add($"无法读取贴图 {relativePath} 的图像宽高；FrameTrail 将以近似比例显示。");
            var rectWidth = Math.Clamp(width, .01, 1);
            var rectHeight = Math.Clamp(rectWidth * canvasAspect / Math.Max(imageAspect ?? 1, .01), .01, 1);
            var position = SafePoint(Get(clip, "position"), new(.5, .5));
            var rect = new NormalizedRect(position.X - rectWidth / 2, position.Y - rectHeight / 2, rectWidth, rectHeight).Clamp();
            if (rect.Width <= 0 || rect.Height <= 0) { warnings.Add($"贴图 {relativePath} 的位置范围无效，片段已跳过。"); continue; }
            var copied = CopyProjectAsset(sourceRoot, staging, relativePath, "assets/stickers/sticker-" + Guid.NewGuid().ToString("N"), "", warnings);
            var animation = MapStickerMotion(String(clip, "animation"), warnings);
            var exit = String(clip, "exitAnimation");
            project.Overlays.Add(new OverlayClip
            {
                Id = GuidOrNew(clip, "id"), MediaPath = copied, Start = start, Duration = duration,
                Rect = rect, Rotation = Number(clip, "rotationDegrees", 0), Opacity = Math.Clamp(Number(clip, "opacity", 1), 0, 1),
                Layer = Int(clip, "layerIndex", 0), EnterMotion = animation,
                ExitMotion = string.IsNullOrEmpty(exit) ? animation : MapStickerMotion(exit, warnings),
                EnterDuration = Math.Clamp(Number(clip, "enterDuration", .7), 0, 5),
                ExitDuration = Math.Clamp(Number(clip, "exitDuration", .22), 0, 5),
                CornerRadius = Math.Clamp(Number(clip, "cornerRadius", 0) / shortEdge, 0, .5),
                Blur = 0
            });
            if (Number(clip, "borderWidth", 0) > 0)
                warnings.Add($"贴图 {relativePath} 的边框宽度/颜色没有目标字段；已保留贴图本体。");
            if (Number(clip, "shadowOpacity", 0) > 0 || Number(clip, "shadowRadius", 0) > 0 || Number(clip, "shadowOffsetX", 0) != 0 || Number(clip, "shadowOffsetY", 0) != 0)
                warnings.Add($"贴图 {relativePath} 的投影参数没有目标字段；已保留贴图本体。");
            if (Number(clip, "backdropBlur", 0) > 0 || Bool(clip, "backdropBlurIncludesCamera", false))
                warnings.Add($"贴图 {relativePath} 的背景/摄像头柔化没有目标字段；已保留贴图本体。");
            if (Bool(clip, "hidesScreen", false) || Bool(clip, "hidesCamera", false))
                warnings.Add($"贴图 {relativePath} 的显示期间隐藏屏幕/摄像头设置没有目标字段；已保留贴图本体。");
            if (String(clip, "animationCurve") is { Length: > 0 } curve && !curve.Equals("swift", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"贴图 {relativePath} 的动画曲线 {curve} 未单独迁移；进入/退出动画使用 FrameTrail 标准缓动。");
        }
    }

    private static OverlayMotion MapStickerMotion(string value, List<string> warnings)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized is "slidetopleft" or "slidetopright" or "slidebottomleft" or "slidebottomright")
            warnings.Add($"DogSC 贴图斜向动画 {value} 已映射为同轴滑入；对角位移未保留。");
        return normalized switch
        {
            "none" => OverlayMotion.None, "fade" => OverlayMotion.Fade, "pop" => OverlayMotion.Scale,
            "slideleft" => OverlayMotion.SlideLeft, "slideright" => OverlayMotion.SlideRight,
            "slideup" or "slidetopleft" or "slidetopright" => OverlayMotion.SlideUp,
            "slidedown" or "slidebottomleft" or "slidebottomright" => OverlayMotion.SlideDown,
            _ => UnknownStickerMotion(value, warnings)
        };
    }

    private static OverlayMotion UnknownStickerMotion(string value, List<string> warnings)
    {
        warnings.Add($"未知 DogSC 贴图动画 {value} 被映射为淡入淡出。"); return OverlayMotion.Fade;
    }

    private static FocusEffect MapFocusEffect(JsonElement source, List<string> warnings, string owner)
    {
        var shape = String(source, "shape"); var target = String(source, "target");
        var hasShape = Get(source, "shape").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (!hasShape) warnings.Add($"{owner} focusEffect 没有明确形状；按线性固定焦点近似导入。");
        return new FocusEffect
        {
            Shape = !hasShape || shape.Equals("linear", StringComparison.OrdinalIgnoreCase) ? FocusEffectShape.Linear : FocusEffectShape.Radial,
            Target = !hasShape ? FocusEffectTarget.Fixed : target.Equals("pointer", StringComparison.OrdinalIgnoreCase) ? FocusEffectTarget.Pointer
                : target.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? FocusEffectTarget.Fixed : FocusEffectTarget.Animation,
            Center = SafePoint(Get(source, "center"), new(.5, .5)), Size = Math.Clamp(Number(source, "size", .45), .1, 1),
            Blur = Math.Clamp(Number(source, "blur", .35), 0, 1), Softness = Math.Clamp(Number(source, "softness", .5), 0, 1),
            Dimming = Math.Clamp(Number(source, "dimming", hasShape ? .18 : 0), 0, .75), AngleDegrees = Math.Clamp(Number(source, "angleDegrees", 0), -360, 360)
        };
    }

    private static void MapOpening(JsonElement root, FrameTrailProject project, List<string> warnings)
    {
        var opening = Get(root, "openingSequence");
        if (opening.ValueKind != JsonValueKind.Object) return;
        var rawPreset = String(opening, "preset");
        var preset = rawPreset switch
        {
            "聚拢" or "converge" => OpeningPreset.Converge,
            "侧滑" or "sideSlide" => OpeningPreset.SideSlide,
            "轻 3D" or "light3D" => OpeningPreset.Light3D,
            _ => OpeningPreset.Custom
        };
        var duration = Math.Clamp(Number(opening, "duration", 2.2), .4, 8);
        var included = ReadOpeningElements(Get(opening, "includedElements"), [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays], warnings, "includedElements");
        var order = ReadOpeningElements(Get(opening, "elementOrder"), [OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays], warnings, "elementOrder");
        order = order.Concat(new[] { OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays }).Distinct().ToList();
        var gapCount = Math.Max(included.Count - 1, 0);
        var maxStagger = gapCount == 0 ? 1.2 : Math.Min(1.2, Math.Max(duration - .18, 0) / gapCount);
        var curve = String(opening, "motionCurve");
        var easing = curve.Equals("smooth", StringComparison.OrdinalIgnoreCase) ? ZoomEasing.Quintic
            : curve.Equals("swift", StringComparison.OrdinalIgnoreCase) || curve.Equals("gentle", StringComparison.OrdinalIgnoreCase) ? ZoomEasing.Cubic
            : MapEasing(curve);
        project.Settings.Opening = new OpeningSettings
        {
            Enabled = Bool(opening, "isEnabled", false), Preset = preset,
            Duration = duration,
            Easing = easing,
            Stagger = Math.Clamp(Number(opening, "stagger", .16), 0, maxStagger),
            IncludedElements = included,
            ElementOrder = order
        };
        if (curve.Equals("swift", StringComparison.OrdinalIgnoreCase) || curve.Equals("gentle", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"DogSC 开场曲线 {curve} 近似映射到 FrameTrail 缓动；每个元素的入场顺序和错开时间已保留。");
        if (rawPreset.Length > 0 && preset == OpeningPreset.Custom)
            warnings.Add($"未知 DogSC 开场预设 {rawPreset} 未识别；仅保留开场启用状态和时长。");
    }

    private static List<OpeningElement> ReadOpeningElements(JsonElement value, IReadOnlyList<OpeningElement> fallback, List<string> warnings, string field)
    {
        if (value.ValueKind != JsonValueKind.Array) return fallback.ToList();
        var result = new List<OpeningElement>();
        foreach (var item in value.EnumerateArray())
        {
            var raw = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            var element = raw.ToLowerInvariant() switch
            {
                "屏幕" or "screen" => OpeningElement.Screen,
                "摄像头" or "camera" => OpeningElement.Camera,
                "贴图" or "stickers" or "overlays" => OpeningElement.Overlays,
                _ => (OpeningElement?)null
            };
            if (element is null) warnings.Add($"DogSC 开场 {field} 含未知元素 {raw}；该元素已忽略。");
            else if (!result.Contains(element.Value)) result.Add(element.Value);
        }
        return result;
    }

    private static double? ReadImageAspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(stream.Length, 1024 * 1024);
            if (length < 10) return null;
            var bytes = new byte[length]; stream.ReadExactly(bytes);
            if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                var width = ReadBigEndian(bytes.AsSpan(16, 4)); var height = ReadBigEndian(bytes.AsSpan(20, 4));
                return width > 0 && height > 0 ? width / (double)height : null;
            }
            if (bytes.Length >= 10 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                var width = BitConverter.ToUInt16(bytes.AsSpan(6, 2)); var height = BitConverter.ToUInt16(bytes.AsSpan(8, 2));
                return width > 0 && height > 0 ? width / (double)height : null;
            }
            if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                if (bytes.Length < 26) return null;
                var width = BitConverter.ToInt32(bytes.AsSpan(18, 4)); var height = Math.Abs(BitConverter.ToInt32(bytes.AsSpan(22, 4)));
                return width > 0 && height > 0 ? width / (double)height : null;
            }
            if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
            {
                var offset = 2;
                while (offset + 9 < bytes.Length)
                {
                    if (bytes[offset++] != 0xff) continue;
                    while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                    if (offset >= bytes.Length) break;
                    var marker = bytes[offset++];
                    if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
                    if (offset + 2 > bytes.Length) break;
                    var segmentLength = (bytes[offset] << 8) | bytes[offset + 1];
                    if (segmentLength < 2 || offset + segmentLength > bytes.Length) break;
                    if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                    {
                        var height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                        var width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                        return width > 0 && height > 0 ? width / (double)height : null;
                    }
                    offset += segmentLength;
                }
            }
        }
        catch (IOException) { }
        return null;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> value) =>
        (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static void MapPointerEvents(string sourceRoot, string staging, JsonElement media, FrameTrailProject project, List<string> warnings)
    {
        var pointer = Get(media, "pointerEvents");
        if (pointer.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("DogSC 工程没有指针事件轨迹；自动缩放焦点和录制指针无法导入。");
            return;
        }
        var path = String(pointer, "relativePath");
        if (string.IsNullOrWhiteSpace(path))
        {
            warnings.Add("DogSC 指针事件引用缺少相对路径；指针轨迹未导入。");
            return;
        }
        var source = ResolvePackageFile(sourceRoot, path);
        if (!File.Exists(source))
            throw new FileNotFoundException("DogSC 项目引用的指针事件文件不存在。", source);
        RejectReparsePoints(sourceRoot, source);
        if (new FileInfo(source).Length > 256 * 1024 * 1024)
            throw new InvalidDataException("DogSC 指针 JSONL 超过 256 MiB 安全上限。");

        const int maxEvents = 5_000_000;
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var events = new List<PointerEvent>();
        string? line;
        var lineNumber = 0;
        var modifiersWerePresent = false;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            if (line.Length > 1024 * 1024)
                throw new InvalidDataException($"指针 JSONL 第 {lineNumber} 行过长。");
            using var eventDocument = JsonDocument.Parse(line, DocumentOptions);
            var record = eventDocument.RootElement;
            var time = Number(record, "time", double.NaN);
            var point = Get(record, "location");
            var location = Point(point, new(double.NaN, double.NaN));
            var rawKind = String(record, "kind");
            var eventKind = rawKind.Equals("leftClick", StringComparison.OrdinalIgnoreCase) ? PointerEventKind.LeftClick
                : rawKind.Equals("rightClick", StringComparison.OrdinalIgnoreCase) ? PointerEventKind.RightClick
                : rawKind.Equals("move", StringComparison.OrdinalIgnoreCase) ? PointerEventKind.Move
                : (PointerEventKind?)null;
            if (!double.IsFinite(time) || time < 0 || !location.IsFinite || location.X is < 0 or > 1 || location.Y is < 0 or > 1 || eventKind is null)
            {
                warnings.Add($"指针 JSONL 第 {lineNumber} 行的时间、坐标或事件类型无效，已跳过。");
                continue;
            }
            var cursorAssetValue = Get(record, "cursorAssetID");
            var cursorAsset = cursorAssetValue.ValueKind == JsonValueKind.String ? cursorAssetValue.GetString() : null;
            var modifiers = Get(record, "modifiers");
            modifiersWerePresent |= modifiers.ValueKind == JsonValueKind.Array && modifiers.GetArrayLength() > 0;
            events.Add(new PointerEvent(time, location, eventKind.Value, cursorAsset));
            if (events.Count > maxEvents) throw new InvalidDataException("DogSC 指针事件超过 500 万条上限。");
        }
        events.Sort((left, right) => left.Time.CompareTo(right.Time));
        project.PointerEvents = events;
        if (modifiersWerePresent)
            warnings.Add("指针事件的键盘修饰键未导入；位置、点击类别及记录的 cursorAssetID 字符串已保留。");
        var startOffset = Number(pointer, "startOffset", 0);
        var sourceStart = Number(pointer, "sourceStartTime", 0);
        var scale = Number(pointer, "sourceTimeScale", 1);
        project.MouseClock = new MouseClockCalibration(startOffset - sourceStart, scale);
        CopyProjectAsset(sourceRoot, staging, path, "events/pointer", ".jsonl", warnings);
    }

    private static void MapMarkers(JsonElement root, FrameTrailProject project, List<string> warnings)
    {
        var markers = Get(root, "recordingMarkers");
        if (markers.ValueKind != JsonValueKind.Array) return;
        foreach (var marker in markers.EnumerateArray())
        {
            var time = Number(marker, "sourceTime", double.NaN);
            if (!double.IsFinite(time) || time < 0)
            {
                warnings.Add("无效的 DogSC 录制标记已跳过。");
                continue;
            }
            var number = Int(marker, "number", project.Markers.Count + 1);
            project.Markers.Add(new RecordingMarker(GuidOrNew(marker, "id"), time, $"标记 {number}"));
        }
    }

    private static void ValidateImportedTimeline(FrameTrailProject project, double mediaDuration)
    {
        project.Timeline.Validate();
        foreach (var segment in project.Timeline.Segments)
            if (segment.SourceEnd > mediaDuration + 0.05)
                throw new InvalidDataException($"DogSC 时间线片段超出主素材时长：{segment.SourceEnd:0.###} > {mediaDuration:0.###} 秒。");
        foreach (var clip in project.ZoomClips)
            if (!double.IsFinite(clip.Start + clip.End + clip.Scale + clip.EnterDuration + clip.ExitDuration)
                || clip.Start < 0 || clip.End <= clip.Start || clip.End > mediaDuration + 0.05
                || clip.Scale < 1 || !clip.Focus.IsFinite)
                throw new InvalidDataException("DogSC 缩放片段超出支持范围或主素材时长。");
    }

    private static void MapCanvas(JsonElement root, string sourceRoot, string staging, FrameTrailProject project, List<string> warnings)
    {
        var canvas = Get(root, "canvas");
        if (canvas.ValueKind != JsonValueKind.Object) return;
        var settings = project.Settings;
        var crop = Get(canvas, "crop");
        var requestedCrop = new NormalizedRect(Number(crop, "x", 0), Number(crop, "y", 0), Number(crop, "width", 1), Number(crop, "height", 1)).Clamp();
        if (requestedCrop.Width <= 0 || requestedCrop.Height <= 0)
        {
            requestedCrop = NormalizedRect.Full;
            warnings.Add("DogSC 画布裁切范围无效；已恢复为完整画面。");
        }
        var aspect = String(canvas, "aspectRatio");
        var (aspectWidth, aspectHeight) = MapAspect(aspect, project.Media.Width, project.Media.Height);
        settings.Canvas = new CanvasSettings
        {
            AspectWidth = aspectWidth,
            AspectHeight = aspectHeight,
            Crop = requestedCrop,
            ContentPosition = SafePoint(Get(canvas, "contentPosition"), new(.5, .5)),
            ContentScale = Math.Clamp(Number(canvas, "contentScale", 1), .1, 4),
            Frame = MapScreenFrame(String(canvas, "screenFrame")),
            FrameScale = Math.Clamp(Number(canvas, "screenFrameScale", 1), .6, 1.6),
            FrameToolbarScale = Math.Clamp(Number(canvas, "screenFrameToolbarScale", 1), .65, 1.6),
            FrameOuterRadius = Get(canvas, "screenFrameOuterCornerRadius").ValueKind == JsonValueKind.Number
                ? Math.Clamp(Number(canvas, "screenFrameOuterCornerRadius", 18), 0, 160) : null,
            BorderWidth = Math.Clamp(Number(canvas, "borderWidth", 0), 0, 60),
            BorderColor = Color(String(canvas, "borderHex"), "#FFFFFF"),
            Title = String(canvas, "screenFrameTitle"),
            Address = String(canvas, "screenFrameBrowserAddress")
        };
        settings.CanvasWidth = aspectWidth;
        settings.CanvasHeight = aspectHeight;
        settings.Padding = Math.Clamp(Number(canvas, "padding", 80) / Math.Max(1, Math.Min(aspectWidth, aspectHeight)), 0, .5);
        settings.CornerRadius = Math.Clamp((settings.Canvas.Frame == ScreenFrameKind.None
            ? Number(canvas, "cornerRadius", 24) : Number(canvas, "screenFrameContentCornerRadius", 0)) / Math.Max(1, Math.Min(aspectWidth, aspectHeight)), 0, .5);
        settings.Shadow = Math.Clamp(Number(canvas, "shadowStrength", .28), 0, 1);

        var source = Get(canvas, "backgroundSource");
        var sourceKind = String(source, "kind");
        if (sourceKind.Equals("projectImage", StringComparison.OrdinalIgnoreCase))
        {
            var path = String(source, "relativePath");
            if (string.IsNullOrWhiteSpace(path))
                warnings.Add("DogSC 图片背景缺少相对素材路径；已回退到纯色背景。");
            else
            {
                var importedPath = CopyProjectAsset(sourceRoot, staging, path, "assets/background", "", warnings);
                settings.BackgroundStyle = new BackgroundSettings { Kind = BackgroundKind.Image, ImagePath = importedPath, ImageFit = BackgroundImageFit.Cover, Blur = Math.Clamp(Number(canvas, "backgroundBlur", 0) / 96, 0, 1) };
            }
        }
        else if (sourceKind.Equals("projectVideo", StringComparison.OrdinalIgnoreCase))
        {
            var path = String(source, "relativePath");
            if (string.IsNullOrWhiteSpace(path)) warnings.Add("DogSC 项目视频背景缺少相对素材路径；已回退到纯色背景。");
            else
            {
                var importedPath = CopyProjectAsset(sourceRoot, staging, path, "assets/background", "", warnings);
                settings.BackgroundStyle = new BackgroundSettings { Kind = BackgroundKind.Video, VideoPath = importedPath, VideoLoop = true, ImageFit = BackgroundImageFit.Cover, Blur = Math.Clamp(Number(canvas, "backgroundBlur", 0) / 96, 0, 1) };
            }
        }
        else if (sourceKind.Equals("systemImage", StringComparison.OrdinalIgnoreCase) || sourceKind.Equals("systemVideo", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"背景源 {sourceKind} 未导入；FrameTrail 仅接收项目内相对素材，不复制系统壁纸路径。");
        }
        else if (sourceKind.Equals("pattern", StringComparison.OrdinalIgnoreCase))
        {
            var preset = String(source, "patternPreset");
            var dots = preset.Contains("dot", StringComparison.OrdinalIgnoreCase) || preset.Contains("点", StringComparison.Ordinal) || preset.Contains("星", StringComparison.Ordinal);
            settings.BackgroundStyle = new BackgroundSettings
            {
                Kind = BackgroundKind.Pattern,
                Pattern = dots ? BackgroundPatternKind.Dots : BackgroundPatternKind.Grid,
                PatternStrength = Math.Clamp(Number(canvas, "patternOpacity", 1), 0, 1),
                PatternSpacing = Math.Clamp(48 / Math.Clamp(Number(canvas, "patternScale", 1), .4, 6), 8, 512)
            };
            warnings.Add(preset is "isometricMesh" or "等角网格"
                ? "DogSC 等角网格背景已近似映射为普通网格；透视格线、配色和动态细节未迁移。"
                : $"DogSC 网格/点阵背景预设 {preset} 已映射到最接近的基础图案；原配色和密度参数未完整迁移。");
        }
        else if (sourceKind.Equals("dynamicFlow", StringComparison.OrdinalIgnoreCase))
        {
            var preset = String(source, "dynamicPreset");
            settings.BackgroundStyle = new BackgroundSettings
            {
                Kind = BackgroundKind.Dynamic,
                Preset = preset.Contains("点阵", StringComparison.Ordinal) || preset.Contains("星", StringComparison.Ordinal) ? DynamicBackgroundPreset.Ocean
                    : preset.Contains("流体", StringComparison.Ordinal) || preset.Contains("极光", StringComparison.Ordinal) ? DynamicBackgroundPreset.Aurora
                    : DynamicBackgroundPreset.Sunset,
                Speed = 1,
                PatternStrength = Math.Clamp(Number(canvas, "patternOpacity", 1), 0, 1)
            };
            warnings.Add($"DogSC 动态背景预设 {preset} 已映射到近似程序化风格；原始流场和动画参数未迁移。");
            if (Number(canvas, "patternOpacity", 1) != 1)
                warnings.Add("DogSC 动态背景透明度没有目标字段；已使用不透明程序化背景。");
        }
        else if (!string.IsNullOrEmpty(sourceKind))
        {
            warnings.Add($"未知背景类型 {sourceKind} 未导入，已采用 FrameTrail 安全默认背景。");
        }
        if (!string.IsNullOrEmpty(aspect) && aspect.Contains("自定义", StringComparison.Ordinal))
            warnings.Add("DogSC 自定义画布比例未保留原自定义字段，按媒体比例近似导入。");
        if (String(canvas, "screenFrame").StartsWith("device", StringComparison.OrdinalIgnoreCase))
            warnings.Add("DogSC 设备样机与内建几何参数未迁移，已映射到最接近的 Phone/Tablet 外框。");
        if (Math.Abs(Number(canvas, "insetOpacity", .125) - .125) > .0001)
            warnings.Add("DogSC 样机插入画面的透明度没有对应字段；使用不透明画面。");
    }

    private static void MapCursor(JsonElement root, FrameTrailProject project, List<string> warnings)
    {
        var cursor = Get(root, "cursorStyle");
        if (cursor.ValueKind != JsonValueKind.Object) return;
        var rawAsset = Get(cursor, "assetID");
        var asset = rawAsset.ValueKind == JsonValueKind.String ? rawAsset.GetString() ?? "" : "";
        var hidden = asset.Equals("system.hidden", StringComparison.OrdinalIgnoreCase)
            || asset.Equals("hidden", StringComparison.OrdinalIgnoreCase);
        project.Settings.CursorVisible = !hidden;
        var click = String(cursor, "clickEffectStyle");
        project.Settings.Cursor = new CursorSettings
        {
            Visible = !hidden,
            Scale = Math.Clamp(Number(cursor, "size", 1), .1, 5),
            HideWhenIdle = Bool(cursor, "hideWhenIdle", false),
            IdleSeconds = Math.Clamp(Number(cursor, "idleDelay", 1.5), 0, 60),
            ClickEffectsEnabled = !click.Equals("none", StringComparison.OrdinalIgnoreCase),
            ClickEffect = click.Equals("none", StringComparison.OrdinalIgnoreCase) ? ClickEffectStyle.None
                : click.Equals("glow", StringComparison.OrdinalIgnoreCase) ? ClickEffectStyle.Ring : ClickEffectStyle.Pulse,
            ClickColor = Color(String(cursor, "clickColor"), "#FFE066"),
            ClickDuration = click.ToLowerInvariant() switch { "ripple" => .36, "glow" => .32, "burst" => .22, _ => .38 }
        };
        if (!string.IsNullOrEmpty(asset) && !hidden
            && !asset.Equals("system.arrow", StringComparison.OrdinalIgnoreCase)
            && !asset.Equals("system.automatic", StringComparison.OrdinalIgnoreCase))
            warnings.Add("DogSC 光标资源 ID/外观没有迁移；指针位置与点击事件保留，导入后使用 FrameTrail 系统箭头。");
        if (!click.Equals("none", StringComparison.OrdinalIgnoreCase) && !click.Equals("pulse", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"DogSC 点击效果 {click} 已映射为 FrameTrail 的近似样式，原动画外形与透明度/缩放参数未保留。");
        if (Get(cursor, "motionTiltStrength").ValueKind == JsonValueKind.Number)
            warnings.Add("DogSC 光标运动倾斜与资源热区配置没有对应的 FrameTrail 字段。");
    }

    private static void MapCameraAndAudio(
        JsonElement root, string sourceRoot, string staging, JsonElement media,
        FrameTrailProject project, double screenDuration, List<string> warnings)
    {
        var cameraRef = Get(media, "camera");
        if (cameraRef.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(String(cameraRef, "relativePath")))
        {
            var path = CopyProjectAsset(sourceRoot, staging, String(cameraRef, "relativePath"), "media/camera", "", warnings);
            var camera = Get(root, "camera");
            var timelineStart = Math.Clamp(Number(cameraRef, "startOffset", 0), -86400, 86400);
            var sourceStart = Math.Max(0, Number(cameraRef, "sourceStartTime", 0));
            var end = Number(cameraRef, "sourceEndTime", double.NaN);
            var duration = double.IsFinite(end) && end > sourceStart ? end - sourceStart : Math.Max(0, screenDuration - Math.Max(timelineStart, 0));
            if (duration > 0)
            {
                var shape = String(camera, "shape");
                var size = Number(camera, "size", .29);
                var contentScale = Math.Clamp(Number(camera, "contentScale", 1), 1, 4);
                var cropSize = 1 / contentScale;
                var contentPosition = Point(Get(camera, "contentPosition"), new(.5, .5));
                var crop = new NormalizedRect(contentPosition.X - cropSize / 2, contentPosition.Y - cropSize / 2, cropSize, cropSize).Clamp();
                project.Camera = new CameraTrack
                {
                    MediaPath = path,
                    SourceStart = sourceStart,
                    Duration = duration,
                    TimelineStart = timelineStart,
                    Position = SafePoint(Get(camera, "position"), new(.96, .47)),
                    Size = Math.Clamp(size, .02, 1),
                    CornerRadius = Math.Clamp(Number(camera, "roundness", shape.Contains("circle", StringComparison.OrdinalIgnoreCase) ? .5 : .12), 0, .5),
                    BorderWidth = Math.Clamp(Number(camera, "borderWidth", 0), 0, 18),
                    Shape = shape.Contains("circle", StringComparison.OrdinalIgnoreCase) ? CameraShape.Circle : CameraShape.RoundedRectangle,
                    Crop = crop,
                    Opacity = Bool(camera, "isHidden", false) ? 0 : 1,
                    Volume = 1,
                    Muted = true,
                    Mirrored = Bool(camera, "isMirrored", true)
                };
                if (Number(cameraRef, "sourceTimeScale", 1) != 1 || Get(cameraRef, "syncAnchors").ValueKind == JsonValueKind.Array && Get(cameraRef, "syncAnchors").GetArrayLength() > 0)
                    warnings.Add("摄像头的时钟速率/分段同步锚点未迁移；素材和基础时间偏移已导入，需人工核对音画同步。");
                if (Number(camera, "scaleDuringZoom", 1) != 1)
                    warnings.Add("摄像头随屏幕缩放联动的大小变化没有迁移；固定尺寸和内部取景裁切已导入。");
                if (Number(camera, "shadowStrength", 0) > 0)
                    warnings.Add("DogSC 摄像头阴影没有目标字段；摄像头素材、形状、边框和取景裁切已导入。");
            }
        }

        var microphone = Get(media, "microphone");
        if (microphone.ValueKind == JsonValueKind.Object && !string.IsNullOrWhiteSpace(String(microphone, "relativePath")))
        {
            var path = CopyProjectAsset(sourceRoot, staging, String(microphone, "relativePath"), "media/microphone", "", warnings);
            var start = Math.Max(0, Number(microphone, "sourceStartTime", 0));
            var end = Number(microphone, "sourceEndTime", double.NaN);
            var duration = double.IsFinite(end) && end > start ? end - start : screenDuration;
            var audio = Get(root, "audio");
            if (double.IsFinite(duration) && duration > 0)
                project.AudioTracks.Add(new AudioTrack
            {
                MediaPath = path,
                SourceStart = start,
                Duration = duration,
                OffsetSeconds = Math.Clamp(Number(microphone, "startOffset", 0), -86400, 86400),
                Volume = Math.Clamp(Number(audio, "microphoneVolume", .9), 0, 4),
                Muted = Bool(audio, "isMicrophoneMuted", false)
            });
            else warnings.Add("无效的 DogSC 麦克风时间范围已跳过。");
            if (Number(microphone, "sourceTimeScale", 1) != 1 || Get(microphone, "syncAnchors").ValueKind == JsonValueKind.Array && Get(microphone, "syncAnchors").GetArrayLength() > 0)
                warnings.Add("麦克风时钟速率/分段同步锚点未迁移；音轨偏移已导入，需人工核对同步。");
        }

        var audioSettings = Get(root, "audio");
        project.Settings.MainAudioMuted = Bool(audioSettings, "isSystemMuted", false);
        project.Settings.MainAudioVolume = Math.Clamp(Number(audioSettings, "systemVolume", 1), 0, 1);
        if (Get(media, "screen").ValueKind == JsonValueKind.Object && Number(Get(media, "screen"), "sourceTimeScale", 1) != 1)
            warnings.Add("屏幕母片时钟速率不是 1，FrameTrail 当前主素材模型不保存该同步校正。");
        if (Get(root, "exportSettings").ValueKind == JsonValueKind.Object)
            warnings.Add("DogSC 导出分辨率/帧率偏好没有迁移到 FrameTrail 设置；本次媒体探测值作为主素材尺寸和时长。");
    }

    private static string CopyProjectAsset(string sourceRoot, string staging, string relativePath, string targetStem, string forcedSuffix, List<string> warnings)
    {
        var source = ResolvePackageFile(sourceRoot, relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException($"DogSC 项目引用的素材不存在：{relativePath}", source);
        RejectReparsePoints(sourceRoot, source);
        var extension = forcedSuffix.Length > 0 ? forcedSuffix : Path.GetExtension(source);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var targetRelative = targetStem + extension.ToLowerInvariant();
        var target = SafeCombine(staging, targetRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
        if (!Path.GetExtension(source).Equals(extension, StringComparison.OrdinalIgnoreCase) && forcedSuffix.Length > 0)
            warnings.Add($"素材 {relativePath} 以固定扩展名复制；内容字节保持原样。");
        return targetRelative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ResolvePackageFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException($"DogSC 项目素材路径不是安全相对路径：{relative}");
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException($"DogSC 项目素材路径含有越界或空目录段：{relative}");
        var path = SafeCombine(root, Path.Combine(parts));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"DogSC 项目素材路径越过项目根目录：{relative}");
        return path;
    }

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导入路径越过目标根目录。");
        return full;
    }

    private static void RejectReparsePoints(string root, string file)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(file);
        var relative = Path.GetRelativePath(fullRoot, current);
        var walk = fullRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            walk = Path.Combine(walk, part);
            var attributes = File.GetAttributes(walk);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"拒绝通过符号链接或重解析点读取项目素材：{walk}");
        }
    }

    private static void TryDeleteOwnedDirectory(string path, string parent)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        catch { /* Preserve the original import exception; report cleanup separately at the caller if needed. */ }
    }

    private static bool IsSameOrChildPath(string parent, string candidate)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(p, c, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnumerateImportedMedia(FrameTrailProject project)
    {
        var paths = new List<string> { project.MediaPath };
        if (project.Camera is { } camera) paths.Add(camera.MediaPath);
        paths.AddRange(project.AudioTracks.Select(track => track.MediaPath));
        paths.AddRange(project.Overlays.Select(overlay => overlay.MediaPath));
        if (project.Settings.BackgroundStyle?.ImagePath is { } background) paths.Add(background);
        if (project.Settings.BackgroundStyle?.VideoPath is { } backgroundVideo) paths.Add(backgroundVideo);
        return paths;
    }

    private static bool ValidMediaInfo(MediaAsset media) =>
        double.IsFinite(media.Width) && media.Width >= 2 &&
        double.IsFinite(media.Height) && media.Height >= 2 &&
        double.IsFinite(media.Duration) && media.Duration > 0;

    private static (int Width, int Height) MapAspect(string ratio, double sourceWidth, double sourceHeight)
    {
        var fixedRatio = ratio switch
        {
            "16:9" => (16, 9), "4:3" => (4, 3), "9:16" => (9, 16), "1:1" => (1, 1),
            "3:4" => (3, 4), "2.39:1" => (239, 100), "1:2.39" => (100, 239), _ => (0, 0)
        };
        if (fixedRatio.Item1 > 0) return (fixedRatio.Item1 * 120, fixedRatio.Item2 * 120);
        var width = double.IsFinite(sourceWidth) && sourceWidth > 0 ? sourceWidth : 16;
        var height = double.IsFinite(sourceHeight) && sourceHeight > 0 ? sourceHeight : 9;
        var divisor = GreatestCommonDivisor((int)Math.Round(width), (int)Math.Round(height));
        return (Math.Max((int)Math.Round(width / Math.Max(divisor, 1)), 2), Math.Max((int)Math.Round(height / Math.Max(divisor, 1)), 2));
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static ScreenFrameKind MapScreenFrame(string style) => style switch
    {
        "windowLight" => ScreenFrameKind.WindowLight,
        "windowDark" => ScreenFrameKind.WindowDark,
        "browserLight" => ScreenFrameKind.BrowserLight,
        "browserDark" => ScreenFrameKind.BrowserDark,
        "devicePhone" or "devicePhonePortrait" or "devicePhoneLandscape" => ScreenFrameKind.Phone,
        "deviceTablet" or "deviceTabletPortrait" or "deviceTabletLandscape" => ScreenFrameKind.Tablet,
        _ => ScreenFrameKind.None
    };

    private static ZoomEasing MapEasing(string value) => value switch
    {
        "线性" or "linear" => ZoomEasing.Linear,
        "二次方缓动" or "二次" or "quadratic" => ZoomEasing.Quadratic,
        "五次平滑" or "quintic" => ZoomEasing.Quintic,
        "弹簧" or "spring" => ZoomEasing.Spring,
        _ => ZoomEasing.Cubic
    };

    private static NormalizedPoint Point(JsonElement value, NormalizedPoint fallback) =>
        value.ValueKind == JsonValueKind.Object
            ? new NormalizedPoint(Number(value, "x", fallback.X), Number(value, "y", fallback.Y))
            : fallback;

    private static NormalizedPoint SafePoint(JsonElement value, NormalizedPoint fallback)
    {
        var point = Point(value, fallback);
        if (!point.IsFinite) return fallback;
        return new(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));
    }

    private static JsonElement Get(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string String(JsonElement element, string name)
    {
        var value = Get(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static double Number(JsonElement element, string name, double fallback)
    {
        var value = Get(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)) return result;
        return fallback;
    }

    private static int Int(JsonElement element, string name, int fallback)
    {
        var value = Get(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : fallback;
    }

    private static bool Bool(JsonElement element, string name, bool fallback)
    {
        var value = Get(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    }

    private static Guid GuidOrNew(JsonElement element, string name) =>
        Guid.TryParse(String(element, name), out var value) && value != Guid.Empty ? value : Guid.NewGuid();

    private static string Color(string candidate, string fallback)
    {
        if (candidate.StartsWith('#') && (candidate.Length == 7 || candidate.Length == 9) && candidate.Skip(1).All(Uri.IsHexDigit))
            return candidate;
        return fallback;
    }
}

public sealed record DogScProjectImportReport(
    string SourcePackage,
    int SourceSchemaVersion,
    string DestinationProject,
    IReadOnlyList<string> ImportedMedia,
    IReadOnlyList<string> Warnings,
    string PreservationNote);

public sealed record DogScProjectImportResult(
    string ProjectFilePath,
    FrameTrailProject Project,
    DogScProjectImportReport Report);
