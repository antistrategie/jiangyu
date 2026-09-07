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

    /// <summary>
    /// Locates the game binaries the supplement is extracted from: the
    /// GameAssembly beside the data directory (<c>.so</c> on a native Linux
    /// build, <c>.dll</c> otherwise) and <c>global-metadata.dat</c> inside it.
    /// </summary>
    public static bool TryResolveGamePaths(string gameDataPath, out string gameAssemblyPath, out string metadataPath)
    {
        gameAssemblyPath = "";
        metadataPath = "";
        var gameRoot = Path.GetDirectoryName(gameDataPath);
        if (gameRoot is null) return false;

        gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.so");
        if (!File.Exists(gameAssemblyPath))
            gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.dll");
        metadataPath = Path.Combine(gameDataPath, "il2cpp_data", "Metadata", "global-metadata.dat");
        return File.Exists(gameAssemblyPath) && File.Exists(metadataPath);
    }

    /// <summary>
    /// Leaves a supplement in the cache that is at least as new as the game
    /// binaries: keeps a fresh one, rebuilds a missing or stale one, or every
    /// time when <paramref name="force"/> is set. Returns <c>true</c> when a
    /// fresh supplement is in place afterwards, including a fresh one that a
    /// forced rebuild failed to replace. <paramref name="detectVersion"/> runs
    /// only when a rebuild happens.
    /// </summary>
    public static bool BuildIfStale(
        string cacheRoot,
        string gameDataPath,
        Func<UnityVersion?> detectVersion,
        ILogSink log,
        bool force = false)
    {
        if (!TryResolveGamePaths(gameDataPath, out var gameAssemblyPath, out var metadataPath))
        {
            log.Info("IL2CPP metadata files not found (Mono build?). Skipping the metadata supplement.");
            return false;
        }

        if (!force && LoadIfFresh(cacheRoot, gameAssemblyPath, metadataPath) is not null)
            return true;

        UnityVersion? unityVersion;
        try
        {
            unityVersion = detectVersion();
        }
        catch (Exception ex)
        {
            log.Warning($"Could not detect the Unity version ({ex.Message}). Skipping the metadata supplement.");
            return HasFreshSupplement(cacheRoot, gameAssemblyPath, metadataPath);
        }
        if (unityVersion is null || unityVersion.Value == default)
        {
            log.Warning("Could not detect the Unity version. Skipping the metadata supplement.");
            return HasFreshSupplement(cacheRoot, gameAssemblyPath, metadataPath);
        }

        try
        {
            BuildAndPersist(cacheRoot, gameAssemblyPath, metadataPath, unityVersion.Value, log);
            return true;
        }
        catch (Exception ex)
        {
            // The catalogue keeps working without the supplement, just
            // without attribute-derived hints and interface implementations.
            log.Warning($"IL2CPP metadata extract failed (catalogue will fall back): {ex.Message}");
            return HasFreshSupplement(cacheRoot, gameAssemblyPath, metadataPath);
        }
    }

    private static bool HasFreshSupplement(string cacheRoot, string gameAssemblyPath, string metadataPath)
        => LoadIfFresh(cacheRoot, gameAssemblyPath, metadataPath) is not null;

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
