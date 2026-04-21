using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Studio.Host;

public static partial class RpcDispatcher
{
    private static JsonElement HandleAssetsIndexStatus(IInfiniFrameWindow _, JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            return JsonSerializer.SerializeToElement(new AssetIndexStatusDto
            {
                State = "noGame",
                Reason = resolution.Error,
            });
        }

        var service = resolution.Context!.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var status = service.GetIndexStatus();
        var manifest = service.LoadManifest();

        return JsonSerializer.SerializeToElement(new AssetIndexStatusDto
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

        var service = resolution.Context!.CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        service.BuildIndex();
        var manifest = service.LoadManifest();
        return JsonSerializer.SerializeToElement(new AssetIndexStatusDto
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
                    if (!service.ExportAudio(assetName, baseDir, collection, pathId))
                        throw new InvalidOperationException($"Failed to export audio '{assetName}'.");
                    // ExportAudio picks the file extension at decode time and writes to
                    // Path.Combine(baseDir, "{assetName}{ext}"). Match by stem without
                    // sanitising, since the file on disk uses the raw assetName.
                    outputPath = Directory.EnumerateFiles(baseDir)
                        .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), assetName, StringComparison.Ordinal))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                        ?? throw new InvalidOperationException($"Exported audio file not found for '{assetName}'.");
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported asset kind: {kind}");
        }

        return JsonSerializer.SerializeToElement(new AssetExportResultDto { OutputPath = outputPath });
    }

    private static JsonElement HandlePickDirectory(IInfiniFrameWindow window, JsonElement? parameters)
    {
        var title = TryGetString(parameters, "title") ?? "Select directory";
        var initial = TryGetString(parameters, "initial");
        var results = window.ShowOpenFolder(title, defaultPath: initial);
        var path = results.FirstOrDefault(p => p is not null);
        return JsonSerializer.SerializeToElement(path);
    }

    internal sealed class AssetIndexStatusDto
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

    internal sealed class AssetExportResultDto
    {
        [JsonPropertyName("outputPath")]
        public required string OutputPath { get; set; }
    }
}
