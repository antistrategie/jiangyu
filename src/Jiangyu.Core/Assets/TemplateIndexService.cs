using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public sealed class TemplateIndexService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private const string IndexFileName = "template-index.json";
    private const string ManifestFileName = "template-index-manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string GameDataPath { get; } = gameDataPath;
    public string CachePath { get; } = cachePath;

    private readonly IProgressSink _progress = progress;
    private readonly ILogSink _log = log;

    public bool IsIndexCurrent()
    {
        return GetIndexStatus().IsCurrent;
    }

    public CachedIndexStatus GetIndexStatus()
    {
        string indexPath = Path.Combine(CachePath, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Missing,
                Reason = "Template index not found. Run 'jiangyu templates index' first.",
            };
        }

        TemplateIndexManifest? manifest = LoadManifest();
        if (manifest is null)
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = "Template index manifest is unreadable. Rebuild it with 'jiangyu templates index'.",
            };
        }

        TemplateClassificationMetadata classification = TemplateClassifier.GetMetadata();
        string? currentHash = ComputeGameAssemblyHash();

        if (currentHash is null
            || !string.Equals(currentHash, manifest.GameAssemblyHash, StringComparison.Ordinal)
            || !string.Equals(manifest.RuleVersion, classification.RuleVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.RuleDescription, classification.RuleDescription, StringComparison.Ordinal))
        {
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = "Template index is missing or stale for the current game version. Run 'jiangyu templates index' first.",
            };
        }

        return new CachedIndexStatus
        {
            State = CachedIndexState.Current,
        };
    }

    public void BuildIndex()
    {
        _log.Info($"Loading game data from: {GameDataPath}");

        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;

        var adapter = new AssetRipperProgressAdapter(_progress);
        Logger.Add(adapter);

        TemplateIndex index;
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

            _progress.SetPhase("Building template index");
            index = BuildTemplateIndex(gameData.GameBundle.FetchAssetCollections(), gameData.AssemblyManager);
            _progress.Finish();
        }
        finally
        {
            Logger.Remove(adapter);
        }

        Directory.CreateDirectory(CachePath);
        File.WriteAllText(
            Path.Combine(CachePath, IndexFileName),
            JsonSerializer.Serialize(index, JsonOptions));

        TemplateClassificationMetadata classification = TemplateClassifier.GetMetadata();
        var manifest = new TemplateIndexManifest
        {
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = GameDataPath,
            RuleVersion = classification.RuleVersion,
            RuleDescription = classification.RuleDescription,
            TemplateTypeCount = index.TemplateTypes.Count,
            InstanceCount = index.Instances.Count,
        };
        File.WriteAllText(
            Path.Combine(CachePath, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));

        _log.Info($"Indexed {index.Instances.Count} template instances across {index.TemplateTypes.Count} template types to: {CachePath}");
    }

    public TemplateIndex? LoadIndex()
    {
        string indexPath = Path.Combine(CachePath, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TemplateIndex>(File.ReadAllText(indexPath), JsonOptions);
    }

    public TemplateIndexManifest? LoadManifest()
    {
        string manifestPath = Path.Combine(CachePath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TemplateIndexManifest>(File.ReadAllText(manifestPath), JsonOptions);
    }

    internal static TemplateIndex BuildTemplateIndex(IEnumerable<AssetCollection> collections, IAssemblyManager? assemblyManager)
    {
        var instances = new List<TemplateInstanceEntry>();

        // Track classification method per concrete class name so we can aggregate to TemplateTypeEntry.
        var classificationByType = new Dictionary<string, (string ClassifiedVia, string? Ancestor)>(StringComparer.OrdinalIgnoreCase);

        foreach (AssetCollection collection in collections)
        {
            string collectionName = collection.Name;
            foreach (IUnityObjectBase asset in collection)
            {
                // Fast path: suffix match.
                if (TemplateClassifier.TryGetTemplateClassName(asset, out string? templateClassName))
                {
                    instances.Add(new TemplateInstanceEntry
                    {
                        Name = asset.GetBestName(),
                        ClassName = templateClassName!,
                        Identity = new TemplateIdentity
                        {
                            Collection = collectionName,
                            PathId = asset.PathID,
                        },
                    });
                    classificationByType.TryAdd(templateClassName!, ("suffix", null));
                    continue;
                }

                // Inheritance fallback: walk managed type hierarchy for a Template-named ancestor.
                if (assemblyManager is not null
                    && asset is IMonoBehaviour monoBehaviour
                    && TemplateClassifier.TryGetInheritedTemplateClassName(
                        monoBehaviour, assemblyManager, out string? inheritedClassName, out string? ancestorClassName))
                {
                    instances.Add(new TemplateInstanceEntry
                    {
                        Name = asset.GetBestName(),
                        ClassName = inheritedClassName!,
                        Identity = new TemplateIdentity
                        {
                            Collection = collectionName,
                            PathId = asset.PathID,
                        },
                    });
                    classificationByType.TryAdd(inheritedClassName!, ("inheritance", ancestorClassName));
                }
            }
        }

        instances = [.. instances
            .OrderBy(instance => instance.ClassName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.PathId)];

        var templateTypes = instances
            .GroupBy(instance => instance.ClassName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var (classifiedVia, ancestor) = classificationByType.TryGetValue(group.Key, out var info)
                    ? info
                    : ("suffix", (string?)null);

                return new TemplateTypeEntry
                {
                    ClassName = group.Key,
                    Count = group.Count(),
                    ClassifiedVia = classifiedVia,
                    TemplateAncestor = ancestor,
                };
            })
            .OrderBy(entry => entry.ClassName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = templateTypes,
            Instances = instances,
        };
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

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            using var stream = File.OpenRead(candidate);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        return null;
    }
}
