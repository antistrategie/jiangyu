using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsmResolver.DotNet;
using AssetRipper.Assets;
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

public sealed class StructuralBaselineService(string gameDataPath, string cachePath, IProgressSink progress, ILogSink log)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Depth 4: covers template top-level fields plus up to two levels of support-type nesting.
    /// </summary>
    private const int InspectionMaxDepth = 4;

    /// <summary>
    /// Only one element is needed to determine array element type.
    /// </summary>
    private const int InspectionMaxArraySample = 1;

    public StructuralBaseline GenerateBaseline(BaselineSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var indexService = new TemplateIndexService(gameDataPath, cachePath, progress, log);
        CachedIndexStatus indexStatus = indexService.GetIndexStatus();
        if (!indexStatus.IsCurrent)
        {
            throw new InvalidOperationException(
                $"Template index is not current: {indexStatus.Reason}");
        }

        TemplateIndex index = indexService.LoadIndex()
            ?? throw new InvalidOperationException("Template index could not be loaded.");
        TemplateIndexManifest manifest = indexService.LoadManifest()
            ?? throw new InvalidOperationException("Template index manifest could not be loaded.");

        var resolver = new TemplateResolver(index);

        // Resolve all samples upfront — fail fast on any unresolvable.
        var resolvedTemplates = ResolveTemplateSamples(sources.Templates, resolver);
        var resolvedSupportTypes = ResolveSupportTypeSamples(sources.SupportTypes, index);

        log.Info($"Loading game data from: {gameDataPath}");
        var settings = new CoreConfiguration();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;

        var adapter = new AssetRipperProgressAdapter(progress);
        Logger.Add(adapter);

        try
        {
            progress.SetPhase("Loading assets");
            var gameStructure = GameStructure.Load([gameDataPath], LocalFileSystem.Instance, settings);
            var gameData = GameData.FromGameStructure(gameStructure);

            if (!gameData.GameBundle.HasAnyAssetCollections())
            {
                throw new InvalidOperationException("No asset collections found in game data.");
            }

            progress.Finish();

            progress.SetPhase("Processing");
            RunProcessors(gameData);
            progress.Finish();

            progress.SetPhase("Generating baseline");
            var types = new List<BaselineTypeEntry>();

            foreach (var (source, samples) in resolvedTemplates)
            {
                types.Add(BuildTemplateEntry(source, samples, gameData));
            }

            foreach (var (source, samples) in resolvedSupportTypes)
            {
                types.Add(BuildSupportTypeEntry(source, samples, gameData));
            }

            types = [.. types
                .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase)];

            progress.Finish();

            return new StructuralBaseline
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                GameAssemblyHash = manifest.GameAssemblyHash,
                Types = types,
            };
        }
        finally
        {
            Logger.Remove(adapter);
        }
    }

    public static BaselineSources? LoadSources(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<BaselineSources>(File.ReadAllText(path), JsonOptions);
    }

    public static StructuralBaseline? LoadBaseline(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<StructuralBaseline>(File.ReadAllText(path), JsonOptions);
    }

    public static BaselineDiff DiffBaselines(StructuralBaseline previous, StructuralBaseline current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousByName = previous.Types.ToDictionary(t => t.TypeName, StringComparer.Ordinal);
        var currentByName = current.Types.ToDictionary(t => t.TypeName, StringComparer.Ordinal);

        var allTypeNames = previousByName.Keys
            .Union(currentByName.Keys)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var addedTypes = new List<string>();
        var removedTypes = new List<string>();
        var changedTypes = new List<BaselineTypeDiff>();

        foreach (string typeName in allTypeNames)
        {
            bool inPrevious = previousByName.TryGetValue(typeName, out var prevType);
            bool inCurrent = currentByName.TryGetValue(typeName, out var currType);

            if (!inPrevious)
            {
                addedTypes.Add(typeName);
                continue;
            }

            if (!inCurrent)
            {
                removedTypes.Add(typeName);
                continue;
            }

            var typeDiff = DiffType(prevType!, currType!);
            if (typeDiff is not null)
            {
                changedTypes.Add(typeDiff);
            }
        }

        return new BaselineDiff
        {
            PreviousGeneratedAt = previous.GeneratedAt,
            CurrentGeneratedAt = current.GeneratedAt,
            AddedTypes = addedTypes,
            RemovedTypes = removedTypes,
            ChangedTypes = changedTypes,
        };
    }

    private static List<(BaselineSourceEntry Source, List<ResolvedTemplateCandidate> Samples)> ResolveTemplateSamples(
        List<BaselineSourceEntry> templates,
        TemplateResolver resolver)
    {
        var result = new List<(BaselineSourceEntry, List<ResolvedTemplateCandidate>)>();
        var errors = new List<string>();

        foreach (BaselineSourceEntry entry in templates)
        {
            if (entry.SampleNames.Count == 0)
            {
                errors.Add($"Template '{entry.TypeName}' has no sample names.");
                continue;
            }

            var samples = new List<ResolvedTemplateCandidate>();
            foreach (string sampleName in entry.SampleNames)
            {
                TemplateResolutionResult resolution = resolver.Resolve(entry.TypeName, sampleName);
                switch (resolution.Status)
                {
                    case TemplateResolutionStatus.Success:
                        samples.Add(resolution.Resolved!);
                        break;
                    case TemplateResolutionStatus.NotFound:
                        errors.Add($"Template sample '{sampleName}' not found for type '{entry.TypeName}'.");
                        break;
                    case TemplateResolutionStatus.Ambiguous:
                        errors.Add($"Template sample '{sampleName}' is ambiguous for type '{entry.TypeName}'.");
                        break;
                    default:
                        errors.Add($"Template sample '{sampleName}' could not be resolved for type '{entry.TypeName}': {resolution.Status}.");
                        break;
                }
            }

            result.Add((entry, samples));
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to resolve baseline samples:\n  {string.Join("\n  ", errors)}");
        }

        return result;
    }

    private static List<(BaselineSourceEntry Source, List<ResolvedTemplateCandidate> Samples)> ResolveSupportTypeSamples(
        List<BaselineSourceEntry> supportTypes,
        TemplateIndex index)
    {
        var result = new List<(BaselineSourceEntry, List<ResolvedTemplateCandidate>)>();
        var errors = new List<string>();

        foreach (BaselineSourceEntry entry in supportTypes)
        {
            if (entry.SampleNames.Count == 0)
            {
                errors.Add($"Support type '{entry.TypeName}' has no sample names.");
                continue;
            }

            var samples = new List<ResolvedTemplateCandidate>();
            foreach (string sampleName in entry.SampleNames)
            {
                var candidates = index.Instances
                    .Where(i => string.Equals(i.Name, sampleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0)
                {
                    errors.Add($"Support type sample '{sampleName}' not found in template index (for type '{entry.TypeName}').");
                }
                else if (candidates.Count > 1)
                {
                    string matches = string.Join(", ", candidates.Select(c => c.ClassName));
                    errors.Add($"Support type sample '{sampleName}' is ambiguous in template index (for type '{entry.TypeName}'): matches {matches}.");
                }
                else
                {
                    TemplateInstanceEntry instance = candidates[0];
                    samples.Add(new ResolvedTemplateCandidate
                    {
                        Name = instance.Name,
                        ClassName = instance.ClassName,
                        Identity = new TemplateIdentity
                        {
                            Collection = instance.Identity.Collection,
                            PathId = instance.Identity.PathId,
                        },
                    });
                }
            }

            result.Add((entry, samples));
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to resolve baseline samples:\n  {string.Join("\n  ", errors)}");
        }

        return result;
    }

    private static BaselineTypeEntry BuildTemplateEntry(
        BaselineSourceEntry source,
        List<ResolvedTemplateCandidate> samples,
        GameData gameData)
    {
        List<BaselineFieldEntry>? canonicalFields = null;
        string? canonicalSampleName = null;

        foreach (ResolvedTemplateCandidate sample in samples)
        {
            List<InspectedFieldNode> inspectedFields = InspectStructureFields(sample, gameData);
            List<BaselineFieldEntry> fields = ExtractFieldEntries(
                inspectedFields,
                elementTypeName => InferStorageFromElementTypeName(gameData.AssemblyManager, elementTypeName));

            if (canonicalFields is null)
            {
                canonicalFields = fields;
                canonicalSampleName = sample.Name;
                continue;
            }

            AssertFieldsConsistent(source.TypeName, canonicalSampleName!, canonicalFields, sample.Name, fields);
        }

        return new BaselineTypeEntry
        {
            TypeName = source.TypeName,
            Category = "template",
            FieldCount = canonicalFields!.Count,
            SampleNames = [.. source.SampleNames.Order(StringComparer.OrdinalIgnoreCase)],
            Fields = canonicalFields,
        };
    }

    private static BaselineTypeEntry BuildSupportTypeEntry(
        BaselineSourceEntry source,
        List<ResolvedTemplateCandidate> samples,
        GameData gameData)
    {
        List<BaselineFieldEntry>? canonicalFields = null;
        string? canonicalSampleName = null;

        foreach (ResolvedTemplateCandidate sample in samples)
        {
            List<InspectedFieldNode> inspectedFields = InspectStructureFields(sample, gameData);

            InspectedFieldNode? supportTypeNode = FindFieldByTypeName(inspectedFields, source.TypeName)
                ?? throw new InvalidOperationException(
                    $"Support type '{source.TypeName}' not found in template '{sample.Name}' ({sample.ClassName}).");

            if (supportTypeNode.Fields is null || supportTypeNode.Fields.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Support type '{source.TypeName}' in template '{sample.Name}' has no fields (may need deeper inspection depth).");
            }

            List<BaselineFieldEntry> fields = ExtractFieldEntries(
                supportTypeNode.Fields,
                elementTypeName => InferStorageFromElementTypeName(gameData.AssemblyManager, elementTypeName));

            if (canonicalFields is null)
            {
                canonicalFields = fields;
                canonicalSampleName = sample.Name;
                continue;
            }

            AssertFieldsConsistent(source.TypeName, canonicalSampleName!, canonicalFields, sample.Name, fields);
        }

        return new BaselineTypeEntry
        {
            TypeName = source.TypeName,
            Category = "supportType",
            FieldCount = canonicalFields!.Count,
            SampleNames = [.. source.SampleNames.Order(StringComparer.OrdinalIgnoreCase)],
            Fields = canonicalFields,
        };
    }

    private static void AssertFieldsConsistent(
        string typeName,
        string canonicalSampleName,
        List<BaselineFieldEntry> canonicalFields,
        string otherSampleName,
        List<BaselineFieldEntry> otherFields)
    {
        var canonicalByName = canonicalFields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var otherByName = otherFields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var mismatches = new List<string>();

        // Fields present in canonical but missing from other.
        foreach (string name in canonicalByName.Keys.Except(otherByName.Keys, StringComparer.Ordinal))
        {
            mismatches.Add($"  field '{name}' present in '{canonicalSampleName}' but missing in '{otherSampleName}'");
        }

        // Fields present in other but missing from canonical.
        foreach (string name in otherByName.Keys.Except(canonicalByName.Keys, StringComparer.Ordinal))
        {
            mismatches.Add($"  field '{name}' present in '{otherSampleName}' but missing in '{canonicalSampleName}'");
        }

        // Fields present in both but structurally different.
        foreach (string name in canonicalByName.Keys.Intersect(otherByName.Keys, StringComparer.Ordinal))
        {
            BaselineFieldEntry a = canonicalByName[name];
            BaselineFieldEntry b = otherByName[name];

            if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal))
            {
                mismatches.Add($"  field '{name}' kind differs: '{a.Kind}' in '{canonicalSampleName}' vs '{b.Kind}' in '{otherSampleName}'");
            }

            if (!string.Equals(a.FieldTypeName, b.FieldTypeName, StringComparison.Ordinal))
            {
                mismatches.Add($"  field '{name}' fieldTypeName differs: '{a.FieldTypeName}' in '{canonicalSampleName}' vs '{b.FieldTypeName}' in '{otherSampleName}'");
            }

            if (!string.Equals(a.ElementTypeName, b.ElementTypeName, StringComparison.Ordinal))
            {
                mismatches.Add($"  field '{name}' elementTypeName differs: '{a.ElementTypeName}' in '{canonicalSampleName}' vs '{b.ElementTypeName}' in '{otherSampleName}'");
            }

            if (!string.Equals(a.Storage, b.Storage, StringComparison.Ordinal))
            {
                mismatches.Add($"  field '{name}' storage differs: '{a.Storage}' in '{canonicalSampleName}' vs '{b.Storage}' in '{otherSampleName}'");
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Structural inconsistency in '{typeName}' across curated samples:\n{string.Join("\n", mismatches)}");
        }
    }

    private static List<InspectedFieldNode> InspectStructureFields(
        ResolvedTemplateCandidate sample,
        GameData gameData)
    {
        IUnityObjectBase asset = FindAsset(gameData, sample.Identity.Collection, sample.Identity.PathId)
            ?? throw new InvalidOperationException(
                $"Asset '{sample.Name}' not found in collection '{sample.Identity.Collection}' with pathId {sample.Identity.PathId}.");

        ObjectFieldInspection inspection = ObjectFieldInspector.Inspect(asset, InspectionMaxDepth, InspectionMaxArraySample);

        if (asset is IMonoBehaviour monoBehaviour)
        {
            ManagedTypeInspectionEnricher.Enrich(monoBehaviour, gameData.AssemblyManager, inspection.Fields);
        }

        InspectedFieldNode? structureNode = inspection.Fields
            .FirstOrDefault(f => string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));

        if (structureNode?.Fields is null || structureNode.Fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Template '{sample.Name}' ({sample.ClassName}) has no m_Structure fields.");
        }

        return structureNode.Fields;
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

    internal static InspectedFieldNode? FindFieldByTypeName(List<InspectedFieldNode> fields, string typeName)
    {
        foreach (InspectedFieldNode field in fields)
        {
            if (MatchesTypeName(field.FieldTypeName, typeName)
                && field.Fields is { Count: > 0 })
            {
                return field;
            }

            if (field.Fields is { Count: > 0 })
            {
                InspectedFieldNode? found = FindFieldByTypeName(field.Fields, typeName);
                if (found is not null)
                {
                    return found;
                }
            }

            if (field.Elements is not { Count: > 0 })
            {
                continue;
            }

            foreach (InspectedFieldNode element in field.Elements)
            {
                if (MatchesTypeName(element.FieldTypeName, typeName)
                    && element.Fields is { Count: > 0 })
                {
                    return element;
                }

                if (element.Fields is { Count: > 0 })
                {
                    InspectedFieldNode? found = FindFieldByTypeName(element.Fields, typeName);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    internal static bool MatchesTypeName(string? observedTypeName, string? expectedTypeName)
    {
        if (string.IsNullOrWhiteSpace(observedTypeName) || string.IsNullOrWhiteSpace(expectedTypeName))
        {
            return false;
        }

        if (string.Equals(observedTypeName, expectedTypeName, StringComparison.Ordinal))
        {
            return true;
        }

        string observedSimple = GetSimpleTypeName(observedTypeName);
        string expectedSimple = GetSimpleTypeName(expectedTypeName);
        return string.Equals(observedSimple, expectedSimple, StringComparison.Ordinal);
    }

    private static string GetSimpleTypeName(string typeName)
    {
        int genericStart = typeName.IndexOf('<');
        if (genericStart >= 0)
        {
            typeName = typeName[..genericStart];
        }

        int lastDot = typeName.LastIndexOf('.');
        return lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;
    }

    internal static List<BaselineFieldEntry> ExtractFieldEntries(
        List<InspectedFieldNode> fields,
        Func<string, string?>? inferStorageFromElementTypeName = null)
    {
        return
        [
            .. fields.Select(f => new BaselineFieldEntry
            {
                Name = f.Name ?? "(unnamed)",
                Kind = f.Kind,
                FieldTypeName = f.FieldTypeName,
                ElementTypeName = ResolveElementTypeName(f),
                Storage = ResolveStorage(f, inferStorageFromElementTypeName),
            }),
        ];
    }

    private static string? ResolveElementTypeName(InspectedFieldNode field)
    {
        if (!string.Equals(field.Kind, "array", StringComparison.Ordinal))
        {
            return null;
        }

        string? declaredElementType = TryParseArrayElementTypeName(field.FieldTypeName);
        if (!string.IsNullOrWhiteSpace(declaredElementType))
        {
            return declaredElementType;
        }

        if (field.Elements is { Count: > 0 })
        {
            return field.Elements[0].FieldTypeName;
        }

        return null;
    }

    internal static string? ResolveStorage(
        InspectedFieldNode field,
        Func<string, string?>? inferStorageFromElementTypeName = null)
    {
        if (!string.Equals(field.Kind, "array", StringComparison.Ordinal))
        {
            return null;
        }

        if (field.Elements is { Count: > 0 })
        {
            return string.Equals(field.Elements[0].Kind, "reference", StringComparison.Ordinal)
                ? "reference"
                : "inline";
        }

        string? elementTypeName = ResolveElementTypeName(field);
        if (!string.IsNullOrWhiteSpace(elementTypeName) && inferStorageFromElementTypeName is not null)
        {
            return inferStorageFromElementTypeName(elementTypeName);
        }

        return null;
    }

    internal static string? InferStorageFromElementTypeName(IAssemblyManager assemblyManager, string? elementTypeName)
    {
        if (string.IsNullOrWhiteSpace(elementTypeName))
        {
            return null;
        }

        if (IsKnownInlineTypeName(elementTypeName))
        {
            return "inline";
        }

        TypeDefinition? typeDefinition = ResolveTypeDefinitionByName(assemblyManager, elementTypeName);
        if (typeDefinition is null)
        {
            return null;
        }

        return DerivesFrom(typeDefinition, "UnityEngine.Object")
            ? "reference"
            : "inline";
    }

    private static bool IsKnownInlineTypeName(string typeName)
    {
        string simpleName = GetSimpleTypeName(typeName);
        return simpleName switch
        {
            "Boolean" or
            "Byte" or
            "SByte" or
            "Char" or
            "Decimal" or
            "Double" or
            "Single" or
            "Int16" or
            "Int32" or
            "Int64" or
            "UInt16" or
            "UInt32" or
            "UInt64" or
            "String" or
            "DateTime" => true,
            _ => false,
        };
    }

    private static TypeDefinition? ResolveTypeDefinitionByName(IAssemblyManager assemblyManager, string typeName)
    {
        foreach (AssemblyDefinition assembly in assemblyManager.GetAssemblies())
        {
            foreach (ModuleDefinition module in assembly.Modules)
            {
                foreach (TypeDefinition type in EnumerateTypes(module.TopLevelTypes))
                {
                    if (string.Equals(type.FullName, typeName, StringComparison.Ordinal)
                        || string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    {
                        return type;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;

            foreach (TypeDefinition nestedType in EnumerateTypes(type.NestedTypes))
            {
                yield return nestedType;
            }
        }
    }

    private static bool DerivesFrom(TypeDefinition typeDefinition, string fullName)
    {
        TypeDefinition? current = typeDefinition;
        while (current is not null)
        {
            if (string.Equals(current.FullName, fullName, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                current = current.BaseType?.Resolve();
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    internal static string? TryParseArrayElementTypeName(string? fieldTypeName)
    {
        if (string.IsNullOrWhiteSpace(fieldTypeName))
        {
            return null;
        }

        fieldTypeName = fieldTypeName.Trim();

        if (fieldTypeName.EndsWith("[]", StringComparison.Ordinal))
        {
            string arrayElement = fieldTypeName[..^2].Trim();
            return arrayElement.Length == 0 ? null : arrayElement;
        }

        int genericStart = fieldTypeName.IndexOf('<');
        if (genericStart < 0 || !fieldTypeName.EndsWith('>'))
        {
            return null;
        }

        string genericTypeName = fieldTypeName[..genericStart].Trim();
        bool isArrayLike =
            string.Equals(genericTypeName, "Array", StringComparison.Ordinal)
            || genericTypeName.EndsWith(".List`1", StringComparison.Ordinal)
            || string.Equals(genericTypeName, "List", StringComparison.Ordinal)
            || string.Equals(genericTypeName, "List`1", StringComparison.Ordinal);

        if (!isArrayLike)
        {
            return null;
        }

        string inner = fieldTypeName[(genericStart + 1)..^1].Trim();
        return inner.Length == 0 ? null : inner;
    }

    private static BaselineTypeDiff? DiffType(BaselineTypeEntry previous, BaselineTypeEntry current)
    {
        var prevFieldsByName = previous.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var currFieldsByName = current.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        var allFieldNames = prevFieldsByName.Keys
            .Union(currFieldsByName.Keys)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var addedFields = new List<string>();
        var removedFields = new List<string>();
        var changedFields = new List<BaselineFieldDiff>();

        foreach (string fieldName in allFieldNames)
        {
            bool inPrevious = prevFieldsByName.TryGetValue(fieldName, out var prevField);
            bool inCurrent = currFieldsByName.TryGetValue(fieldName, out var currField);

            if (!inPrevious)
            {
                addedFields.Add(fieldName);
                continue;
            }

            if (!inCurrent)
            {
                removedFields.Add(fieldName);
                continue;
            }

            bool kindChanged = !string.Equals(prevField!.Kind, currField!.Kind, StringComparison.Ordinal);
            bool typeChanged = !string.Equals(prevField.FieldTypeName, currField.FieldTypeName, StringComparison.Ordinal);
            bool elementTypeChanged = !string.Equals(prevField.ElementTypeName, currField.ElementTypeName, StringComparison.Ordinal);
            bool storageChanged = !string.Equals(prevField.Storage, currField.Storage, StringComparison.Ordinal);

            if (kindChanged || typeChanged || elementTypeChanged || storageChanged)
            {
                changedFields.Add(new BaselineFieldDiff
                {
                    Name = fieldName,
                    PreviousKind = kindChanged ? prevField.Kind : null,
                    CurrentKind = kindChanged ? currField.Kind : null,
                    PreviousFieldTypeName = typeChanged ? prevField.FieldTypeName : null,
                    CurrentFieldTypeName = typeChanged ? currField.FieldTypeName : null,
                    PreviousElementTypeName = elementTypeChanged ? prevField.ElementTypeName : null,
                    CurrentElementTypeName = elementTypeChanged ? currField.ElementTypeName : null,
                    PreviousStorage = storageChanged ? prevField.Storage : null,
                    CurrentStorage = storageChanged ? currField.Storage : null,
                });
            }
        }

        if (addedFields.Count == 0 && removedFields.Count == 0 && changedFields.Count == 0)
        {
            return null;
        }

        int? fieldCountDelta = current.FieldCount != previous.FieldCount
            ? current.FieldCount - previous.FieldCount
            : null;

        return new BaselineTypeDiff
        {
            TypeName = previous.TypeName,
            FieldCountDelta = fieldCountDelta,
            AddedFields = addedFields,
            RemovedFields = removedFields,
            ChangedFields = changedFields,
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
}
