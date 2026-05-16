using System.Security.Cryptography;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.Processing.Textures;
using AssetRipper.Export.Modules.Audio;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Assets;

public sealed class AssetPipelineService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private const string IndexFileName = "asset-index.json";
    private const string ManifestFileName = "index-manifest.json";
    private const string PreviewDirName = "previews";
    private const int ThumbnailMaxDimension = 256;
    // Bump on any change to AssetEntry shape or BuildAssetIndex behaviour
    // that older index files can't represent. Stale indexes get rebuilt by
    // GetIndexStatus's version check.
    //   v3: baseline (sprite atlas + AudioClip frequency/channels).
    //   v4: per-asset NamedChildren for prototype-source surfaces
    //       (Stem.SoundBank sounds[].name lookup).
    internal const int CurrentFormatVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string GameDataPath { get; } = gameDataPath;
    public string CachePath { get; } = cachePath;

    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    /// <summary>
    /// Checks whether the index is current by comparing the GameAssembly hash.
    /// </summary>
    public bool IsIndexCurrent()
    {
        return GetIndexStatus().IsCurrent;
    }

    public CachedIndexStatus GetIndexStatus()
    {
        var indexPath = Path.Combine(CachePath, IndexFileName);
        var manifestPath = Path.Combine(CachePath, ManifestFileName);
        if (!File.Exists(indexPath) || !File.Exists(manifestPath))
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Missing,
                Reason = "Asset index not found. Run 'jiangyu assets index' first.",
            };
        }

        var manifest = JsonSerializer.Deserialize<IndexManifest>(
            File.ReadAllText(manifestPath), JsonOptions);
        if (manifest is null)
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = "Asset index manifest is unreadable. Rebuild it with 'jiangyu assets index'.",
            };
        }

        var currentHash = ComputeGameAssemblyHash();
        if (currentHash is null || currentHash != manifest.GameAssemblyHash
            || manifest.FormatVersion != CurrentFormatVersion)
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = "Asset index is stale for the current game version. Run 'jiangyu assets index' first.",
            };
        }

        return new CachedIndexStatus
        {
            State = CachedIndexState.Current,
        };
    }

    /// <summary>
    /// Loads game data via AssetRipper, builds a searchable asset index, and writes it to the cache.
    /// No asset files are exported — only metadata. Always rebuilds from scratch.
    /// </summary>
    public void BuildIndex()
    {
        var gameData = LoadAndProcessGameData();
        BuildIndexFromGameData(gameData);
    }

    /// <summary>
    /// Builds the asset index from pre-loaded game data. Use this overload when
    /// the caller already holds a <see cref="GameData"/> instance (e.g. from a
    /// cached load) to avoid re-loading.
    /// </summary>
    public void BuildIndexFromGameData(GameData gameData)
    {
        _progress.SetPhase("Building index");
        var index = BuildAssetIndex(gameData);
        _progress.Finish();

        // Wipe cached previews so stale entries from an older game version
        // (which may reuse the same collection/pathId) don't survive.
        var previewDir = Path.Combine(CachePath, PreviewDirName);
        if (Directory.Exists(previewDir))
            Directory.Delete(previewDir, recursive: true);

        // Write index
        Directory.CreateDirectory(CachePath);
        File.WriteAllText(
            Path.Combine(CachePath, IndexFileName),
            JsonSerializer.Serialize(index, JsonOptions));

        // Write manifest
        var manifest = new IndexManifest
        {
            FormatVersion = CurrentFormatVersion,
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = GameDataPath,
            AssetCount = index.Assets?.Count ?? 0,
        };
        File.WriteAllText(
            Path.Combine(CachePath, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));

        _log.Info($"Indexed {index.Assets?.Count ?? 0} assets to: {CachePath}");
    }

    /// <summary>
    /// Loads game data via AssetRipper and runs all processors. Returns the
    /// fully-processed <see cref="GameData"/> ready for asset extraction.
    /// Callers may cache the result for repeated lookups.
    /// </summary>
    public GameData LoadAndProcessGameData()
    {
        _log.Info($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        // Level2 inflates MonoBehaviour structures via the typed managed
        // metadata (the same level ObjectInspectionService uses). Required
        // for the asset-index build to walk Stem.SoundBank.sounds[] and
        // populate AssetEntry.NamedChildren; lower levels leave
        // m_Structure as an opaque node with no Fields list.
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);

        try
        {
            _progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                _progress.Finish();
                throw new InvalidOperationException("No asset collections found in game data.");
            }

            _progress.Finish();

            int collectionCount = gameData.GameBundle.FetchAssetCollections().Count();
            _log.Info($"Loaded {collectionCount} asset collections");

            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            return gameData;
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    /// <summary>
    /// Loads and returns the asset index from the cache. Returns null if no index exists.
    /// </summary>
    public AssetIndex? LoadIndex()
    {
        var indexPath = Path.Combine(CachePath, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(indexPath), JsonOptions);
    }

    public IndexManifest? LoadManifest()
    {
        var manifestPath = Path.Combine(CachePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<IndexManifest>(File.ReadAllText(manifestPath), JsonOptions);
    }

    /// <summary>
    /// Searches the asset index with optional query and type filter.
    /// Returns matching entries, up to the specified limit.
    /// Returns an empty list if no index exists.
    /// </summary>
    public List<AssetEntry> Search(string? query = null, string? typeFilter = null, int limit = 50)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return [];
        }

        return [.. index.Assets
            .Where(a =>
                (string.IsNullOrEmpty(query)
                 || (a.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                 || (a.CanonicalPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                && (typeFilter is null || string.Equals(a.ClassName, typeFilter, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)];
    }

    /// <summary>
    /// Resolves the first asset matching the given name and any of the specified class names.
    /// Returns null if no index exists or no match is found.
    /// </summary>
    public AssetEntry? ResolveAsset(string assetName, params string[] classNames)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return null;
        }

        return index.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
            && classNames.Any(cn => string.Equals(a.ClassName, cn, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Returns every asset matching the given name and any of the specified class names,
    /// optionally narrowed to a specific collection and/or pathId. Caller decides how to
    /// handle zero / one / many matches.
    /// </summary>
    public IReadOnlyList<AssetEntry> FindAssets(
        string assetName,
        IReadOnlyList<string> classNames,
        string? collection = null,
        long? pathId = null)
    {
        var index = LoadIndex();
        if (index?.Assets is null)
        {
            return [];
        }

        return [.. index.Assets.Where(a =>
            string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase)
            && classNames.Any(cn => string.Equals(a.ClassName, cn, StringComparison.OrdinalIgnoreCase))
            && (collection is null || string.Equals(a.Collection, collection, StringComparison.OrdinalIgnoreCase))
            && (!pathId.HasValue || a.PathId == pathId.Value))];
    }

    // JIANGYU-CONTRACT: PrefabHierarchyObject is the preferred modder-facing model target.
    // Both compile-time target resolution and CLI model export collapse a PHO to the single
    // same-named GameObject via the asset index, then work against that GameObject. Valid for
    // the current proven export/replacement path; ambiguity is a hard error rather than a guess.
    /// <summary>
    /// If <paramref name="target"/> is a PrefabHierarchyObject, returns the single
    /// same-named GameObject from <paramref name="index"/> that backs it. Otherwise returns
    /// <paramref name="target"/> unchanged. Throws when the backing GameObject is missing
    /// or ambiguous.
    /// </summary>
    public static AssetEntry ResolveGameObjectBacking(AssetIndex index, AssetEntry target)
    {
        if (!string.Equals(target.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal))
        {
            return target;
        }

        var name = target.Name ?? string.Empty;
        var backing = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "GameObject", StringComparison.Ordinal) &&
                string.Equals(entry.Name, name, StringComparison.Ordinal))
            .ToList()
            ?? [];

        if (backing.Count == 0)
        {
            throw new InvalidOperationException(
                $"PrefabHierarchyObject '{name}' has no backing GameObject of the same name in the asset index.");
        }

        if (backing.Count > 1)
        {
            var matches = backing
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"PrefabHierarchyObject '{name}' has multiple backing GameObjects: {string.Join(", ", matches)}");
        }

        return backing[0];
    }

    /// <summary>
    /// Extracts a vanilla game prefab plus its referenced assets (meshes,
    /// materials, textures) as Unity-native files into
    /// <paramref name="destDir"/>. The output is shaped so a modder can drop
    /// it under <c>unity/Assets/Imported/&lt;name&gt;/</c> and Unity Editor
    /// imports it as a reference / clone-from-base for prefab cloning work.
    ///
    /// Implementation strategy: runs AssetRipper's full project export to a
    /// game-version-keyed cache directory, then surgically copies the target
    /// prefab and its transitively-referenced files out of the cache into
    /// <paramref name="destDir"/>. The first call for a given game version is
    /// slow (full extraction); subsequent calls reuse the cache.
    /// </summary>
    /// <param name="destDir">Output directory. Created if missing.</param>
    /// <param name="collection">Optional collection name from `assets search`.</param>
    /// <param name="pathId">Optional path ID from `assets search`. Pass -1 to resolve by name only.</param>
    public void ImportPrefabAsUnityAssets(string assetName, string destDir, string? collection, long pathId)
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
            _progress.SetPhase("Loading game data");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);
            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                _progress.Finish();
                _log.Error("No asset collections found in game data.");
                return;
            }

            _progress.Finish();
            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            // Locate the target asset. If the caller passed an explicit
            // (collection, pathId), use that; otherwise scan by name.
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
            {
                _log.Error($"No asset named '{assetName}' found. Confirm with `jiangyu assets search`.");
                return;
            }

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
                _log.Error($"'{assetName}' resolved to {target.GetType().Name}, not a prefab/GameObject. Try `assets export model` for non-prefab assets.");
                return;
            }

            // Walk the dependency closure breadth-first. Each step pulls
            // PPtrs out of the asset via FetchDependencies; we resolve each
            // PPtr to its concrete asset and queue it.
            _progress.SetPhase("Walking dependencies");
            var keep = WalkDependencyClosure(gameData, target);
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

            // Export just the kept assets via the Jiangyu-patched
            // ExportHandler.ExportSubset path. AssetRipper builds collections
            // for every asset in the bundle (so cross-references resolve) and
            // only writes the ones intersecting our dependency closure.
            // AssetRipper's native layout writes everything under
            // <destDir>/ExportedProject/Assets/. We flatten that one level
            // down so the modder sees unity/Assets/Imported/<name>/{GameObject,
            // Mesh, Material, ...}/ directly.
            _progress.SetPhase("Exporting");
            Directory.CreateDirectory(destDir);
            var handler = new AssetRipper.Export.UnityProjects.ExportHandler(settings);
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
        finally
        {
            Logger.Remove(adapter);
        }
    }

    private static HashSet<IUnityObjectBase> WalkDependencyClosure(GameData gameData, IUnityObjectBase root)
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

    /// <summary>
    /// Loads game data, finds the named asset, and exports a self-contained model package.
    /// Clean export layout:
    ///   {packageDir}/model.gltf          (references textures via standard glTF material channels)
    ///   {packageDir}/textures/*.png
    /// Raw export layout:
    ///   {packageDir}/model.glb            (self-contained, no textures)
    /// </summary>
    /// <param name="collection">The asset collection name (from index).</param>
    /// <param name="pathId">The asset PathID (from index).</param>
    public void ExportModel(string assetName, string packageDir, bool clean, string collection, long pathId)
    {
        _log.Info($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);

        try
        {
            _progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                _progress.Finish();
                _log.Error("No asset collections found in game data.");
                return;
            }

            _progress.Finish();

            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            // Find the asset by stable indexed identity (collection + pathId)
            IUnityObjectBase? found = null;
            foreach (var col in gameData.GameBundle.FetchAssetCollections())
            {
                if (col.Name != collection)
                {
                    continue;
                }

                found = col.FirstOrDefault(a => a.PathID == pathId);
                break;
            }

            if (found is IGameObject gameObject)
            {
                _log.Info($"Found GameObject: {gameObject.Name}");
                ExportGameObjectPackage(gameObject, packageDir, clean);
                return;
            }

            if (found is PrefabHierarchyObject prefabHierarchy)
            {
                _log.Info($"Found PrefabHierarchyObject: {prefabHierarchy.Name}");
                ExportGameObjectPackage(prefabHierarchy.Root, packageDir, clean, prefabHierarchy.Assets);
                return;
            }

            if (found is IMesh mesh)
            {
                _log.Info($"Found Mesh: {mesh.Name}");
                Directory.CreateDirectory(packageDir);
                var glbPath = Path.Combine(packageDir, "model.glb");
                ExportMeshAsGlb(mesh, glbPath);
                return;
            }

            _log.Error($"No GameObject or Mesh named '{assetName}' found.");
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    /// <summary>
    /// Decodes the indexed Texture2D asset to a PNG at <paramref name="outputFilePath"/>.
    /// The image reflects the game's current runtime pixels for that texture instance.
    /// </summary>
    public bool ExportTexture(string assetName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ITexture2D>(assetName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            using var stream = File.Create(outputFilePath);
            bitmap.SaveAsPng(stream);
            _log.Info($"  -> {outputFilePath} ({bitmap.Width}x{bitmap.Height})");
            return true;
        });
    }

    /// <summary>
    /// Loads the indexed Texture2D asset and decodes it to RGBA32 pixel data.
    /// Used by compile-time atlas compositing to obtain the original atlas image.
    /// </summary>
    public (int Width, int Height, byte[] Rgba)? LoadTexture2dRgba(string assetName, string collection, long pathId)
    {
        (int Width, int Height, byte[] Rgba)? result = null;
        ExportAssetFromIndexed<ITexture2D>(assetName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }
            result = (bitmap.Width, bitmap.Height, bitmap.ToRgba32());
            return true;
        });
        return result;
    }

    /// <summary>
    /// Decodes the indexed Sprite asset to a PNG at <paramref name="outputFilePath"/>. Produces
    /// the sprite's framed rect (not the full atlas backing texture); atlas-backed sprites still
    /// decode to just their own region.
    /// </summary>
    public bool ExportSprite(string assetName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ISprite>(assetName, collection, pathId, "Sprite", (sprite) =>
        {
            if (!SpriteConverter.TryConvertToBitmap(sprite, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Sprite '{sprite.Name}'.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            using var stream = File.Create(outputFilePath);
            bitmap.SaveAsPng(stream);
            _log.Info($"  -> {outputFilePath} ({bitmap.Width}x{bitmap.Height})");
            return true;
        });
    }

    /// <summary>
    /// Decodes the indexed AudioClip asset. Writes to <paramref name="outputDirectory"/>; the
    /// file extension is chosen by the decoder (usually <c>.ogg</c> or <c>.wav</c> for PCM
    /// Fmod samples, occasionally module formats like <c>.it</c>/<c>.xm</c>). Returns the
    /// written path on success, or <c>null</c> if decode / export failed.
    /// </summary>
    public string? ExportAudio(string assetName, string outputDirectory, string collection, long pathId)
    {
        string? writtenPath = null;
        var success = ExportAssetFromIndexed<IAudioClip>(assetName, collection, pathId, "AudioClip", (audioClip) =>
        {
            if (!AudioClipDecoder.TryDecode(audioClip, out var decodedData, out var fileExtension, out var message))
            {
                _log.Error($"Failed to decode AudioClip '{audioClip.Name}': {message}");
                return false;
            }

            // AssetRipper's AudioClipDecoder returns extensions without a leading dot
            // (e.g. "ogg", "wav"); normalise so we produce "<name>.<ext>" rather than
            // "<name><ext>" which reads as "nameogg".
            if (!fileExtension.StartsWith('.'))
                fileExtension = "." + fileExtension;

            Directory.CreateDirectory(outputDirectory);
            var outputFilePath = Path.Combine(outputDirectory, $"{assetName}{fileExtension}");
            File.WriteAllBytes(outputFilePath, decodedData);
            _log.Info($"  -> {outputFilePath} ({decodedData.Length} bytes)");
            writtenPath = outputFilePath;
            return true;
        });
        return success ? writtenPath : null;
    }

    /// <summary>
    /// Exports the atlas Texture2D as a PNG with coloured sprite-region outlines drawn over it,
    /// plus a companion legend file mapping outline colours to sprite names and rects. Intended
    /// for modders who want to see which sprite occupies which region of the atlas.
    /// </summary>
    public bool ExportAtlas(string atlasName, string outputFilePath, string collection, long pathId)
    {
        return ExportAssetFromIndexed<ITexture2D>(atlasName, collection, pathId, "Texture2D", (texture) =>
        {
            if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap) || bitmap.IsEmpty)
            {
                _log.Error($"Failed to decode Texture2D '{texture.Name}'.");
                return false;
            }

            var rgba = bitmap.ToRgba32();
            int width = bitmap.Width;
            int height = bitmap.Height;

            var index = LoadIndex();
            var sprites = index?.Assets?
                .Where(a =>
                    string.Equals(a.ClassName, "Sprite", StringComparison.Ordinal) &&
                    a.SpriteBackingTexturePathId == pathId &&
                    string.Equals(a.SpriteBackingTextureCollection, collection, StringComparison.Ordinal) &&
                    a.SpriteTextureRectWidth.HasValue &&
                    a.SpriteTextureRectHeight.HasValue)
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToList()
                ?? [];

            if (sprites.Count == 0)
            {
                _log.Info("No sprites reference this texture; exporting plain atlas.");
            }

            // Distinct palette for outlines, cycling if there are more sprites than colours.
            ReadOnlySpan<(byte R, byte G, byte B)> palette =
            [
                (255,  50,  50),  // red
                ( 50, 200,  50),  // green
                ( 50, 100, 255),  // blue
                (255, 200,  50),  // yellow
                (255,  50, 200),  // magenta
                ( 50, 220, 220),  // cyan
                (255, 140,  50),  // orange
                (180,  50, 255),  // purple
            ];

            var legendLines = new List<string> { $"Atlas: {atlasName} ({width} x {height})", "" };

            for (int i = 0; i < sprites.Count; i++)
            {
                var sprite = sprites[i];
                var (cr, cg, cb) = palette[i % palette.Length];

                int rx = (int)Math.Round(sprite.SpriteTextureRectX ?? 0);
                int ry = (int)Math.Round(sprite.SpriteTextureRectY ?? 0);
                int rw = (int)Math.Round(sprite.SpriteTextureRectWidth ?? 0);
                int rh = (int)Math.Round(sprite.SpriteTextureRectHeight ?? 0);

                // Unity textureRect: bottom-left origin. RGBA buffer: top-left origin.
                int topLeftY = height - ry - rh;
                DrawRectOutline(rgba, width, height, rx, topLeftY, rw, rh, cr, cg, cb);

                legendLines.Add($"  [{i + 1}] {sprite.Name ?? "(unnamed)"}  rect=({rx}, {ry}, {rw}, {rh})  colour=#{cr:X2}{cg:X2}{cb:X2}");
            }

            if (sprites.Count > 0)
            {
                legendLines.Add("");
                legendLines.Add($"{sprites.Count} sprite(s) outlined.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);

            using (var stream = File.Create(outputFilePath))
            {
                var writer = new StbImageWriteSharp.ImageWriter();
                writer.WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
            }

            var legendPath = Path.ChangeExtension(outputFilePath, ".txt");
            File.WriteAllLines(legendPath, legendLines);

            _log.Info($"  -> {outputFilePath} ({width}x{height}, {sprites.Count} sprite outlines)");
            _log.Info($"  -> {legendPath} (legend)");
            return true;
        });
    }

    /// <summary>
    /// Draws a 2px outlined rectangle into an RGBA buffer (top-left origin). The outer ring is
    /// drawn in black for contrast, the inner ring in the specified colour.
    /// </summary>
    private static void DrawRectOutline(byte[] rgba, int imgWidth, int imgHeight,
        int x, int y, int w, int h, byte r, byte g, byte b)
    {
        // Outer ring (black, 1px)
        DrawHollowRect(rgba, imgWidth, imgHeight, x, y, w, h, 0, 0, 0, 255);
        // Inner ring (colour, 1px inset)
        if (w > 2 && h > 2)
            DrawHollowRect(rgba, imgWidth, imgHeight, x + 1, y + 1, w - 2, h - 2, r, g, b, 255);
    }

    private static void DrawHollowRect(byte[] rgba, int imgWidth, int imgHeight,
        int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (int dx = 0; dx < w; dx++)
        {
            SetPixel(rgba, imgWidth, imgHeight, x + dx, y, r, g, b, a);
            SetPixel(rgba, imgWidth, imgHeight, x + dx, y + h - 1, r, g, b, a);
        }
        for (int dy = 0; dy < h; dy++)
        {
            SetPixel(rgba, imgWidth, imgHeight, x, y + dy, r, g, b, a);
            SetPixel(rgba, imgWidth, imgHeight, x + w - 1, y + dy, r, g, b, a);
        }
    }

    private static void SetPixel(byte[] rgba, int imgWidth, int imgHeight,
        int px, int py, byte r, byte g, byte b, byte a)
    {
        if (px < 0 || px >= imgWidth || py < 0 || py >= imgHeight) return;
        int offset = (py * imgWidth + px) * 4;
        rgba[offset] = r;
        rgba[offset + 1] = g;
        rgba[offset + 2] = b;
        rgba[offset + 3] = a;
    }

    private bool ExportAssetFromIndexed<T>(
        string assetName,
        string collection,
        long pathId,
        string expectedTypeLabel,
        Func<T, bool> exporter)
        where T : class, IUnityObjectBase
    {
        _log.Info($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);

        try
        {
            _progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                _progress.Finish();
                _log.Error("No asset collections found in game data.");
                return false;
            }

            _progress.Finish();
            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            IUnityObjectBase? found = null;
            foreach (var col in gameData.GameBundle.FetchAssetCollections())
            {
                if (col.Name != collection)
                    continue;

                found = col.FirstOrDefault(a => a.PathID == pathId);
                break;
            }

            if (found is not T typed)
            {
                _log.Error(
                    $"No {expectedTypeLabel} named '{assetName}' found in collection '{collection}' at pathId={pathId} " +
                    $"(found={found?.GetType().Name ?? "null"}).");
                return false;
            }

            return exporter(typed);
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    /// <summary>
    /// MENACE material property → standard glTF PBR channel.
    /// Only properties with genuine glTF PBR equivalents are mapped here.
    /// MENACE-specific properties (_MaskMap, _Effect_Map) are NOT mapped — they go to extras.
    /// </summary>
    internal static readonly Dictionary<string, string> StandardChannelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_BaseColorMap"] = "BaseColor",
        ["_MainTex"] = "BaseColor",
        ["_BaseMap"] = "BaseColor",
        ["_NormalMap"] = "Normal",
        ["_BumpMap"] = "Normal",
        ["_MetallicGlossMap"] = "MetallicRoughness",
        ["_EmissionMap"] = "Emissive",
        ["_OcclusionMap"] = "Occlusion",
    };

    internal sealed class DiscoveredTexture
    {
        public required string Name { get; init; }
        public required string MaterialName { get; init; }
        /// <summary>Stable source identity: "collection:pathId". Matches extras.jiangyu.sourceMaterial in the GLB.</summary>
        public required string SourceMaterialId { get; init; }
        public required string Property { get; init; }
        public required byte[] PngData { get; init; }
    }

    private void ExportGameObjectPackage(
        IGameObject gameObject,
        string packageDir,
        bool clean,
        IEnumerable<IUnityObjectBase>? exportAssets = null)
    {
        Directory.CreateDirectory(packageDir);

        IGameObject root = gameObject.GetRoot();
        var assets = exportAssets ?? root.FetchHierarchy().Cast<IUnityObjectBase>();
        SceneBuilder rawScene = GlbLevelBuilder.Build(assets, false);

        if (clean)
        {
            // Write raw GLB to temp file for cleanup
            var tempGlbPath = Path.Combine(packageDir, ".model.tmp.glb");
            using (var stream = File.Create(tempGlbPath))
            {
                if (!GlbWriter.TryWrite(rawScene, stream, out string? errorMessage))
                {
                    _log.Error($"Error writing GLB: {errorMessage}");
                    return;
                }
            }

            // Discover textures from source asset hierarchy
            var textures = DiscoverTextures(root);

            // Prepare standard-channel textures for MaterialBuilder attachment during cleanup.
            // Keyed by stable source material identity (collection:pathId), not material name.
            // This matches extras.jiangyu.sourceMaterial embedded in the GLB by GlbLevelBuilder.
            var materialTextures = new Dictionary<string, List<(string channelKey, byte[] pngData)>>(StringComparer.Ordinal);
            foreach (var tex in textures)
            {
                if (StandardChannelMap.TryGetValue(tex.Property, out var channelKey))
                {
                    if (!materialTextures.TryGetValue(tex.SourceMaterialId, out var list))
                    {
                        list = [];
                        materialTextures[tex.SourceMaterialId] = list;
                    }
                    list.Add((channelKey, tex.PngData));
                }
            }

            var cleanScene = ModelCleanupService.BuildCleanScene(tempGlbPath, _log, materialTextures);
            File.Delete(tempGlbPath);
            _log.Info("  Cleaned: 1x authoring scale");
            var sourceSkinBindings = CollectSourceSkinBindings(root);

            // Build .gltf — standard textures already attached at MaterialBuilder level,
            // SaveGltfPackage only handles non-standard textures + extras
            var gltfPath = Path.Combine(packageDir, "model.gltf");
            SaveGltfPackage(cleanScene, textures, gltfPath, sourceSkinBindings);

            if (textures.Count > 0)
            {
                _log.Info($"  Exported {textures.Count} textures");
            }
        }
        else
        {
            // Raw export: .glb + textures (for inspection, not authoring)
            var glbPath = Path.Combine(packageDir, "model.glb");
            using (var stream = File.Create(glbPath))
            {
                if (GlbWriter.TryWrite(rawScene, stream, out string? errorMessage))
                {
                    _log.Info($"  Model: {glbPath}");
                }
                else
                {
                    _log.Error($"Error writing GLB: {errorMessage}");
                    return;
                }
            }

            // Export textures as loose files for inspection context
            var textures = DiscoverTextures(root);
            if (textures.Count > 0)
            {
                var texturesDir = Path.Combine(packageDir, "textures");
                Directory.CreateDirectory(texturesDir);
                foreach (var tex in textures)
                {
                    File.WriteAllBytes(Path.Combine(texturesDir, $"{tex.Name}.png"), tex.PngData);
                }
                _log.Info($"  Exported {textures.Count} textures");
            }
        }

        _log.Info($"Package: {packageDir}");
    }

    /// <summary>
    /// Walks the source asset hierarchy, resolves material → texture references,
    /// and returns discovered textures with their PNG data in memory.
    /// </summary>
    private List<DiscoveredTexture> DiscoverTextures(IGameObject root)
    {
        var textures = new List<DiscoveredTexture>();
        var seen = new HashSet<string>();

        foreach (var editorExt in root.FetchHierarchy())
        {
            if (editorExt is not IGameObject go)
            {
                continue;
            }

            IRenderer? renderer = null;
            if (go.TryGetComponent(out ISkinnedMeshRenderer? smr))
            {
                renderer = smr;
            }
            else if (go.TryGetComponent(out IRenderer? mr))
            {
                renderer = mr;
            }

            if (renderer is null)
            {
                continue;
            }

            foreach (var materialPPtr in renderer.Materials_C25)
            {
                var material = materialPPtr.TryGetAsset(renderer.Collection);
                if (material is null)
                {
                    continue;
                }

                foreach (var (propName, texEnv) in material.GetTextureProperties())
                {
                    if (texEnv.Texture.TryGetAsset(material.Collection) is not ITexture2D texture || string.IsNullOrEmpty(texture.Name))
                    {
                        continue;
                    }

                    if (!seen.Add(texture.Name))
                    {
                        // Same texture referenced by multiple materials — expected for shared textures.
                        // Different textures with the same name would be a data issue upstream.
                        continue;
                    }

                    if (!TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
                    {
                        continue;
                    }

                    using var ms = new MemoryStream();
                    bitmap.SaveAsPng(ms);

                    textures.Add(new DiscoveredTexture
                    {
                        Name = texture.Name,
                        MaterialName = material.Name,
                        SourceMaterialId = $"{material.Collection.Name}:{material.PathID}",
                        Property = propName.ToString(),
                        PngData = ms.ToArray(),
                    });

                    _log.Info($"  Texture: {texture.Name}.png ({propName})");
                }
            }
        }

        return textures;
    }

    /// <summary>
    /// Converts a clean SceneBuilder to a .gltf file with external texture references.
    /// Standard-channel textures are already attached at the MaterialBuilder level (by
    /// BuildCleanScene). This method handles non-standard textures (written to disk,
    /// referenced in material extras) and root extras (cleaned flag).
    /// </summary>
    internal static void SaveGltfPackage(
        SceneBuilder scene,
        List<DiscoveredTexture> textures,
        string gltfPath,
        IReadOnlyList<SourceSkinBinding>? sourceSkinBindings = null)
    {
        var model = scene.ToGltf2();
        var recoveredSkins = FindMissingSkinBindings(model, sourceSkinBindings);
        var recoveredAssignments = CreateRecoveredSkinAssignments(model, recoveredSkins);

        // Images created by MaterialBuilder don't have Name or AlternateWriteFileName.
        // Match them to discovered textures by content so we can set meaningful names.
        var standardTextures = textures
            .Where(t => StandardChannelMap.ContainsKey(t.Property))
            .ToList();
        foreach (var image in model.LogicalImages)
        {
            if (!string.IsNullOrEmpty(image.Name))
            {
                continue;
            }

            var imageContent = image.Content.Content;
            var match = standardTextures.FirstOrDefault(t =>
                imageContent.Length == t.PngData.Length &&
                imageContent.Span.SequenceEqual(t.PngData));
            if (match is not null)
            {
                image.Name = match.Name;
                image.AlternateWriteFileName = $"textures/{match.Name}.png";
            }
        }

        var gltfDir = Path.GetDirectoryName(gltfPath)!;
        var texturesDir = Path.Combine(gltfDir, "textures");

        // Build source material ID → Schema2 Material lookup (for non-standard extras only).
        // Uses the same stable identity embedded by GlbLevelBuilder.
        var materialsBySourceId = new Dictionary<string, Material>(StringComparer.Ordinal);
        foreach (var mat in model.LogicalMaterials)
        {
            if (mat.Extras is JsonObject matExtras &&
                matExtras.TryGetPropertyValue("jiangyu", out var jNode) &&
                jNode is JsonObject jObj &&
                jObj.TryGetPropertyValue("sourceMaterial", out var smNode) &&
                smNode is JsonObject smObj)
            {
                var collection = smObj["collection"]?.GetValue<string>();
                var pathId = smObj["pathId"]?.GetValue<long>();
                if (collection is not null && pathId is not null)
                {
                    materialsBySourceId.TryAdd($"{collection}:{pathId}", mat);
                }
            }
        }

        // Write non-standard textures to disk and add extras references
        var texturesByMaterial = textures
            .Where(t => !StandardChannelMap.ContainsKey(t.Property))
            .GroupBy(t => t.SourceMaterialId, StringComparer.Ordinal);

        foreach (var group in texturesByMaterial)
        {
            var nonStandardTextures = new JsonObject();
            foreach (var tex in group)
            {
                Directory.CreateDirectory(texturesDir);
                File.WriteAllBytes(Path.Combine(texturesDir, $"{tex.Name}.png"), tex.PngData);
                nonStandardTextures[tex.Property] = $"textures/{tex.Name}.png";
            }

            if (nonStandardTextures.Count > 0 &&
                materialsBySourceId.TryGetValue(group.Key, out var material))
            {
                // Merge into existing jiangyu extras (preserving any other fields)
                var materialExtras = material.Extras as JsonObject ?? [];
                var jiangyuObj = materialExtras["jiangyu"] as JsonObject ?? [];
                jiangyuObj["textures"] = nonStandardTextures;
                materialExtras["jiangyu"] = jiangyuObj;
                material.Extras = materialExtras;
            }
        }

        // Strip internal sourceMaterial identity from output — it's pipeline-internal,
        // not useful for modders or the compiler reading the final .gltf
        foreach (var mat in model.LogicalMaterials)
        {
            if (mat.Extras is JsonObject matExtras &&
                matExtras["jiangyu"] is JsonObject jObj)
            {
                jObj.Remove("sourceMaterial");
            }
        }

        // Set root extras
        model.Extras = new JsonObject
        {
            ["jiangyu"] = new JsonObject
            {
                ["cleaned"] = true
            }
        };

        // Save as .gltf with external satellite images
        var settings = new WriteSettings
        {
            ImageWriting = ResourceWriteMode.SatelliteFile
        };
        model.SaveGLTF(gltfPath, settings);

        if (recoveredAssignments.Count > 0)
        {
            ApplyRecoveredSkinAssignmentsToGltf(gltfPath, recoveredAssignments);
        }
    }

    internal static IReadOnlyList<RecoveredSkinBinding> FindMissingSkinBindings(
        ModelRoot model,
        IReadOnlyList<SourceSkinBinding>? sourceSkinBindings = null)
    {
        var recovered = new List<RecoveredSkinBinding>();
        var pathToNodeIndex = BuildRelativePathNodeLookup(model);
        var sourceBindingsByMeshName = sourceSkinBindings?
            .GroupBy(binding => binding.MeshName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var node in model.LogicalNodes)
        {
            if (node.Mesh is null || node.Skin is not null)
            {
                continue;
            }

            int requiredJointCount = GetRequiredJointCount(node.Mesh);
            if (requiredJointCount <= 0)
            {
                continue;
            }

            var meshName = node.Mesh.Name;
            var meshNodePath = GetRelativeNodePath(node);
            if (!string.IsNullOrWhiteSpace(meshName) &&
                sourceBindingsByMeshName is not null &&
                sourceBindingsByMeshName.TryGetValue(meshName, out var sourceBindingCandidates))
            {
                var orderedCandidates = sourceBindingCandidates
                    .OrderByDescending(binding => GetPathMatchScore(meshNodePath, binding.MeshNodePath))
                    .ToArray();

                foreach (var candidate in orderedCandidates)
                {
                    if (TryResolveSourceBonePaths(candidate.BonePaths, pathToNodeIndex, out var sourceJointNodeIndices))
                    {
                        recovered.Add(new RecoveredSkinBinding(node.LogicalIndex, sourceJointNodeIndices));
                        goto NextNode;
                    }
                }
            }

            var parent = node.VisualParent;
            if (parent is null)
            {
                continue;
            }

            var hierarchyCandidates = parent.VisualChildren
                .Where(child => child.LogicalIndex != node.LogicalIndex && child.Mesh is null)
                .OrderByDescending(child => string.Equals(child.Name, "root", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(CountNonMeshNodes)
                .ToList();

            foreach (var candidate in hierarchyCandidates)
            {
                var joints = EnumerateNonMeshNodesDepthFirst(candidate)
                    .Take(requiredJointCount)
                    .ToArray();

                if (joints.Length != requiredJointCount)
                {
                    continue;
                }

                // JIANGYU-CONTRACT: Some MENACE vehicle exports include JOINTS_0/WEIGHTS_0
                // but no glTF skin. We recover bindings against the sibling non-mesh
                // hierarchy (preferring node name "root"), in depth-first order.
                recovered.Add(new RecoveredSkinBinding(
                    node.LogicalIndex,
                    [.. joints.Select(j => j.LogicalIndex)]));
                break;
            }

        NextNode:
            continue;
        }

        return recovered;
    }

    private static int GetRequiredJointCount(SharpGLTF.Schema2.Mesh mesh)
    {
        int maxJoint = -1;

        foreach (var primitive in mesh.Primitives)
        {
            var joints = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
            var weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
            if (joints is null)
            {
                continue;
            }

            for (int i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                var w = weights is not null && i < weights.Count ? weights[i] : Vector4.Zero;

                if (w.X > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.X));
                if (w.Y > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.Y));
                if (w.Z > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.Z));
                if (w.W > 0f) maxJoint = Math.Max(maxJoint, (int)MathF.Floor(j.W));
            }
        }

        return maxJoint + 1;
    }

    private static IEnumerable<Node> EnumerateNonMeshNodesDepthFirst(Node root)
    {
        if (root.Mesh is null)
        {
            yield return root;
        }

        foreach (var child in root.VisualChildren)
        {
            foreach (var descendant in EnumerateNonMeshNodesDepthFirst(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountNonMeshNodes(Node root)
    {
        int count = 0;
        foreach (var _ in EnumerateNonMeshNodesDepthFirst(root))
        {
            count++;
        }

        return count;
    }

    private static IReadOnlyList<SourceSkinBinding> CollectSourceSkinBindings(IGameObject root)
    {
        var bindings = new List<SourceSkinBinding>();

        foreach (var asset in root.FetchHierarchy())
        {
            if (asset is not ISkinnedMeshRenderer skinnedMeshRenderer)
            {
                continue;
            }

            IMesh? mesh = skinnedMeshRenderer.MeshP;
            if (mesh is null || !mesh.IsSet() || string.IsNullOrWhiteSpace(mesh.Name))
            {
                continue;
            }

            var rendererGameObject = skinnedMeshRenderer.GameObject_C25P;
            var rendererTransform = rendererGameObject?.GetTransform();
            if (rendererTransform is null)
            {
                continue;
            }

            var meshNodePath = GetRelativeTransformPath(rendererTransform, root);
            if (string.IsNullOrEmpty(meshNodePath))
            {
                continue;
            }

            var bonePaths = skinnedMeshRenderer.BonesP
                .Select(bone => bone is null ? string.Empty : GetRelativeTransformPath(bone, root))
                .ToArray();

            if (bonePaths.Length == 0 || bonePaths.Any(string.IsNullOrEmpty))
            {
                continue;
            }

            bindings.Add(new SourceSkinBinding(mesh.Name, meshNodePath, bonePaths));
        }

        return bindings;
    }

    private static string GetRelativeTransformPath(ITransform transform, IGameObject root)
    {
        var segments = new List<string>();
        ITransform? current = transform;
        ITransform rootTransform = root.GetTransform();

        while (current is not null)
        {
            if (current == rootTransform)
            {
                break;
            }

            var gameObject = current.GameObject_C4P;
            if (gameObject is not null)
            {
                segments.Add(gameObject.Name);
            }

            current = current.Father_C4P;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static Dictionary<string, int> BuildRelativePathNodeLookup(ModelRoot model)
    {
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in model.LogicalNodes)
        {
            var path = GetRelativeNodePath(node);
            if (!string.IsNullOrEmpty(path))
            {
                lookup.TryAdd(path, node.LogicalIndex);
            }
        }

        return lookup;
    }

    private static string GetRelativeNodePath(Node node)
    {
        var segments = new List<string>();
        var current = node;

        while (current.VisualParent is not null)
        {
            if (!string.IsNullOrEmpty(current.Name))
            {
                segments.Add(current.Name);
            }

            current = current.VisualParent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static bool TryResolveSourceBonePaths(
        IReadOnlyList<string> sourceBonePaths,
        IReadOnlyDictionary<string, int> pathToNodeIndex,
        out int[] jointNodeIndices)
    {
        jointNodeIndices = new int[sourceBonePaths.Count];
        for (int i = 0; i < sourceBonePaths.Count; i++)
        {
            var path = sourceBonePaths[i];
            if (string.IsNullOrEmpty(path) || !pathToNodeIndex.TryGetValue(path, out var nodeIndex))
            {
                jointNodeIndices = [];
                return false;
            }

            jointNodeIndices[i] = nodeIndex;
        }

        return true;
    }

    private static int GetPathMatchScore(string targetPath, string candidatePath)
    {
        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(candidatePath))
        {
            return 0;
        }

        var targetSegments = targetPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        var candidateSegments = candidatePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (targetSegments.Length == candidateSegments.Length &&
            targetSegments.SequenceEqual(candidateSegments, StringComparer.Ordinal))
        {
            return 10_000 + targetSegments.Length;
        }

        int prefixLength = 0;
        int maxPrefix = Math.Min(targetSegments.Length, candidateSegments.Length);
        while (prefixLength < maxPrefix &&
               string.Equals(targetSegments[prefixLength], candidateSegments[prefixLength], StringComparison.Ordinal))
        {
            prefixLength++;
        }

        int suffixLength = 0;
        int maxSuffix = Math.Min(targetSegments.Length, candidateSegments.Length);
        while (suffixLength < maxSuffix &&
               string.Equals(
                   targetSegments[targetSegments.Length - 1 - suffixLength],
                   candidateSegments[candidateSegments.Length - 1 - suffixLength],
                   StringComparison.Ordinal))
        {
            suffixLength++;
        }

        return (prefixLength * 100) + suffixLength;
    }

    internal static IReadOnlyList<RecoveredSkinAssignment> CreateRecoveredSkinAssignments(
        ModelRoot model,
        IReadOnlyList<RecoveredSkinBinding> recoveredSkins)
    {
        var assignments = new List<RecoveredSkinAssignment>(recoveredSkins.Count);
        if (recoveredSkins.Count == 0)
        {
            return assignments;
        }

        var skinCache = new Dictionary<string, Skin>(StringComparer.Ordinal);

        foreach (var recovered in recoveredSkins)
        {
            if (recovered.NodeIndex < 0 || recovered.NodeIndex >= model.LogicalNodes.Count)
            {
                continue;
            }

            var node = model.LogicalNodes[recovered.NodeIndex];
            if (node.Mesh is null || node.Skin is not null)
            {
                continue;
            }

            var jointNodes = new List<Node>(recovered.JointNodeIndices.Length);
            bool valid = true;
            foreach (int jointIndex in recovered.JointNodeIndices)
            {
                if (jointIndex < 0 || jointIndex >= model.LogicalNodes.Count)
                {
                    valid = false;
                    break;
                }

                jointNodes.Add(model.LogicalNodes[jointIndex]);
            }

            if (!valid || jointNodes.Count == 0)
            {
                continue;
            }

            var meshBindTransform = node.WorldMatrix;
            var cacheKey = $"{string.Join(",", recovered.JointNodeIndices)}|{GetMatrixCacheKey(meshBindTransform)}";
            if (!skinCache.TryGetValue(cacheKey, out var skin))
            {
                skin = model.CreateSkin();
                skin.BindJoints(meshBindTransform, [.. jointNodes]);
                skinCache[cacheKey] = skin;
            }

            assignments.Add(new RecoveredSkinAssignment(node.LogicalIndex, skin.LogicalIndex));
        }

        return assignments;
    }

    private static string GetMatrixCacheKey(Matrix4x4 matrix)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{matrix.M11:R},{matrix.M12:R},{matrix.M13:R},{matrix.M14:R}|{matrix.M21:R},{matrix.M22:R},{matrix.M23:R},{matrix.M24:R}|{matrix.M31:R},{matrix.M32:R},{matrix.M33:R},{matrix.M34:R}|{matrix.M41:R},{matrix.M42:R},{matrix.M43:R},{matrix.M44:R}");
    }

    internal static void ApplyRecoveredSkinAssignmentsToGltf(
        string gltfPath,
        IReadOnlyList<RecoveredSkinAssignment> assignments)
    {
        if (assignments.Count == 0)
        {
            return;
        }

        JsonNode? rootNode = JsonNode.Parse(File.ReadAllText(gltfPath));
        if (rootNode is not JsonObject root || root["nodes"] is not JsonArray nodes)
        {
            return;
        }

        foreach (var assignment in assignments)
        {
            if (assignment.NodeIndex < 0 || assignment.NodeIndex >= nodes.Count)
            {
                continue;
            }

            if (nodes[assignment.NodeIndex] is JsonObject nodeObject)
            {
                nodeObject["skin"] = assignment.SkinIndex;
            }
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        File.WriteAllText(gltfPath, root.ToJsonString(options));
    }

    internal sealed record SourceSkinBinding(string MeshName, string MeshNodePath, string[] BonePaths);
    internal sealed record RecoveredSkinBinding(int NodeIndex, int[] JointNodeIndices);
    internal sealed record RecoveredSkinAssignment(int NodeIndex, int SkinIndex);

    private void ExportMeshAsGlb(IMesh mesh, string outputPath)
    {
        SceneBuilder sceneBuilder = GlbMeshBuilder.Build(mesh);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        if (GlbWriter.TryWrite(sceneBuilder, stream, out string? errorMessage))
        {
            _log.Info($"Exported to: {outputPath}");
        }
        else
        {
            _log.Error($"Error writing GLB: {errorMessage}");
        }
    }

    private static AssetIndex BuildAssetIndex(GameData gameData)
    {
        var entries = new List<AssetEntry>();

        foreach (var collection in gameData.GameBundle.FetchAssetCollections())
        {
            string collectionName = collection.Name;

            foreach (IUnityObjectBase asset in collection)
            {
                var entry = new AssetEntry
                {
                    Name = asset.GetBestName(),
                    CanonicalPath = BuildCanonicalAssetPath(collectionName, asset.ClassName, asset.GetBestName(), asset.PathID),
                    ClassName = asset.ClassName,
                    ClassId = asset.ClassID,
                    PathId = asset.PathID,
                    Collection = collectionName,
                };

                if (asset is ISprite sprite)
                {
                    var backing = ResolveSpriteBackingTexture(sprite);
                    if (backing is not null)
                    {
                        entry.SpriteBackingTexturePathId = backing.PathID;
                        entry.SpriteBackingTextureCollection = backing.Collection.Name;
                        entry.SpriteBackingTextureName = backing.GetBestName();
                    }

                    PopulateSpriteAtlasMetadata(sprite, entry);
                }
                else if (asset is IAudioClip audioClip)
                {
                    if (audioClip.Has_Frequency())
                        entry.AudioFrequency = audioClip.Frequency;
                    if (audioClip.Has_Channels())
                        entry.AudioChannels = audioClip.Channels;
                }
                else if (asset is AssetRipper.SourceGenerated.Classes.ClassID_114.IMonoBehaviour monoBehaviour
                         && entry.Name is { } entryName
                         && entryName.EndsWith("_soundbank", StringComparison.Ordinal))
                {
                    entry.NamedChildren = TryExtractSoundBankSoundNames(monoBehaviour, gameData);
                }

                entries.Add(entry);
            }
        }

        return new AssetIndex { Assets = entries };
    }

    /// <summary>
    /// Walks a Stem.SoundBank asset's <c>m_Structure.sounds[]</c> list and
    /// returns each entry's <c>name</c> field. Used by the asset-index build
    /// to bake per-bank prototype names into AssetEntry.NamedChildren so the
    /// Studio's composite from= autocomplete can answer "what's in this
    /// bank" via a flat dictionary lookup instead of re-running live
    /// AssetRipper inspection.
    /// </summary>
    private static List<string>? TryExtractSoundBankSoundNames(
        AssetRipper.SourceGenerated.Classes.ClassID_114.IMonoBehaviour monoBehaviour,
        GameData gameData)
    {
        try
        {
            // Depth 4 is enough for m_Structure -> sounds -> element -> name.
            // ArraySample 4096 covers any bank without truncation.
            var inspection = ObjectFieldInspector.Inspect(monoBehaviour, maxDepth: 4, maxArraySampleLength: 4096);
            ManagedTypeInspectionEnricher.Enrich(monoBehaviour, gameData.AssemblyManager, inspection.Fields);
            OdinPayloadEnricher.Enrich(inspection.Fields);

            var structure = inspection.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
            var sounds = structure?.Fields?.FirstOrDefault(f =>
                string.Equals(f.Name, "sounds", StringComparison.Ordinal));
            if (sounds?.Elements is null || sounds.Elements.Count == 0)
                return null;

            var names = new List<string>(sounds.Elements.Count);
            foreach (var element in sounds.Elements)
            {
                if (element.Fields is null) continue;
                var nameField = element.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, "name", StringComparison.Ordinal));
                var value = nameField?.Value?.ToString();
                if (!string.IsNullOrEmpty(value))
                    names.Add(value);
            }
            return names.Count > 0 ? names : null;
        }
        catch
        {
            // Per-bank inspection failure is non-fatal: a missing
            // NamedChildren list just falls back to empty autocomplete for
            // that one bank rather than failing the whole index build.
            return null;
        }
    }

    private static string BuildCanonicalAssetPath(string? collectionName, string? className, string? assetName, long pathId)
    {
        var collectionSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(collectionName) ? "unknown-collection" : collectionName);
        var classSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(className) ? "UnknownClass" : className);
        var nameSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(assetName) ? "unnamed" : assetName);
        return $"{collectionSegment}/{classSegment}/{nameSegment}--{pathId}";
    }

    /// <summary>
    /// Replaces every character outside <c>[A-Za-z0-9._-]</c> with <c>_</c>. Used for
    /// building canonical asset paths and any filename derived from an asset name.
    /// Empty input collapses to <c>"_"</c> so callers always get a usable segment.
    /// </summary>
    public static string SanitizeAssetPathSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                buffer[index++] = c;
            }
            else
            {
                buffer[index++] = '_';
            }
        }

        return index == 0 ? "_" : new string(buffer[..index]);
    }

    private static ITexture2D? ResolveSpriteBackingTexture(
        ISprite sprite)
    {
        // Non-atlas sprites carry the texture reference directly on sprite.RD.
        var direct = sprite.RD.Texture.TryGetAsset(sprite.Collection);
        if (direct is not null)
            return direct;

        // Atlas-packed sprites carry a SpriteAtlas pointer; the backing texture lives
        // in the atlas's RenderDataMap, keyed by the sprite's RenderDataKey. This
        // mirrors AssetRipper.Processing.Textures.SpriteProcessor.ProcessSprite, which
        // we intentionally don't run as a processor here because it mutates sprite.Rect
        // /Pivot/Border and clears SpriteAtlasP, side-effects we don't want to apply to
        // the indexed game data.
        var atlas = sprite.SpriteAtlasP;
        if (atlas is null || !sprite.Has_RenderDataKey())
            return null;

        if (!atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out var atlasData))
            return null;

        return atlasData.Texture.TryGetAsset(atlas.Collection);
    }

    /// <summary>
    /// Extracts the sprite's atlas-space textureRect and packing rotation from either the
    /// SpriteAtlas RenderDataMap (for atlas-packed sprites) or the sprite's own RenderData
    /// (for standalone sprites). Populates the corresponding <see cref="AssetEntry"/> fields.
    /// </summary>
    private static void PopulateSpriteAtlasMetadata(ISprite sprite, AssetEntry entry)
    {
        var atlas = sprite.SpriteAtlasP;
        if (atlas is not null && sprite.Has_RenderDataKey()
            && atlas.RenderDataMap.TryGetValue(sprite.RenderDataKey, out var atlasData))
        {
            var rect = atlasData.TextureRect;
            entry.SpriteTextureRectX = rect.X;
            entry.SpriteTextureRectY = rect.Y;
            entry.SpriteTextureRectWidth = rect.Width;
            entry.SpriteTextureRectHeight = rect.Height;
            entry.SpritePackingRotation = (int)(atlasData.SettingsRaw >> 2 & 0xF);
        }
        else
        {
            var rd = sprite.RD;
            var rect = rd.TextureRect;
            entry.SpriteTextureRectX = rect.X;
            entry.SpriteTextureRectY = rect.Y;
            entry.SpriteTextureRectWidth = rect.Width;
            entry.SpriteTextureRectHeight = rect.Height;
            entry.SpritePackingRotation = (int)(rd.SettingsRaw >> 2 & 0xF);
        }
    }

    private static void RunProcessors(GameData gameData)
    {
        IAssetProcessor[] processors =
        [
            new SceneDefinitionProcessor(),
            new MainAssetProcessor(),
            new AnimatorControllerProcessor(),
            new PrefabProcessor(),
            new SpriteProcessor(),
        ];

        foreach (var processor in processors)
        {
            processor.Process(gameData);
        }
    }

    private string? ComputeGameAssemblyHash()
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetDirectoryName(GameDataPath)!, "GameAssembly.so"),
            Path.Combine(Path.GetDirectoryName(GameDataPath)!, "GameAssembly.dll"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                using var stream = File.OpenRead(candidate);
                var hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        return null;
    }

    // ── Thumbnail generation ──────────────────────────────────────────────

    /// <summary>
    /// Returns the file path for a cached thumbnail, or <c>null</c> if no thumbnail
    /// has been generated for this asset.
    /// </summary>
    /// <summary>
    /// Result of a lazy on-demand asset preview generation.
    /// </summary>
    public sealed record AssetPreviewResult(byte[] Data, string MimeType, string FileExtension);

    /// <summary>
    /// Generates an on-demand preview for a single asset. Returns a thumbnail PNG for
    /// textures/sprites, decoded audio bytes for audio clips, or a raw GLB for models.
    /// Results are cached to disk under <c>&lt;cache&gt;/previews/</c>.
    /// </summary>
    public AssetPreviewResult? GeneratePreview(GameData gameData, string collection, long pathId, string className)
    {
        // Check disk cache first
        var cached = FindCachedPreview(collection, pathId);
        if (cached is not null)
        {
            var mime = MimeTypeForExtension(Path.GetExtension(cached));
            return new AssetPreviewResult(File.ReadAllBytes(cached), mime, Path.GetExtension(cached));
        }

        IUnityObjectBase? found = FindAsset(gameData, collection, pathId);
        if (found is null)
        {
            _log.Warning($"Preview: asset not found in {collection} at pathId={pathId}");
            return null;
        }

        byte[] data;
        string ext;
        string mime2;

        switch (className)
        {
            case "Texture2D" when found is ITexture2D texture:
                if (!TextureConverter.TryConvertToBitmap(texture, out var texBitmap) || texBitmap.IsEmpty)
                    return null;
                data = GenerateThumbnailPng(texBitmap);
                ext = ".png";
                mime2 = "image/png";
                break;

            case "Sprite" when found is ISprite sprite:
                // Get the full backing texture bitmap first.
                DirectBitmap? fullBitmap = null;
                if (SpriteConverter.TryConvertToBitmap(sprite, out var sprBitmap) && !sprBitmap.IsEmpty)
                    fullBitmap = sprBitmap;
                else
                {
                    var backingTexture = ResolveSpriteBackingTexture(sprite);
                    if (backingTexture is not null &&
                        TextureConverter.TryConvertToBitmap(backingTexture, out var backingBmp) &&
                        !backingBmp.IsEmpty)
                        fullBitmap = backingBmp;
                }
                if (fullBitmap is null)
                {
                    _log.Warning($"Preview: failed to decode Sprite at {collection}/{pathId}");
                    return null;
                }

                // Crop to the sprite's TextureRect within the atlas.
                // TextureRect is in Unity coords (bottom-left origin); the bitmap
                // has been FlipY'd to top-left origin by TextureConverter.
                var texRect = sprite.RD.TextureRect;
                int rx = Math.Max(0, (int)texRect.X);
                int rw = Math.Min(fullBitmap.Width - rx, (int)Math.Ceiling(texRect.Width));
                int rh = Math.Min(fullBitmap.Height, (int)Math.Ceiling(texRect.Height));
                int ry = Math.Max(0, fullBitmap.Height - (int)texRect.Y - rh);

                if (rw > 0 && rh > 0 && (rw < fullBitmap.Width || rh < fullBitmap.Height))
                {
                    var cropped = fullBitmap.Crop(rx..(rx + rw), ry..(ry + rh));
                    data = GenerateThumbnailPng(cropped);
                }
                else
                {
                    data = GenerateThumbnailPng(fullBitmap);
                }
                ext = ".png";
                mime2 = "image/png";
                break;

            case "AudioClip" when found is IAudioClip audioClip:
                if (!AudioClipDecoder.TryDecode(audioClip, out var audioData, out var audioExt, out var decodeMsg))
                {
                    _log.Warning($"Preview: failed to decode AudioClip: {decodeMsg}");
                    return null;
                }
                ext = audioExt.StartsWith('.') ? audioExt : "." + audioExt;
                mime2 = ext switch
                {
                    ".ogg" => "audio/ogg",
                    ".wav" => "audio/wav",
                    _ => "application/octet-stream",
                };
                data = audioData;
                break;

            case "GameObject" or "PrefabHierarchyObject" or "Mesh":
                data = GenerateModelGlb(found);
                if (data.Length == 0) return null;
                ext = ".glb";
                mime2 = "model/gltf-binary";
                break;

            default:
                _log.Warning($"Preview: unsupported className={className} or type mismatch (found={found.GetType().Name})");
                return null;
        }

        // Cache to disk
        var cachePath = BuildPreviewCachePath(collection, pathId, ext);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllBytes(cachePath, data);

        return new AssetPreviewResult(data, mime2, ext);
    }

    private static IUnityObjectBase? FindAsset(GameData gameData, string collection, long pathId)
    {
        foreach (var col in gameData.GameBundle.FetchAssetCollections())
        {
            if (col.Name != collection) continue;
            return col.FirstOrDefault(a => a.PathID == pathId);
        }
        return null;
    }

    private static byte[] GenerateThumbnailPng(DirectBitmap bitmap)
    {
        if (bitmap.Width <= ThumbnailMaxDimension && bitmap.Height <= ThumbnailMaxDimension)
        {
            using var ms = new MemoryStream();
            bitmap.SaveAsPng(ms);
            return ms.ToArray();
        }

        int srcW = bitmap.Width;
        int srcH = bitmap.Height;
        float scale = Math.Min((float)ThumbnailMaxDimension / srcW, (float)ThumbnailMaxDimension / srcH);
        int dstW = Math.Max(1, (int)(srcW * scale));
        int dstH = Math.Max(1, (int)(srcH * scale));

        byte[] rgba = bitmap.ToRgba32();
        byte[] scaled = BoxFilterDownscale(rgba, srcW, srcH, dstW, dstH);

        using var ms2 = new MemoryStream();
        var writer = new StbImageWriteSharp.ImageWriter();
        writer.WritePng(scaled, dstW, dstH, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms2);
        return ms2.ToArray();
    }

    private byte[] GenerateModelGlb(IUnityObjectBase asset)
    {
        SceneBuilder? scene = null;

        if (asset is IGameObject go)
        {
            var root = go.GetRoot();
            var assets = root.FetchHierarchy().Cast<IUnityObjectBase>();
            scene = GlbLevelBuilder.Build(assets, false);
        }
        else if (asset is PrefabHierarchyObject pho)
        {
            scene = GlbLevelBuilder.Build(pho.Assets, false);
        }
        else if (asset is IMesh mesh)
        {
            scene = GlbMeshBuilder.Build(mesh);
        }

        if (scene is null) return [];

        using var ms = new MemoryStream();
        if (GlbWriter.TryWrite(scene, ms, out string? errorMessage))
            return ms.ToArray();

        _log.Warning($"Preview: GLB write failed: {errorMessage}");
        return [];
    }

    private string BuildPreviewCachePath(string collection, long pathId, string extension)
    {
        var safeCollection = SanitizeAssetPathSegment(collection);
        return Path.Combine(CachePath, PreviewDirName, $"{safeCollection}--{pathId}{extension}");
    }

    private string? FindCachedPreview(string collection, long pathId)
    {
        var safeCollection = SanitizeAssetPathSegment(collection);
        var dir = Path.Combine(CachePath, PreviewDirName);
        if (!Directory.Exists(dir)) return null;
        var pattern = $"{safeCollection}--{pathId}.*";
        var files = Directory.GetFiles(dir, pattern);
        return files.Length > 0 ? files[0] : null;
    }

    private static string MimeTypeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".glb" => "model/gltf-binary",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// Area-average (box filter) downscale of RGBA32 pixel data.
    /// </summary>
    private static byte[] BoxFilterDownscale(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
    {
        byte[] result = new byte[dstW * dstH * 4];
        float xScale = (float)srcW / dstW;
        float yScale = (float)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            int srcY0 = (int)(dy * yScale);
            int srcY1 = Math.Min((int)((dy + 1) * yScale), srcH);
            if (srcY1 <= srcY0) srcY1 = srcY0 + 1;

            for (int dx = 0; dx < dstW; dx++)
            {
                int srcX0 = (int)(dx * xScale);
                int srcX1 = Math.Min((int)((dx + 1) * xScale), srcW);
                if (srcX1 <= srcX0) srcX1 = srcX0 + 1;

                int r = 0, g = 0, b = 0, a = 0, count = 0;
                for (int sy = srcY0; sy < srcY1; sy++)
                {
                    for (int sx = srcX0; sx < srcX1; sx++)
                    {
                        int si = (sy * srcW + sx) * 4;
                        r += rgba[si];
                        g += rgba[si + 1];
                        b += rgba[si + 2];
                        a += rgba[si + 3];
                        count++;
                    }
                }

                int di = (dy * dstW + dx) * 4;
                result[di] = (byte)(r / count);
                result[di + 1] = (byte)(g / count);
                result[di + 2] = (byte)(b / count);
                result[di + 3] = (byte)(a / count);
            }
        }

        return result;
    }

}
