using System.Security.Cryptography;
using FrameTrail;

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("失败：" + label);
    Console.WriteLine("通过：" + label);
}

static void Write(string root, string relative, byte[] data)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, data);
}

var temporaryRoot = Path.Combine(Path.GetTempPath(), "frametrail-dogsc-import-fixture-" + Guid.NewGuid().ToString("N"));
var source = Path.Combine(temporaryRoot, "schema-fixture.dogscproject");
var destination = Path.Combine(temporaryRoot, "Imported.frametrail");
Directory.CreateDirectory(source);
try
{
    var screenBytes = Enumerable.Range(0, 257).Select(i => (byte)(i % 251)).ToArray();
    var cameraBytes = "camera-fixture"u8.ToArray();
    var microphoneBytes = "microphone-fixture"u8.ToArray();
    var backgroundVideoBytes = "background-video-fixture"u8.ToArray();
    var stickerBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 40, 0, 0, 0, 20 };
    Write(source, "media/screen-0001.mp4", screenBytes);
    Write(source, "media/camera-0001.mov", cameraBytes);
    Write(source, "media/microphone.m4a", microphoneBytes);
    Write(source, "assets/background.mp4", backgroundVideoBytes);
    Write(source, "assets/coast.png", stickerBytes);
    Write(source, "events/pointer.jsonl", """
        {"time":0.25,"location":{"x":0.2,"y":0.3},"kind":"move"}
        {"time":1.0,"location":{"x":0.72,"y":0.61},"kind":"leftClick","modifiers":[],"cursorAssetID":"system.arrow"}
        {"time":2.0,"location":{"x":0.8,"y":0.7},"kind":"rightClick"}
        """u8.ToArray());

    var sourceJson = """
    {
      "version":27,
      "title":"迁移验证工程",
      "createdAt":"2026-10-08T12:00:00Z",
      "media":{
        "screen":{"relativePath":"media/screen-0001.mp4","sourceTimeScale":1.0},
        "camera":{"relativePath":"media/camera-0001.mov","startOffset":0.1,"sourceStartTime":0.2,"sourceEndTime":3.8,"sourceTimeScale":1.0},
        "microphone":{"relativePath":"media/microphone.m4a","startOffset":0.05,"sourceStartTime":0.1,"sourceEndTime":3.9,"sourceTimeScale":1.0},
        "pointerEvents":{"relativePath":"events/pointer.jsonl","startOffset":0.2,"sourceStartTime":0.4,"sourceTimeScale":1.2}
      },
      "timeline":{
        "sourceSequence":{"kind":"edited","segments":[
          {"id":"11111111-1111-1111-1111-111111111111","sourceStart":0.5,"sourceDuration":2.0,"playbackRate":2.0},
          {"id":"22222222-2222-2222-2222-222222222222","sourceStart":2.5,"sourceDuration":1.0,"playbackRate":1.0}
        ]},
        "zoomClips":[{"id":"33333333-3333-3333-3333-333333333333","startTime":0.8,"endTime":1.5,"scale":1.8,"focus":{"x":0.72,"y":0.61},"origin":"automatic","easing":"三次方缓动","enterDuration":0.3,"exitDuration":0.4}],
        "screenMotionClips":[{"id":"44444444-4444-4444-4444-444444444444","timing":{"startTime":1.1,"duration":2.0,"leadInDuration":0.5,"returnDuration":0.5,"easing":"三次方缓动"},"target":{"position":{"x":0.6,"y":0.5},"scale":1.25,"rotationX":8,"rotationY":-4,"rotationZ":2,"perspective":0.6},"focusEffect":{"shape":"radial","target":"pointer","center":{"x":0.5,"y":0.5},"size":0.4,"blur":0.6,"softness":0.3,"dimming":0.2}}],
        "cameraMotionClips":[{"id":"66666666-6666-6666-6666-666666666666","timing":{"startTime":1.5,"duration":1.0,"leadInDuration":0.3,"returnDuration":0.4,"easing":"线性"},"target":{"layout":{"kind":"shape","shape":"circle"},"position":{"x":0.72,"y":0.75},"size":0.32,"roundness":0.5,"opacity":0.9}}],
        "mosaicClips":[{"id":"77777777-7777-7777-7777-777777777777","timing":{"startTime":0.8,"duration":1.6},"sourceRect":{"x":0.2,"y":0.25,"width":0.3,"height":0.2},"cornerRadius":0.08,"style":"blur","intensity":0.7,"spotlightDimming":0.22,"transitionStyle":"smooth","transitionInDuration":0.25,"transitionOutDuration":0.3}],
        "stickerClips":[{"id":"88888888-8888-8888-8888-888888888888","timing":{"startTime":0.5,"duration":2.5},"relativePath":"assets/coast.png","position":{"x":0.75,"y":0.75},"width":0.3,"rotationDegrees":4,"opacity":0.85,"cornerRadius":18,"borderWidth":2,"borderColor":"#FFFFFF","shadowOpacity":0.3,"shadowRadius":20,"shadowOffsetX":0,"shadowOffsetY":5,"animation":"pop","animationCurve":"swift","exitAnimation":"fade","enterDuration":0.4,"exitDuration":0.3,"backdropBlur":12,"backdropBlurIncludesCamera":false,"hidesScreen":false,"hidesCamera":false,"layerIndex":2}]
      },
      "openingSequence":{"isEnabled":true,"preset":"轻 3D","motionCurve":"swift","duration":2.4,"stagger":0.2,"includedElements":["屏幕","贴图"],"elementOrder":["屏幕","摄像头","贴图"]},
      "recordingMarkers":[{"id":"55555555-5555-5555-5555-555555555555","sourceTime":1.25,"number":2}],
      "canvas":{"aspectRatio":"16:9","padding":90,"cornerRadius":24,"shadowStrength":0.4,"contentPosition":{"x":0.4,"y":0.6},"contentScale":1.2,"crop":{"x":0.1,"y":0.05,"width":0.8,"height":0.9},"screenFrame":"windowDark","screenFrameScale":1.1,"screenFrameToolbarScale":1.3,"screenFrameOuterCornerRadius":32,"screenFrameContentCornerRadius":8,"borderWidth":3,"borderHex":"#AABBCC","screenFrameTitle":"Demo","screenFrameBrowserAddress":"example.test","backgroundSource":{"kind":"projectVideo","relativePath":"assets/background.mp4"}},
      "cursorStyle":{"assetID":"system.arrow","hideWhenIdle":true,"idleDelay":2.0,"size":1.2,"clickEffectStyle":"ripple","clickColor":"#FFAA00"},
      "camera":{"shape":"circle","position":{"x":0.83,"y":0.78},"size":0.24,"borderWidth":2,"roundness":0.5,"isMirrored":true,"isHidden":true,"contentPosition":{"x":0.55,"y":0.45},"contentScale":1.4},
      "audio":{"systemVolume":0.82,"microphoneVolume":0.73,"isSystemMuted":false,"isMicrophoneMuted":false},
      "exportSettings":{"frameRate":"60 FPS","resolution":"1080p"}
    }
    """;
    var manifest = Path.Combine(source, "project.json");
    File.WriteAllText(manifest, sourceJson);
    var manifestHashBefore = SHA256.HashData(File.ReadAllBytes(manifest));

    var result = DogScProjectImporter.Import(source, destination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
    var loaded = FrameTrailProjectStore.Load(result.ProjectFilePath);
    Check(File.Exists(result.ProjectFilePath), "目标工程已保存为独立 FrameTrail 项目");
    Check(loaded.DisplayName == "迁移验证工程", "DogSC 项目标题映射到目标项目名称");
    Check(loaded.MediaPath == "media/screen.mp4" && File.ReadAllBytes(Path.Combine(destination, loaded.MediaPath)).SequenceEqual(screenBytes), "主视频素材在目标工程内逐字节复制");
    Check(loaded.Timeline.Segments.Count == 2 && Math.Abs(loaded.Timeline.Segments[0].SourceStart - .5) < 1e-9 && loaded.Timeline.Segments[0].PlaybackRate == 2, "已编辑片段顺序、裁剪区间和播放速率映射");
    Check(loaded.ZoomClips.Count == 1 && loaded.ZoomClips[0].Automatic && loaded.ZoomClips[0].Focus == new NormalizedPoint(.72, .61), "缩放片段及自动来源/焦点映射");
    Check(loaded.PointerEvents.Count == 3 && loaded.PointerEvents[1].Kind == PointerEventKind.LeftClick && loaded.PointerEvents[1].CursorAsset == "system.arrow" && Math.Abs(loaded.MouseClock.OffsetSeconds + .2) < 1e-9 && Math.Abs(loaded.MouseClock.Rate - 1.2) < 1e-9, "指针 JSONL、点击类型/光标 ID 和独立时钟校正映射");
    Check(loaded.Markers.Count == 1 && Math.Abs(loaded.Markers[0].SourceTime - 1.25) < 1e-9, "录制标记以源时间导入");
    Check(loaded.Camera is not null && Math.Abs(loaded.Camera.TimelineStart - .1) < 1e-9 && loaded.Camera.Opacity == 0 && File.ReadAllBytes(Path.Combine(destination, loaded.Camera.MediaPath)).SequenceEqual(cameraBytes), "摄像头素材、基础布局和隐藏状态映射");
    Check(loaded.Camera?.Shape == CameraShape.Circle && loaded.Camera.Crop.Width < 1, "摄像头圆形布局和内部取景缩放映射");
    Check(loaded.AudioTracks.Count == 1 && Math.Abs(loaded.AudioTracks[0].Volume - .73) < 1e-9 && File.ReadAllBytes(Path.Combine(destination, loaded.AudioTracks[0].MediaPath)).SequenceEqual(microphoneBytes), "独立麦克风轨道和素材映射");
    Check(Math.Abs(loaded.Settings.MainAudioVolume - .82) < 1e-9 && !loaded.Settings.MainAudioMuted, "内嵌系统音轨音量和静音状态映射");
    Check(loaded.Settings.Canvas?.Frame == ScreenFrameKind.WindowDark && loaded.Settings.Canvas.Crop == new NormalizedRect(.1, .05, .8, .9), "常用画布布局和窗口外框映射");
    Check(loaded.Settings.Canvas!.FrameToolbarScale == 1.3 && loaded.Settings.Canvas.FrameOuterRadius == 32 && Math.Abs(loaded.Settings.CornerRadius - 8d / 1080) < 1e-9, "样机顶栏、外圆角与内容圆角独立导入");
    Check(loaded.Settings.BackgroundStyle?.Kind == BackgroundKind.Video && loaded.Settings.BackgroundStyle.VideoPath == "assets/background.mp4" && File.ReadAllBytes(Path.Combine(destination, loaded.Settings.BackgroundStyle.VideoPath)).SequenceEqual(backgroundVideoBytes), "DogSC 项目循环视频背景安全复制并持久化");
    Check(loaded.Settings.Cursor?.Scale == 1.2 && Math.Abs(loaded.Settings.Cursor.ClickDuration - .36) < 1e-9, "系统光标尺寸、隐藏规则和相近点击反馈映射");
    Check(loaded.ScreenMotions.Count == 1 && loaded.ScreenMotions[0].Focus?.Target == FocusEffectTarget.Pointer && loaded.ScreenMotions[0].Target.RotationY == -4, "屏幕 3D 姿态与指针聚焦效果映射");
    Check(loaded.CameraMotions.Count == 1 && loaded.CameraMotions[0].TargetSize == .32 && loaded.CameraMotions[0].TargetPosition.X == .72, "摄像头运动位置、尺寸与时间映射");
    Check(loaded.BlurClips.Count == 1 && loaded.BlurClips[0].Rect == new NormalizedRect(.2, .25, .3, .2) && loaded.BlurClips[0].EnterDuration == .25, "DogSC 区域柔化与过渡映射");
    Check(loaded.Overlays.Count == 1 && File.ReadAllBytes(Path.Combine(destination, loaded.Overlays[0].MediaPath)).SequenceEqual(stickerBytes) && loaded.Overlays[0].EnterMotion == OverlayMotion.Scale, "贴图素材安全复制且主体/动画映射");
    Check(Math.Abs(loaded.Overlays[0].Rect.Width / loaded.Overlays[0].Rect.Height * 16 / 9 - 2) < .02, "贴图按原始图像宽高比映射到画布");
    Check(loaded.Settings.Opening?.Enabled == true && loaded.Settings.Opening.Preset == OpeningPreset.Light3D && loaded.Settings.Opening.Duration == 2.4 && loaded.Settings.Opening.IncludedElements.SequenceEqual([OpeningElement.Screen, OpeningElement.Overlays]) && loaded.Settings.Opening.ElementOrder.SequenceEqual([OpeningElement.Screen, OpeningElement.Camera, OpeningElement.Overlays]) && loaded.Settings.Opening.Stagger == .2, "开场元素启用、顺序、延迟、Light3D预设和时长映射");
    Check(result.Report.Warnings.Any(x => x.Contains("曲线", StringComparison.Ordinal)) && result.Report.Warnings.Any(x => x.Contains("边框", StringComparison.Ordinal)), "报告逐项说明开场缓动近似和贴图边框等未支持效果");
    Check(File.Exists(Path.Combine(destination, "dogsc-import-report.json")), "目标工程附带字段损失/未支持项报告");
    Check(SHA256.HashData(File.ReadAllBytes(manifest)).SequenceEqual(manifestHashBefore), "原 DogSC project.json 未发生改写");

    var unsafeSource = Path.Combine(temporaryRoot, "unsafe.dogscproject");
    Directory.CreateDirectory(unsafeSource);
    File.WriteAllText(Path.Combine(unsafeSource, "project.json"), """
        {"version":27,"media":{"screen":{"relativePath":"../outside.mp4"}}}
        """);
    var unsafeDestination = Path.Combine(temporaryRoot, "Unsafe.frametrail");
    try
    {
        DogScProjectImporter.Import(unsafeSource, unsafeDestination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：越界素材路径应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝素材路径越界"); }
    Check(!Directory.Exists(unsafeDestination), "路径校验失败时不留下半成品目标工程");

    var unsafeStickerSource = Path.Combine(temporaryRoot, "unsafe-sticker.dogscproject");
    Directory.CreateDirectory(unsafeStickerSource);
    Write(unsafeStickerSource, "screen.mp4", screenBytes);
    File.WriteAllText(Path.Combine(unsafeStickerSource, "project.json"), """
        {"version":27,"media":{"screen":{"relativePath":"screen.mp4"}},"timeline":{"stickerClips":[{"timing":{"startTime":0,"duration":1},"relativePath":"../escape.png"}]}}
        """);
    var unsafeStickerDestination = Path.Combine(temporaryRoot, "UnsafeSticker.frametrail");
    try
    {
        DogScProjectImporter.Import(unsafeStickerSource, unsafeStickerDestination, new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：越界贴图路径应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝贴图素材路径越界"); }
    Check(!Directory.Exists(unsafeStickerDestination), "贴图路径校验失败时不留下半成品目标工程");

    var futureSource = Path.Combine(temporaryRoot, "future.dogscproject");
    Directory.CreateDirectory(futureSource);
    File.WriteAllText(Path.Combine(futureSource, "project.json"), """
        {"version":28,"media":{"screen":{"relativePath":"media/screen.mp4"}}}
        """);
    try
    {
        DogScProjectImporter.Import(futureSource, Path.Combine(temporaryRoot, "Future.frametrail"), new MediaAsset { Width = 1280, Height = 720, Duration = 4 });
        throw new InvalidOperationException("失败：未来 schema 应被拒绝");
    }
    catch (InvalidDataException) { Console.WriteLine("通过：拒绝未知未来 DogSC schema"); }

    Console.WriteLine("DOGSC_IMPORT_FIXTURE=PASS；此构造 schema fixture 不是原 Mac 项目互通认证。");
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}
