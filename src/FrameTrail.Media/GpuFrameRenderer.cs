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

    public void Render(byte[] bgraSource, FrameScene scene, byte[] bgraOutput)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bgraSource); ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(bgraOutput);
        var sourceBytes = checked(sourceWidth * sourceHeight * 4); var outputBytes = checked(outputWidth * outputHeight * 4);
        if (bgraSource.Length != sourceBytes) throw new ArgumentException($"源 BGRA 缓冲区长度应为 {sourceBytes} 字节。", nameof(bgraSource));
        if (bgraOutput.Length != outputBytes) throw new ArgumentException($"输出 BGRA 缓冲区长度应为 {outputBytes} 字节。", nameof(bgraOutput));
        var constants = MakeConstants(scene);
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
        context.PSSetSampler(0, sampler!);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
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

    private ShaderConstants MakeConstants(FrameScene scene)
    {
        var padding = Math.Clamp(double.IsFinite(scene.Padding) ? scene.Padding : 0, 0, .49);
        var availableWidth = outputWidth * (1 - 2 * padding); var availableHeight = outputHeight * (1 - 2 * padding);
        var fit = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
        var screenWidth = sourceWidth * fit; var screenHeight = sourceHeight * fit;
        var left = (outputWidth - screenWidth) * .5; var top = (outputHeight - screenHeight) * .5;
        var shortEdge = Math.Min(outputWidth, outputHeight);
        var radius = Math.Clamp(scene.CornerRadius, 0, .5) * shortEdge;
        var shadowRadius = Math.Clamp(scene.Shadow, 0, 1) * shortEdge * .045;
        var crop = scene.CropOrigin.IsFinite && scene.CropSize.IsFinite ? scene.CropOrigin : new NormalizedPoint(0, 0);
        var cropSize = scene.CropSize.IsFinite ? scene.CropSize : new NormalizedPoint(1, 1);
        cropSize = new(Math.Clamp(cropSize.X, .0001, 1), Math.Clamp(cropSize.Y, .0001, 1));
        var zoom = Math.Max(double.IsFinite(scene.Zoom) ? scene.Zoom : 1, 1);
        // Crop size is authoritative when supplied; zoom remains available to consumers and helps guard legacy scenes.
        if (cropSize.X >= .9999 && cropSize.Y >= .9999 && zoom > 1.0001) cropSize = new(1 / zoom, 1 / zoom);
        crop = new(Math.Clamp(crop.X, 0, 1 - cropSize.X), Math.Clamp(crop.Y, 0, 1 - cropSize.Y));
        var cursorVisible = scene.Cursor is { IsFinite: true } p
            && p.X >= crop.X && p.X <= crop.X + cropSize.X
            && p.Y >= crop.Y && p.Y <= crop.Y + cropSize.Y;
        var cursor = cursorVisible ? scene.Cursor!.Value : new NormalizedPoint(-1, -1);
        var cursorScale = shortEdge / 1080f;
        var background = ParseColor(scene.Background);
        return new ShaderConstants
        {
            OutputSourceSize = new(outputWidth, outputHeight, sourceWidth, sourceHeight),
            Crop = new((float)crop.X, (float)crop.Y, (float)cropSize.X, (float)cropSize.Y),
            ScreenRect = new((float)left, (float)top, (float)screenWidth, (float)screenHeight),
            Style = new((float)radius, (float)shadowRadius, (float)Math.Clamp(scene.Shadow, 0, 1), cursorVisible ? (float)Math.Clamp(scene.ClickStrength, 0, 1) : 0),
            Cursor = new((float)cursor.X, (float)cursor.Y, cursorVisible ? 1 : 0, cursorScale),
            Background = background
        };
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
    }

    private static readonly string ShaderSource = ReadShaderSource();
    private static string ReadShaderSource()
    {
        using var stream = typeof(GpuFrameRenderer).Assembly.GetManifestResourceStream("FrameTrail.Media.Scene.hlsl")
            ?? throw new InvalidOperationException("内嵌 GPU 场景 shader 缺失。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
