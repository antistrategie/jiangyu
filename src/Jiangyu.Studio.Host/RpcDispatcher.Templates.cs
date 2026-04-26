using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Templates;
using Jiangyu.Shared;

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

    private static JsonElement HandleTemplatesIndex(IInfiniFrameWindow _, JsonElement? __)
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

    private static JsonElement HandleTemplatesSearch(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var query = TryGetString(parameters, "query");
        var className = TryGetString(parameters, "className");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
        var cachePath = resolution.Context.CachePath;
        if (_cachedIndex is null || _cachedIndexPath != cachePath)
        {
            _cachedIndex = service.LoadIndex();
            _cachedIndexPath = cachePath;
        }
        var index = _cachedIndex ?? throw new InvalidOperationException("Template index not found. Build it first.");

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

        return JsonSerializer.SerializeToElement(MapQueryResult(catalog, result), TemplatesJsonOptions);
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

    private static TemplateQueryResult MapQueryResult(TemplateTypeCatalog catalog, QueryResult result)
    {
        return new TemplateQueryResult
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

    private static readonly JsonSerializerOptions InspectJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
    private static Dictionary<string, List<InspectedFieldNode>>? _cachedValues;
    private static string? _cachedValuesPath;

    private static void ClearIndexCaches()
    {
        _cachedIndex = null;
        _cachedIndexPath = null;
        _cachedValues = null;
        _cachedValuesPath = null;
    }

    private static JsonElement HandleTemplatesInspect(IInfiniFrameWindow _, JsonElement? parameters)
    {
        var collection = RequireString(parameters, "collection");
        var pathId = RequireLong(parameters, "pathId");

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success)
            throw new InvalidOperationException(resolution.Error ?? "Could not resolve game data path.");

        var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);

        // Cache both the index and values in memory after first load.
        var cachePath = resolution.Context.CachePath;
        if (_cachedValues is null || _cachedValuesPath != cachePath)
        {
            _cachedValues = service.LoadValues();
            _cachedValuesPath = cachePath;
        }
        var values = _cachedValues;

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

    internal static void PreloadTemplateCaches()
    {
        try
        {
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success) return;
            var service = resolution.Context!.CreateTemplateIndexService(NullProgressSink.Instance, NullLogSink.Instance);
            _cachedIndex = service.LoadIndex();
            _cachedIndexPath = resolution.Context.CachePath;
            _cachedValues = service.LoadValues();
            _cachedValuesPath = resolution.Context.CachePath;
        }
        catch
        {
            // Silently ignore — caches will load on first RPC call instead.
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
