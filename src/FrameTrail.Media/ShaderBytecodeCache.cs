using System.Security.Cryptography;
using System.Text;
using Vortice.D3DCompiler;

namespace FrameTrail.Media;

/// <summary>
/// Small, best-effort persistent cache for D3DCompiler output. Cache files are
/// treated as untrusted input: only bounded payloads with a matching digest are
/// returned to the caller.
/// </summary>
internal static class ShaderBytecodeCache
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FTDXBC1\0");
    private static readonly byte[] DxbcMagic = Encoding.ASCII.GetBytes("DXBC");
    private const int DigestLength = 32;
    private const int HeaderLength = 8 + DigestLength;
    private const int MaximumPayloadLength = 16 * 1024 * 1024;

    internal static byte[] GetOrCompile(
        string shaderSource,
        string entryPoint,
        string profile,
        Func<byte[]> compile,
        string? cacheRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(shaderSource);
        ArgumentNullException.ThrowIfNull(entryPoint);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(compile);

        var cacheRoot = cacheRootOverride ?? GetDefaultCacheRoot();
        var cachePath = Path.Combine(cacheRoot, MakeKey(shaderSource, entryPoint, profile) + ".bin");
        if (TryRead(cachePath, out var cached))
            return cached;

        var bytecode = compile();
        if (bytecode.Length == 0 || bytecode.Length > MaximumPayloadLength)
            return bytecode;

        TryWrite(cachePath, bytecode);
        return bytecode;
    }

    private static string GetDefaultCacheRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(Path.GetTempPath(), "FrameTrail", "cache", "shaders");
        return Path.Combine(localAppData, "FrameTrail", "cache", "shaders");
    }

    private static string MakeKey(string source, string entryPoint, string profile)
    {
        var compilerVersion = typeof(Compiler).Assembly.GetName().Version?.ToString() ?? "unknown";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPart(hash, source);
        AppendPart(hash, entryPoint);
        AppendPart(hash, profile);
        AppendPart(hash, compilerVersion);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool TryRead(string path, out byte[] bytecode)
    {
        bytecode = Array.Empty<byte>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < HeaderLength + 1 || info.Length > HeaderLength + MaximumPayloadLength)
                return false;

            var contents = File.ReadAllBytes(path);
            if (contents.Length < HeaderLength + 1 || contents.Length > HeaderLength + MaximumPayloadLength)
                return false;
            if (!contents.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                return false;

            var payload = contents.AsSpan(HeaderLength);
            if (payload.Length < DxbcMagic.Length || !payload[..DxbcMagic.Length].SequenceEqual(DxbcMagic))
                return false;
            Span<byte> digest = stackalloc byte[DigestLength];
            SHA256.HashData(payload, digest);
            if (!CryptographicOperations.FixedTimeEquals(contents.AsSpan(Magic.Length, DigestLength), digest))
                return false;

            bytecode = payload.ToArray();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void TryWrite(string path, byte[] bytecode)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var contents = new byte[HeaderLength + bytecode.Length];
            Magic.CopyTo(contents, 0);
            SHA256.HashData(bytecode, contents.AsSpan(Magic.Length, DigestLength));
            bytecode.CopyTo(contents, HeaderLength);
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
        }
        catch (IOException)
        {
            // A cache failure must never prevent an otherwise successful compile.
        }
        catch (UnauthorizedAccessException)
        {
            // The cache is optional; keep the in-memory bytecode.
        }
        catch (NotSupportedException)
        {
            // The cache is optional; keep the in-memory bytecode.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
