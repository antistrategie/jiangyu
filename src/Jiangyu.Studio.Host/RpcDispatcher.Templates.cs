using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniFrame;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

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
        var index = service.LoadIndex();

        if (index is null)
            throw new InvalidOperationException("Template index not found. Build it first.");

        // If no query, return the full index (types + instances).
        // Client does filtering.
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(className))
        {
            return JsonSerializer.SerializeToElement(new TemplateSearchResultDto
            {
                Types = index.TemplateTypes,
                Instances = index.Instances,
                ReferencedBy = index.ReferencedBy,
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

        using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs);

        var queryPath = string.IsNullOrWhiteSpace(fieldPath)
            ? typeName
            : $"{typeName}.{fieldPath}";

        var result = TemplateMemberQuery.Run(catalog, queryPath);

        if (result.Kind == QueryResultKind.Error)
            throw new InvalidOperationException(result.ErrorMessage ?? "Query failed.");

        return JsonSerializer.SerializeToElement(MapQueryResult(catalog, result));
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
            EnumMemberNames = result.EnumMemberNames.Count > 0 ? result.EnumMemberNames.ToList() : null,
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
    }

}
