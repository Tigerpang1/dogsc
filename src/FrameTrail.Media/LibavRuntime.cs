using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace FrameTrail.Media;

/// <summary>Loads the separately shipped LGPL decoder from the application directory.</summary>
internal static class LibavRuntime
{
    private static readonly Lazy<bool> Available = new(Initialize);
    // AutoGen keeps function delegates for the process lifetime, so their modules
    // must also stay loaded. Preloading in dependency order avoids PATH searches.
    private static readonly List<nint> Modules = [];
    internal static string? UnavailableReason { get; private set; }
    internal static bool TryInitialize() => Available.Value;

    private static bool Initialize()
    {
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "tools", "libav");
            string[] names = ["avutil-60.dll", "swresample-6.dll", "avcodec-62.dll", "avformat-62.dll", "swscale-9.dll"];
            if (names.Any(name => !File.Exists(Path.Combine(root, name))))
            {
                UnavailableReason = "The bundled libav runtime is unavailable.";
                return false;
            }
            foreach (var name in names) Modules.Add(NativeLibrary.Load(Path.Combine(root, name)));
            DynamicallyLoadedBindings.LibrariesPath = root;
            DynamicallyLoadedBindings.Initialize();
            if ((ffmpeg.avcodec_version() >> 16) != 62 || (ffmpeg.avformat_version() >> 16) != 62 ||
                (ffmpeg.avutil_version() >> 16) != 60 || (ffmpeg.swscale_version() >> 16) != 9)
                throw new NotSupportedException("The libav decoder ABI does not match the application bindings.");
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or
            EntryPointNotFoundException or NotSupportedException or TypeInitializationException)
        {
            UnavailableReason = error.Message;
            return false;
        }
    }
}
