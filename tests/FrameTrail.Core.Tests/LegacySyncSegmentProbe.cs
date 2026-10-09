using System.Text.Json;
using FrameTrail;

namespace FrameTrail.Core.Tests;

public static class LegacySyncSegmentProbe
{
    public static void Run()
    {
        var legacy = JsonSerializer.Deserialize<CameraSyncSegment>("""{"sourceStart":1.25,"timelineStart":2.5,"duration":3}""");
        if (legacy is null || legacy.SourceRate != 1)
            throw new InvalidOperationException("legacy sync segment without sourceRate must load at 1x");
        var current = JsonSerializer.Serialize(legacy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (!current.Contains("\"sourceRate\":1", StringComparison.Ordinal))
            throw new InvalidOperationException("new sync segment JSON must persist the rate using camelCase");
        var explicitRate = JsonSerializer.Deserialize<CameraSyncSegment>("""{"sourceStart":1,"timelineStart":2,"duration":3,"sourceRate":1.25}""");
        if (explicitRate?.SourceRate != 1.25)
            throw new InvalidOperationException("sync segment JSON must retain an explicit source rate");
        Console.WriteLine("通过：旧 CameraSyncSegment JSON 默认 1x，新格式按 camelCase 持久化 SourceRate");
    }
}
