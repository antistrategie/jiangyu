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
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Jiangyu.Compiler.Services;

public sealed class ExtractionService
{
    private const string IndexFileName = "asset-index.json";
    private const string ManifestFileName = "index-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string GameDataPath { get; }
    public string CachePath { get; }

    public ExtractionService(string gameDataPath, string cachePath)
    {
        GameDataPath = gameDataPath;
        CachePath = cachePath;
    }

    /// <summary>
    /// Checks whether the index is current by comparing the GameAssembly hash.
    /// </summary>
    public bool IsIndexCurrent()
    {
        var manifestPath = Path.Combine(CachePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var manifest = JsonSerializer.Deserialize<IndexManifest>(
            File.ReadAllText(manifestPath), JsonOptions);
        if (manifest is null)
        {
            return false;
        }

        var currentHash = ComputeGameAssemblyHash();
        return currentHash is not null && currentHash == manifest.GameAssemblyHash;
    }

    /// <summary>
    /// Loads game data via AssetRipper, builds a searchable asset index, and writes it to the cache.
    /// No asset files are exported — only metadata. Always rebuilds from scratch.
    /// </summary>
    public void BuildIndex()
    {
        Console.WriteLine($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        var progress = new ProgressLogger();
        Logger.Add(progress);

        AssetIndex index;
        try
        {
            progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                progress.Finish();
                Console.Error.WriteLine("Error: no asset collections found in game data.");
                return;
            }

            progress.Finish();

            int collectionCount = gameData.GameBundle.FetchAssetCollections().Count();
            Console.WriteLine($"Loaded {collectionCount} asset collections");

            progress.SetPhase("Processing");
            RunProcessors(gameData);
            progress.Finish();

            progress.SetPhase("Building index");
            index = BuildAssetIndex(gameData);
            progress.Finish();
        }
        finally
        {
            Logger.Remove(progress);
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

        Console.WriteLine($"Indexed {index.Assets?.Count ?? 0} assets to: {CachePath}");
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
        Console.WriteLine($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;

        var progress = new ProgressLogger();
        Logger.Add(progress);

        try
        {
            progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([GameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                progress.Finish();
                Console.Error.WriteLine("Error: no asset collections found in game data.");
                return;
            }

            progress.Finish();

            progress.SetPhase("Processing");
            RunProcessors(gameData);
            progress.Finish();

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
                Console.WriteLine($"Found GameObject: {gameObject.Name}");
                ExportGameObjectPackage(gameObject, packageDir, clean);
                return;
            }

            if (found is IMesh mesh)
            {
                Console.WriteLine($"Found Mesh: {mesh.Name}");
                Directory.CreateDirectory(packageDir);
                var glbPath = Path.Combine(packageDir, "model.glb");
                ExportMeshAsGlb(mesh, glbPath);
                return;
            }

            Console.Error.WriteLine($"Error: no GameObject or Mesh named '{assetName}' found.");
        }
        finally
        {
            Logger.Remove(progress);
        }
    }

    /// <summary>
    /// MENACE material property → standard glTF PBR channel.
    /// Only properties with genuine glTF PBR equivalents are mapped here.
    /// MENACE-specific properties (_MaskMap, _Effect_Map) are NOT mapped — they go to extras.
    /// </summary>
    private static readonly Dictionary<string, string> StandardChannelMap = new(StringComparer.OrdinalIgnoreCase)
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

    private sealed class DiscoveredTexture
    {
        public required string Name { get; init; }
        public required string MaterialName { get; init; }
        /// <summary>Stable source identity: "collection:pathId". Matches extras.jiangyu.sourceMaterial in the GLB.</summary>
        public required string SourceMaterialId { get; init; }
        public required string Property { get; init; }
        public required byte[] PngData { get; init; }
    }

    private static void ExportGameObjectPackage(IGameObject gameObject, string packageDir, bool clean)
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
                    Console.Error.WriteLine($"Error writing GLB: {errorMessage}");
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

            var cleanScene = ModelCleanupService.BuildCleanScene(tempGlbPath, materialTextures);
            File.Delete(tempGlbPath);
            Console.WriteLine("  Cleaned: 1x authoring scale");

            // Build .gltf — standard textures already attached at MaterialBuilder level,
            // SaveGltfPackage only handles non-standard textures + extras
            var gltfPath = Path.Combine(packageDir, "model.gltf");
            SaveGltfPackage(cleanScene, textures, gltfPath);

            if (textures.Count > 0)
            {
                Console.WriteLine($"  Exported {textures.Count} textures");
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
                    Console.WriteLine($"  Model: {glbPath}");
                }
                else
                {
                    Console.Error.WriteLine($"Error writing GLB: {errorMessage}");
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
                Console.WriteLine($"  Exported {textures.Count} textures");
            }
        }

        Console.WriteLine($"Package: {packageDir}");
    }

    /// <summary>
    /// Walks the source asset hierarchy, resolves material → texture references,
    /// and returns discovered textures with their PNG data in memory.
    /// </summary>
    private static List<DiscoveredTexture> DiscoverTextures(IGameObject root)
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
                    var texture = texEnv.Texture.TryGetAsset(material.Collection) as ITexture2D;
                    if (texture is null || string.IsNullOrEmpty(texture.Name))
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

                    Console.WriteLine($"  Texture: {texture.Name}.png ({propName})");
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
    private static void SaveGltfPackage(SceneBuilder scene, List<DiscoveredTexture> textures, string gltfPath)
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
                var materialExtras = material.Extras as JsonObject ?? new JsonObject();
                var jiangyuObj = materialExtras["jiangyu"] as JsonObject ?? new JsonObject();
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
        var settings = new WriteSettings();
        settings.ImageWriting = ResourceWriteMode.SatelliteFile;
        model.SaveGLTF(gltfPath, settings);
    }

    private static void ExportMeshAsGlb(IMesh mesh, string outputPath)
    {
        SceneBuilder sceneBuilder = GlbMeshBuilder.Build(mesh);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        if (GlbWriter.TryWrite(sceneBuilder, stream, out string? errorMessage))
        {
            Console.WriteLine($"Exported to: {outputPath}");
        }
        else
        {
            Console.Error.WriteLine($"Error writing GLB: {errorMessage}");
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
                    ClassName = asset.ClassName,
                    ClassId = asset.ClassID,
                    PathId = asset.PathID,
                    Collection = collectionName,
                });
            }
        }

        return new AssetIndex { Assets = entries };
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

    public sealed class AssetIndex
    {
        [JsonPropertyName("assets")]
        public List<AssetEntry>? Assets { get; set; }
    }

    public sealed class AssetEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("className")]
        public string? ClassName { get; set; }

        [JsonPropertyName("classId")]
        public int ClassId { get; set; }

        [JsonPropertyName("pathId")]
        public long PathId { get; set; }

        [JsonPropertyName("collection")]
        public string? Collection { get; set; }
    }

    private sealed class IndexManifest
    {
        [JsonPropertyName("gameAssemblyHash")]
        public string? GameAssemblyHash { get; set; }

        [JsonPropertyName("indexedAt")]
        public DateTimeOffset IndexedAt { get; set; }

        [JsonPropertyName("gameDataPath")]
        public string? GameDataPath { get; set; }

        [JsonPropertyName("assetCount")]
        public int AssetCount { get; set; }
    }
}
