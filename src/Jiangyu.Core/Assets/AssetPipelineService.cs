using System.Security.Cryptography;
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
using AssetRipper.Export.Modules.Textures;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Assets;

public sealed class AssetPipelineService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private const string IndexFileName = "asset-index.json";
    private const string ManifestFileName = "index-manifest.json";

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
        if (currentHash is null || currentHash != manifest.GameAssemblyHash)
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
        _log.Info($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);

        AssetIndex index;
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

            int collectionCount = gameData.GameBundle.FetchAssetCollections().Count();
            _log.Info($"Loaded {collectionCount} asset collections");

            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            _progress.SetPhase("Building index");
            index = BuildAssetIndex(gameData);
            _progress.Finish();
        }
        finally
        {
            Logger.Remove(adapter);
        }

        // Write index
        Directory.CreateDirectory(CachePath);
        File.WriteAllText(
            Path.Combine(CachePath, IndexFileName),
            JsonSerializer.Serialize(index, JsonOptions));

        // Write manifest
        var manifest = new IndexManifest
        {
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

    private void ExportGameObjectPackage(IGameObject gameObject, string packageDir, bool clean)
    {
        Directory.CreateDirectory(packageDir);

        IGameObject root = gameObject.GetRoot();
        var assets = root.FetchHierarchy().Cast<IUnityObjectBase>();
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

            // Build .gltf — standard textures already attached at MaterialBuilder level,
            // SaveGltfPackage only handles non-standard textures + extras
            var gltfPath = Path.Combine(packageDir, "model.gltf");
            SaveGltfPackage(cleanScene, textures, gltfPath);

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
    internal static void SaveGltfPackage(SceneBuilder scene, List<DiscoveredTexture> textures, string gltfPath)
    {
        var model = scene.ToGltf2();

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
        var materialsBySourceId = new Dictionary<string, SharpGLTF.Schema2.Material>(StringComparer.Ordinal);
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
    }

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
                entries.Add(new AssetEntry
                {
                    Name = asset.GetBestName(),
                    CanonicalPath = BuildCanonicalAssetPath(collectionName, asset.ClassName, asset.GetBestName(), asset.PathID),
                    ClassName = asset.ClassName,
                    ClassId = asset.ClassID,
                    PathId = asset.PathID,
                    Collection = collectionName,
                });
            }
        }

        return new AssetIndex { Assets = entries };
    }

    private static string BuildCanonicalAssetPath(string? collectionName, string? className, string? assetName, long pathId)
    {
        var collectionSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(collectionName) ? "unknown-collection" : collectionName);
        var classSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(className) ? "UnknownClass" : className);
        var nameSegment = SanitizeAssetPathSegment(string.IsNullOrWhiteSpace(assetName) ? "unnamed" : assetName);
        return $"{collectionSegment}/{classSegment}/{nameSegment}--{pathId}";
    }

    private static string SanitizeAssetPathSegment(string value)
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

    private static void RunProcessors(GameData gameData)
    {
        IAssetProcessor[] processors =
        [
            new SceneDefinitionProcessor(),
            new MainAssetProcessor(),
            new AnimatorControllerProcessor(),
            new PrefabProcessor(),
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

}
