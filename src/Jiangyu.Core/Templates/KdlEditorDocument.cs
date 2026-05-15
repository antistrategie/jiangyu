using System.Text.Json.Serialization;
using Jiangyu.Shared.Templates;

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

    /// <summary>
    /// Inner-relative member path. Mirrors
    /// <see cref="CompiledTemplateSetOperation.FieldPath"/>: the path on the
    /// destination instance reached after walking <see cref="Descent"/>, or
    /// the top-level template when descent is empty.
    /// </summary>
    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>Insert position for insert ops.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>
    /// Multi-dimensional cell address for Set ops against an N-dim
    /// Odin-routed array (e.g. AOETiles' bool[,]). Mutually exclusive with
    /// <see cref="Index"/>. Mirrors
    /// <see cref="CompiledTemplateSetOperation.IndexPath"/>.
    /// </summary>
    [JsonPropertyName("indexPath")]
    public List<int>? IndexPath { get; set; }

    /// <summary>Absent for remove ops.</summary>
    [JsonPropertyName("value")]
    public KdlEditorValue? Value { get; set; }

    /// <summary>
    /// Outer descent prefix as a structural step list, mirroring
    /// <see cref="CompiledTemplateSetOperation.Descent"/>. The serialiser
    /// groups consecutive directives sharing the same descent prefix back
    /// into a single <c>set "Field" index=N type="X" { ... }</c> block.
    /// </summary>
    [JsonPropertyName("descent")]
    public List<TemplateDescentStep>? Descent { get; set; }

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

    /// <summary>
    /// ScriptableObject construction for a polymorphic-reference array
    /// element (e.g. EventHandlers). Mirrors
    /// <see cref="Jiangyu.Shared.Templates.CompiledTemplateValueKind.HandlerConstruction"/>;
    /// the directive-list shape on
    /// <see cref="KdlEditorValue.CompositeDirectives"/> /
    /// <see cref="KdlEditorValue.CompositeType"/> is reused. The serialiser
    /// emits <c>handler="X" { ... }</c> instead of <c>composite="X" { ... }</c>
    /// when this kind is present.
    /// </summary>
    HandlerConstruction,

    /// <summary>
    /// Reference to a Unity asset shipped in the mod under
    /// <c>assets/additions/&lt;category&gt;/</c> (or matching a vanilla
    /// game asset by name). Mirrors
    /// <see cref="Jiangyu.Shared.Templates.CompiledTemplateValueKind.AssetReference"/>.
    /// The category is derived from the destination field's declared Unity
    /// type at apply time, so the editor only stores the asset's logical
    /// name on <see cref="KdlEditorValue.AssetName"/>.
    /// </summary>
    AssetReference,

    /// <summary>
    /// Explicit null literal. Mirrors
    /// <see cref="Jiangyu.Shared.Templates.CompiledTemplateValueKind.Null"/>.
    /// Used to clear a scalar reference field; the destination field's type
    /// is checked at apply time.
    /// </summary>
    Null,
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

    /// <summary>
    /// Asset reference logical name. Set when
    /// <see cref="Kind"/> is <see cref="KdlEditorValueKind.AssetReference"/>;
    /// matches the file path under <c>assets/additions/&lt;category&gt;/</c>
    /// with the extension stripped and slashes preserved.
    /// </summary>
    [JsonPropertyName("assetName")]
    public string? AssetName { get; set; }

    /// <summary>
    /// Patch operations applied to the constructed composite/handler instance.
    /// Same shape as <see cref="KdlEditorNode.Directives"/> at the outer
    /// level — every op (Set/Append/Insert/Remove/Clear) is allowed inside.
    /// A scalar-field write ends up as one Set directive on a primitive
    /// fieldPath; appending to a collection sub-field ends up as an Append
    /// directive with a composite/handler value of its own.
    /// </summary>
    [JsonPropertyName("compositeDirectives")]
    public List<KdlEditorDirective>? CompositeDirectives { get; set; }
}

public sealed class KdlEditorError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; set; }
}
