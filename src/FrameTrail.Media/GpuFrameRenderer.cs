using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FrameTrail;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FrameTrail.Media;

/// <summary>A decoded BGRA image/video layer positioned on the output canvas. Input buffers are consumed synchronously.</summary>
public sealed record FrameLayerInput(int Width, int Height, byte[] Bgra, NormalizedRect Rect, double Rotation = 0, double Opacity = 1, int Layer = 0, double CornerRadius = 0, bool Mirrored = false, bool BehindScreen = false, BackgroundImageFit Fit = BackgroundImageFit.Stretch, double Blur = 0, CameraShape Shape = CameraShape.RoundedRectangle, NormalizedRect? SourceCrop = null, bool FollowScreenTransform = false, double BorderWidth = 0, string BorderColor = "#FFFFFF");

/// <summary>One-frame Direct3D 11 compositor. Calls may move between threads, but must remain sequential.</summary>
public sealed class GpuFrameRenderer : IDisposable
{
    private readonly int sourceWidth, sourceHeight, outputWidth, outputHeight;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11Texture2D? sourceTexture, targetTexture, stagingTexture;
    private ID3D11ShaderResourceView? sourceView;
    private ID3D11RenderTargetView? targetView;
    private ID3D11VertexShader? vertexShader;
    private ID3D11PixelShader? pixelShader;
    private ID3D11SamplerState? sampler;
    private ID3D11Buffer? constantsBuffer;
    private readonly LayerSlot?[] layerSlots = new LayerSlot?[9];
    private bool disposed;

    public string Backend { get; }

