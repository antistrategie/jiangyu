using AssetRipper.Assets;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Surgically copies a vanilla game prefab plus its transitive dependencies
/// out of the game data into a modder-facing <c>unity/Assets/Imported/X/</c>
/// directory. The first call for a game version runs AssetRipper's full
/// project export to a cache directory; subsequent calls reuse the cache.
///
/// <para>Two entry points: <see cref="ImportPrefabAsUnityAssets(string, string, string?, long)"/>
/// loads its own <see cref="GameData"/>; the <see cref="GameData"/>-typed
/// overload reuses a caller-cached instance to skip the multi-second
/// <c>GameStructure.Load</c> + <c>RunProcessors</c> cold start (Studio uses
/// this overload).</para>
/// </summary>
public sealed class PrefabImportService(string gameDataPath, IProgressSink progress, ILogSink log)
{
    private readonly string _gameDataPath = gameDataPath;
    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    public void ImportPrefabAsUnityAssets(string assetName, string destDir, string? collection, long pathId)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            throw new ArgumentException("assetName is required.", nameof(assetName));
        if (string.IsNullOrWhiteSpace(destDir))
            throw new ArgumentException("destDir is required.", nameof(destDir));

        var exportSettings = new AssetRipper.Export.Configuration.FullConfiguration();
        exportSettings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;

        using var session = new GameDataSession(_gameDataPath, _progress, includeSpriteProcessor: true);
        if (!session.HasAnyAssetCollections)
            throw new InvalidOperationException("No asset collections found in game data.");

        ImportPrefabSubsetFromGameData(session.GameData, exportSettings, assetName, destDir, collection, pathId);
    }

    /// <summary>
    /// Overload that reuses an already-loaded <see cref="GameData"/>. The
    /// passed game data must have been loaded with
    /// <c>ScriptContentLevel.Level2</c> import settings and run through
    /// processors; otherwise dependency walk and prefab resolution may
    /// misbehave.
    /// </summary>
    public void ImportPrefabAsUnityAssets(GameData gameData, string assetName, string destDir, string? collection, long pathId)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            throw new ArgumentException("assetName is required.", nameof(assetName));
        if (string.IsNullOrWhiteSpace(destDir))
            throw new ArgumentException("destDir is required.", nameof(destDir));

        var settings = new AssetRipper.Export.Configuration.FullConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);
        try
        {
            ImportPrefabSubsetFromGameData(gameData, settings, assetName, destDir, collection, pathId);
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    private void ImportPrefabSubsetFromGameData(
        GameData gameData,
        AssetRipper.Export.Configuration.FullConfiguration settings,
        string assetName,
        string destDir,
        string? collection,
        long pathId)
    {
        IUnityObjectBase? target = null;
        if (!string.IsNullOrEmpty(collection) && pathId >= 0)
        {
            foreach (var col in gameData.GameBundle.FetchAssetCollections())
            {
                if (col.Name != collection) continue;
                target = col.FirstOrDefault(a => a.PathID == pathId);
                break;
            }
        }
        else
        {
            foreach (var col in gameData.GameBundle.FetchAssetCollections())
            {
                foreach (var asset in col)
                {
                    if (asset is IGameObject go && string.Equals(go.Name, assetName, StringComparison.Ordinal))
                    {
                        target = go;
                        break;
                    }
                }
                if (target is not null) break;
            }
        }

        if (target is null)
            throw new InvalidOperationException(
                $"No asset named '{assetName}' found. Confirm with `jiangyu assets search`.");

        // PrefabHierarchyObject is AssetRipper's wrapper for an extracted
        // prefab hierarchy; if the modder named it directly that's fine,
        // but we want to export the wrapped root + the wrapper itself.
        if (target is PrefabHierarchyObject pho)
        {
            _log.Info($"Resolved target to PrefabHierarchyObject ({pho.Name}); using its hierarchy.");
        }
        else if (target is IGameObject go)
        {
            _log.Info($"Resolved target to GameObject ({go.Name}).");
        }
        else
        {
            throw new InvalidOperationException(
                $"'{assetName}' resolved to {target.GetType().Name}, not a prefab/GameObject. Try `assets export model` for non-prefab assets.");
        }

        _progress.SetPhase("Walking dependencies");
        var keep = WalkDependencyClosure(target);
        _log.Info($"Dependency closure: {keep.Count} asset(s).");
        _progress.Finish();

        var componentScripts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var asset in keep)
        {
            if (asset is not IMonoBehaviour mb) continue;
            if (!mb.IsComponentOnGameObject()) continue;
            if (!mb.TryGetScript(out IMonoScript? script)) continue;
            var ns = script.Namespace.String;
            var cls = script.ClassName_R.String;
            var key = string.IsNullOrEmpty(ns) ? cls : $"{ns}.{cls}";
            componentScripts.TryGetValue(key, out var n);
            componentScripts[key] = n + 1;
        }
        if (componentScripts.Count > 0)
        {
            var summary = string.Join(", ", componentScripts.Select(kv => $"{kv.Value}x {kv.Key}"));
            _log.Info($"MonoBehaviour components on imported prefab graph: {summary}");
        }

        // AssetRipper builds collections for every asset in the bundle (so
        // cross-references resolve) and only writes the ones intersecting
        // our dependency closure. Its native layout writes everything under
        // <destDir>/ExportedProject/Assets/. We flatten that one level
        // down so the modder sees unity/Assets/Imported/<name>/{GameObject,
        // Mesh, Material, ...}/ directly.
        _progress.SetPhase("Exporting");
        Directory.CreateDirectory(destDir);
        var handler = new JiangyuExportHandler(settings);
        handler.ExportSubset(gameData, destDir, LocalFileSystem.Instance, keep);

        var exportedAssetsDir = Path.Combine(destDir, "ExportedProject", "Assets");
        if (Directory.Exists(exportedAssetsDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(exportedAssetsDir))
            {
                var entryName = Path.GetFileName(entry);
                var moveTo = Path.Combine(destDir, entryName);
                if (Directory.Exists(moveTo)) Directory.Delete(moveTo, recursive: true);
                else if (File.Exists(moveTo)) File.Delete(moveTo);
                Directory.Move(entry, moveTo);
            }
            Directory.Delete(Path.Combine(destDir, "ExportedProject"), recursive: true);
        }
        _progress.Finish();

        _log.Info($"Imported prefab '{assetName}' into {destDir}");
        _log.Info("Open unity/ in Unity Editor; the prefab appears under Assets/Imported/" + assetName + "/.");
    }

    private static HashSet<IUnityObjectBase> WalkDependencyClosure(IUnityObjectBase root)
    {
        var keep = new HashSet<IUnityObjectBase>();
        var queue = new Queue<IUnityObjectBase>();
        queue.Enqueue(root);
        keep.Add(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (_, pptr) in current.FetchDependencies())
            {
                var dep = current.Collection.TryGetAsset(pptr);
                if (dep is null || !keep.Add(dep))
                    continue;
                queue.Enqueue(dep);
            }
        }

        return keep;
    }
}
