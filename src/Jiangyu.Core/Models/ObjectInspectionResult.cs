using Jiangyu.Shared;

namespace Jiangyu.Core.Models;

public sealed class ObjectInspectionRequest
{
    public string? Collection { get; init; }
    public long? PathId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public int MaxDepth { get; init; } = 4;
    public int MaxArraySampleLength { get; init; } = 0;
}

[RpcType]
public sealed class ObjectInspectionResult
{
    public required InspectedObjectIdentity Object { get; init; }
    public required ObjectInspectionOptions Options { get; init; }
    public required List<InspectedFieldNode> Fields { get; init; }
}

[RpcType]
public sealed class InspectedObjectIdentity
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required string Collection { get; init; }
    public required long PathId { get; init; }
}

[RpcType]
public sealed class ObjectInspectionOptions
{
    public required int MaxDepth { get; init; }
    public required int MaxArraySampleLength { get; init; }
    public required bool Truncated { get; init; }
}

[RpcType]
public sealed class InspectedFieldNode
{
    /// <summary>
    /// For top-level field nodes this is the field's name. For elements of a
    /// <c>[NamedArray(typeof(T))]</c> array it's the paired enum member's
    /// name (so each slot reads as e.g. "Vitality" instead of just an
    /// index). Null on positional elements of plain arrays.
    /// </summary>
    public string? Name { get; set; }
    public required string Kind { get; set; }
    public string? FieldTypeName { get; set; }
    public bool? Null { get; set; }
    public bool? Truncated { get; set; }
    public object? Value { get; set; }
    public int? Count { get; set; }
    public InspectedReference? Reference { get; set; }
    public List<InspectedFieldNode>? Elements { get; set; }
    public List<InspectedFieldNode>? Fields { get; set; }
    public string? Reason { get; set; }
    /// <summary>
    /// Per-axis lengths for nodes with <c>Kind == "matrix"</c> — Sirenix
    /// Odin emits multi-dim arrays as a flat sequence prefixed by a "ranks"
    /// header (e.g. "9|9"); the decoder reshapes that into a matrix node so
    /// the browser can render a 2D grid and modders can address cells via
    /// patch <c>cell="r,c"</c> writes. <see cref="Elements"/> stays flat in
    /// row-major order; consumers compute (row, col) from the index.
    /// </summary>
    public List<int>? Dimensions { get; set; }
}

[RpcType]
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
