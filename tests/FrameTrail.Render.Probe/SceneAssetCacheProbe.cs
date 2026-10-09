using FrameTrail;
using System.IO;
using FrameTrail.Media;

internal static class SceneAssetCacheProbe
{
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "validation", "scene-cache-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        var projectFile = Path.Combine(root, "cache.frametrail");
        var relativePaths = new[] { "media/a.png", "media/b.png", "media/c.png", "media/oversize.png" };
        var colors = new[] { "red", "lime", "yellow", "magenta" };
        var dimensions = new[] { 32, 32, 32, 64 };
        for (var i = 0; i < relativePaths.Length; i++)
        {
            var path = FrameTrailProjectStore.ResolveAssetPath(projectFile, relativePaths[i]);
            await MediaTools.RunAsync(MediaTools.Ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", $"color=c={colors[i]}:size={dimensions[i]}x{dimensions[i]}", "-frames:v", "1", "-threads", "1", "-y", path]);
        }

        const long imageBytes = 32L * 32 * 4;
        const long budget = imageBytes * 2;
        byte[]? bPixels = null;
        using (var loader = new SceneAssetLoader(projectFile, 320, 180, budget))
        {
            _ = await Load(loader, relativePaths[0]);
            bPixels = (await Load(loader, relativePaths[1])).Bgra.ToArray();
            _ = await Load(loader, relativePaths[0]); // A becomes more recent than B.
            if (loader.ImageCacheHitCount != 1 || loader.CachedImageCount != 2)
                throw new InvalidOperationException("A repeated image load did not hit the cache or the two-entry budget is wrong.");

            _ = await Load(loader, relativePaths[2]); // C must evict B, leaving A+C.
            if (loader.ImageCacheEvictionCount != 1 || loader.CachedImageCount != 2 || loader.CachedImageBytes != budget)
                throw new InvalidOperationException("A/B/A/C did not evict exactly the least-recently-used B image.");
            _ = await Load(loader, "MEDIA/A.PNG"); // Windows path identity is case-insensitive.
            if (loader.ImageCacheHitCount != 2 || loader.ImageCacheEvictionCount != 1)
                throw new InvalidOperationException("Image cache path identity is not Windows case-insensitive.");

            var bReloaded = await Load(loader, relativePaths[1]);
            if (!bPixels.AsSpan().SequenceEqual(bReloaded.Bgra))
                throw new InvalidOperationException("Image B changed after LRU eviction and re-decode.");

            var overBudget = await Load(loader, relativePaths[3]);
            if (overBudget.Width != 64 || overBudget.Height != 64 || overBudget.Bgra.Length != 64 * 64 * 4)
                throw new InvalidOperationException("An image larger than the cache budget was not returned for the current frame.");
            if (loader.CachedImageBytes > budget || loader.CachedImageCount != 2)
                throw new InvalidOperationException("An oversized image was retained or the image cache exceeded its budget.");
        }

        const long textBudget = 110_000;
        using (var loader = new SceneAssetLoader(projectFile, 320, 180, textBudget))
        {
            var titleA = await LoadText(loader, "Demo", "C");
            var titleLower = await LoadText(loader, "demo", "C");
            if (loader.ImageCacheMissCount != 2 || loader.ImageCacheEvictionCount != 1
                || loader.CachedImageCount != 1 || loader.CachedImageBytes > textBudget
                || titleA.Bgra.AsSpan().SequenceEqual(titleLower.Bgra))
                throw new InvalidOperationException("Case-distinct title text did not use separate cache entries under the shared byte budget.");

            var delimiterA = await LoadText(loader, "A|B", "C");
            var delimiterB = await LoadText(loader, "A", "B|C");
            if (loader.ImageCacheMissCount != 4 || loader.ImageCacheEvictionCount != 3
                || delimiterA.Bgra.AsSpan().SequenceEqual(delimiterB.Bgra)
                || loader.CachedImageBytes > textBudget)
                throw new InvalidOperationException("Text cache keys collided when title/address contained a delimiter, or shared-budget eviction failed.");
        }
        Console.WriteLine($"通过：图片LRU命中、A/B/A/C逐出、超预算直用不缓存、文本大小写和结构化身份均已验证；图片上限 {budget} 字节，文本上限 {textBudget} 字节。");

        async Task<FrameLayerInput> Load(SceneAssetLoader loader, string path)
        {
            var layers = await loader.LoadAsync(Scene(path, null, null));
            return layers.Single(x => x.Width >= 32 && x.Height >= 32 && x.Layer == 0);
        }

        async Task<FrameLayerInput> LoadText(SceneAssetLoader loader, string title, string address)
        {
            var layers = await loader.LoadAsync(Scene(null, title, address));
            return layers.Single(x => x.Layer == int.MinValue);
        }

        static FrameScene Scene(string? path, string? title, string? address) => new(
            0, 0, new(.5, .5), new(1, 1), 1, null, 0, "#202020", 0, 0, 320, 180, 0,
            Canvas: title is null && address is null ? null : new CanvasSettings { Frame = ScreenFrameKind.WindowLight, Title = title ?? "", Address = address ?? "" },
            Overlays: path is null ? null : [new FrameOverlayScene(Guid.NewGuid(), path, new(.25, .25, .5, .5), 0, 1, 0, 0, 0)]);
    }
}
