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
    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public CompiledTemplateValue? Value { get; set; }
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
}
