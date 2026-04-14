using System.Security.Cryptography;
using System.Text.Json;
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
using SharpGLTF.Scenes;

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
    /// Package layout:
    ///   {packageDir}/model.glb
    ///   {packageDir}/jiangyu.export.json
    ///   {packageDir}/textures/*.png
    /// </summary>
    /// <param name="collection">If specified, restricts the search to this collection (from index).</param>
    /// <param name="pathId">If specified, finds the exact asset by PathID (from index).</param>
    public void ExportModel(string assetName, string packageDir, bool clean = true, string? collection = null, long? pathId = null)
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

            // Find the asset — prefer stable identity (collection + pathId) over name
            IUnityObjectBase? found = null;

            if (pathId is not null && collection is not null)
            {
                // Exact lookup by indexed identity
                foreach (var col in gameData.GameBundle.FetchAssetCollections())
                {
                    if (col.Name != collection)
                    {
                        continue;
                    }

                    found = col.FirstOrDefault(a => a.PathID == pathId.Value);
                    break;
                }
            }

            if (found is null)
            {
                // Fallback to name matching (for manual CLI use without index)
                var allAssets = gameData.GameBundle.FetchAssets();
                found = allAssets.OfType<IGameObject>()
                    .FirstOrDefault(go => string.Equals(go.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    as IUnityObjectBase
                    ?? allAssets.OfType<IMesh>()
                    .FirstOrDefault(m => string.Equals(m.Name, assetName, StringComparison.OrdinalIgnoreCase));
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

    private static void ExportGameObjectPackage(IGameObject gameObject, string packageDir, bool clean)
    {
        Directory.CreateDirectory(packageDir);

        // Export GLB
        IGameObject root = gameObject.GetRoot();
        var assets = root.FetchHierarchy().Cast<IUnityObjectBase>();
        SceneBuilder sceneBuilder = GlbLevelBuilder.Build(assets, false);

        var glbPath = Path.Combine(packageDir, "model.glb");
        using (var stream = File.Create(glbPath))
        {
            if (GlbWriter.TryWrite(sceneBuilder, stream, out string? errorMessage))
            {
                Console.WriteLine($"  Model: {glbPath}");
            }
            else
            {
                Console.Error.WriteLine($"Error writing GLB: {errorMessage}");
                return;
            }
        }

        if (clean)
        {
            ModelCleanupService.CleanupGlb(glbPath);
            Console.WriteLine("  Cleaned: 1x authoring scale");
        }

        // Export referenced textures and write package manifest
        ExportReferencedTextures(root, packageDir, clean);
        Console.WriteLine($"Package: {packageDir}");
    }

    /// <summary>
    /// Walks the hierarchy, resolves material → texture references, exports textures as PNGs,
    /// and writes a package manifest. All paths in the manifest are relative to the package directory.
    /// </summary>
    private static void ExportReferencedTextures(IGameObject root, string packageDir, bool clean)
    {
        var texturesDir = Path.Combine(packageDir, "textures");
        var exportedNames = new HashSet<string>();
        var textureEntries = new List<ExportManifestTexture>();

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

                    if (!exportedNames.Add(texture.Name))
                    {
                        continue;
                    }

                    var relativePath = $"textures/{texture.Name}.png";
                    if (TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
                    {
                        Directory.CreateDirectory(texturesDir);
                        using var texStream = File.Create(Path.Combine(packageDir, relativePath));
                        bitmap.SaveAsPng(texStream);
                        Console.WriteLine($"  Texture: {texture.Name}.png ({propName})");
                    }

                    textureEntries.Add(new ExportManifestTexture
                    {
                        Name = texture.Name,
                        Property = propName.ToString(),
                        Path = relativePath,
                    });
                }
            }
        }

        if (textureEntries.Count > 0)
        {
            // Derive texture prefix from the common prefix of all exported texture names
            var texturePrefix = FindCommonPrefix(exportedNames);

            var manifest = new ExportManifest
            {
                TexturePrefix = texturePrefix,
                Textures = textureEntries,
                Cleaned = clean,
            };

            var manifestPath = Path.Combine(packageDir, "jiangyu.export.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            Console.WriteLine($"Exported {textureEntries.Count} textures, manifest: {Path.GetFileName(manifestPath)}");
        }
    }

    private static string? FindCommonPrefix(HashSet<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var sorted = names.OrderBy(n => n.Length).ToList();
        var shortest = sorted[0];

        for (int len = shortest.Length; len > 0; len--)
        {
            var candidate = shortest[..len];
            // Trim trailing underscore or separator
            if (candidate.EndsWith('_'))
            {
                candidate = candidate[..^1];
            }

            if (sorted.All(n => n.StartsWith(candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        return null;
    }

    public sealed class ExportManifest
    {
        [JsonPropertyName("texturePrefix")]
        public string? TexturePrefix { get; set; }

        [JsonPropertyName("textures")]
        public List<ExportManifestTexture>? Textures { get; set; }

        /// <summary>
        /// True if vertices have been normalised to metre scale (authoring-ready).
        /// False/absent for raw extraction with native cm-scale vertices.
        /// </summary>
        [JsonPropertyName("cleaned")]
        public bool Cleaned { get; set; }
    }

    public sealed class ExportManifestTexture
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("property")]
        public string? Property { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }
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
