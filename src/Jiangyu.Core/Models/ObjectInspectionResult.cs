namespace Jiangyu.Core.Models;

public sealed class ObjectInspectionRequest
{
    public string? Collection { get; init; }
    public long? PathId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public int MaxDepth { get; init; } = 4;
    public int MaxArraySampleLength { get; init; } = 8;
}

public sealed class ObjectInspectionResult
{
    public required InspectedObjectIdentity Object { get; init; }
    public required ObjectInspectionOptions Options { get; init; }
    public required List<InspectedFieldNode> Fields { get; init; }
}

public sealed class InspectedObjectIdentity
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required string Collection { get; init; }
    public required long PathId { get; init; }
}

public sealed class ObjectInspectionOptions
{
    public required int MaxDepth { get; init; }
    public required int MaxArraySampleLength { get; init; }
    public required bool Truncated { get; init; }
}

public sealed class InspectedFieldNode
{
    public string? Name { get; init; }
    public required string Kind { get; init; }
    public string? FieldTypeName { get; init; }
    public bool? Null { get; init; }
    public bool? Truncated { get; init; }
    public object? Value { get; init; }
    public int? Count { get; init; }
    public InspectedReference? Reference { get; init; }
    public List<InspectedFieldNode>? Elements { get; init; }
    public List<InspectedFieldNode>? Fields { get; init; }
    public string? Reason { get; init; }
}

public sealed class InspectedReference
{
    public int? FileId { get; init; }
    public long? PathId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
}

public enum ObjectResolutionStatus
{
    Success,
    NotFound,
    Ambiguous,
    IndexUnavailable,
}

public sealed class ResolvedObjectCandidate
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required string Collection { get; init; }
    public required long PathId { get; init; }
}

public sealed class ObjectResolutionResult
{
    public required ObjectResolutionStatus Status { get; init; }
    public ResolvedObjectCandidate? Resolved { get; init; }
    public List<ResolvedObjectCandidate> Candidates { get; init; } = [];
}
