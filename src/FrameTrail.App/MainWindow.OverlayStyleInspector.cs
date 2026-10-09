namespace FrameTrail.App;

public partial class MainWindow
{
    private void OverlayStyleControls(OverlayClip clip)
    {
        var id = clip.Id;
        OverlayClip Target(FrameTrailProject p) => p.Overlays.First(x => x.Id == id);
        Section("边框与阴影");
        Number("贴图描边", clip.BorderWidth, 0, 60, v => ApplyEdit(p => Target(p).BorderWidth = v), " px");
        if (clip.BorderWidth > 0)
            TextField("描边颜色", clip.BorderColor, v => ApplyEdit(p => Target(p).BorderColor = ValidColor(v)));
        Number("阴影不透明度", clip.ShadowOpacity * 100, 0, 100, v => ApplyEdit(p => Target(p).ShadowOpacity = v / 100), "%", "0");
        if (clip.ShadowOpacity > 0)
        {
            Number("阴影柔和度", clip.ShadowRadius, 0, 160, v => ApplyEdit(p => Target(p).ShadowRadius = v), " px");
            Number("阴影水平偏移", clip.ShadowOffsetX, -250, 250, v => ApplyEdit(p => Target(p).ShadowOffsetX = v), " px");
            Number("阴影垂直偏移", clip.ShadowOffsetY, -250, 250, v => ApplyEdit(p => Target(p).ShadowOffsetY = v), " px");
        }
        Choices("动画节奏", clip.AnimationCurve is { } c ? (int)c : -1,
            new[] { (-1, "线性"), ((int)ElementMotionCurve.Smooth, "顺滑"), ((int)ElementMotionCurve.Swift, "利落"), ((int)ElementMotionCurve.Gentle, "柔和") },
            v => ApplyEdit(p => Target(p).AnimationCurve = v < 0 ? null : (ElementMotionCurve)v));
        Section("贴图背后的画面");
        Number("背景柔化", clip.BackdropBlur, 0, 100, v => ApplyEdit(p => Target(p).BackdropBlur = v), " px");
        Toggle("同时柔化摄像头", clip.BackdropBlurIncludesCamera, v => ApplyEdit(p => Target(p).BackdropBlurIncludesCamera = v));
        Toggle("贴图显示时隐藏屏幕", clip.HidesScreen, v => ApplyEdit(p => Target(p).HidesScreen = v));
        Toggle("贴图显示时隐藏摄像头", clip.HidesCamera, v => ApplyEdit(p => Target(p).HidesCamera = v));
    }
}
