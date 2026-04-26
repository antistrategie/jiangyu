using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Core.Models;

public sealed class TemplateIndex
{
    [JsonPropertyName("classification")]
    public required TemplateClassificationMetadata Classification { get; init; }

    [JsonPropertyName("templateTypes")]
    public required List<TemplateTypeEntry> TemplateTypes { get; init; }

    [JsonPropertyName("instances")]
    public required List<TemplateInstanceEntry> Instances { get; init; }

    /// <summary>
    /// Reverse lookup: for each target identity key ("collection:pathId"),
    /// the list of sources that reference it.
    /// </summary>
    [JsonPropertyName("referencedBy")]
    public Dictionary<string, List<TemplateReferenceEntry>>? ReferencedBy { get; set; }

    public static string IdentityKey(TemplateIdentity id) => $"{id.Collection}:{id.PathId}";

    /// <summary>
    /// Restrict a reverse-lookup <c>referencedBy</c> dictionary so each target
    /// only lists sources present in <paramref name="visibleInstances"/>. Drops
    /// targets whose source list becomes empty. Returns <c>null</c> when the
    /// input is null/empty or when the filter leaves nothing.
    /// </summary>
    public static Dictionary<string, List<TemplateReferenceEntry>>? FilterReferencedBy(
        Dictionary<string, List<TemplateReferenceEntry>>? referencedBy,
        IReadOnlyList<TemplateInstanceEntry> visibleInstances)
    {
        if (referencedBy is null || referencedBy.Count == 0)
            return referencedBy;

        var visibleKeys = new HashSet<string>(
            visibleInstances.Select(i => IdentityKey(i.Identity)),
            StringComparer.Ordinal);

        var filtered = new Dictionary<string, List<TemplateReferenceEntry>>(referencedBy.Count);
        foreach (var (targetKey, entries) in referencedBy)
        {
            var kept = entries
                .Where(e => visibleKeys.Contains(IdentityKey(e.Source)))
                .ToList();
            if (kept.Count > 0)
                filtered[targetKey] = kept;
        }

        return filtered.Count > 0 ? filtered : null;
    }
}

public sealed class TemplateClassificationMetadata
{
    [JsonPropertyName("ruleVersion")]
    public required string RuleVersion { get; init; }

    [JsonPropertyName("ruleDescription")]
    public required string RuleDescription { get; init; }
}

[RpcType]
public sealed class TemplateTypeEntry
{
    [JsonPropertyName("className")]
    public required string ClassName { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("classifiedVia")]
    public required string ClassifiedVia { get; init; }

    [JsonPropertyName("templateAncestor")]
    public string? TemplateAncestor { get; init; }
}

[RpcType]
public sealed class TemplateIdentity
{
    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    [JsonPropertyName("pathId")]
    public required long PathId { get; init; }
}

[RpcType]
public sealed class TemplateInstanceEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("className")]
    public required string ClassName { get; init; }

    [JsonPropertyName("identity")]
    public required TemplateIdentity Identity { get; init; }

    [JsonPropertyName("references")]
    public List<TemplateEdge>? References { get; set; }
}

[RpcType]
public sealed class TemplateEdge
{
    [JsonPropertyName("fieldName")]
    public required string FieldName { get; init; }

    [JsonPropertyName("target")]
    public required TemplateIdentity Target { get; init; }
}

[RpcType]
public sealed class TemplateReferenceEntry
{
    [JsonPropertyName("source")]
    public required TemplateIdentity Source { get; init; }

    [JsonPropertyName("fieldName")]
    public required string FieldName { get; init; }
}

public sealed class TemplateIndexManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("gameAssemblyHash")]
    public string? GameAssemblyHash { get; init; }

    [JsonPropertyName("indexedAt")]
    public required DateTimeOffset IndexedAt { get; init; }

    [JsonPropertyName("gameDataPath")]
    public required string GameDataPath { get; init; }

    [JsonPropertyName("ruleVersion")]
    public required string RuleVersion { get; init; }

    [JsonPropertyName("ruleDescription")]
    public required string RuleDescription { get; init; }

    [JsonPropertyName("templateTypeCount")]
    public required int TemplateTypeCount { get; init; }

    [JsonPropertyName("instanceCount")]
    public required int InstanceCount { get; init; }

    [JsonPropertyName("valueCount")]
    public int ValueCount { get; init; }
}

public enum TemplateResolutionStatus
{
    Success,
    NotFound,
    Ambiguous,
    IndexUnavailable,
}

public sealed class ResolvedTemplateCandidate
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("className")]
    public required string ClassName { get; init; }

    [JsonPropertyName("identity")]
    public required TemplateIdentity Identity { get; init; }
}

public sealed class TemplateResolutionResult
{
    [JsonPropertyName("status")]
    public required TemplateResolutionStatus Status { get; init; }

    [JsonPropertyName("resolved")]
    public ResolvedTemplateCandidate? Resolved { get; init; }

    [JsonPropertyName("candidates")]
    public List<ResolvedTemplateCandidate> Candidates { get; init; } = [];
}
