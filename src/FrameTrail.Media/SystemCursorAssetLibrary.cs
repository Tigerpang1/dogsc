using System.Buffers.Binary;

namespace FrameTrail.Media;

internal sealed record SystemCursorRaster(string Id, int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra);

/// <summary>
/// Reads the Windows installation's standard aero arrow/link cursor files once and keeps immutable CPU rasters.
/// Cursor roles and file names follow dogsc/windows/src/renderer/editor/cursors.js at 3bdcf00d4eb12a107c093e98aac7a894aedd2843;
/// cursor pixels are read from the local Windows installation, and I-beam fallback geometry is drawn here.
/// </summary>
internal static class SystemCursorAssetLibrary
{
    private static readonly Lazy<IReadOnlyDictionary<string, SystemCursorRaster>> Assets = new(LoadAll);

    internal static IReadOnlyDictionary<string, SystemCursorRaster> All => Assets.Value;
    internal static SystemCursorRaster Resolve(string? id)
        => Assets.Value.TryGetValue(Normalize(id), out var raster) ? raster : Assets.Value["system.arrow"];

    internal static string Normalize(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "system.hand" or "hand" or "pointinghand" => "system.hand",
        "system.ibeam" or "ibeam" or "text" => "system.ibeam",
        _ => "system.arrow"
    };

