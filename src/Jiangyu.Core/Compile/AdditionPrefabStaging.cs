using Jiangyu.Core.Abstractions;
using Jiangyu.Core.IO;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Staging logic for addition prefab bundles. Walks one or more source
/// directories, copies <c>*.bundle</c> files into the compile output, and
/// records their logical names on the compiled manifest's
/// <see cref="ModManifest.AdditionPrefabs"/> list.
///
/// Two source dirs feed this today: pre-built bundles dropped by the modder
/// in <c>assets/additions/prefabs/</c> (the escape hatch), and freshly-built
/// bundles produced by Unity batchmode against the modder's <c>unity/</c>
/// project (output to <c>.jiangyu/unity_build/</c>). Both flow through the
/// same staging step. Later-listed sources override earlier ones on name
/// collision so a Unity-built bundle takes precedence over a stale
/// hand-shipped one of the same name.
///
/// Convention: each bundle's filename stem equals the Unity Object.name of
/// the GameObject inside, which is what KDL <c>asset=</c> references resolve
/// against at runtime via <c>ModAssetResolver</c>'s GameObject dispatch.
///
/// <para>Staging runs hollow-AnimationClip restoration over every bundle it
/// copies. When a restored-cache directory is supplied, each bundle's restored
/// output is kept there keyed on the source bundle's content hash plus the
/// game and Jiangyu versions, so an unchanged bundle costs one hash and one
/// file copy on later compiles instead of a full restoration scan.</para>
/// </summary>
internal static class AdditionPrefabStaging
{
    public static void Stage(
        IReadOnlyList<string> sourceDirs,
        string outputDir,
        ModManifest compiledManifest,
        string gameDataPath,
        ILogSink log,
        string? restoredCacheDir = null,
        string? cacheKeyContext = null)
    {
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sourceDir in sourceDirs)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                continue;

            foreach (var source in Directory.EnumerateFiles(sourceDir, "*.bundle", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(source);
                if (string.IsNullOrWhiteSpace(stem))
                {
                    log.Warning($"  Addition prefab bundle '{source}' has no usable filename stem; skipping.");
                    continue;
                }

                staged[stem] = source;
            }
        }

        if (staged.Count == 0)
            return;

        Directory.CreateDirectory(outputDir);
        var gameClips = new AnimationClipRestoration.GameClipIndex(gameDataPath);
        // Restoration reads game clip data, so the cache key must move when the game's
        // data changes even on a hotfix that keeps the engine version. Metadata (names,
        // sizes, mtimes) is enough of a signal and cheap enough to compute per compile.
        var gameDataFingerprint = restoredCacheDir is null
            ? string.Empty
            : FileFingerprint.OfDirectoryMetadata(gameDataPath);
        var names = new List<string>(staged.Count);
        var restoredFromCache = 0;
        foreach (var (stem, source) in staged.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var dest = Path.Combine(outputDir, stem + ".bundle");
            // Clip restoration scans and rewrites the whole bundle, which across a mod
            // with dozens of rigs dominates the cost of an otherwise-cached compile. The
            // restored output is a pure function of the source bundle bytes, the game
            // data the clips come from, and the restoration code, so a cached copy keyed
            // on those is exact.
            var cacheKey = restoredCacheDir is null
                ? null
                : FileFingerprint.Combine(FileFingerprint.OfFile(source), cacheKeyContext ?? string.Empty, gameDataFingerprint, JiangyuVersion.Current);
            if (cacheKey is not null && TryCopyFromRestoredCache(restoredCacheDir!, stem, cacheKey, dest))
            {
                restoredFromCache++;
            }
            else
            {
                File.Copy(source, dest, overwrite: true);
                AnimationClipRestoration.RestoreStagedBundle(dest, gameClips, log);
                if (cacheKey is not null)
                    StoreInRestoredCache(restoredCacheDir!, stem, cacheKey, dest);
            }
            names.Add(stem);
            log.Info($"  Staged addition prefab bundle: {stem}.bundle");
        }

