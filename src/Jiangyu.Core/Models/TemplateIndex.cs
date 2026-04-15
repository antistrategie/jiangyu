using System.Text.Json.Serialization;

namespace Jiangyu.Core.Models;

public sealed class TemplateIndex
{
    [JsonPropertyName("classification")]
    public required TemplateClassificationMetadata Classification { get; init; }

    [JsonPropertyName("templateTypes")]
    public required List<TemplateTypeEntry> TemplateTypes { get; init; }

    [JsonPropertyName("instances")]
    public required List<TemplateInstanceEntry> Instances { get; init; }
}

public sealed class TemplateClassificationMetadata
{
    [JsonPropertyName("ruleVersion")]
    public required string RuleVersion { get; init; }

    [JsonPropertyName("ruleDescription")]
    public required string RuleDescription { get; init; }
}

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

public sealed class TemplateIdentity
{
    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    [JsonPropertyName("pathId")]
    public required long PathId { get; init; }
}

public sealed class TemplateInstanceEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("className")]
    public required string ClassName { get; init; }

    [JsonPropertyName("identity")]
    public required TemplateIdentity Identity { get; init; }
}

public sealed class TemplateIndexManifest
{
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
