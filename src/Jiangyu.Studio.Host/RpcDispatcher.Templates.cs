using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Templates;

namespace Jiangyu.Studio.Host;

public static partial class RpcDispatcher
{
    private const string DefaultAssemblyRelativePath = "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll";
    private const string MelonLoaderNet6RelativePath = "MelonLoader/net6";

    private static JsonElement HandleTemplatesIndexStatus(IInfiniFrameWindow _, JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            return JsonSerializer.SerializeToElement(new TemplateIndexStatusDto
            {
                State = "noGame",
                Reason = resolution.Error,
            });
        }

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var status = service.GetIndexStatus();
        var manifest = service.LoadManifest();

        return JsonSerializer.SerializeToElement(new TemplateIndexStatusDto
        {
            State = status.State switch
            {
                CachedIndexState.Current => "current",
                CachedIndexState.Stale => "stale",
                CachedIndexState.Missing => "missing",
                _ => "missing",
            },
            Reason = status.Reason,
            InstanceCount = manifest?.InstanceCount,
            TypeCount = manifest?.TemplateTypeCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    private static JsonElement HandleTemplatesIndex(IInfiniFrameWindow _, JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        service.BuildIndex();

        var manifest = service.LoadManifest();
        return JsonSerializer.SerializeToElement(new TemplateIndexStatusDto
        {
            State = "current",
            InstanceCount = manifest?.InstanceCount,
            TypeCount = manifest?.TemplateTypeCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    private static JsonElement HandleTemplatesSearch(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var query = TryGetString(parameters, "query");
        var className = TryGetString(parameters, "className");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var index = service.LoadIndex() ?? throw new InvalidOperationException("Template index not found. Build it first.");

        // If no query, return the full index (types + instances).
        // When className is set but query is empty, filter instances by class.
        if (string.IsNullOrWhiteSpace(query))
        {
            List<TemplateInstanceEntry> instances = string.IsNullOrWhiteSpace(className)
                ? [.. index.Instances]
                : [.. index.Instances
                    .Where(i => string.Equals(i.ClassName, className, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                ];

            return JsonSerializer.SerializeToElement(new TemplateSearchResultDto
            {
                Types = string.IsNullOrWhiteSpace(className) ? index.TemplateTypes : [],
                Instances = instances,
                ReferencedBy = TemplateIndex.FilterReferencedBy(index.ReferencedBy, instances),
            });
        }

        var searchService = new TemplateSearchService(index);
        var result = searchService.Search(query ?? "", className);

        return JsonSerializer.SerializeToElement(new TemplateSearchResultDto
        {
            Types = result.MatchingTypes,
            Instances = result.MatchingInstances,
            ReferencedBy = TemplateIndex.FilterReferencedBy(index.ReferencedBy, result.MatchingInstances),
        });
    }

    private static JsonElement HandleTemplatesQuery(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");
        var fieldPath = TryGetString(parameters, "fieldPath");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var gamePath = Path.GetDirectoryName(resolution.Context!.GameDataPath)
            ?? throw new InvalidOperationException("Could not derive game directory.");

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException($"Assembly-CSharp.dll not found at: {assemblyPath}");

        var additionalSearchDirs = new List<string>();
        var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonNet6))
            additionalSearchDirs.Add(melonNet6);

        var supplement = Il2CppMetadataCache.LoadIfPresent(resolution.Context!.CachePath);
        using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs, supplement);

        var queryPath = string.IsNullOrWhiteSpace(fieldPath)
            ? typeName
            : $"{typeName}.{fieldPath}";

        var result = TemplateMemberQuery.Run(catalog, queryPath);

        if (result.Kind == QueryResultKind.Error)
            throw new InvalidOperationException(result.ErrorMessage ?? "Query failed.");

        return JsonSerializer.SerializeToElement(MapQueryResult(catalog, result));
    }

    // Catalog loads are expensive (reflection over Assembly-CSharp.dll), and the
    // source-view parse RPC fires on every debounced keystroke. Cache the
    // catalog per assembly path so repeated parses reuse one instance; reload
    // only when the game path changes.
    private static TemplateTypeCatalog? _cachedCatalog;
    private static string? _cachedCatalogPath;
    private static readonly Lock _catalogLock = new();

    private static TemplateTypeCatalog? TryGetCachedCatalog()
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success) return null;

        var gamePath = Path.GetDirectoryName(resolution.Context!.GameDataPath);
        if (gamePath is null) return null;

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath)) return null;

        lock (_catalogLock)
        {
            if (_cachedCatalog != null && _cachedCatalogPath == assemblyPath)
                return _cachedCatalog;

            _cachedCatalog?.Dispose();
            _cachedCatalog = null;
            _cachedCatalogPath = null;

            var additionalSearchDirs = new List<string>();
            var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
            if (Directory.Exists(melonNet6))
                additionalSearchDirs.Add(melonNet6);

            try
            {
                var supplement = Il2CppMetadataCache.LoadIfPresent(resolution.Context!.CachePath);
                _cachedCatalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs, supplement);
                _cachedCatalogPath = assemblyPath;
                return _cachedCatalog;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"templatesParse: failed to load template catalog: {ex.Message}");
                return null;
            }
        }
    }

    private static JsonElement HandleTemplatesParse(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var text = RequireString(parameters, "text");
        var document = KdlTemplateParser.ParseText(text);

        // Validate node/directive shapes against the template type catalogue
        // when available. Failures are non-fatal: if the catalog can't load
        // (no game configured, bad path), we just return parse errors only.
        var catalog = TryGetCachedCatalog();
        if (catalog != null)
            TemplateCatalogValidator.ValidateEditorDocument(document, catalog);

        return JsonSerializer.SerializeToElement(document);
    }

    private static JsonElement HandleTemplatesSerialise(IInfiniFrameWindow _, JsonElement? parameters)
    {
        if (parameters is not { } p)
            throw new ArgumentException("Missing parameters");

        var document = p.Deserialize<KdlEditorDocument>()
            ?? throw new ArgumentException("Could not deserialise editor document");

        var text = KdlTemplateSerialiser.Serialise(document);
        return JsonSerializer.SerializeToElement(new { text });
    }

    private static JsonElement HandleTemplatesProjectClones(IInfiniFrameWindow _, JsonElement? __)
    {
        var root = ProjectWatcher.ProjectRoot;
        if (root is null)
            return JsonSerializer.SerializeToElement(new { clones = Array.Empty<object>() });

        var templatesDir = Path.Combine(root, "templates");
        if (!Directory.Exists(templatesDir))
            return JsonSerializer.SerializeToElement(new { clones = Array.Empty<object>() });

        var clones = new List<ProjectCloneEntryDto>();
        foreach (var file in Directory.EnumerateFiles(templatesDir, "*.kdl", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                var doc = KdlTemplateParser.ParseText(text);
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                foreach (var node in doc.Nodes)
                {
                    if (node.Kind == KdlEditorNodeKind.Clone && !string.IsNullOrEmpty(node.CloneId))
                    {
                        clones.Add(new ProjectCloneEntryDto
                        {
                            TemplateType = node.TemplateType,
                            Id = node.CloneId,
                            File = relativePath,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"templatesProjectClones: failed to parse '{file}': {ex.Message}");
            }
        }

        return JsonSerializer.SerializeToElement(new { clones });
    }

    private static JsonElement HandleTemplatesEnumMembers(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var gamePath = Path.GetDirectoryName(resolution.Context!.GameDataPath)
            ?? throw new InvalidOperationException("Could not derive game directory.");

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException($"Assembly-CSharp.dll not found at: {assemblyPath}");

        var additionalSearchDirs = new List<string>();
        var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonNet6))
            additionalSearchDirs.Add(melonNet6);

        using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs);
        // Note: `out _` would bind to the IInfiniFrameWindow parameter named
        // `_` on this method, so the discard is spelt with an explicit type.
        var type = catalog.ResolveType(typeName, out IReadOnlyList<Type> _, out var error);
        if (type == null || !type.IsEnum)
            throw new InvalidOperationException(error ?? $"'{typeName}' is not a known enum type.");

        var members = type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => new EnumMemberEntry
            {
                Name = f.Name,
                Value = Convert.ToInt32(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture),
            })
            .OrderBy(e => e.Value)
            .ToList();
        return JsonSerializer.SerializeToElement(new { members });
    }

    internal sealed class EnumMemberEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("value")]
        public required int Value { get; set; }
    }

    private static string? ComputeMemberPatchScalarKind(MemberShape m)
    {
        // For the member's effective leaf type, determine what patch value kind it uses.
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;

        if (TemplateTypeCatalog.IsScalar(leafType))
        {
            if (leafType.IsEnum) return "Enum";
            return leafType.FullName switch
            {
                "System.Boolean" => "Boolean",
                "System.Byte" => "Byte",
                "System.Int32" => "Int32",
                "System.Single" => "Single",
                "System.String" => "String",
                _ => null,
            };
        }

        if (TemplateTypeCatalog.IsTemplateReferenceTarget(leafType))
            return "TemplateReference";

        return null;
    }

    private static string? ComputeElementTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var elementType = TemplateTypeCatalog.GetElementType(m.MemberType);
        return elementType != null ? catalog.FriendlyName(elementType) : null;
    }

    private static string? ComputeEnumTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        return leafType.IsEnum ? catalog.FriendlyName(leafType) : null;
    }

    private static string? ComputeReferenceTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        return TemplateTypeCatalog.IsTemplateReferenceTarget(leafType)
            ? catalog.FriendlyName(leafType) : null;
    }

    private static TemplateQueryResultDto MapQueryResult(TemplateTypeCatalog catalog, QueryResult result)
    {
        return new TemplateQueryResultDto
        {
            Kind = result.Kind.ToString().ToLowerInvariant(),
            ResolvedPath = result.ResolvedPath,
            TypeName = result.CurrentType != null ? catalog.FriendlyName(result.CurrentType) : null,
            TypeFullName = result.CurrentType?.FullName,
            IsWritable = result.IsWritable,
            PatchScalarKind = result.PatchScalarKind?.ToString(),
            EnumMemberNames = result.EnumMemberNames.Count > 0 ? [.. result.EnumMemberNames] : null,
            ReferenceTargetTypeName = result.ReferenceTargetTypeName,
            IsLikelyOdinOnly = result.IsLikelyOdinOnly ? true : null,
            Members = result.Members?.Select(m => new TemplateMemberDto
            {
                Name = m.Name,
                TypeName = catalog.FriendlyName(m.MemberType),
                TypeFullName = m.MemberType.FullName,
                IsWritable = m.IsWritable,
                IsInherited = m.IsInherited,
                IsLikelyOdinOnly = m.IsLikelyOdinOnly ? true : null,
                IsCollection = TemplateTypeCatalog.GetElementType(m.MemberType) != null ? true : null,
                IsScalar = TemplateTypeCatalog.IsScalar(m.MemberType) ? true : null,
                IsTemplateReference = TemplateTypeCatalog.IsTemplateReferenceTarget(m.MemberType) ? true : null,
                PatchScalarKind = ComputeMemberPatchScalarKind(m),
                ElementTypeName = ComputeElementTypeName(catalog, m),
                EnumTypeName = ComputeEnumTypeName(catalog, m),
                ReferenceTypeName = ComputeReferenceTypeName(catalog, m),
                NamedArrayEnumTypeName = m.NamedArrayEnumTypeName,
                NumericMin = m.NumericMin,
                NumericMax = m.NumericMax,
                Tooltip = m.Tooltip,
                IsHiddenInInspector = m.IsHiddenInInspector ? true : null,
                IsSoundIdField = m.IsSoundIdField ? true : null,
            }).ToList(),
        };
    }

    // --- DTOs ---

    internal sealed class TemplateIndexStatusDto
    {
        [JsonPropertyName("state")]
        public required string State { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("instanceCount")]
        public int? InstanceCount { get; set; }

        [JsonPropertyName("typeCount")]
        public int? TypeCount { get; set; }

        [JsonPropertyName("indexedAt")]
        public DateTimeOffset? IndexedAt { get; set; }
    }

    internal sealed class TemplateSearchResultDto
    {
        [JsonPropertyName("types")]
        public required List<TemplateTypeEntry> Types { get; set; }

        [JsonPropertyName("instances")]
        public required List<TemplateInstanceEntry> Instances { get; set; }

        [JsonPropertyName("referencedBy")]
        public Dictionary<string, List<TemplateReferenceEntry>>? ReferencedBy { get; set; }
    }

    internal sealed class TemplateQueryResultDto
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; set; }

        [JsonPropertyName("resolvedPath")]
        public string? ResolvedPath { get; set; }

        [JsonPropertyName("typeName")]
        public string? TypeName { get; set; }

        [JsonPropertyName("typeFullName")]
        public string? TypeFullName { get; set; }

        [JsonPropertyName("isWritable")]
        public bool IsWritable { get; set; } = true;

        [JsonPropertyName("patchScalarKind")]
        public string? PatchScalarKind { get; set; }

        [JsonPropertyName("enumMemberNames")]
        public List<string>? EnumMemberNames { get; set; }

        [JsonPropertyName("referenceTargetTypeName")]
        public string? ReferenceTargetTypeName { get; set; }

        [JsonPropertyName("isLikelyOdinOnly")]
        public bool? IsLikelyOdinOnly { get; set; }

        [JsonPropertyName("members")]
        public List<TemplateMemberDto>? Members { get; set; }
    }

    internal sealed class TemplateMemberDto
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("typeName")]
        public required string TypeName { get; set; }

        [JsonPropertyName("typeFullName")]
        public string? TypeFullName { get; set; }

        [JsonPropertyName("isWritable")]
        public bool IsWritable { get; set; }

        [JsonPropertyName("isInherited")]
        public bool IsInherited { get; set; }

        [JsonPropertyName("isLikelyOdinOnly")]
        public bool? IsLikelyOdinOnly { get; set; }

        [JsonPropertyName("isCollection")]
        public bool? IsCollection { get; set; }

        [JsonPropertyName("isScalar")]
        public bool? IsScalar { get; set; }

        [JsonPropertyName("isTemplateReference")]
        public bool? IsTemplateReference { get; set; }

        [JsonPropertyName("patchScalarKind")]
        public string? PatchScalarKind { get; set; }

        [JsonPropertyName("elementTypeName")]
        public string? ElementTypeName { get; set; }

        [JsonPropertyName("enumTypeName")]
        public string? EnumTypeName { get; set; }

        [JsonPropertyName("referenceTypeName")]
        public string? ReferenceTypeName { get; set; }

        /// <summary>Short name of the enum paired with a
        /// <c>[NamedArray(typeof(T))]</c> array member; null otherwise.</summary>
        [JsonPropertyName("namedArrayEnumTypeName")]
        public string? NamedArrayEnumTypeName { get; set; }

        [JsonPropertyName("numericMin")]
        public double? NumericMin { get; set; }

        [JsonPropertyName("numericMax")]
        public double? NumericMax { get; set; }

        [JsonPropertyName("tooltip")]
        public string? Tooltip { get; set; }

        [JsonPropertyName("isHiddenInInspector")]
        public bool? IsHiddenInInspector { get; set; }

        [JsonPropertyName("isSoundIdField")]
        public bool? IsSoundIdField { get; set; }
    }

    internal sealed class ProjectCloneEntryDto
    {
        [JsonPropertyName("templateType")]
        public required string TemplateType { get; set; }

        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("file")]
        public required string File { get; set; }
    }

}
