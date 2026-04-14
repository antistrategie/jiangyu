using AssetRipper.Assets;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public sealed class ObjectInspectionService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    public string GameDataPath { get; } = gameDataPath;
    public string CachePath { get; } = cachePath;

    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    public ObjectResolutionResult Resolve(ObjectInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Collection) && request.PathId is long pathId)
        {
            return new ObjectResolutionResult
            {
                Status = ObjectResolutionStatus.Success,
                Resolved = new ResolvedObjectCandidate
                {
                    Name = request.Name ?? "(unresolved)",
                    ClassName = request.ClassName ?? "Unknown",
                    Collection = request.Collection,
                    PathId = pathId,
                },
            };
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Name resolution requires a request name.");
        }

        var pipeline = new AssetPipelineService(GameDataPath, CachePath, _progress, _log);
        var resolver = new ObjectIdentityResolver(pipeline.LoadIndex());
        return resolver.Resolve(request.Name, request.ClassName);
    }

    public ObjectInspectionResult Inspect(ObjectInspectionRequest request, ResolvedObjectCandidate? resolved = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        string collectionName = resolved?.Collection
            ?? request.Collection
            ?? throw new InvalidOperationException("Inspection requires a resolved collection.");

        long pathId = resolved?.PathId
            ?? request.PathId
            ?? throw new InvalidOperationException("Inspection requires a resolved path ID.");

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
                throw new InvalidOperationException("No asset collections found in game data.");
            }

            _progress.Finish();

            _progress.SetPhase("Processing");
            RunProcessors(gameData);
            _progress.Finish();

            IUnityObjectBase asset = FindAsset(gameData, collectionName, pathId)
                ?? throw new InvalidOperationException($"No asset found in collection '{collectionName}' with pathId {pathId}.");

            ObjectFieldInspection inspection = ObjectFieldInspector.Inspect(asset, request.MaxDepth, request.MaxArraySampleLength);

            return new ObjectInspectionResult
            {
                Object = new InspectedObjectIdentity
                {
                    Name = asset.GetBestName(),
                    ClassName = asset.ClassName,
                    Collection = asset.Collection.Name,
                    PathId = asset.PathID,
                },
                Options = new ObjectInspectionOptions
                {
                    MaxDepth = request.MaxDepth,
                    MaxArraySampleLength = request.MaxArraySampleLength,
                    Truncated = inspection.Truncated,
                },
                Fields = inspection.Fields,
            };
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    private static IUnityObjectBase? FindAsset(GameData gameData, string collectionName, long pathId)
    {
        foreach (var collection in gameData.GameBundle.FetchAssetCollections())
        {
            if (!string.Equals(collection.Name, collectionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (collection.TryGetAsset(pathId, out IUnityObjectBase? asset))
            {
                return asset;
            }
        }

        return null;
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
}
