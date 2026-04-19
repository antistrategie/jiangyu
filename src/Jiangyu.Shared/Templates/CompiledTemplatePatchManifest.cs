using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

public sealed class CompiledTemplatePatchManifest
{
    [JsonPropertyName("templatePatches")]
    public List<CompiledTemplatePatch>? TemplatePatches { get; set; }
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
    public CompiledTemplateScalarValue? Value { get; set; }
}

public sealed class CompiledTemplateScalarValue
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompiledTemplateScalarValueKind Kind { get; set; }

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
}

public enum CompiledTemplateScalarValueKind
{
    Boolean,
    Byte,
    Int32,
    Single,
    String,
    Enum,
}
