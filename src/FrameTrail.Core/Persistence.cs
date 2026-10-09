using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameTrail;

/// <summary>Persists FrameTrail's own versioned project format with a recoverable previous copy.</summary>
public static class FrameTrailProjectStore
{
    public const string BackupSuffix = ".bak";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static FrameTrailProject Load(string projectFile)
    {
        var path = Path.GetFullPath(projectFile); using var stream = File.OpenRead(path);
        var project = JsonSerializer.Deserialize<FrameTrailProject>(stream, JsonOptions) ?? throw new InvalidDataException("项目文件为空。");
        Validate(project, Path.GetDirectoryName(path)!); return project;
    }
    public static void Save(string projectFile, FrameTrailProject project)
    {
        ArgumentNullException.ThrowIfNull(project); var path = Path.GetFullPath(projectFile); var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory); Validate(project, directory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, project, JsonOptions); stream.Flush(true); }
            if (File.Exists(path)) File.Copy(path, path + BackupSuffix, true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static FrameTrailProject RestoreBackup(string projectFile)
    {
        var path = Path.GetFullPath(projectFile); var backup = path + BackupSuffix;
        if (!File.Exists(backup)) throw new FileNotFoundException("找不到项目备份。", backup);
        var restored = Load(backup); var temp = path + ".restore-" + Guid.NewGuid().ToString("N");
        try { File.Copy(backup, temp); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return restored;
    }
    public static string ResolveMediaPath(string projectFile, FrameTrailProject project)
    {
        ValidateRelativeMediaPath(project.MediaPath);
        var root = Path.GetFullPath(Path.GetDirectoryName(projectFile)!);
        var result = Path.GetFullPath(Path.Combine(root, project.MediaPath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && !string.Equals(result, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("素材路径越出项目目录。");
        return result;
    }
    private static void Validate(FrameTrailProject project, string projectDirectory)
    {
        if (project.Format != "FrameTrailProject" || project.FormatVersion != 1 || project.ProjectId == Guid.Empty) throw new InvalidDataException("不支持此项目格式或版本；FrameTrail 项目与原 dogscproject 格式不同。");
        project.Timeline.Validate(); _ = ResolveMediaPath(Path.Combine(projectDirectory, "project.frametrail"), project);
        if (project.Media.Duration < 0 || project.Media.Width < 0 || project.Media.Height < 0 || !double.IsFinite(project.Media.Duration + project.Media.Width + project.Media.Height)) throw new InvalidDataException("媒体元数据无效。");
        if (project.Settings.CanvasWidth < 2 || project.Settings.CanvasHeight < 2 || !double.IsFinite(project.Settings.Padding) || project.Settings.Padding is < 0 or > .5 || !double.IsFinite(project.Settings.CornerRadius) || project.Settings.CornerRadius is < 0 or > .5 || !double.IsFinite(project.Settings.Shadow) || project.Settings.Shadow is < 0 or > 1) throw new InvalidDataException("画布设置无效。");
        if (project.PointerEvents.Any(x => !double.IsFinite(x.Time) || x.Time < 0 || !x.Location.IsFinite) || project.ZoomClips.Any(x => !double.IsFinite(x.Start + x.End + x.Scale + x.EnterDuration + x.ExitDuration) || x.Start < 0 || x.End <= x.Start || x.Scale < 1 || x.EnterDuration is < 0 or > 5 || x.ExitDuration is < 0 or > 5 || !x.Focus.IsFinite)) throw new InvalidDataException("指针或缩放轨道数据无效。");
    }
    private static void ValidateRelativeMediaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\')) throw new InvalidDataException("素材必须使用项目目录内的相对路径。");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(x => x is ".." or "")) throw new InvalidDataException("素材相对路径无效。");
    }
}

/// <summary>Bounded, snapshot-based undo/redo for immutable or cloneable project states.</summary>
public sealed class UndoRedoHistory<T> where T : class
{
    private readonly Func<T, T> clone;
    private readonly int capacity;
    private readonly List<T> undo = [];
    private readonly List<T> redo = [];
    public T Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public UndoRedoHistory(T initial, Func<T, T> clone, int capacity = 100)
    { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); Current = clone(initial); this.clone = clone; this.capacity = capacity; }
    public void Commit(T next)
    { undo.Add(clone(Current)); if (undo.Count > capacity) undo.RemoveAt(0); Current = clone(next); redo.Clear(); }
    public T Undo()
    { if (!CanUndo) throw new InvalidOperationException("没有可撤销的操作。"); redo.Add(clone(Current)); Current = undo[^1]; undo.RemoveAt(undo.Count - 1); return clone(Current); }
    public T Redo()
    { if (!CanRedo) throw new InvalidOperationException("没有可重做的操作。"); undo.Add(clone(Current)); Current = redo[^1]; redo.RemoveAt(redo.Count - 1); return clone(Current); }
}