    public GpuFrameRenderer(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        this.sourceWidth = sourceWidth; this.sourceHeight = sourceHeight;
        this.outputWidth = outputWidth; this.outputHeight = outputHeight;
        try
        {
            try
            {
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device!, out _, out context!).CheckError();
                Backend = "Direct3D 11 GPU hardware";
            }
            catch
            {
                device?.Dispose(); context?.Dispose(); device = null; context = null;
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device!, out _, out context!).CheckError();
                Backend = "Direct3D 11 WARP software";
            }
            Initialize();
        }
        catch { Dispose(); throw; }
    }

    public void Render(byte[] bgraSource, FrameScene scene, byte[] bgraOutput) => Render(bgraSource, scene, bgraOutput, null);

    public void Render(byte[] bgraSource, FrameScene scene, byte[] bgraOutput, IReadOnlyList<FrameLayerInput>? layers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bgraSource); ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(bgraOutput);
        var sourceBytes = checked(sourceWidth * sourceHeight * 4); var outputBytes = checked(outputWidth * outputHeight * 4);
        if (bgraSource.Length != sourceBytes) throw new ArgumentException($"源 BGRA 缓冲区长度应为 {sourceBytes} 字节。", nameof(bgraSource));
        if (bgraOutput.Length != outputBytes) throw new ArgumentException($"输出 BGRA 缓冲区长度应为 {outputBytes} 字节。", nameof(bgraOutput));
        if ((scene.BlurRegions?.Count ?? 0) > 4) throw new ArgumentException("单帧最多支持 4 个独立模糊区域。", nameof(scene));
        if (layers is { Count: > 9 }) throw new ArgumentException("单帧最多支持 8 个前景和 1 个背景合成图层。", nameof(layers));
        var orderedLayers = layers?.OrderBy(x => x.Layer).ToArray() ?? [];
        var underlays = orderedLayers.Where(x => x.BehindScreen).ToArray();
        if (underlays.Length > 1) throw new ArgumentException("单帧最多支持 1 个屏幕后置背景图层。", nameof(layers));
        var underlay = underlays.FirstOrDefault();
        var foregroundLayers = orderedLayers.Where(x => !x.BehindScreen).ToArray();
        if (foregroundLayers.Length > 8) throw new ArgumentException("单帧最多支持 8 个前景合成图层。", nameof(layers));
        var constants = MakeConstants(scene, foregroundLayers, underlay, sourceWidth, sourceHeight, outputWidth, outputHeight);
        unsafe
        {
            fixed (byte* sourcePtr = bgraSource)
                context!.UpdateSubresource(sourceTexture!, 0, null, (IntPtr)sourcePtr, (uint)(sourceWidth * 4), 0);
        }
        context!.UpdateSubresource(in constants, constantsBuffer!);
        context.OMSetRenderTargets(targetView!, null);
        context.RSSetViewports(new[] { new Viewport(0, 0, outputWidth, outputHeight) });
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShader);
        context.PSSetConstantBuffer(0, constantsBuffer!);
        context.PSSetShaderResource(0, sourceView!);
        if (underlay is { } bg)
        {
            if (bg.Bgra.Length != checked(bg.Width * bg.Height * 4)) throw new ArgumentException("背景 BGRA 缓冲区长度与尺寸不匹配。", nameof(layers));
            var slot = EnsureLayerSlot(8, bg.Width, bg.Height);
            unsafe { fixed (byte* ptr = bg.Bgra) context.UpdateSubresource(slot.Texture, 0, null, (IntPtr)ptr, (uint)(bg.Width * 4), 0); }
            context.PSSetShaderResource(9, slot.View);
        }
        else context.PSSetShaderResource(9, null!);
        for (var i = 0; i < 8; i++)
        {
            if (i < foregroundLayers.Length)
            {
                var layer = foregroundLayers[i];
                if (layer.Bgra.Length != checked(layer.Width * layer.Height * 4)) throw new ArgumentException($"合成图层 {i} BGRA 缓冲区长度与尺寸不匹配。", nameof(layers));
                var slot = EnsureLayerSlot(i, layer.Width, layer.Height);
                unsafe
                {
                    fixed (byte* layerPtr = layer.Bgra)
                        context.UpdateSubresource(slot.Texture, 0, null, (IntPtr)layerPtr, (uint)(layer.Width * 4), 0);
                }
                context.PSSetShaderResource((uint)(i + 1), slot.View);
            }
            else context.PSSetShaderResource((uint)(i + 1), null!);
        }
        context.PSSetSampler(0, sampler!);
        context.Draw(3, 0);
        for (var i = 0; i < 10; i++) context.PSSetShaderResource((uint)i, null!);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        context.CopyResource(stagingTexture!, targetTexture!);
        context.Map(stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                fixed (byte* dst = bgraOutput)
                    for (var row = 0; row < outputHeight; row++)
                        Buffer.MemoryCopy(src + row * mapped.RowPitch, dst + row * outputWidth * 4, outputWidth * 4, outputWidth * 4);
            }
        }
        finally { context.Unmap(stagingTexture!, 0); }
    }

    private void Initialize()
    {
        sourceTexture = device!.CreateTexture2D(TextureDescription(sourceWidth, sourceHeight, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        sourceView = device.CreateShaderResourceView(sourceTexture);
        targetTexture = device.CreateTexture2D(TextureDescription(outputWidth, outputHeight, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
        targetView = device.CreateRenderTargetView(targetTexture);
        stagingTexture = device.CreateTexture2D(TextureDescription(outputWidth, outputHeight, Format.B8G8R8A8_UNorm, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        var shader = Compiler.Compile(ShaderSource, "VSMain", "FrameTrail.Scene.hlsl", "vs_5_0").ToArray();
        vertexShader = device.CreateVertexShader(shader);
        shader = Compiler.Compile(ShaderSource, "PSMain", "FrameTrail.Scene.hlsl", "ps_5_0").ToArray();
        pixelShader = device.CreatePixelShader(shader);
        constantsBuffer = device.CreateBuffer((uint)Marshal.SizeOf<ShaderConstants>(), BindFlags.ConstantBuffer);
        sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
            MaxAnisotropy = 1
        });
    }

    /// <summary>Maps a normalized canvas coordinate onto the recorded source image when it hits the visible screen body.</summary>
    public static bool TryMapCanvasPointToSource(FrameScene scene, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight,
        NormalizedPoint canvasPoint, out NormalizedPoint sourcePoint)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        sourcePoint = default;
        if (!canvasPoint.IsFinite || canvasPoint.X is < 0 or > 1 || canvasPoint.Y is < 0 or > 1) return false;

        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var centerX = constants.ScreenRect.X + constants.ScreenRect.Z * .5f;
        var centerY = constants.ScreenRect.Y + constants.ScreenRect.W * .5f;
        var pixelX = (float)canvasPoint.X * outputWidth;
        var pixelY = (float)canvasPoint.Y * outputHeight;
        var angle = -constants.Motion.X * (MathF.PI / 180);
        var dx = pixelX - centerX; var dy = pixelY - centerY;
        var contentX = centerX + MathF.Cos(angle) * dx - MathF.Sin(angle) * dy;
        var contentY = centerY + MathF.Sin(angle) * dx + MathF.Cos(angle) * dy;
        var halfWidth = Math.Max(constants.ScreenRect.Z * .5f, 1);
        var halfHeight = Math.Max(constants.ScreenRect.W * .5f, 1);
        var targetX = (contentX - centerX) / halfWidth;
        var targetY = (contentY - centerY) / halfHeight;
        var projected = TransformPoint(targetX, targetY, constants.Inverse0, constants.Inverse1, constants.Inverse2);
        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z) || Math.Abs(projected.Z) < 1e-6f) return false;
        var sign = MathF.Sign(projected.Z);
        var sourceX = projected.X / Math.Abs(projected.Z) * sign * .5f + .5f;
        var sourceY = projected.Y / Math.Abs(projected.Z) * sign * .5f + .5f;
        const float edgeTolerance = .001f;
        if (sourceX < -edgeTolerance || sourceX > 1 + edgeTolerance || sourceY < -edgeTolerance || sourceY > 1 + edgeTolerance) return false;
        sourceX = Math.Clamp(sourceX, 0, 1); sourceY = Math.Clamp(sourceY, 0, 1);

        var pX = (sourceX * 2 - 1) * halfWidth; var pY = (sourceY * 2 - 1) * halfHeight;
        var radius = constants.Style.X;
        var qX = Math.Abs(pX) - halfWidth + radius;
        var qY = Math.Abs(pY) - halfHeight + radius;
        var outsideX = Math.Max(qX, 0); var outsideY = Math.Max(qY, 0);
        var distance = MathF.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(qX, qY), 0) - radius;
        if (distance > .5f) return false;
        var header = constants.FrameStyle.X;
        var cardHalfHeight = halfHeight + header * .5f;
        var cardPointY = pY + header * .5f;
        var cardRadius = Math.Max(constants.Style2.W, radius + constants.FrameStyle.W);
        var cardQx = Math.Abs(pX) - halfWidth + cardRadius;
        var cardQy = Math.Abs(cardPointY) - cardHalfHeight + cardRadius;
        var cardOutsideX = Math.Max(cardQx, 0); var cardOutsideY = Math.Max(cardQy, 0);
        var cardDistance = MathF.Sqrt(cardOutsideX * cardOutsideX + cardOutsideY * cardOutsideY)
            + Math.Min(Math.Max(cardQx, cardQy), 0) - cardRadius;
        if (cardDistance > .5f) return false;

        sourcePoint = new NormalizedPoint(
            constants.Crop.X + sourceX * constants.Crop.Z,
            constants.Crop.Y + sourceY * constants.Crop.W);
        return sourcePoint.IsFinite;
    }

    /// <summary>Returns the projected screen quadrilateral in canvas-normalized TL, TR, BR, BL order.</summary>
    public static IReadOnlyList<NormalizedPoint> GetProjectedScreenCorners(FrameScene scene, int sourceWidth, int sourceHeight,
        int outputWidth, int outputHeight, bool includeHeader = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sourceWidth <= 0 || sourceHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源图像与输出画布的宽高必须为正数。");
        var constants = MakeConstants(scene, [], null, sourceWidth, sourceHeight, outputWidth, outputHeight);
        var halfWidth = Math.Max(constants.ScreenRect.Z * .5f, 1);
        var halfHeight = Math.Max(constants.ScreenRect.W * .5f, 1);
        var centerX = constants.ScreenRect.X + halfWidth;
        var centerY = constants.ScreenRect.Y + halfHeight;
        var headerExtension = includeHeader ? 2 * constants.FrameStyle.X / Math.Max(constants.ScreenRect.W, 1) : 0;
        var localCorners = new[] { (-1f, -1f - headerExtension), (1f, -1f - headerExtension), (1f, 1f), (-1f, 1f) };
        var angle = constants.Motion.X * (MathF.PI / 180);
        var result = new NormalizedPoint[4];
        for (var i = 0; i < localCorners.Length; i++)
        {
            var projected = TransformPoint(localCorners[i].Item1, localCorners[i].Item2,
                constants.Transform0, constants.Transform1, constants.Transform2);
            if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) || !float.IsFinite(projected.Z) || Math.Abs(projected.Z) < 1e-6f)
                throw new InvalidOperationException("屏幕透视矩阵无法投影选区顶点。");
            var x = centerX + projected.X / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * halfWidth;
            var y = centerY + projected.Y / Math.Abs(projected.Z) * MathF.Sign(projected.Z) * halfHeight;
            var dx = x - centerX; var dy = y - centerY;
            var rotatedX = centerX + MathF.Cos(angle) * dx - MathF.Sin(angle) * dy;
            var rotatedY = centerY + MathF.Sin(angle) * dx + MathF.Cos(angle) * dy;
            result[i] = new(rotatedX / outputWidth, rotatedY / outputHeight);
        }
        return result;
    }

    private static Vector3 TransformPoint(float x, float y, Vector4 row0, Vector4 row1, Vector4 row2) => new(
        x * row0.X + y * row1.X + row2.X,
        x * row0.Y + y * row1.Y + row2.Y,
        x * row0.Z + y * row1.Z + row2.Z);

    private static ShaderConstants MakeConstants(FrameScene scene, FrameLayerInput[] layers, FrameLayerInput? underlay,
        int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var padding = Math.Clamp(double.IsFinite(scene.Padding) ? scene.Padding : 0, 0, .49);
        var availableWidth = outputWidth * (1 - 2 * padding); var availableHeight = outputHeight * (1 - 2 * padding);
        var frame = scene.Canvas?.Frame ?? ScreenFrameKind.None;
        var shortEdge = Math.Min(outputWidth, outputHeight);
        var frameScale = scene.Canvas?.FrameScale ?? 1;
        var isPhone = frame == ScreenFrameKind.Phone;
        var isTablet = frame == ScreenFrameKind.Tablet;
        var deviceKind = isPhone ? 1f : isTablet ? 2f : 0f;
        var bezelBase = shortEdge * (isPhone ? .026 : isTablet ? .035 : 0);
        var outerRadiusBase = shortEdge * (isPhone ? .11 : isTablet ? .065 : 0);
        // Match the source renderer's 46px toolbar at 1080p; browser chrome uses its 1.08 toolbar scale.
        var toolbarScale = Math.Clamp(scene.Canvas?.FrameToolbarScale ?? 1, .65, 1.6);
        var baseHeaderHeight = (frame switch { ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark => shortEdge * (46d / 1080), ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => shortEdge * (46d * 1.08 / 1080), _ => 0 }) * toolbarScale;
        var reservedHeader = baseHeaderHeight * frameScale;
        var fit = Math.Min(Math.Max(2, availableWidth - bezelBase * 2) / sourceWidth,
            Math.Max(2, availableHeight - reservedHeader - bezelBase * 2) / sourceHeight);
        var screenWidth = sourceWidth * fit; var screenHeight = sourceHeight * fit;
        var contentScale = scene.Canvas is { } canvasSettings ? Math.Clamp(canvasSettings.ContentScale, .1, 4) : 1;
        var position = scene.ScreenMotion?.Position ?? scene.Canvas?.ContentPosition ?? new NormalizedPoint(.5, .5);
        if (!position.IsFinite) position = new(.5, .5);
        var opening = scene.Opening ?? new FrameOpeningState();
        var screenCenterX = Math.Clamp(position.X, 0, 1) * outputWidth + Math.Clamp(opening.OffsetX, -1, 1) * outputWidth;
        var screenCenterY = Math.Clamp(position.Y, 0, 1) * outputHeight + Math.Clamp(opening.OffsetY, -1, 1) * outputHeight;
        var motionScale = scene.ScreenMotion?.Scale ?? 1;
        var totalScale = Math.Clamp(contentScale * frameScale * (double.IsFinite(motionScale) ? motionScale : 1) * Math.Clamp(opening.Scale, .1, 4), .1, 8);
        var headerHeight = baseHeaderHeight * totalScale;
        screenCenterY += headerHeight * .5;
        screenWidth *= totalScale; screenHeight *= totalScale;
        var left = screenCenterX - screenWidth * .5; var top = screenCenterY - screenHeight * .5;
        var deviceBezel = bezelBase * totalScale;
        var authoredOuterRadius = scene.Canvas?.FrameOuterRadius;
        var deviceOuterRadius = authoredOuterRadius.HasValue
            ? shortEdge * Math.Clamp(authoredOuterRadius.Value, 0, 160) / 1080d * totalScale
            : outerRadiusBase * totalScale;
        var radius = deviceKind > 0 ? Math.Max(1, deviceOuterRadius - deviceBezel) : Math.Clamp(scene.CornerRadius, 0, .5) * shortEdge;
        var chromeOuterRadius = frame switch
        {
            ScreenFrameKind.WindowLight or ScreenFrameKind.WindowDark => shortEdge * totalScale * ((float)(authoredOuterRadius ?? 18) / 1080f),
            ScreenFrameKind.BrowserLight or ScreenFrameKind.BrowserDark => shortEdge * totalScale * ((float)(authoredOuterRadius ?? 22) / 1080f),
            _ => 0
        };
        var shadowRadius = Math.Clamp(scene.Shadow, 0, 1) * shortEdge * .045;
        var crop = scene.CropOrigin.IsFinite && scene.CropSize.IsFinite ? scene.CropOrigin : new NormalizedPoint(0, 0);
        var cropSize = scene.CropSize.IsFinite ? scene.CropSize : new NormalizedPoint(1, 1);
        if (scene.Canvas is { } c)
        {
            crop = new(c.Crop.X + crop.X * c.Crop.Width, c.Crop.Y + crop.Y * c.Crop.Height);
            cropSize = new(cropSize.X * c.Crop.Width, cropSize.Y * c.Crop.Height);
        }
        cropSize = new(Math.Clamp(cropSize.X, .0001, 1), Math.Clamp(cropSize.Y, .0001, 1));
        var zoom = Math.Max(double.IsFinite(scene.Zoom) ? scene.Zoom : 1, 1);
        // Crop size is authoritative when supplied; zoom remains available to consumers and helps guard legacy scenes.
        if (cropSize.X >= .9999 && cropSize.Y >= .9999 && zoom > 1.0001) cropSize = new(1 / zoom, 1 / zoom);
        crop = new(Math.Clamp(crop.X, 0, 1 - cropSize.X), Math.Clamp(crop.Y, 0, 1 - cropSize.Y));
        var cursorVisible = scene.CursorAppearance?.Visible != false && scene.Cursor is { IsFinite: true } p
            && p.X >= crop.X && p.X <= crop.X + cropSize.X
            && p.Y >= crop.Y && p.Y <= crop.Y + cropSize.Y;
        var cursor = cursorVisible ? scene.Cursor!.Value : new NormalizedPoint(-1, -1);
        var cursorScale = shortEdge / 1080f * (float)Math.Clamp(scene.CursorAppearance?.Scale ?? 1, .1, 5);
        var style = scene.CursorAppearance?.Style ?? CursorStyle.SystemArrow;
        var clickStyle = scene.CursorAppearance is { ClickEffectsEnabled: false } ? ClickEffectStyle.None : scene.CursorAppearance?.ClickEffect ?? ClickEffectStyle.Pulse;
        var gradient = scene.BackgroundStyle ?? new BackgroundSettings { Color = scene.Background, EndColor = scene.Background };
        var background = ParseColor(gradient.Color);
        var endBackground = ParseColor(gradient.Kind == BackgroundKind.LinearGradient ? gradient.EndColor : gradient.Color);
        var angle = (float)((double.IsFinite(gradient.AngleDegrees) ? gradient.AngleDegrees : 90) * Math.PI / 180);
        var patternKind = gradient.Kind == BackgroundKind.Pattern ? gradient.Pattern == BackgroundPatternKind.Grid ? 1 : 2 : 0;
        var patternSpacing = (float)(Math.Clamp(gradient.PatternSpacing, 8, 512) * shortEdge / 1080d);
        var patternStrength = gradient.Kind == BackgroundKind.Pattern ? (float)Math.Clamp(gradient.PatternStrength, 0, 1) : 0;
        var frameLight = frame is ScreenFrameKind.WindowLight or ScreenFrameKind.BrowserLight;
        var motionState = scene.ScreenMotion ?? new ScreenMotionState();
        if (scene.Opening is { } screenOpening)
            motionState = motionState with
            {
                RotationX = motionState.RotationX + screenOpening.RotationX,
                RotationY = motionState.RotationY + screenOpening.RotationY,
                Perspective = Math.Clamp(motionState.Perspective + screenOpening.Perspective, 0, 2)
            };
        var transform = MakeHomography(motionState);
        var inverse = InvertHomography(transform);
        var result = new ShaderConstants
        {
            OutputSourceSize = new(outputWidth, outputHeight, sourceWidth, sourceHeight),
            Crop = new((float)crop.X, (float)crop.Y, (float)cropSize.X, (float)cropSize.Y),
            ScreenRect = new((float)left, (float)top, (float)screenWidth, (float)screenHeight),
            Style = new((float)radius, (float)shadowRadius, (float)Math.Clamp(scene.Shadow, 0, 1), cursorVisible ? (float)Math.Clamp(scene.ClickStrength, 0, 1) : 0),
            Cursor = new((float)cursor.X, (float)cursor.Y, cursorVisible ? 1 : 0, cursorScale),
            Background = background,
            Style2 = new(angle, (float)style, (float)clickStyle, (float)chromeOuterRadius),
            BackgroundEnd = endBackground,
            BackgroundPattern = new(patternKind, patternSpacing, patternStrength, 0),
            PatternColor = ParseColor(gradient.PatternColor),
            BackgroundAnimation = new(gradient.Kind == BackgroundKind.Dynamic ? 1 : 0, (float)gradient.Preset,
                (float)((double.IsFinite(scene.OutputTime) ? scene.OutputTime : 0) * Math.Clamp(gradient.Speed, 0, 10)), 0),
            FrameStyle = new((float)headerHeight, (float)frame, frameLight ? 1 : 0, (float)Math.Clamp(scene.Canvas?.BorderWidth ?? 0, 0, 60) * outputHeight / Math.Max(scene.CanvasHeight, 1)),
            DeviceFrame = new((float)deviceBezel, (float)deviceOuterRadius, (float)(deviceBezel * .18), deviceKind),
            FrameColor = frameLight ? new(.92f, .93f, .95f, 1) : new(.12f, .13f, .15f, 1),
            Motion = new((float)((scene.ScreenMotion?.RotationZ ?? 0) + opening.Rotation), (float)Math.Clamp(opening.Opacity, 0, 1), 0, 0),
            Transform0 = transform.Row0, Transform1 = transform.Row1, Transform2 = transform.Row2,
            Inverse0 = inverse.Row0, Inverse1 = inverse.Row1, Inverse2 = inverse.Row2,
            ClickColor = ParseColor(scene.CursorAppearance?.ClickColor),
            Focus = scene.Focus is { } focus ? new((float)focus.Center.X, (float)focus.Center.Y, (float)focus.Size, (float)focus.Blur) : Vector4.Zero,
            FocusStyle = scene.Focus is { } focusStyle ? new((float)focusStyle.Shape, (float)focusStyle.AngleDegrees, (float)focusStyle.Softness, (float)focusStyle.Dimming) : Vector4.Zero,
            FocusProgress = new((float)Math.Clamp(scene.FocusProgress, 0, 1), 0, 0, 0),
            BlurRect0 = BlurRect(scene, 0), BlurRect1 = BlurRect(scene, 1), BlurRect2 = BlurRect(scene, 2), BlurRect3 = BlurRect(scene, 3),
            BlurStyle0 = BlurStyle(scene, 0), BlurStyle1 = BlurStyle(scene, 1), BlurStyle2 = BlurStyle(scene, 2), BlurStyle3 = BlurStyle(scene, 3),
            LayerCount = new(layers.Length, 0, 0, 0)
        };
        for (var i = 0; i < layers.Length; i++)
        {
            var layer = layers[i]; var rect = layer.Rect;
            if (!rect.IsFinite || rect.Width <= 0 || rect.Height <= 0) throw new ArgumentException("场景图层矩形必须是有限的正尺寸区域。", nameof(layers));
            SetLayer(ref result, i, new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height),
                new((float)Math.Clamp(layer.Opacity, 0, 1), layer.Shape == CameraShape.Circle ? -1 : (float)Math.Clamp(layer.CornerRadius, 0, .5), (float)layer.Rotation, (layer.Mirrored ? 1 : 0) + (layer.FollowScreenTransform ? 2 : 0) + (float)Math.Clamp(layer.Blur, 0, 1) * .25f),
                ValidCrop(layer.SourceCrop), new((float)Math.Clamp(layer.BorderWidth, 0, 60) * outputHeight / Math.Max(scene.CanvasHeight, 1), ParseColor(layer.BorderColor).X, ParseColor(layer.BorderColor).Y, ParseColor(layer.BorderColor).Z));
        }
        if (underlay is { } backgroundLayer)
            result.UnderlayStyle = new((float)Math.Clamp(backgroundLayer.Opacity, 0, 1), 1, (float)backgroundLayer.Fit, (float)Math.Clamp(backgroundLayer.Blur, 0, 1) * 100);
        return result;
    }

    private static (Vector4 Row0, Vector4 Row1, Vector4 Row2) MakeHomography(ScreenMotionState s)
    {
        var rx = Math.Clamp(s.RotationX, -80, 80) * Math.PI / 180; var ry = Math.Clamp(s.RotationY, -80, 80) * Math.PI / 180;
        var cx = Math.Cos(rx); var sx = Math.Sin(rx); var cy = Math.Cos(ry); var sy = Math.Sin(ry); var p = Math.Clamp(s.Perspective, 0, 2);
        // Rows are stored for HLSL's row-vector mul(float3, float3x3). Perspective terms therefore
        // belong in the Z column so the projected denominator varies with X/Y.
        return (new((float)cy, 0, (float)(-p * sy), 0), new((float)(p * sy * sx), (float)cx, (float)(p * cy * sx), 0), new(0, 0, 1, 0));
    }

    private static (Vector4 Row0, Vector4 Row1, Vector4 Row2) InvertHomography((Vector4 Row0, Vector4 Row1, Vector4 Row2) m)
    {
        var a=m.Row0.X; var b=m.Row0.Y; var c=m.Row0.Z; var d=m.Row1.X; var e=m.Row1.Y; var f=m.Row1.Z; var g=m.Row2.X; var h=m.Row2.Y; var i=m.Row2.Z;
        var A=e*i-f*h; var B=c*h-b*i; var C=b*f-c*e; var D=f*g-d*i; var E=a*i-c*g; var F=c*d-a*f; var G=d*h-e*g; var H=b*g-a*h; var I=a*e-b*d;
        var determinant=a*A+b*D+c*G;
        if (!float.IsFinite(determinant) || Math.Abs(determinant) < 1e-6) return (new(1,0,0,0),new(0,1,0,0),new(0,0,1,0));
        var scale=1/determinant;
        return (new(A*scale,B*scale,C*scale,0),new(D*scale,E*scale,F*scale,0),new(G*scale,H*scale,I*scale,0));
    }

    private static void SetLayer(ref ShaderConstants c, int i, Vector4 rect, Vector4 style, Vector4 crop, Vector4 border)
    {
        switch (i)
        {
            case 0: c.LayerRect0=rect; c.LayerStyle0=style; c.LayerCrop0=crop; c.LayerBorder0=border; break; case 1: c.LayerRect1=rect; c.LayerStyle1=style; c.LayerCrop1=crop; c.LayerBorder1=border; break;
            case 2: c.LayerRect2=rect; c.LayerStyle2=style; c.LayerCrop2=crop; c.LayerBorder2=border; break; case 3: c.LayerRect3=rect; c.LayerStyle3=style; c.LayerCrop3=crop; c.LayerBorder3=border; break;
            case 4: c.LayerRect4=rect; c.LayerStyle4=style; c.LayerCrop4=crop; c.LayerBorder4=border; break; case 5: c.LayerRect5=rect; c.LayerStyle5=style; c.LayerCrop5=crop; c.LayerBorder5=border; break;
            case 6: c.LayerRect6=rect; c.LayerStyle6=style; c.LayerCrop6=crop; c.LayerBorder6=border; break; case 7: c.LayerRect7=rect; c.LayerStyle7=style; c.LayerCrop7=crop; c.LayerBorder7=border; break;
        }
    }

    private static Vector4 ValidCrop(NormalizedRect? crop)
    {
        var r = crop ?? NormalizedRect.Full;
        if (!r.IsFinite || r.X < 0 || r.Y < 0 || r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1 || r.Y + r.Height > 1)
            throw new ArgumentException("图层素材裁切范围无效。", nameof(crop));
        return new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }

    private LayerSlot EnsureLayerSlot(int index, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new ArgumentOutOfRangeException(nameof(width), "图层尺寸无效。");
        var existing = layerSlots[index];
        if (existing is { } slot && slot.Width == width && slot.Height == height) return slot;
        existing?.Dispose();
        var texture = device!.CreateTexture2D(TextureDescription(width, height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        var created = new LayerSlot(width, height, texture, device.CreateShaderResourceView(texture));
        layerSlots[index] = created;
        return created;
    }

    private static Vector4 BlurRect(FrameScene scene, int index)
    {
        var list = scene.BlurRegions;
        if (list is null || index >= list.Count) return Vector4.Zero;
        var item = list[index]; var rect = item.Rect;
        return new((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
    }
    private static Vector4 BlurStyle(FrameScene scene, int index)
    {
        var list = scene.BlurRegions;
        if (list is null || index >= list.Count) return Vector4.Zero;
        var item = list[index];
        return new((float)item.Strength, (float)item.Kind, (float)item.CornerRadius, (float)item.Opacity);
    }

    private static Texture2DDescription TextureDescription(int width, int height, Format format, BindFlags bind, ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
        Format = format, SampleDescription = new SampleDescription(1, 0),
        Usage = usage, BindFlags = bind, CPUAccessFlags = cpu, MiscFlags = ResourceOptionFlags.None
    };

    private static Vector4 ParseColor(string? color)
    {
        if (color is not null && Regex.Match(color, "^#([0-9A-Fa-f]{6})$") is { Success: true } m)
        {
            var value = Convert.ToUInt32(m.Groups[1].Value, 16);
            return new(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1);
        }
        return new(.125f, .129f, .141f, 1);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        context?.ClearState(); context?.Flush();
        foreach (var slot in layerSlots) slot?.Dispose();
        constantsBuffer?.Dispose(); sampler?.Dispose(); pixelShader?.Dispose(); vertexShader?.Dispose();
        targetView?.Dispose(); sourceView?.Dispose(); stagingTexture?.Dispose(); targetTexture?.Dispose(); sourceTexture?.Dispose();
        context?.Dispose(); device?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public Vector4 OutputSourceSize;
        public Vector4 Crop;
        public Vector4 ScreenRect;
        public Vector4 Style;
        public Vector4 Cursor; // normalized source position, visible flag, 1080-short-edge size scale
        public Vector4 Background;
        public Vector4 Style2; // gradient angle, cursor style, click effect style, reserved
        public Vector4 BackgroundEnd;
        public Vector4 FrameStyle;
        public Vector4 DeviceFrame; // bezel px, outer radius px, rim width px, kind (1 phone, 2 tablet)
        public Vector4 FrameColor;
        public Vector4 Motion;
        public Vector4 Transform0, Transform1, Transform2;
        public Vector4 Inverse0, Inverse1, Inverse2;
        public Vector4 ClickColor;
        public Vector4 Focus; // source normalized center, region size, blur amount
        public Vector4 FocusStyle; // shape, angle, softness, dimming
        public Vector4 FocusProgress;
        public Vector4 BlurRect0, BlurRect1, BlurRect2, BlurRect3;
        public Vector4 BlurStyle0, BlurStyle1, BlurStyle2, BlurStyle3;
        public Vector4 LayerCount;
        public Vector4 LayerRect0, LayerRect1, LayerRect2, LayerRect3, LayerRect4, LayerRect5, LayerRect6, LayerRect7;
        public Vector4 LayerStyle0, LayerStyle1, LayerStyle2, LayerStyle3, LayerStyle4, LayerStyle5, LayerStyle6, LayerStyle7;
        public Vector4 UnderlayStyle;
        public Vector4 BackgroundPattern;
        public Vector4 PatternColor;
        public Vector4 BackgroundAnimation;
        public Vector4 LayerCrop0, LayerCrop1, LayerCrop2, LayerCrop3, LayerCrop4, LayerCrop5, LayerCrop6, LayerCrop7;
        public Vector4 LayerBorder0, LayerBorder1, LayerBorder2, LayerBorder3, LayerBorder4, LayerBorder5, LayerBorder6, LayerBorder7;
    }

    private sealed record LayerSlot(int Width, int Height, ID3D11Texture2D Texture, ID3D11ShaderResourceView View) : IDisposable
    { public void Dispose() { View.Dispose(); Texture.Dispose(); } }

    private static readonly string ShaderSource = ReadShaderSource();
    private static string ReadShaderSource()
    {
        using var stream = typeof(GpuFrameRenderer).Assembly.GetManifestResourceStream("FrameTrail.Media.Scene.hlsl")
            ?? throw new InvalidOperationException("内嵌 GPU 场景 shader 缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
