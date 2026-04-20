using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

public sealed class CompiledTemplatePatchManifest
{
    [JsonPropertyName("templatePatches")]
    public List<CompiledTemplatePatch>? TemplatePatches { get; set; }

    [JsonPropertyName("templateClones")]
    public List<CompiledTemplateClone>? TemplateClones { get; set; }
}

/// <summary>
/// Directive to deep-copy an existing live template of <see cref="TemplateType"/>
/// identified by <see cref="SourceId"/> and register the copy under
/// <see cref="CloneId"/>. Clones run before patches apply so subsequent
/// <see cref="CompiledTemplatePatch"/> entries can target the new ID.
/// </summary>
public sealed class CompiledTemplateClone
{
    [JsonPropertyName("templateType")]
    public string? TemplateType { get; set; }

    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("cloneId")]
    public string CloneId { get; set; } = string.Empty;
}

public sealed class CompiledTemplatePatch
{
    /// <summary>
    /// Name of the DataTemplate subtype this patch targets (e.g. "EntityTemplate",
    /// "WeaponTemplate", "UnitLeaderTemplate"). When omitted, the loader treats
    /// the patch as targeting EntityTemplate for backward-compat with mods
    /// written against the first slice.
    /// </summary>
    [JsonPropertyName("templateType")]
    public string? TemplateType { get; set; }

    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("set")]
    public List<CompiledTemplateSetOperation> Set { get; set; } = [];
}

public sealed class CompiledTemplateSetOperation
{
    [JsonPropertyName("op")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompiledTemplateOp Op { get; set; } = CompiledTemplateOp.Set;

    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>
    /// Insert position for <see cref="CompiledTemplateOp.InsertAt"/>. Must be
    /// non-negative and no greater than the current collection length. Ignored
    /// for other ops.
    /// </summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("value")]
    public CompiledTemplateValue? Value { get; set; }
}

public enum CompiledTemplateOp
{
    /// <summary>Write the value at the (optionally indexed) fieldPath.</summary>
    Set,

    /// <summary>
    /// Append the value as a new element at the end of the collection at
    /// fieldPath. Supports <c>List&lt;T&gt;</c> (via Add), reference-type
    /// arrays (rebuild + replace field), and struct-type arrays.
    /// </summary>
    Append,

    /// <summary>
    /// Insert the value at <see cref="CompiledTemplateSetOperation.Index"/>
    /// in the collection at fieldPath. Same collection shapes as Append.
    /// </summary>
    InsertAt,

    /// <summary>
    /// Remove the element identified by an indexed terminal fieldPath (e.g.
    /// <c>Skills[2]</c>). No value is required.
    /// </summary>
    Remove,
}

public sealed class CompiledTemplateValue
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompiledTemplateValueKind Kind { get; set; }

    [JsonPropertyName("boolean")]
    public bool? Boolean { get; set; }

    [JsonPropertyName("byte")]
    public byte? Byte { get; set; }

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

    [JsonPropertyName("reference")]
    public CompiledTemplateReference? Reference { get; set; }

    [JsonPropertyName("composite")]
    public CompiledTemplateComposite? Composite { get; set; }
}

/// <summary>
/// Payload for a <see cref="CompiledTemplateValueKind.Composite"/> value —
/// constructs a new instance of <see cref="TypeName"/> (resolved via the same
/// dispatch as <see cref="CompiledTemplateReference"/>: DataTemplate subtype
/// or ScriptableObject subtype or plain support type) and recursively writes
/// each entry in <see cref="Fields"/> to the named member of the new instance.
/// Used to append/insert a freshly-constructed support-type element (e.g. a
/// new <c>Perk</c>) into a collection rather than referencing an existing
/// one via <see cref="CompiledTemplateReference"/>.
/// </summary>
public sealed class CompiledTemplateComposite
{
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public Dictionary<string, CompiledTemplateValue> Fields { get; set; } = [];
}

/// <summary>
/// Payload for a <see cref="CompiledTemplateValueKind.TemplateReference"/>
/// value — identifies an existing live DataTemplate by
/// <c>(templateType, templateId)</c>. The applier resolves this to an Il2Cpp
/// wrapper at apply time via <c>TemplateRuntimeAccess</c>; unknown types and
/// missing IDs fail loudly. Used for ref-typed element replacements into
/// arrays/lists of templates (e.g. swap a <c>SkillTemplate</c> in
/// <c>EntityTemplate.Skills</c>).
/// </summary>
public sealed class CompiledTemplateReference
{
    [JsonPropertyName("templateType")]
    public string TemplateType { get; set; } = string.Empty;

    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;
}

public enum CompiledTemplateValueKind
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
