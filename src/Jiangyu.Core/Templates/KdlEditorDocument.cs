using System.Text.Json.Serialization;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Editor-facing AST for a single KDL template file. Preserves document
/// shape — node order, clone inline directives, and per-node structure —
/// so the visual editor can round-trip without losing information.
/// </summary>
public sealed class KdlEditorDocument
{
    [JsonPropertyName("nodes")]
    public List<KdlEditorNode> Nodes { get; set; } = [];

    [JsonPropertyName("errors")]
    public List<KdlEditorError> Errors { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KdlEditorNodeKind
{
    Patch,
    Clone,
}

public sealed class KdlEditorNode
{
    [JsonPropertyName("kind")]
    public KdlEditorNodeKind Kind { get; set; }

    [JsonPropertyName("templateType")]
    public string TemplateType { get; set; } = string.Empty;

    /// <summary>Patch: the target template ID. Clone: unused (see <see cref="CloneId"/>).</summary>
    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    /// <summary>Clone only: source template ID.</summary>
    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    /// <summary>Clone only: new template ID.</summary>
    [JsonPropertyName("cloneId")]
    public string? CloneId { get; set; }

    /// <summary>1-based source line of the node. Set by the parser for diagnostic output.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("directives")]
    public List<KdlEditorDirective> Directives { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KdlEditorOp
{
    Set,
    Append,
    Insert,
    Remove,
    Clear,
}

public sealed class KdlEditorDirective
{
    [JsonPropertyName("op")]
    public KdlEditorOp Op { get; set; }

    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>Insert position for insert ops.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>Absent for remove ops.</summary>
    [JsonPropertyName("value")]
    public KdlEditorValue? Value { get; set; }

    /// <summary>1-based source line of the directive. Set by the parser for diagnostic output.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KdlEditorValueKind
{
    Boolean,
    Byte,
    Int32,
    Single,
    String,
    Enum,
    TemplateReference,
    Composite,
}

public sealed class KdlEditorValue
{
    [JsonPropertyName("kind")]
    public KdlEditorValueKind Kind { get; set; }

    [JsonPropertyName("boolean")]
    public bool? Boolean { get; set; }

    [JsonPropertyName("int32")]
    public int? Int32 { get; set; }

    [JsonPropertyName("single")]
    public float? Single { get; set; }

    [JsonPropertyName("string")]
    public string? String { get; set; }

    [JsonPropertyName("enumType")]
    public string? EnumType { get; set; }

    [JsonPropertyName("enumValue")]
    public string? EnumValue { get; set; }

    [JsonPropertyName("referenceType")]
    public string? ReferenceType { get; set; }

    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("compositeType")]
    public string? CompositeType { get; set; }

    [JsonPropertyName("compositeFields")]
    public Dictionary<string, KdlEditorValue>? CompositeFields { get; set; }
}

public sealed class KdlEditorError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; set; }
}
