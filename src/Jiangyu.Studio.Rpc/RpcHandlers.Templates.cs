using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Shared;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    private const string DefaultAssemblyRelativePath = "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll";
    private const string MelonLoaderNet6RelativePath = "MelonLoader/net6";

    [McpTool("jiangyu_templates_index_status",
        "Check whether the MENACE template index exists and is current. Returns state (\"current\", \"noGame\", \"stale\", etc.), instance/type counts, and indexed-at timestamp.")]
    internal static JsonElement TemplatesIndexStatus(JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            return JsonSerializer.SerializeToElement(new TemplateIndexStatus
            {
                State = "noGame",
                Reason = resolution.Error,
            });
        }

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var status = service.GetIndexStatus();
        var manifest = service.LoadManifest();

        return JsonSerializer.SerializeToElement(new TemplateIndexStatus
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

    [McpTool("jiangyu_templates_index",
        "Rebuild the MENACE template index. Required before template search/query/inspect work. Long-running; waits until complete.")]
    internal static JsonElement TemplatesIndex(JsonElement? __)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var ctx = resolution.Context!;
        ClearIndexCaches();

        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        service.BuildIndex();

        // Build supplement AFTER template index — AssetRipper Level2 initialises
        // LibCpp2IL first, then Cpp2IL can register instruction sets idempotently.
        BuildSupplementIfStale(ctx);

        var manifest = service.LoadManifest();
        return JsonSerializer.SerializeToElement(new TemplateIndexStatus
        {
            State = "current",
            InstanceCount = manifest?.InstanceCount,
            TypeCount = manifest?.TemplateTypeCount,
            IndexedAt = manifest?.IndexedAt,
        });
    }

    [McpTool("jiangyu_templates_search",
        "Search MENACE game templates by name or type substring. Returns {types, instances, referencedBy}.")]
    [McpParam("query", "string", "Search substring to match against template names. Empty returns the full index.")]
    [McpParam("className", "string", "Filter results to a specific template type (e.g. \"EntityTemplate\").")]
    internal static JsonElement TemplatesSearch(JsonElement? parameters)
    {
        var query = TryGetString(parameters, "query");
        var className = TryGetString(parameters, "className");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var index = EnsureIndexCached(resolution.Context!)
            ?? throw new InvalidOperationException("Template index not found. Build it first.");

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

            return JsonSerializer.SerializeToElement(new TemplateSearchResult
            {
                Types = string.IsNullOrWhiteSpace(className) ? index.TemplateTypes : [],
                Instances = instances,
                ReferencedBy = TemplateIndex.FilterReferencedBy(index.ReferencedBy, instances),
            });
        }

        var searchService = new TemplateSearchService(index);
        var result = searchService.Search(query ?? "", className);

        return JsonSerializer.SerializeToElement(new TemplateSearchResult
        {
            Types = result.MatchingTypes,
            Instances = result.MatchingInstances,
            ReferencedBy = TemplateIndex.FilterReferencedBy(index.ReferencedBy, result.MatchingInstances),
        });
    }

    [McpTool("jiangyu_templates_query",
        "Field schema for a template type, or for a specific field path. Returns field names, types, collection/scalar/reference flags, polymorphic base classes, writable status, member lists, and — for any enum or [NamedArray(typeof(T))] field — the paired enum's full {name, value} member list inlined as `enumMembers` (and `namedArrayEnumTypeName` for NamedArray fields). Always call this first when you need enum-member names or NamedArray index labels; do NOT guess the enum type and do NOT call jiangyu_templates_enum_members until you've seen the exact name in this response.")]
    [McpParam("typeName", "string", "Template type name (e.g. \"EntityTemplate\").", Required = true)]
    [McpParam("fieldPath", "string", "Dot-separated field path to drill into (e.g. \"Properties.Accuracy\").")]
    internal static JsonElement TemplatesQuery(JsonElement? parameters)
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

        EnsureIndexCached(resolution.Context!);
        return JsonSerializer.SerializeToElement(
            MapQueryResult(catalog, result, _cachedInstantiatedClassNames), TemplatesJsonOptions);
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

    [McpTool("jiangyu_templates_parse",
        "Parse a KDL template patch string and return the structured document or errors with line numbers. Use this to validate KDL you've written before saving to disk.")]
    [McpParam("text", "string", "KDL template patch source text to parse.", Required = true)]
    internal static JsonElement TemplatesParse(JsonElement? parameters)
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

    [McpTool("jiangyu_templates_serialise",
        "Serialise a parsed KDL template document back to KDL text. Returns {\"text\": \"...\"}. Useful for round-tripping after programmatic edits.")]
    [McpParam("document", "object", "The KdlEditorDocument object from jiangyu_templates_parse.", Required = true)]
    internal static JsonElement TemplatesSerialise(JsonElement? parameters)
    {
        if (parameters is not { } p)
            throw new ArgumentException("Missing parameters");

        var document = p.Deserialize<KdlEditorDocument>()
            ?? throw new ArgumentException("Could not deserialise editor document");

        var text = KdlTemplateSerialiser.Serialise(document);
        return JsonSerializer.SerializeToElement(new { text });
    }

    [McpTool("jiangyu_templates_project_clones",
        "List template clones already defined in the current project's templates/*.kdl files. Returns {\"clones\": [{\"templateType\", \"id\", \"file\"}, ...]}.")]
    internal static JsonElement TemplatesProjectClones(JsonElement? __)
    {
        var root = RpcContext.ProjectRoot;
        if (root is null)
            return JsonSerializer.SerializeToElement(new { clones = Array.Empty<object>() });

        var templatesDir = Path.Combine(root, "templates");
        if (!Directory.Exists(templatesDir))
            return JsonSerializer.SerializeToElement(new { clones = Array.Empty<object>() });

        var results = new List<ProjectCloneEntry>();
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
                        results.Add(new ProjectCloneEntry
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

        return JsonSerializer.SerializeToElement(new { clones = results });
    }

    [McpTool("jiangyu_templates_enum_members",
        "Direct enum-member lookup by exact type name. Returns {\"members\": [{\"name\": \"Basic\", \"value\": 0}, ...]}. PREFER jiangyu_templates_query first — its response inlines enumMembers for any enum or NamedArray field, in one call, with no need to know the enum's name in advance. Only fall back to this tool when you already have the exact enum type name in hand (e.g. from a prior query response) and don't need the surrounding schema.")]
    [McpParam("typeName", "string", "Fully qualified or short enum type name (e.g. \"PerkTier\"). Must match the assembly exactly — do not guess. Get the correct name from jiangyu_templates_query's namedArrayEnumTypeName / enumTypeName fields.", Required = true)]
    internal static JsonElement TemplatesEnumMembers(JsonElement? parameters)
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
        return JsonSerializer.SerializeToElement(new EnumMembersResult { Members = members });
    }

    [RpcType]
    internal sealed class EnumMembersResult
    {
        [JsonPropertyName("members")]
        public required List<EnumMemberEntry> Members { get; set; }
    }

    [RpcType]
    internal sealed class EnumMemberEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("value")]
        public required int Value { get; set; }
    }

    private static string? ComputeMemberPatchScalarKind(MemberShape m)
    {
        // Single source of truth: TemplateMemberQuery.MapScalarKind
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;

        if (TemplateTypeCatalog.IsScalar(leafType))
            return TemplateMemberQuery.MapScalarKind(leafType)?.ToString();

        // ScriptableObject leaves split two ways: DataTemplate descendants are
        // patched via a TemplateReference (loader looks them up by name in
        // m_TemplateMaps); non-DataTemplate ScriptableObjects (e.g. owned
        // EventHandler list elements) are constructed inline as
        // HandlerConstruction. Returning null for the latter routes the visual
        // editor to the composite/handler default-value path instead.
        if (TemplateTypeCatalog.IsTemplateReferenceTarget(leafType)
            && TemplateTypeCatalog.IsDataTemplateType(leafType))
        {
            return "TemplateReference";
        }

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

    /// <summary>
    /// Resolves the enum paired with this member (either as a regular enum
    /// scalar/element or via <c>[NamedArray(typeof(T))]</c>) and returns its
    /// members as <see cref="EnumMemberEntry"/> pairs. Inlined on member
    /// schema so the visual editor doesn't need a follow-up
    /// <c>templatesEnumMembers</c> RPC for either dropdown surface.
    /// </summary>
    private static List<EnumMemberEntry>? ComputeEnumMembers(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        Type? enumType = leafType.IsEnum ? leafType : null;

        if (enumType is null && m.NamedArrayEnumTypeName is not null)
        {
            enumType = catalog.ResolveType(m.NamedArrayEnumTypeName, out _, out _);
        }

        return enumType is { IsEnum: true } ? BuildEnumMembers(enumType) : null;
    }

    private static List<EnumMemberEntry> BuildEnumMembers(Type enumType)
    {
        return [.. enumType
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => new EnumMemberEntry
            {
                Name = f.Name,
                Value = Convert.ToInt32(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture),
            })
            .OrderBy(e => e.Value)];
    }

    private static string? ComputeReferenceTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        // Mirror ComputeMemberPatchScalarKind: ReferenceTypeName drives the
        // ref-combobox UX, which only makes sense for DataTemplate-backed
        // refs. Owned ScriptableObjects use ElementSubtypes instead.
        return TemplateTypeCatalog.IsTemplateReferenceTarget(leafType)
            && TemplateTypeCatalog.IsDataTemplateType(leafType)
            ? catalog.FriendlyName(leafType) : null;
    }

    /// <summary>
    /// True when the destination's reference target has descendant reference
    /// types in the assembly, so the modder needs to disambiguate with an
    /// explicit concrete type. The editor uses this to keep the ref-type
    /// combobox visible. Structural check (any strict ref-target descendant)
    /// rather than <see cref="Type.IsAbstract"/> because Il2CppInterop
    /// wrappers strip the abstract bit on generation. Null when the field is
    /// not a reference target or the leaf type is the only ref target in its
    /// branch (selector hidden).
    /// </summary>
    private static bool? ComputeReferenceTypeIsPolymorphic(
        TemplateTypeCatalog catalog,
        MemberShape m,
        IReadOnlySet<string> instantiatedClassNames)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        if (!TemplateTypeCatalog.IsTemplateReferenceTarget(leafType))
            return null;
        // Construction-style polymorphism (owned ScriptableObject lists like
        // EventHandlers) is reported via ElementSubtypes; the ref-polymorphism
        // flag is reserved for DataTemplate-backed lookups so the visual
        // editor's two pickers don't both fire on the same member.
        if (!TemplateTypeCatalog.IsDataTemplateType(leafType))
            return null;
        if (!catalog.HasReferenceSubtype(leafType))
            return null;
        // Refine: structural subtype existence alone over-flags concrete
        // first-class types whose descendants happen to inherit from them
        // (e.g. PerkTemplate : SkillTemplate, even though SkillTemplate has
        // its own 500+ instances and modders don't pick a subtype here).
        // Only treat as polymorphic when the leaf type has zero direct
        // instances in the index, the same shape an abstract base like
        // BaseItemTemplate has.
        var leafFriendlyName = catalog.FriendlyName(leafType);
        if (instantiatedClassNames.Contains(leafFriendlyName))
            return null;
        return true;
    }

    /// <summary>
    /// Concrete subtypes the modder can pick when appending to a polymorphic
    /// owned-element collection (e.g. EventHandlers). Populated only for
    /// collections whose element type is a non-DataTemplate ScriptableObject
    /// base with strict descendants — this is the construction-style
    /// polymorphism (the modder builds a fresh subordinate object), distinct
    /// from ref-style polymorphism where the modder picks an existing
    /// DataTemplateLoader instance. Null otherwise so the visual editor falls
    /// through to the standard composite/ref flow.
    /// </summary>
    private static List<string>? ComputeElementSubtypes(TemplateTypeCatalog catalog, MemberShape m)
    {
        var elementType = TemplateTypeCatalog.GetElementType(m.MemberType);
        if (elementType is null) return null;
        if (!TemplateTypeCatalog.IsTemplateReferenceTarget(elementType)) return null;
        if (TemplateTypeCatalog.IsDataTemplateType(elementType)) return null;
        if (!catalog.HasReferenceSubtype(elementType)) return null;

        var subtypes = catalog.EnumerateConcreteSubtypes(elementType);
        if (subtypes.Count == 0) return null;
        return [.. subtypes
            .Select(catalog.FriendlyName)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    private static TemplateQueryResult MapQueryResult(
        TemplateTypeCatalog catalog,
        QueryResult result,
        IReadOnlySet<string> instantiatedClassNames)
    {
        // Leaf-path enum members: when the terminal type is an enum, surface
        // {name, value} pairs so the visual editor doesn't need a separate
        // templatesEnumMembers RPC for the dropdown. NamedArray leaves (the
        // array unwraps to its element type — Byte for `InitialAttributes` —
        // so the leaf type isn't an enum, but the paired-enum name lives on
        // result.NamedArrayEnumTypeName; resolve and inline that too).
        Type? leafEnumType = result.CurrentType is { IsEnum: true } e
            ? e
            : result.NamedArrayEnumTypeName is { } namedEnum
                ? catalog.ResolveType(namedEnum, out _, out _)
                : null;
        var leafEnumMembers = leafEnumType is { IsEnum: true }
            ? BuildEnumMembers(leafEnumType)
            : null;

        return new TemplateQueryResult
        {
            Kind = result.Kind.ToString().ToLowerInvariant(),
            ResolvedPath = result.ResolvedPath,
            TypeName = result.CurrentType != null ? catalog.FriendlyName(result.CurrentType) : null,
            TypeFullName = result.CurrentType?.FullName,
            IsWritable = result.IsWritable,
            PatchScalarKind = result.PatchScalarKind?.ToString(),
            EnumMemberNames = result.EnumMemberNames.Count > 0 ? [.. result.EnumMemberNames] : null,
            EnumMembers = leafEnumMembers,
            NamedArrayEnumTypeName = result.NamedArrayEnumTypeName,
            ReferenceTargetTypeName = result.ReferenceTargetTypeName,
            IsLikelyOdinOnly = result.IsLikelyOdinOnly ? true : null,
            Members = result.Members?.Select(m => new TemplateMember
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
                IsReferenceTypePolymorphic = ComputeReferenceTypeIsPolymorphic(catalog, m, instantiatedClassNames),
                ElementSubtypes = ComputeElementSubtypes(catalog, m),
                NamedArrayEnumTypeName = m.NamedArrayEnumTypeName,
                EnumMembers = ComputeEnumMembers(catalog, m),
                NumericMin = m.NumericMin,
                NumericMax = m.NumericMax,
                Tooltip = m.Tooltip,
                IsHiddenInInspector = m.IsHiddenInInspector ? true : null,
                IsSoundIdField = m.IsSoundIdField ? true : null,
            }).ToList(),
        };
    }

    [RpcType]
    internal sealed class TemplateIndexStatus
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

    [RpcType]
    internal sealed class TemplateSearchResult
    {
        [JsonPropertyName("types")]
        public required List<TemplateTypeEntry> Types { get; set; }

        [JsonPropertyName("instances")]
        public required List<TemplateInstanceEntry> Instances { get; set; }

        [JsonPropertyName("referencedBy")]
        public Dictionary<string, List<TemplateReferenceEntry>>? ReferencedBy { get; set; }
    }

    [RpcType]
    internal sealed class TemplateQueryResult
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

        /// <summary>{name, value} pairs for the leaf enum type. Set whenever
        /// the terminal type is an enum OR the leaf is a
        /// <c>[NamedArray(typeof(T))]</c> primitive-element field (in which
        /// case the members come from the paired enum, not from the leaf
        /// element type). Lets agents and the visual editor render dropdowns
        /// without a follow-up <c>templatesEnumMembers</c> call.</summary>
        [JsonPropertyName("enumMembers")]
        public List<EnumMemberEntry>? EnumMembers { get; set; }

        /// <summary>Short name of the enum paired with a
        /// <c>[NamedArray(typeof(T))]</c> primitive-element leaf. Null
        /// otherwise. Mirrors the same field on member entries.</summary>
        [JsonPropertyName("namedArrayEnumTypeName")]
        public string? NamedArrayEnumTypeName { get; set; }

        [JsonPropertyName("referenceTargetTypeName")]
        public string? ReferenceTargetTypeName { get; set; }

        [JsonPropertyName("isLikelyOdinOnly")]
        public bool? IsLikelyOdinOnly { get; set; }

        [JsonPropertyName("members")]
        public List<TemplateMember>? Members { get; set; }
    }

    [RpcType]
    internal sealed class TemplateMember
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

        /// <summary>True when <see cref="ReferenceTypeName"/> is an abstract
        /// base. The editor keeps the ref-type combobox visible so the modder
        /// can pick a concrete subtype; null for monomorphic / non-reference
        /// fields so JSON omits the property.</summary>
        [JsonPropertyName("isReferenceTypePolymorphic")]
        public bool? IsReferenceTypePolymorphic { get; set; }

        /// <summary>Concrete subtype short-names the modder can pick when
        /// appending to an owned polymorphic-element collection. Populated for
        /// construction-style polymorphic collections only (e.g. EventHandlers
        /// → BaseEventHandlerTemplate); null otherwise so the visual editor
        /// keeps the standard composite/ref flow.</summary>
        [JsonPropertyName("elementSubtypes")]
        public List<string>? ElementSubtypes { get; set; }

        /// <summary>Short name of the enum paired with a
        /// <c>[NamedArray(typeof(T))]</c> array member; null otherwise.</summary>
        [JsonPropertyName("namedArrayEnumTypeName")]
        public string? NamedArrayEnumTypeName { get; set; }

        /// <summary>{name, value} pairs for the member's enum (regular enum
        /// scalar/element OR the named-array's paired enum). Inlined so the
        /// visual editor can populate dropdowns without a follow-up
        /// <c>templatesEnumMembers</c> RPC. Null when the member doesn't
        /// touch an enum type.</summary>
        [JsonPropertyName("enumMembers")]
        public List<EnumMemberEntry>? EnumMembers { get; set; }

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

    [RpcType]
    internal sealed class ProjectCloneEntry
    {
        [JsonPropertyName("templateType")]
        public required string TemplateType { get; set; }

        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("file")]
        public required string File { get; set; }
    }

    [RpcType]
    internal sealed class TemplateValueResult
    {
        /// <summary>
        /// True when the (typeName, id) tuple resolved to a known template
        /// instance and its serialised values were loaded. False when the
        /// tuple does not match any vanilla template (e.g. a clone the modder
        /// just authored, or a misspelt id), in which case <see cref="Fields"/>
        /// is an empty list.
        /// </summary>
        [JsonPropertyName("found")]
        public required bool Found { get; set; }

        /// <summary>
        /// Top-level serialised fields of the matched template's m_Structure
        /// (or the whole inspection tree for non-MonoBehaviour templates).
        /// Empty when <see cref="Found"/> is false or when the values cache
        /// has not been built (caller should fall back to neutral defaults).
        /// </summary>
        [JsonPropertyName("fields")]
        public required List<InspectedFieldNode> Fields { get; set; }
    }

    private static readonly JsonSerializerOptions InspectJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Match TemplateIndexService.JsonOptions: cached values can carry
        // Infinity/NaN floats (game defaults), so the wire format has to
        // accept them too or templatesValue / templatesInspect crash on send.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// Options used for templatesQuery / templatesParse / templatesSerialise responses.
    /// Omits nulls so the frontend sees <c>undefined</c> (not <c>null</c>) for absent
    /// optional fields like numericMin/numericMax — null-on-the-wire false-triggers
    /// numeric validation range checks.
    /// </summary>
    private static readonly JsonSerializerOptions TemplatesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static TemplateIndex? _cachedIndex;
    private static string? _cachedIndexPath;
    private static DateTime _cachedIndexMtime;
    private static Dictionary<string, List<InspectedFieldNode>>? _cachedValues;
    private static string? _cachedValuesPath;
    private static DateTime _cachedValuesMtime;

    // Derived projections rebuilt alongside _cachedIndex so per-RPC reads stay
    // O(1): membership lookup for the polymorphism heuristic, and
    // (className, name)-keyed instance lookup for templatesValue. Without
    // these the visual editor pays an O(instances) scan per NodeCard mount.
    private static HashSet<string> _cachedInstantiatedClassNames = new(StringComparer.Ordinal);
    private static Dictionary<(string ClassName, string Name), TemplateInstanceEntry> _cachedInstanceLookup = new();

    private static void ClearIndexCaches()
    {
        _cachedIndex = null;
        _cachedIndexPath = null;
        _cachedIndexMtime = default;
        _cachedInstantiatedClassNames.Clear();
        _cachedInstanceLookup.Clear();
        _cachedValues = null;
        _cachedValuesPath = null;
        _cachedValuesMtime = default;
    }

    // Reloads <see cref="_cachedIndex"/> from disk when the file's mtime
    // diverges from the cached one (handles CLI rebuilds during a Studio
    // session). Also rebuilds the derived class-name set and instance-lookup
    // caches so callers can read them directly. Returns the cached index
    // (possibly null when the index file doesn't exist yet).
    private static TemplateIndex? EnsureIndexCached(EnvironmentContext ctx)
    {
        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var cachePath = ctx.CachePath;
        DateTime indexMtime;
        try { indexMtime = File.GetLastWriteTimeUtc(service.IndexPath); }
        catch { return _cachedIndex; }

        if (_cachedIndex is not null && _cachedIndexPath == cachePath && _cachedIndexMtime == indexMtime)
            return _cachedIndex;

        _cachedIndex = service.LoadIndex();
        _cachedIndexPath = cachePath;
        _cachedIndexMtime = indexMtime;
        _cachedInstantiatedClassNames.Clear();
        _cachedInstanceLookup.Clear();
        if (_cachedIndex is not null)
        {
            foreach (var instance in _cachedIndex.Instances)
            {
                _cachedInstantiatedClassNames.Add(instance.ClassName);
                _cachedInstanceLookup[(instance.ClassName, instance.Name)] = instance;
            }
        }
        return _cachedIndex;
    }

    // Reloads <see cref="_cachedValues"/> from disk when the file's mtime
    // diverges from the cached one. Returns the cached values dictionary
    // (possibly null when no values file exists yet).
    private static Dictionary<string, List<InspectedFieldNode>>? EnsureValuesCached(EnvironmentContext ctx)
    {
        var service = ctx.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var cachePath = ctx.CachePath;
        DateTime valuesMtime;
        try { valuesMtime = File.GetLastWriteTimeUtc(service.ValuesPath); }
        catch { return _cachedValues; }

        if (_cachedValues is not null && _cachedValuesPath == cachePath && _cachedValuesMtime == valuesMtime)
            return _cachedValues;

        _cachedValues = service.LoadValues();
        _cachedValuesPath = cachePath;
        _cachedValuesMtime = valuesMtime;
        return _cachedValues;
    }

    [McpTool("jiangyu_templates_inspect",
        "Inspect a specific template instance's full structure and current vanilla values. Returns the object's fields, options, and nested values. Use the collection and pathId from jiangyu_templates_search results. For [NamedArray(typeof(T))] fields each array element carries its paired enum-member name on the element node (so InitialAttributes[4] reads as {\"name\": \"Vitality\", \"value\": 70}); no follow-up enum lookup is needed.")]
    [McpParam("collection", "string", "Asset collection name (e.g. \"resources.assets\").", Required = true)]
    [McpParam("pathId", "integer", "PathID of the template instance (Int64).", Required = true)]
    internal static JsonElement TemplatesInspect(JsonElement? parameters)
    {
        var collection = RequireString(parameters, "collection");
        var pathId = RequireLong(parameters, "pathId");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var values = EnsureValuesCached(resolution.Context!);

        List<InspectedFieldNode> fields;
        if (values is not null)
        {
            var key = $"{collection}:{pathId}";
            fields = values.TryGetValue(key, out var cached) ? cached : [];
        }
        else
        {
            // Fall back to live inspection if values cache is missing.
            var inspectionService = resolution.Context!.CreateObjectInspectionService(
                NullProgressSink.Instance, NullLogSink.Instance);
            var result = inspectionService.Inspect(new ObjectInspectionRequest
            {
                Collection = collection,
                PathId = pathId,
                MaxDepth = 4,
                MaxArraySampleLength = 0,
            });
            var structure = result.Fields.FirstOrDefault(f =>
                string.Equals(f.Name, "m_Structure", StringComparison.Ordinal));
            fields = structure?.Fields ?? result.Fields;
            return JsonSerializer.SerializeToElement(result, InspectJsonOptions);
        }

        // Return m_Structure fields directly — the frontend looks for m_Structure
        // and falls back to the raw list, so pre-extracting is transparent.
        return JsonSerializer.SerializeToElement(new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = $"template ({collection})",
                ClassName = "Template",
                Collection = collection,
                PathId = pathId,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = 0,
                MaxArraySampleLength = 0,
                Truncated = false,
            },
            Fields = fields,
        },
        InspectJsonOptions);
    }

    [McpTool("jiangyu_templates_value",
        "Read field values from a specific template instance by type name and id. Returns {\"found\": true, \"fields\": [...]} with the instance's field tree. Same shape as jiangyu_templates_inspect — [NamedArray(typeof(T))] elements carry the paired enum-member name on each slot.")]
    [McpParam("typeName", "string", "Template type name (e.g. \"EntityTemplate\").", Required = true)]
    [McpParam("id", "string", "Template instance ID (e.g. \"player_squad.darby\").", Required = true)]
    internal static JsonElement TemplatesValue(JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");
        var id = RequireString(parameters, "id");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
        {
            // No game configured: caller falls back to neutral defaults.
            return JsonSerializer.SerializeToElement(
                new TemplateValueResult { Found = false, Fields = [] }, InspectJsonOptions);
        }

        var ctx = resolution.Context!;
        if (EnsureIndexCached(ctx) is null
            || !_cachedInstanceLookup.TryGetValue((typeName, id), out var instance))
        {
            return JsonSerializer.SerializeToElement(
                new TemplateValueResult { Found = false, Fields = [] }, InspectJsonOptions);
        }

        var values = EnsureValuesCached(ctx);
        var key = TemplateIndex.IdentityKey(instance.Identity);
        var fields = (values is not null && values.TryGetValue(key, out var cached)) ? cached : [];

        return JsonSerializer.SerializeToElement(
            new TemplateValueResult { Found = fields.Count > 0, Fields = fields },
            InspectJsonOptions);
    }

    public static void PreloadTemplateCaches()
    {
        try
        {
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success) return;
            EnsureIndexCached(resolution.Context!);
            EnsureValuesCached(resolution.Context!);
        }
        catch
        {
            // Caches will load on first RPC call instead.
        }
    }

    private static void BuildSupplementIfStale(EnvironmentContext ctx)
    {
        var gameRoot = Path.GetDirectoryName(ctx.GameDataPath);
        if (gameRoot is null) return;

        var gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.so");
        if (!File.Exists(gameAssemblyPath))
            gameAssemblyPath = Path.Combine(gameRoot, "GameAssembly.dll");
        if (!File.Exists(gameAssemblyPath)) return;

        var metadataPath = Path.Combine(ctx.GameDataPath, "il2cpp_data", "Metadata", "global-metadata.dat");
        if (!File.Exists(metadataPath)) return;

        if (Il2CppMetadataCache.LoadIfFresh(ctx.CachePath, gameAssemblyPath, metadataPath) is not null)
            return;

        var unityVersion = GetGameUnityVersionCached(ctx.GameDataPath);
        if (unityVersion is null) return;

        Il2CppMetadataCache.BuildAndPersist(ctx.CachePath, gameAssemblyPath, metadataPath, unityVersion.Value, NullLogSink.Instance);
    }
}
