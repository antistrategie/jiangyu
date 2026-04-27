using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
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
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public sealed class TemplateIndexService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private const string IndexFileName = "template-index.json";
    private const string ManifestFileName = "template-index-manifest.json";
    private const string ValuesFileName = "template-values.json";
    internal const int CurrentFormatVersion = 3;
    private const int ValuesInspectDepth = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string GameDataPath { get; } = gameDataPath;
    public string CachePath { get; } = cachePath;

    public string IndexPath => Path.Combine(CachePath, IndexFileName);
    public string ValuesPath => Path.Combine(CachePath, ValuesFileName);

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
            || !string.Equals(manifest.RuleDescription, classification.RuleDescription, StringComparison.Ordinal)
            || manifest.FormatVersion != CurrentFormatVersion
            || IsIl2CppSupplementStale())
        {
            var reason = IsIl2CppSupplementStale()
                ? "IL2CPP metadata supplement is out of date. Rebuild the template index to refresh it."
                : "Template index is missing or stale for the current game version.";
            return new CachedIndexStatus
            {
                State = CachedIndexState.Stale,
                Reason = reason,
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
        Dictionary<string, List<InspectedFieldNode>> values = [];
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

            _progress.SetPhase("Extracting template values");
            values = ExtractTemplateValues(index, gameData);
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

        File.WriteAllText(
            Path.Combine(CachePath, ValuesFileName),
            JsonSerializer.Serialize(values, JsonOptions));

        TemplateClassificationMetadata classification = TemplateClassifier.GetMetadata();
        var manifest = new TemplateIndexManifest
        {
            FormatVersion = CurrentFormatVersion,
            GameAssemblyHash = ComputeGameAssemblyHash(),
            IndexedAt = DateTimeOffset.UtcNow,
            GameDataPath = GameDataPath,
            RuleVersion = classification.RuleVersion,
            RuleDescription = classification.RuleDescription,
            TemplateTypeCount = index.TemplateTypes.Count,
            InstanceCount = index.Instances.Count,
            ValueCount = values.Count,
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

        // Lookup of known template assets by (collection, pathId) for the reference pass.
        var knownTemplates = new HashSet<(string Collection, long PathId)>();

        // Map (collection, pathId) → the IUnityObjectBase for the reference walk.
        var assetByIdentity = new Dictionary<(string Collection, long PathId), IUnityObjectBase>();

        foreach (AssetCollection collection in collections)
        {
            string collectionName = collection.Name;
            foreach (IUnityObjectBase asset in collection)
            {
                // Fast path: suffix match.
                if (TemplateClassifier.TryGetTemplateClassName(asset, out string? templateClassName))
                {
                    var identity = new TemplateIdentity
                    {
                        Collection = collectionName,
                        PathId = asset.PathID,
                    };
                    instances.Add(new TemplateInstanceEntry
                    {
                        Name = asset.GetBestName(),
                        ClassName = templateClassName!,
                        Identity = identity,
                    });
                    classificationByType.TryAdd(templateClassName!, ("suffix", null));
                    knownTemplates.Add((collectionName, asset.PathID));
                    assetByIdentity[(collectionName, asset.PathID)] = asset;
                    continue;
                }

                // Inheritance fallback: walk managed type hierarchy for a Template-named ancestor.
                if (assemblyManager is not null
                    && asset is IMonoBehaviour monoBehaviour
                    && TemplateClassifier.TryGetInheritedTemplateClassName(
                        monoBehaviour, assemblyManager, out string? inheritedClassName, out string? ancestorClassName))
                {
                    var identity = new TemplateIdentity
                    {
                        Collection = collectionName,
                        PathId = asset.PathID,
                    };
                    instances.Add(new TemplateInstanceEntry
                    {
                        Name = asset.GetBestName(),
                        ClassName = inheritedClassName!,
                        Identity = identity,
                    });
                    classificationByType.TryAdd(inheritedClassName!, ("inheritance", ancestorClassName));
                    knownTemplates.Add((collectionName, asset.PathID));
                    assetByIdentity[(collectionName, asset.PathID)] = asset;
                }
            }
        }

        // --- Pass 2: collect template-to-template references ---
        foreach (var instance in instances)
        {
            var key = (instance.Identity.Collection, instance.Identity.PathId);
            if (!assetByIdentity.TryGetValue(key, out var asset))
            {
                continue;
            }

            var collector = new TemplateReferenceCollector(asset.Collection, knownTemplates);
            asset.WalkStandard(collector);

            if (collector.Edges.Count > 0)
            {
                instance.References = [.. collector.Edges
                    .Select(e => new TemplateEdge
                    {
                        FieldName = e.FieldPath,
                        Target = new TemplateIdentity
                        {
                            Collection = e.TargetCollection,
                            PathId = e.TargetPathId,
                        },
                    })];
            }
        }

        // --- Build reverse index ---
        var referencedBy = new Dictionary<string, HashSet<(string Collection, long PathId, string FieldName)>>();
        foreach (var instance in instances)
        {
            if (instance.References is null)
            {
                continue;
            }

            foreach (var edge in instance.References)
            {
                string targetKey = TemplateIndex.IdentityKey(edge.Target);
                if (!referencedBy.TryGetValue(targetKey, out var set))
                {
                    set = [];
                    referencedBy[targetKey] = set;
                }

                set.Add((instance.Identity.Collection, instance.Identity.PathId, edge.FieldName));
            }
        }

        var referencedByLists = referencedBy.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .Select(e => new TemplateReferenceEntry
                {
                    Source = new TemplateIdentity { Collection = e.Collection, PathId = e.PathId },
                    FieldName = e.FieldName,
                })
                .OrderBy(e => e.Source.Collection, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Source.PathId)
                .ToList());

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
                    : ("suffix", null);

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
            ReferencedBy = referencedByLists.Count > 0 ? referencedByLists : null,
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

    /// <summary>
    /// Lightweight <see cref="AssetWalker"/> that collects PPtr references
    /// pointing at known template instances. Ignores primitives and strings.
    /// </summary>
    private sealed class TemplateReferenceCollector(AssetCollection collection, HashSet<(string Collection, long PathId)> knownTemplates) : AssetWalker
    {
        private readonly AssetCollection _collection = collection;
        private readonly HashSet<(string Collection, long PathId)> _knownTemplates = knownTemplates;

        // Dedupe edges so the same source→target via the same field path is recorded once.
        private readonly HashSet<(string FieldPath, string TargetCollection, long TargetPathId)> _seen = [];

        // Stack of field names currently being walked (root → leaf). Joined with '.'
        // to give the full path of the field containing a PPtr, so nested structs
        // produce e.g. "Stats.PrimaryWeapon" rather than just "PrimaryWeapon".
        private readonly Stack<string> _fieldStack = new();

        public List<(string FieldPath, string TargetCollection, long TargetPathId)> Edges { get; } = [];

        public override bool EnterField(IUnityAssetBase asset, string name)
        {
            _fieldStack.Push(name);
            return true;
        }

        public override void ExitField(IUnityAssetBase asset, string name)
        {
            if (_fieldStack.Count > 0)
                _fieldStack.Pop();
        }

        public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
        {
            if (pptr.IsNull)
            {
                return;
            }

            IUnityObjectBase? target = _collection.TryGetAsset(pptr);
            if (target is null)
            {
                return;
            }

            string targetCollection = target.Collection.Name;
            long targetPathId = target.PathID;

            if (!_knownTemplates.Contains((targetCollection, targetPathId)))
            {
                return;
            }

            string fieldPath = _fieldStack.Count > 0
                ? string.Join('.', _fieldStack.Reverse())
                : "<unknown>";
            if (_seen.Add((fieldPath, targetCollection, targetPathId)))
            {
                Edges.Add((fieldPath, targetCollection, targetPathId));
            }
        }
    }

    private bool IsIl2CppSupplementStale()
    {
        return Il2CppMetadataCache.LoadIfPresent(CachePath) is null;
    }

    public Dictionary<string, List<InspectedFieldNode>>? LoadValues()
    {
        var path = Path.Combine(CachePath, ValuesFileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, List<InspectedFieldNode>>>(File.ReadAllText(path), JsonOptions);
    }

    private static Dictionary<string, List<InspectedFieldNode>> ExtractTemplateValues(
        TemplateIndex index, GameData gameData)
    {
        var values = new Dictionary<string, List<InspectedFieldNode>>();
        foreach (var instance in index.Instances)
        {
            var key = TemplateIndex.IdentityKey(instance.Identity);
            IUnityObjectBase? asset = null;
            foreach (var collection in gameData.GameBundle.FetchAssetCollections())
            {
                if (string.Equals(collection.Name, instance.Identity.Collection, StringComparison.OrdinalIgnoreCase)
                    && collection.TryGetAsset(instance.Identity.PathId, out asset))
                    break;
            }
            if (asset is null) continue;

            var inspection = ObjectFieldInspector.Inspect(asset, ValuesInspectDepth, 0);
            if (asset is IMonoBehaviour mono)
            {
                ManagedTypeInspectionEnricher.Enrich(mono, gameData.AssemblyManager, inspection.Fields);
            }

            // Extract m_Structure fields as the template payload.
            var structure = inspection.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
            values[key] = structure?.Fields ?? inspection.Fields;
        }
        return values;
    }
}
