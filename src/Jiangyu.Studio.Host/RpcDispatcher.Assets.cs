using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Processing;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host;

public static partial class RpcDispatcher
{
    /// <summary>
    /// Cached game data from the most recent index build or lazy preview load.
    /// Kept in memory so that successive preview requests don't re-load the full
    /// game data each time (~30-60 s cold start).
    /// </summary>
    private static readonly Lock _gameDataLock = new();
    private static GameData? _cachedGameData;
    private static string? _cachedGameDataPath;

    private static GameData EnsureGameData(AssetPipelineService service, string gameDataPath)
    {
        lock (_gameDataLock)
        {
            if (_cachedGameData is not null &&
                string.Equals(_cachedGameDataPath, gameDataPath, StringComparison.Ordinal))
            {
                return _cachedGameData;
            }

            var gd = service.LoadAndProcessGameData();
            _cachedGameData = gd;
            _cachedGameDataPath = gameDataPath;
            return gd;
        }
    }

    /// <summary>
    /// Replaces the cached game data (e.g. after a fresh index build).
    /// </summary>
    private static void SetCachedGameData(GameData gameData, string gameDataPath)
    {
        lock (_gameDataLock)
        {
            _cachedGameData = gameData;
            _cachedGameDataPath = gameDataPath;
        }
    }

    private static JsonElement HandleAssetsIndexStatus(IInfiniFrameWindow _, JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            return JsonSerializer.SerializeToElement(new AssetIndexStatus
            {
                State = "noGame",
                Reason = resolution.Error,
            });
        }

        var service = resolution.Context!.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var status = service.GetIndexStatus();
        var manifest = service.LoadManifest();

        return JsonSerializer.SerializeToElement(new AssetIndexStatus
        {
            State = status.State switch
            {
                CachedIndexState.Current => "current",
                CachedIndexState.Stale => "stale",
                CachedIndexState.Missing => "missing",
                _ => "missing",
            },
            Reason = status.Reason,
            AssetCount = manifest?.AssetCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    private static JsonElement HandleAssetsIndex(IInfiniFrameWindow _, JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var ctx = resolution.Context!;
        var service = ctx.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);

        // Load game data, build the index, and keep the loaded data in the
        // static cache so subsequent preview requests can reuse it.
        var gameData = service.LoadAndProcessGameData();
        SetCachedGameData(gameData, ctx.GameDataPath);
        service.BuildIndexFromGameData(gameData);

        var manifest = service.LoadManifest();
        return JsonSerializer.SerializeToElement(new AssetIndexStatus
        {
            State = "current",
            AssetCount = manifest?.AssetCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    private static JsonElement HandleAssetsSearch(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var query = TryGetString(parameters, "query");
        var kind = TryGetString(parameters, "kind");
        var collection = TryGetString(parameters, "collection");
        var limit = TryGetInt(parameters, "limit") ?? 500;

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var results = service.Search(query, kind, limit);

        if (!string.IsNullOrEmpty(collection))
        {
            results = [.. results.Where(r => string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase))];
        }

        return JsonSerializer.SerializeToElement(results);
    }

    private static JsonElement HandleAssetsExport(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var assetName = RequireString(parameters, "assetName");
        var collection = RequireString(parameters, "collection");
        var pathId = RequireLong(parameters, "pathId");
        var kind = RequireString(parameters, "kind");
        var baseDir = RequireString(parameters, "baseDir");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);

        string outputPath;
        switch (kind)
        {
            case "GameObject":
            case "PrefabHierarchyObject":
            case "Mesh":
                {
                    // Models always export into a per-asset package directory.
                    var packageDir = Path.Combine(baseDir, AssetPipelineService.SanitizeAssetPathSegment(assetName));
                    Directory.CreateDirectory(packageDir);
                    service.ExportModel(assetName, packageDir, clean: true, collection: collection, pathId: pathId);
                    outputPath = Path.Combine(packageDir, "model.gltf");
                    if (!File.Exists(outputPath))
                        outputPath = Path.Combine(packageDir, "model.glb");
                    break;
                }
            case "Texture2D":
                {
                    Directory.CreateDirectory(baseDir);
                    var file = Path.Combine(baseDir, $"{AssetPipelineService.SanitizeAssetPathSegment(assetName)}.png");
                    if (!service.ExportTexture(assetName, file, collection, pathId))
                        throw new InvalidOperationException($"Failed to export texture '{assetName}'.");
                    outputPath = file;
                    break;
                }
            case "Sprite":
                {
                    Directory.CreateDirectory(baseDir);
                    var file = Path.Combine(baseDir, $"{AssetPipelineService.SanitizeAssetPathSegment(assetName)}.png");
                    if (!service.ExportSprite(assetName, file, collection, pathId))
                        throw new InvalidOperationException($"Failed to export sprite '{assetName}'.");
                    outputPath = file;
                    break;
                }
            case "AudioClip":
                {
                    Directory.CreateDirectory(baseDir);
                    outputPath = service.ExportAudio(assetName, baseDir, collection, pathId)
                        ?? throw new InvalidOperationException($"Failed to export audio '{assetName}'.");
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported asset kind: {kind}");
        }

        return JsonSerializer.SerializeToElement(new AssetExportResult { OutputPath = outputPath });
    }

    private static JsonElement HandleAssetsPreview(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var collection = RequireString(parameters, "collection");
        var pathId = RequireLong(parameters, "pathId");
        var className = RequireString(parameters, "className");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            return NullElement;

        var ctx = resolution.Context!;
        var service = ctx.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var gameData = EnsureGameData(service, ctx.GameDataPath);
        var result = service.GeneratePreview(gameData, collection, pathId, className);

        if (result is null)
            return NullElement;

        return JsonSerializer.SerializeToElement(new AssetPreviewResult
        {
            Data = Convert.ToBase64String(result.Data),
            MimeType = result.MimeType,
        });
    }

    private static JsonElement HandlePickDirectory(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var title = TryGetString(parameters, "title") ?? "Select directory";
        var initial = TryGetString(parameters, "initial");
        var results = window.ShowOpenFolder(title, defaultPath: initial);
        var path = results.FirstOrDefault(p => p is not null);
        return JsonSerializer.SerializeToElement(path);
    }

    [RpcType]
    internal sealed class AssetIndexStatus
    {
        [JsonPropertyName("state")]
        public required string State { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("assetCount")]
        public int? AssetCount { get; set; }

        [JsonPropertyName("indexedAt")]
        public DateTimeOffset? IndexedAt { get; set; }
    }

    [RpcType]
    internal sealed class AssetExportResult
    {
        [JsonPropertyName("outputPath")]
        public required string OutputPath { get; set; }
    }

    [RpcType]
    internal sealed class AssetPreviewResult
    {
        [JsonPropertyName("data")]
        public required string Data { get; set; }

        [JsonPropertyName("mimeType")]
        public required string MimeType { get; set; }
    }
}