    private static IReadOnlyDictionary<string, SystemCursorRaster> LoadAll()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Cursors");
        var arrow = TryLoad(Path.Combine(directory, "aero_arrow_xl.cur"), "system.arrow") ?? MakeArrow();
        var hand = TryLoad(Path.Combine(directory, "aero_link_xl.cur"), "system.hand") ?? MakeHand();
        var beam = MakeIBeam(Math.Max(32, (int)Math.Round(arrow.Height * .9)));
        return new Dictionary<string, SystemCursorRaster>(StringComparer.Ordinal)
        {
            ["system.arrow"] = arrow,
            ["system.hand"] = hand,
            ["system.ibeam"] = beam
        };
    }

    private static SystemCursorRaster? TryLoad(string file, string id)
    {
        try
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 6 || U16(data, 2) != 2) return null;
            var count = U16(data, 4);
            (int Width, int Height, int HotspotX, int HotspotY, int PixelOffset)? best = null;
            for (var i = 0; i < count; i++)
            {
                var entry = 6 + i * 16;
                if (entry + 16 > data.Length) break;
                var offset = checked((int)U32(data, entry + 12));
                if (offset < 0 || offset + 40 > data.Length || U32(data, offset) != 40 || U16(data, offset + 14) != 32) continue;
                var width = I32(data, offset + 4);
                var doubledHeight = I32(data, offset + 8);
                var height = doubledHeight / 2;
                if (width <= 0 || height <= 0 || width > 1024 || height > 1024 || offset + 40L + width * (long)height * 4 > data.Length) continue;
                if (best is null || width * height > best.Value.Width * best.Value.Height)
                    best = (width, height, U16(data, entry + 4), U16(data, entry + 6), offset + 40);
            }
            if (best is not { } selected) return null;
            var sourcePixels = new byte[selected.Width * selected.Height * 4];
            var left = selected.Width; var top = selected.Height; var right = -1; var bottom = -1;
            for (var y = 0; y < selected.Height; y++)
            for (var x = 0; x < selected.Width; x++)
            {
                var src = selected.PixelOffset + ((selected.Height - 1 - y) * selected.Width + x) * 4;
                var dst = (y * selected.Width + x) * 4;
                var a = data[src + 3];
                sourcePixels[dst] = data[src]; sourcePixels[dst + 1] = data[src + 1];
                sourcePixels[dst + 2] = data[src + 2]; sourcePixels[dst + 3] = a;
                if (a <= 8) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            if (right < left || bottom < top) return null;
            var widthOut = right - left + 1; var heightOut = bottom - top + 1;
            var cropped = new byte[widthOut * heightOut * 4];
            for (var y = 0; y < heightOut; y++)
                Buffer.BlockCopy(sourcePixels, ((top + y) * selected.Width + left) * 4, cropped, y * widthOut * 4, widthOut * 4);
            return new SystemCursorRaster(id, widthOut, heightOut, Math.Clamp(selected.HotspotX - left, 0, widthOut - 1),
                Math.Clamp(selected.HotspotY - top, 0, heightOut - 1), cropped);
        }
        catch { return null; }
    }

    private static SystemCursorRaster MakeArrow()
    {
        const int width = 80, height = 128;
        Point[] points = [new(5, 4), new(5, 102), new(28, 79), new(43, 119), new(57, 113), new(42, 74), new(73, 72)];
        var pixels = Rasterize(width, height, (x, y) => InsidePolygon(points, x, y), (x, y) => DistanceToPolygon(points, x, y), 4);
        return new("system.arrow", width, height, 5, 4, pixels);
    }

    private static SystemCursorRaster MakeIBeam(int height)
    {
        var width = Math.Max(18, (int)Math.Round(height * .44));
        var x = width / 2d;
        var halfStroke = height * .045;
        double Distance(double px, double py)
        {
            var inset = height * .08;
            return Math.Min(DistanceSegment(px, py, x, inset, x, height - inset),
                Math.Min(Math.Min(DistanceSegment(px, py, width * .2, inset, width * .42, inset), DistanceSegment(px, py, width * .58, inset, width * .8, inset)),
                    Math.Min(DistanceSegment(px, py, width * .2, height - inset, width * .42, height - inset), DistanceSegment(px, py, width * .58, height - inset, width * .8, height - inset))));
        }
        return new("system.ibeam", width, height, width / 2, height / 2,
            Rasterize(width, height, (px, py) => Distance(px, py) <= halfStroke, (px, py) => Math.Abs(Distance(px, py) - halfStroke), 4));
    }

    private static SystemCursorRaster MakeHand()
    {
        const int width = 96, height = 128;
        Point[][] shapes =
        [
            [new(28, 55), new(72, 55), new(76, 96), new(69, 119), new(38, 119), new(26, 98)],
            [new(26, 58), new(25, 24), new(29, 15), new(37, 14), new(42, 22), new(42, 58)],
            [new(42, 58), new(41, 12), new(46, 4), new(54, 4), new(59, 12), new(58, 58)],
            [new(58, 58), new(58, 20), new(63, 14), new(70, 16), new(74, 24), new(73, 61)],
            [new(72, 63), new(74, 38), new(80, 32), new(87, 37), new(89, 67)],
            [new(28, 63), new(17, 55), new(9, 58), new(7, 67), new(13, 77), new(37, 96), new(45, 87)]
        ];
        bool Inside(double x, double y) => shapes.Any(shape => InsidePolygon(shape, x, y));
        double EdgeDistance(double x, double y) => shapes.Min(shape => DistanceToPolygon(shape, x, y));
        return new("system.hand", width, height, 33, 15, Rasterize(width, height, Inside, EdgeDistance, 4));
    }

    private static byte[] Rasterize(int width, int height, Func<double, double, bool> inside, Func<double, double, double> edgeDistance, int samples)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var fillCount = 0; var strokeCount = 0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var px = x + (sx + .5) / samples; var py = y + (sy + .5) / samples;
                if (inside(px, py)) fillCount++;
                if (edgeDistance(px, py) <= 2.6) strokeCount++;
            }
            var i = (y * width + x) * 4;
            var a = (byte)Math.Clamp((int)Math.Round(255d * Math.Max(fillCount, strokeCount) / (samples * samples)), 0, 255);
            var fill = fillCount >= strokeCount * .55;
            data[i] = fill ? (byte)255 : (byte)22; data[i + 1] = fill ? (byte)255 : (byte)24;
            data[i + 2] = fill ? (byte)255 : (byte)28; data[i + 3] = a;
        }
        return data;
    }

    private readonly record struct Point(double X, double Y);
    private static bool InsidePolygon(Point[] points, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            if (((points[i].Y > y) != (points[j].Y > y)) && x < (points[j].X - points[i].X) * (y - points[i].Y) / (points[j].Y - points[i].Y) + points[i].X) inside = !inside;
        return inside;
    }
    private static double DistanceToPolygon(Point[] points, double x, double y)
    {
        var min = double.PositiveInfinity;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            min = Math.Min(min, DistanceSegment(x, y, a.X, a.Y, b.X, b.Y));
        }
        return min;
    }
    private static double DistanceSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax; var dy = by - ay; var length = dx * dx + dy * dy;
        var t = length <= 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / length, 0, 1);
        var ex = px - (ax + t * dx); var ey = py - (ay + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static int I32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
}
