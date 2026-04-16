using System.Text.Json.Serialization;

namespace Jiangyu.Core.Models;

public sealed class BaselineSources
{
    [JsonPropertyName("templates")]
    public required List<BaselineSourceEntry> Templates { get; init; }

    [JsonPropertyName("supportTypes")]
    public required List<BaselineSourceEntry> SupportTypes { get; init; }
}

public sealed class BaselineSourceEntry
{
    [JsonPropertyName("typeName")]
    public required string TypeName { get; init; }

    [JsonPropertyName("sampleNames")]
    public required List<string> SampleNames { get; init; }
}

public sealed class StructuralBaseline
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("gameAssemblyHash")]
    public string? GameAssemblyHash { get; init; }

    [JsonPropertyName("types")]
    public required List<BaselineTypeEntry> Types { get; init; }
}

public sealed class BaselineTypeEntry
{
    [JsonPropertyName("typeName")]
    public required string TypeName { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("fieldCount")]
    public required int FieldCount { get; init; }

    [JsonPropertyName("sampleNames")]
    public List<string>? SampleNames { get; init; }

    [JsonPropertyName("fields")]
    public required List<BaselineFieldEntry> Fields { get; init; }
}

public sealed class BaselineFieldEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("fieldTypeName")]
    public string? FieldTypeName { get; init; }

    [JsonPropertyName("elementTypeName")]
    public string? ElementTypeName { get; init; }
}

public sealed class BaselineDiff
{
    [JsonPropertyName("previousGeneratedAt")]
    public DateTimeOffset? PreviousGeneratedAt { get; init; }

    [JsonPropertyName("currentGeneratedAt")]
    public DateTimeOffset? CurrentGeneratedAt { get; init; }

    [JsonPropertyName("addedTypes")]
    public required List<string> AddedTypes { get; init; }

    [JsonPropertyName("removedTypes")]
    public required List<string> RemovedTypes { get; init; }

    [JsonPropertyName("changedTypes")]
    public required List<BaselineTypeDiff> ChangedTypes { get; init; }
}

public sealed class BaselineTypeDiff
{
    [JsonPropertyName("typeName")]
    public required string TypeName { get; init; }

    [JsonPropertyName("fieldCountDelta")]
    public int? FieldCountDelta { get; init; }

    [JsonPropertyName("addedFields")]
    public required List<string> AddedFields { get; init; }

    [JsonPropertyName("removedFields")]
    public required List<string> RemovedFields { get; init; }

    [JsonPropertyName("changedFields")]
    public required List<BaselineFieldDiff> ChangedFields { get; init; }
}

public sealed class BaselineFieldDiff
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("previousKind")]
    public string? PreviousKind { get; init; }

    [JsonPropertyName("currentKind")]
    public string? CurrentKind { get; init; }

    [JsonPropertyName("previousFieldTypeName")]
    public string? PreviousFieldTypeName { get; init; }

    [JsonPropertyName("currentFieldTypeName")]
    public string? CurrentFieldTypeName { get; init; }

    [JsonPropertyName("previousElementTypeName")]
    public string? PreviousElementTypeName { get; init; }

    [JsonPropertyName("currentElementTypeName")]
    public string? CurrentElementTypeName { get; init; }
}
