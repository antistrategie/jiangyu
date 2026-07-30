using System.Security.Cryptography;
using System.Text;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Deterministic content fingerprints over files and directories, used by incremental
/// compilation to decide whether a phase's inputs changed since the last build. Hashes each
/// file's relative path plus its bytes, walked in sorted order, so the result is stable
/// across runs and machines and changes when any input is added, removed, or edited.
/// </summary>
public static class FileFingerprint
{
    /// <summary>SHA-256 over every file under <paramref name="dir"/> matching
    /// <paramref name="searchPattern"/> (relative path + content), or empty when the directory is
    /// absent. <paramref name="excludeRelative"/>, when given, is tested against each file's
    /// forward-slashed path relative to <paramref name="dir"/>; matches are skipped (and their
    /// bytes never read), so build-generated subtrees can be left out of an otherwise source-only
    /// fingerprint.</summary>
    public static string OfDirectory(string dir, Func<string, bool>? excludeRelative = null, string searchPattern = "*")
    {
        if (!Directory.Exists(dir))
            return string.Empty;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(dir, searchPattern, SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(dir, path).Replace('\\', '/'), Full: path))
            .Where(entry => excludeRelative is null || !excludeRelative(entry.Relative))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal);

        foreach (var (relative, full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            AppendFile(hash, full);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>SHA-256 over every file's (relative path, length, last write time) under
    /// <paramref name="dir"/>, or empty when the directory is absent. No content is read, so this
    /// is cheap enough for trees far too large to hash (a game install) while still moving
    /// whenever any file there is added, removed, resized, or rewritten.</summary>
    public static string OfDirectoryMetadata(string dir)
    {
        if (!Directory.Exists(dir))
            return string.Empty;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Select(info => (Relative: Path.GetRelativePath(dir, info.FullName).Replace('\\', '/'), info.Length, info.LastWriteTimeUtc.Ticks))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal);

        foreach (var (relative, length, ticks) in files)
            hash.AppendData(Encoding.UTF8.GetBytes($"{relative}\n{length}\n{ticks}\n"));

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>SHA-256 over a single file's bytes, or empty when absent.</summary>
    public static string OfFile(string file)
    {
        if (!File.Exists(file))
            return string.Empty;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, file);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Stream the file through the hash in fixed chunks rather than loading it whole, so a
    // large mod asset (a big texture or imported mesh) doesn't spike memory by its full size.
    private static void AppendFile(IncrementalHash hash, string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    /// <summary>Fold several fingerprints into one, order-sensitively.</summary>
    public static string Combine(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
            hash.AppendData(Encoding.UTF8.GetBytes(part + "\n"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
