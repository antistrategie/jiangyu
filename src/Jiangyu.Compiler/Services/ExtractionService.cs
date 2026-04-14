using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Assets;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;

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
