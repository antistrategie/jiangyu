using AssetRipper.Primitives;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Il2Cpp;

/// <summary>
/// On-disk cache for the IL2CPP metadata supplement. Keyed off the cache
/// directory the rest of the asset pipeline already uses; invalidates when
/// either of the two source files (<c>GameAssembly.dll</c>,
/// <c>global-metadata.dat</c>) has a newer mtime than the cache.
/// </summary>
public static class Il2CppMetadataCache
{
    private const string CacheFileName = "il2cpp-metadata.json";

    public static string GetCachePath(string cacheRoot)
        => Path.Combine(cacheRoot, CacheFileName);

    /// <summary>
    /// Loads the cached supplement when it exists and is fresh against the
    /// game files. Returns null when the cache is missing, malformed, or
    /// stale — callers should rebuild via <see cref="BuildAndPersist"/>.
    /// </summary>
    public static Il2CppMetadataSupplement? LoadIfFresh(
        string cacheRoot,
        string gameAssemblyPath,
        string metadataPath)
    {
        var cachePath = GetCachePath(cacheRoot);
        if (!File.Exists(cachePath)) return null;

        Il2CppMetadataSupplement? supplement;
        try
        {
            supplement = Il2CppMetadataSupplement.FromJson(File.ReadAllText(cachePath));
        }
        catch
        {
            return null;
        }
        if (supplement is null || supplement.SchemaVersion != Il2CppMetadataSupplement.CurrentSchemaVersion)
            return null;

        if (!File.Exists(gameAssemblyPath) || !File.Exists(metadataPath)) return null;
        var gameMtime = new FileInfo(gameAssemblyPath).LastWriteTimeUtc;
        var metaMtime = new FileInfo(metadataPath).LastWriteTimeUtc;
        if (gameMtime > supplement.GameAssemblyMtime || metaMtime > supplement.MetadataMtime)
            return null;

        return supplement;
    }

    /// <summary>
    /// Loads the cached supplement, returning it even if stale — for read
    /// paths that don't need fresh data (e.g. `templatesQuery` over a project
    /// the user just opened). Returns null when the cache is missing or
    /// malformed.
    /// </summary>
    public static Il2CppMetadataSupplement? LoadIfPresent(string cacheRoot)
    {
        var cachePath = GetCachePath(cacheRoot);
        if (!File.Exists(cachePath)) return null;
        try
        {
            var supplement = Il2CppMetadataSupplement.FromJson(File.ReadAllText(cachePath));
            return supplement?.SchemaVersion == Il2CppMetadataSupplement.CurrentSchemaVersion ? supplement : null;
        }
        catch
        {
            return null;
        }
    }

    public static Il2CppMetadataSupplement BuildAndPersist(
        string cacheRoot,
        string gameAssemblyPath,
        string metadataPath,
        UnityVersion unityVersion,
        ILogSink log)
    {
        var supplement = Il2CppMetadataExtractor.Extract(gameAssemblyPath, metadataPath, unityVersion, log);
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(GetCachePath(cacheRoot), supplement.ToJson());
        return supplement;
    }
}
