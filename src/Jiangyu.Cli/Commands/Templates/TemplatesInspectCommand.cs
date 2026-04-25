using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesInspectCommand
{
    private const string DefaultAssemblyRelativePath = "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll";
    private const string MelonLoaderNet6RelativePath = "MelonLoader/net6";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var typeOption = new Option<string?>("--type") { Description = "Template class name (for name-based resolution)" };
        var nameOption = new Option<string?>("--name") { Description = "Template name to resolve through the template index" };
        var collectionOption = new Option<string?>("--collection") { Description = "Asset collection name (stable identity)" };
        var pathIdOption = new Option<long?>("--path-id") { Description = "Asset path ID (stable identity)" };
        var maxDepthOption = new Option<int>("--max-depth") { Description = "Maximum nested depth to include", DefaultValueFactory = _ => 4 };
        var maxArraySampleOption = new Option<int>("--max-array-sample") { Description = "Maximum number of array elements to include", DefaultValueFactory = _ => 8 };
        var outputOption = new Option<string>("--output") { Description = "Output mode: json, pretty, or text", DefaultValueFactory = _ => "pretty" };
        var withModOption = new Option<string?>("--with-mod") { Description = "Apply templateClones/templatePatches from this mod source directory or manifest before rendering" };

        var command = new Command("inspect", "Inspect one template instance via the template index or stable identity")
        {
            typeOption,
            nameOption,
            collectionOption,
            pathIdOption,
            maxDepthOption,
            maxArraySampleOption,
            outputOption,
            withModOption,
        };

        command.SetAction((parseResult) =>
        {
            string? className = parseResult.GetValue(typeOption);
            string? name = parseResult.GetValue(nameOption);
            string? collection = parseResult.GetValue(collectionOption);
            long? pathId = parseResult.GetValue(pathIdOption);
            int maxDepth = parseResult.GetValue(maxDepthOption);
            int maxArraySampleLength = parseResult.GetValue(maxArraySampleOption);
            string output = (parseResult.GetValue(outputOption) ?? "pretty").ToLowerInvariant();
            string? withMod = parseResult.GetValue(withModOption);

            if (!TryValidateInput(className, name, collection, pathId, output, maxDepth, maxArraySampleLength, out string? error))
            {
                Console.Error.WriteLine($"Error: {error}");
                return 1;
            }

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var context = resolution.Context!;
            var objectInspectionService = context.CreateObjectInspectionService(new ConsoleProgressSink(), new ConsoleLogSink());
            var templateIndexService = context.CreateTemplateIndexService(new ConsoleProgressSink(), new ConsoleLogSink());
            TemplateIndex? index = null;
            TemplateModPreviewPlan? previewPlan = null;
            string? previewManifestPath = null;

            try
            {
                bool needsIndex = !string.IsNullOrWhiteSpace(name)
                    || !string.IsNullOrWhiteSpace(withMod)
                    || string.Equals(output, "text", StringComparison.OrdinalIgnoreCase);

                if (needsIndex)
                {
                    CachedIndexStatus indexStatus = templateIndexService.GetIndexStatus();
                    if (!indexStatus.IsCurrent)
                    {
                        Console.Error.WriteLine($"Error: {indexStatus.Reason}");
                        return 1;
                    }

                    index = templateIndexService.LoadIndex();
                }

                if (!string.IsNullOrWhiteSpace(withMod))
                {
                    previewManifestPath = TemplateModPreviewPlan.ResolveManifestPath(withMod);
                    previewPlan = TemplateModPreviewPlan.Load(withMod, new ConsoleLogSink());
                }

                ResolvedInspectTarget target = !string.IsNullOrWhiteSpace(name)
                    ? ResolveByName(index!, previewPlan, className!, name!)
                    : ResolveByIdentity(index, previewPlan, collection!, pathId!.Value);

                var request = new ObjectInspectionRequest
                {
                    Collection = target.SourceCollection,
                    PathId = target.SourcePathId,
                    MaxDepth = maxDepth,
                    MaxArraySampleLength = maxArraySampleLength,
                };

                ObjectInspectionResult result = objectInspectionService.Inspect(request);

                if (target.SourceKey != target.TargetKey)
                {
                    result = TemplateInspectionPreviewApplier.CloneForTarget(result, target.TargetKey);
                }

                if (previewPlan is not null)
                {
                    TemplateInspectionPreviewApplier.Apply(
                        result,
                        previewPlan.GetPatches(target.TargetKey),
                        reference => ResolvePreviewReference(index!, previewPlan, reference));
                }

                if (string.Equals(output, "text", StringComparison.OrdinalIgnoreCase))
                {
                    string templateType = target.TargetKey.TemplateType;
                    string templateId = target.TargetKey.TemplateId;

                    if (string.IsNullOrWhiteSpace(templateType))
                    {
                        templateType = InferTemplateType(result) ?? "Template";
                    }

                    string text = TemplateInspectionTextRenderer.Render(
                        result,
                        new TemplateInspectionTextContext
                        {
                            TemplateType = templateType,
                            TemplateId = templateId,
                            TemplateIndex = index,
                            PreviewManifestPath = previewManifestPath,
                            OdinOnlyFields = GetLikelyOdinOnlyFields(context, result, templateType),
                        });
                    Console.WriteLine(text);
                    return 0;
                }

                string json = JsonSerializer.Serialize(result, output == "json" ? CompactJsonOptions : PrettyJsonOptions);
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: template inspect failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static bool TryValidateInput(
        string? className,
        string? name,
        string? collection,
        long? pathId,
        string output,
        int maxDepth,
        int maxArraySampleLength,
        out string? error)
    {
        bool hasNameMode = !string.IsNullOrWhiteSpace(className) || !string.IsNullOrWhiteSpace(name);
        bool hasIdentityMode = !string.IsNullOrWhiteSpace(collection) || pathId.HasValue;
        bool hasPartialNameMode = !string.IsNullOrWhiteSpace(className) ^ !string.IsNullOrWhiteSpace(name);
        bool hasPartialIdentity = !string.IsNullOrWhiteSpace(collection) ^ pathId.HasValue;

        if (hasPartialNameMode)
        {
            error = "use both --type and --name together.";
            return false;
        }

        if (hasPartialIdentity)
        {
            error = "use both --collection and --path-id together.";
            return false;
        }

        if (hasNameMode == hasIdentityMode)
        {
            error = "use either --type + --name or --collection + --path-id.";
            return false;
        }

        if (!string.Equals(output, "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(output, "pretty", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(output, "text", StringComparison.OrdinalIgnoreCase))
        {
            error = "output must be 'json', 'pretty', or 'text'.";
            return false;
        }

        if (maxDepth < 1)
        {
            error = "--max-depth must be at least 1.";
            return false;
        }

        if (maxArraySampleLength < 0)
        {
            error = "--max-array-sample must be 0 or greater.";
            return false;
        }

        error = null;
        return true;
    }

    private static ResolvedInspectTarget ResolveByName(
        TemplateIndex index,
        TemplateModPreviewPlan? previewPlan,
        string className,
        string templateId)
    {
        TemplatePreviewKey targetKey = new(className, templateId);
        TemplatePreviewKey sourceKey = previewPlan?.ResolveUltimateSource(targetKey) ?? targetKey;

        var resolver = new TemplateResolver(index);
        TemplateResolutionResult templateResolution = resolver.Resolve(sourceKey.TemplateType, sourceKey.TemplateId);

        return templateResolution.Status switch
        {
            TemplateResolutionStatus.Success => new ResolvedInspectTarget(
                targetKey,
                sourceKey,
                templateResolution.Resolved!.Identity.Collection,
                templateResolution.Resolved.Identity.PathId),
            TemplateResolutionStatus.NotFound => throw new InvalidOperationException(
                $"no template named '{sourceKey.TemplateId}' found for type '{sourceKey.TemplateType}'."),
            TemplateResolutionStatus.Ambiguous => throw BuildAmbiguousTemplateException(sourceKey, templateResolution.Candidates),
            _ => throw new InvalidOperationException("template index not found. Run 'jiangyu templates index' first."),
        };
    }

    private static ResolvedInspectTarget ResolveByIdentity(
        TemplateIndex? index,
        TemplateModPreviewPlan? previewPlan,
        string collection,
        long pathId)
    {
        if (index is null)
        {
            return new ResolvedInspectTarget(
                new TemplatePreviewKey(string.Empty, string.Empty),
                new TemplatePreviewKey(string.Empty, string.Empty),
                collection,
                pathId);
        }

        TemplateInstanceEntry? instance = index.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Identity.Collection, collection, StringComparison.OrdinalIgnoreCase)
            && candidate.Identity.PathId == pathId);

        if (instance is null)
        {
            return new ResolvedInspectTarget(
                new TemplatePreviewKey(string.Empty, string.Empty),
                new TemplatePreviewKey(string.Empty, string.Empty),
                collection,
                pathId);
        }

        TemplatePreviewKey targetKey = new(instance.ClassName, instance.Name);
        TemplatePreviewKey sourceKey = previewPlan?.ResolveUltimateSource(targetKey) ?? targetKey;

        if (sourceKey == targetKey)
        {
            return new ResolvedInspectTarget(targetKey, sourceKey, collection, pathId);
        }

        TemplateResolutionResult sourceResolution = new TemplateResolver(index).Resolve(sourceKey.TemplateType, sourceKey.TemplateId);
        if (sourceResolution.Status != TemplateResolutionStatus.Success)
        {
            throw new InvalidOperationException(
                $"no template named '{sourceKey.TemplateId}' found for type '{sourceKey.TemplateType}'.");
        }

        return new ResolvedInspectTarget(
            targetKey,
            sourceKey,
            sourceResolution.Resolved!.Identity.Collection,
            sourceResolution.Resolved.Identity.PathId);
    }

    private static InvalidOperationException BuildAmbiguousTemplateException(
        TemplatePreviewKey sourceKey,
        IReadOnlyList<ResolvedTemplateCandidate> candidates)
    {
        var lines = candidates
            .Select(candidate => $"  {candidate.Name} ({candidate.ClassName}) in {candidate.Identity.Collection} [pathId={candidate.Identity.PathId}]");

        return new InvalidOperationException(
            $"template name '{sourceKey.TemplateId}' is ambiguous for type '{sourceKey.TemplateType}'.{Environment.NewLine}"
            + string.Join(Environment.NewLine, lines)
            + $"{Environment.NewLine}Rerun with --collection and --path-id.");
    }

    private static TemplatePreviewResolvedReference? ResolvePreviewReference(
        TemplateIndex index,
        TemplateModPreviewPlan previewPlan,
        CompiledTemplateReference reference)
    {
        string templateType = string.IsNullOrWhiteSpace(reference.TemplateType)
            ? "EntityTemplate"
            : reference.TemplateType.Trim();
        TemplatePreviewKey key = new(templateType, reference.TemplateId);

        if (previewPlan.TryGetClone(key, out _))
        {
            return new TemplatePreviewResolvedReference
            {
                TemplateType = key.TemplateType,
                TemplateId = key.TemplateId,
            };
        }

        TemplateResolutionResult resolution = new TemplateResolver(index).Resolve(templateType, reference.TemplateId);
        if (resolution.Status != TemplateResolutionStatus.Success)
        {
            return null;
        }

        return new TemplatePreviewResolvedReference
        {
            TemplateType = templateType,
            TemplateId = reference.TemplateId,
            Collection = resolution.Resolved!.Identity.Collection,
            PathId = resolution.Resolved.Identity.PathId,
        };
    }

    private static List<string> GetLikelyOdinOnlyFields(
        EnvironmentContext context,
        ObjectInspectionResult result,
        string templateType)
    {
        InspectedFieldNode? structure = result.Fields.FirstOrDefault(field => string.Equals(field.Name, "m_Structure", StringComparison.Ordinal));
        HashSet<string> serialisedFieldNames = structure?.Fields is null
            ? []
            : structure.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Name))
                .Select(field => field.Name!)
                .ToHashSet(StringComparer.Ordinal);

        string? gamePath = Path.GetDirectoryName(context.GameDataPath);
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return [];
        }

        string assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        var additionalSearchDirectories = new List<string>();
        string melonLoaderNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonLoaderNet6))
        {
            additionalSearchDirectories.Add(melonLoaderNet6);
        }

        using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirectories);
        string typeQuery = structure?.FieldTypeName ?? templateType;
        Type? type = catalog.ResolveType(typeQuery, out _, out _)
            ?? catalog.ResolveType(templateType, out _, out _);
        if (type is null)
        {
            return [];
        }

        return
        [
            .. TemplateTypeCatalog.GetMembers(type, includeReadOnly: true)
                .Where(member => member.IsLikelyOdinOnly && !serialisedFieldNames.Contains(member.Name))
                .Select(member => member.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
    }

    private static string? InferTemplateType(ObjectInspectionResult result)
    {
        string? fullName = result.Fields
            .FirstOrDefault(field => string.Equals(field.Name, "m_Structure", StringComparison.Ordinal))
            ?.FieldTypeName;

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        int dot = fullName.LastIndexOf('.');
        return dot >= 0 ? fullName[(dot + 1)..] : fullName;
    }

    private sealed record ResolvedInspectTarget(
        TemplatePreviewKey TargetKey,
        TemplatePreviewKey SourceKey,
        string SourceCollection,
        long SourcePathId);
}