        if (restoredFromCache > 0)
            log.Info($"  Incremental: {restoredFromCache} of {names.Count} staged bundle(s) reused from the restored cache.");
        if (restoredCacheDir is not null)
            PruneRestoredCache(restoredCacheDir, staged);

        compiledManifest.AdditionPrefabs = names;
    }

    private static bool TryCopyFromRestoredCache(string cacheDir, string stem, string cacheKey, string dest)
    {
        var cachedBundle = Path.Combine(cacheDir, stem + ".bundle");
        var keyFile = cachedBundle + ".key";
        if (!File.Exists(cachedBundle) || !File.Exists(keyFile))
            return false;
        if (!string.Equals(File.ReadAllText(keyFile), cacheKey, StringComparison.Ordinal))
            return false;
        File.Copy(cachedBundle, dest, overwrite: true);
        return true;
    }

    private static void StoreInRestoredCache(string cacheDir, string stem, string cacheKey, string restoredBundle)
    {
        Directory.CreateDirectory(cacheDir);
        var cachedBundle = Path.Combine(cacheDir, stem + ".bundle");
        File.Copy(restoredBundle, cachedBundle, overwrite: true);
        File.WriteAllText(cachedBundle + ".key", cacheKey);
    }

    // A bundle the mod no longer ships has no reason to keep occupying the cache.
    private static void PruneRestoredCache(string cacheDir, Dictionary<string, string> staged)
    {
        if (!Directory.Exists(cacheDir))
            return;
        foreach (var file in Directory.GetFiles(cacheDir))
        {
            var name = Path.GetFileName(file);
            var stem = name.EndsWith(".bundle.key", StringComparison.Ordinal)
                ? name[..^".bundle.key".Length]
                : name.EndsWith(".bundle", StringComparison.Ordinal)
                    ? name[..^".bundle".Length]
                    : null;
            if (stem is null || !staged.ContainsKey(stem))
                ResilientFs.DeleteFile(file);
        }
    }

    /// <summary>
    /// Returns true when the compile pipeline should invoke Unity batchmode
    /// to build addition prefab bundles from the modder's <c>unity/</c>
    /// project. False when the project isn't scaffolded or has no prefabs
    /// to build.
    /// </summary>
    public static bool ShouldInvokeUnityForPrefabs(string projectDir)
    {
        var prefabsDir = Path.Combine(projectDir, "unity", "Assets", "Prefabs");
        if (!Directory.Exists(prefabsDir))
            return false;
        return Directory.EnumerateFiles(prefabsDir, "*.prefab", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// Clears the prefab bundles from the Unity batchmode output staging dir. Runs
    /// when a compile has no prefab work: a modder who deletes their last prefab
    /// must not have the previous compile's bundles sitting here for
    /// <see cref="Stage"/> to re-ship. Only prefab-shaped files (<c>*.bundle</c>
    /// and their Unity <c>*.bundle.manifest</c> siblings) are deleted: the
    /// extensionless replacement bundles and their manifests share this dir and
    /// are the incremental state that lets an unchanged replacement bundle skip
    /// rebuilding, whatever the prefab situation. When prefab work exists nothing
    /// is cleared at all (<c>BuildBundles</c> prunes individually stale bundles
    /// itself and the surviving files carry Unity's incremental state).
    /// </summary>
    public static void ClearStaleBuildOutput(string unityBuildOutputDir)
    {
        Directory.CreateDirectory(unityBuildOutputDir);
        foreach (var stale in Directory.EnumerateFiles(unityBuildOutputDir))
        {
            var name = Path.GetFileName(stale);
            var stem = name.EndsWith(".manifest", StringComparison.Ordinal)
                ? name[..^".manifest".Length]
                : name;
            if (!stem.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                continue;
            ResilientFs.DeleteFile(stale);
        }
    }
}
