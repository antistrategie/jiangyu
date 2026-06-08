using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Code;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_templates_search",
        "Search MENACE game templates by name or type substring. Returns {types, instances, referencedBy}.")]
    [McpParam("query", "string", "Search substring to match against template names. Empty returns the full index.")]
    [McpParam("className", "string", "Filter results to a specific template type (e.g. \"EntityTemplate\").")]
    internal static JsonElement TemplatesSearch(JsonElement? parameters)
    {
        var query = TryGetString(parameters, "query");
        var className = TryGetString(parameters, "className");

        var index = EnsureIndexCached(RpcHelpers.RequireEnvironment())
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

        var result = TemplateSearch.Search(index, query ?? "", className);

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
    [McpParam("namespaceName", "string", "Optional CLR namespace of the script class (e.g. \"Menace.Tactical.Skills.Effects\"). Disambiguates short-name collisions.")]
    [McpParam("elementType", "string", "Optional parent collection element-type for polymorphic subtype lookups. When the typeName is a subtype short name (e.g. \"Attack\") and the caller knows the parent family (e.g. \"SkillEventHandlerTemplate\"), this restricts resolution to that family's concrete subtypes.")]
    internal static JsonElement TemplatesQuery(JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");
        var fieldPath = TryGetString(parameters, "fieldPath");
        var namespaceName = TryGetString(parameters, "namespaceName");
        var elementType = TryGetString(parameters, "elementType");
        // Discriminator-aware lookup: when the editor renders a
        // tagged-string composite, it knows the polymorphic base from the
        // parent member's TaggedPolymorphicBase and the modder-picked
        // discriminator from value.compositeType. Pass both via
        // discriminatorBase + typeName-as-discriminator and the handler
        // resolves to the concrete CLR type before running the standard
        // member walk. Null for non-tagged composite queries.
        var discriminatorBase = TryGetString(parameters, "discriminatorBase");

        var gamePath = Path.GetDirectoryName(RpcHelpers.RequireEnvironment().GameDataPath)
            ?? throw new InvalidOperationException("Could not derive game directory.");

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException($"Assembly-CSharp.dll not found at: {assemblyPath}");

        var additionalSearchDirs = new List<string>();
        var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonNet6))
            additionalSearchDirs.Add(melonNet6);

        // Scan the open project's compiled code DLLs into the catalog so the mod's
        // [JiangyuType]s take part in the normal subtype/member walks: they surface in
        // a polymorphic field's subtype choices (named modId:Name below) and resolve as
        // first-class types when one is queried directly.
        IReadOnlyList<string> codeAssemblies = [];
        string? modId = null;
        if (RpcContext.ProjectRoot is { } projectRoot)
        {
            var (assemblies, searchDirs) = CodeTypeResolver.LoadInputs(Path.Combine(projectRoot, "compiled", Jiangyu.Shared.Bundles.CompiledLayout.CodeDirName));
            codeAssemblies = assemblies;
            // No code DLLs means no mod types to scan or label, so skip the search-dir
            // wiring and the manifest read that only the modId:Name labelling needs.
            if (assemblies.Count > 0)
            {
                additionalSearchDirs.AddRange(searchDirs);
                modId = ModManifest.TryLoad(projectRoot)?.Name;
            }
        }

        var supplement = Il2CppMetadataCache.LoadIfPresent(RpcHelpers.RequireEnvironment().CachePath);
        // Cached + reused across queries; reloads when the game assembly or any code DLL
        // changes (mtime in the key). The cache owns the catalog's lifetime, so no using.
        var catalog = GetOrLoadQueryCatalog(assemblyPath, additionalSearchDirs, supplement, codeAssemblies);

        // Make sure the discriminator allowlist is installed BEFORE
        // ResolveTaggedDiscriminator runs (otherwise the validator's
        // catalog-side gate falls back to heuristic and we'd accept any
        // candidate).
        EnsureTaggedDiscriminatorsInstalled(RpcHelpers.RequireEnvironment());

        // Discriminator-aware resolution. The frontend passes the
        // polymorphic base (FQN) and the modder's picked discriminator
        // (as typeName). Translate to the concrete CLR type so the rest
        // of the query walks its members normally.
        if (!string.IsNullOrWhiteSpace(discriminatorBase))
        {
            var baseType = catalog.ResolveType(discriminatorBase, out _, out var baseError);
            if (baseType is null)
                throw new InvalidOperationException(baseError ?? $"Unknown discriminator base '{discriminatorBase}'.");
            var resolved = catalog.ResolveTaggedDiscriminator(baseType, typeName, out var amb);
            if (resolved is null)
            {
                var note = amb.Count > 0 ? " known: " + string.Join(", ", amb) : string.Empty;
                throw new InvalidOperationException(
                    $"Discriminator '{typeName}' is not valid under {discriminatorBase}.{note}");
            }
            typeName = resolved.FullName ?? resolved.Name;
        }

        // A qualified modId:Name is a mod [JiangyuType]: resolve it to the CLR FullName
        // the member walk understands (its own fields plus the inherited game members).
        if (typeName.Contains(':')
            && CodeTypeResolver.ResolveFullName(catalog, CodeTypeResolver.BareName(typeName)) is { } codeFullName)
            typeName = codeFullName;

        var queryPath = string.IsNullOrWhiteSpace(fieldPath)
            ? typeName
            : $"{typeName}.{fieldPath}";

        var result = TemplateMemberQuery.Run(
            catalog,
            queryPath,
            namespaceHint: namespaceName,
            elementTypeContext: elementType);

        if (result.Kind == QueryResultKind.Error)
            throw new InvalidOperationException(result.ErrorMessage ?? "Query failed.");

        EnsureIndexCached(RpcHelpers.RequireEnvironment());
        // Build the Odin matrix registry now if it isn't already; it needs
        // the values cache plus the index, and templatesQuery is the
        // common entry that surfaces these schemas to the UI.
        EnsureValuesCached(RpcHelpers.RequireEnvironment());
        return JsonSerializer.SerializeToElement(
            MapQueryResult(catalog, result, _cachedInstantiatedClassNames, _cachedOdinMatrixSchemas, modId),
            TemplatesJsonOptions);
    }
    private static bool IsCatchAllRuntimeType(Type type)
    {
        return type.FullName switch
        {
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase" => true,
            "Il2CppSystem.Object" => true,
            "System.Object" => true,
            _ => false,
        };
    }

    private static TemplateQueryResult MapQueryResult(
        TemplateTypeCatalog catalog,
        QueryResult result,
        IReadOnlySet<string> instantiatedClassNames,
        IReadOnlyDictionary<string, Dictionary<string, OdinMatrixFieldSchema>> odinMatrixSchemas,
        string? modId)
    {
        // Owner type for the members list — used as the registry key.
        // Type.Name strips the IL2CPP namespace prefix so it matches the
        // simple class name recorded by the indexer.
        var ownerTypeName = result.CurrentType?.Name;
        odinMatrixSchemas.TryGetValue(ownerTypeName ?? string.Empty, out var ownerMatrixSchemas);

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
            Members = result.Members?.Select(m =>
            {
                OdinMatrixFieldSchema? matrixSchema = null;
                ownerMatrixSchemas?.TryGetValue(m.Name, out matrixSchema);
                return new TemplateMember
                {
                    Name = m.Name,
                    // ResolvableName keeps a scalar-composite member's type=
                    // value resolvable when its short name collides across
                    // namespaces; generic/collection names stay friendly.
                    TypeName = catalog.ResolvableName(m.MemberType),
                    TypeFullName = m.MemberType.FullName,
                    IsWritable = m.IsWritable,
                    IsInherited = m.IsInherited,
                    IsLikelyOdinOnly = m.IsLikelyOdinOnly ? true : null,
                    IsCollection = TemplateTypeCatalog.GetElementType(m.MemberType) != null ? true : null,
                    IsScalar = TemplateTypeCatalog.IsScalar(m.MemberType) ? true : null,
                    IsTemplateReference = TemplateTypeCatalog.IsTemplateReferenceTarget(m.MemberType) ? true : null,
                    IsAssetReference = Jiangyu.Shared.Replacements.AssetCategory.IsSupported(m.MemberType.Name) ? true : null,
                    PatchScalarKind = ComputeMemberPatchScalarKind(catalog, m),
                    ElementTypeName = ComputeElementTypeName(catalog, m),
                    EnumTypeName = ComputeEnumTypeName(catalog, m),
                    ReferenceTypeName = ComputeReferenceTypeName(catalog, m),
                    IsReferenceTypePolymorphic = ComputeReferenceTypeIsPolymorphic(catalog, m, instantiatedClassNames),
                    ElementSubtypes = ComputeElementSubtypes(catalog, m, modId),
                    ScalarSubtypes = ComputeScalarSubtypes(catalog, m, modId),
                    TaggedPolymorphicBase = m.TaggedPolymorphicBase is { } baseType
                        ? catalog.FriendlyName(baseType)
                        : null,
                    TaggedDiscriminators = ComputeTaggedDiscriminators(m),
                    NamedArrayEnumTypeName = m.NamedArrayEnumTypeName,
                    EnumMembers = ComputeEnumMembers(catalog, m),
                    NumericMin = m.NumericMin,
                    NumericMax = m.NumericMax,
                    Tooltip = m.Tooltip,
                    IsHiddenInInspector = m.IsHiddenInInspector ? true : null,
                    IsSoundIdField = m.IsSoundIdField ? true : null,
                    IsOdinMultiDimArray = matrixSchema != null ? true : null,
                    MultiDimRank = matrixSchema?.Rank,
                    MultiDimDimensions = matrixSchema?.Dimensions.ToList(),
                    MultiDimElementType = matrixSchema?.ElementTypeName,
                    MultiDimElementKind = matrixSchema?.ElementKind,
                    IsOdinHashSet = m.IsOdinHashSet ? true : null,
                };
            }).ToList(),
        };
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
}
