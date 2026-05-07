using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Core.Il2Cpp;

/// <summary>
/// Static IL2CPP-side metadata that <c>TemplateTypeCatalog</c> can't see
/// through Il2CppInterop wrappers (which strip custom attribute data).
/// Built once via <see cref="Il2CppMetadataExtractor"/> against the game's
/// <c>GameAssembly.dll</c> + <c>global-metadata.dat</c> and cached on disk.
/// Catalog consumers read it at construction time to enrich
/// <c>MemberShape</c> with attribute-derived hints (currently just
/// <c>[NamedArray(typeof(T))]</c> pairings).
/// </summary>
public sealed class Il2CppMetadataSupplement
{
    // Bumped every time the schema changes — cache files older than the
    // current version are treated as stale and rebuilt.
    public const int CurrentSchemaVersion = 4;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("gameAssemblyMtime")]
    public DateTimeOffset GameAssemblyMtime { get; set; }

    [JsonPropertyName("metadataMtime")]
    public DateTimeOffset MetadataMtime { get; set; }

    [JsonPropertyName("namedArrays")]
    public List<NamedArrayPairing> NamedArrays { get; set; } = [];

    /// <summary>
    /// Per-field attribute metadata captured from IL2CPP — Range / Min /
    /// Tooltip / HideInInspector / SoundID. Indexed by (templateType, field)
    /// short names. Sparse: a field only appears when it has at least one
    /// non-default hint.
    /// </summary>
    [JsonPropertyName("fields")]
    public List<FieldMetadata> Fields { get; set; } = [];

    /// <summary>
    /// Concrete-class to implemented-interface pairs walked from the
    /// Cpp2IL-enriched AsmResolver assemblies. Il2CppInterop wrapper
    /// generation strips interface implementations from CIL, so the
    /// catalogue's <see cref="System.Type.IsAssignableFrom"/> can't see
    /// them — this map fills that gap. Populated only for concrete classes
    /// (interfaces themselves and abstract bases are skipped: the bases are
    /// expressible via class inheritance which IL2CPP does preserve).
    /// </summary>
    [JsonPropertyName("interfaceImpls")]
    public List<InterfaceImplementation> InterfaceImpls { get; set; } = [];

    /// <summary>
    /// Returns the concrete-class full names that implement
    /// <paramref name="interfaceFullName"/>, or an empty list when the
    /// interface has no recorded implementations (or the supplement was
    /// built before this field was populated).
    /// </summary>
    public IReadOnlyList<string> GetInterfaceImplementations(string? interfaceFullName)
    {
        if (string.IsNullOrEmpty(interfaceFullName) || InterfaceImpls.Count == 0)
            return [];

        var matches = new List<string>();
        foreach (var entry in InterfaceImpls)
        {
            if (string.Equals(entry.InterfaceFullName, interfaceFullName, StringComparison.Ordinal))
                matches.Add(entry.ConcreteFullName);
        }
        return matches;
    }

    public bool TryFindNamedArrayEnum(string? declaringTypeShortName, string fieldName, out string? enumShortName)
    {
        enumShortName = null;
        if (string.IsNullOrEmpty(declaringTypeShortName)) return false;
        foreach (var entry in NamedArrays)
        {
            if (string.Equals(entry.TemplateTypeShortName, declaringTypeShortName, StringComparison.Ordinal)
                && string.Equals(entry.FieldName, fieldName, StringComparison.Ordinal))
            {
                enumShortName = entry.EnumTypeShortName;
                return true;
            }
        }
        return false;
    }

    public FieldMetadata? FindFieldMetadata(string? declaringTypeShortName, string fieldName)
    {
        if (string.IsNullOrEmpty(declaringTypeShortName)) return null;
        foreach (var entry in Fields)
        {
            if (string.Equals(entry.TemplateTypeShortName, declaringTypeShortName, StringComparison.Ordinal)
                && string.Equals(entry.FieldName, fieldName, StringComparison.Ordinal))
                return entry;
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static Il2CppMetadataSupplement? FromJson(string json)
        => JsonSerializer.Deserialize<Il2CppMetadataSupplement>(json, JsonOptions);
}

public sealed class FieldMetadata
{
    [JsonPropertyName("templateTypeShortName")]
    public required string TemplateTypeShortName { get; set; }

    [JsonPropertyName("templateTypeFullName")]
    public required string TemplateTypeFullName { get; set; }

    [JsonPropertyName("fieldName")]
    public required string FieldName { get; set; }

    /// <summary>From <c>[Range(min, max)]</c>. Inclusive bounds.</summary>
    [JsonPropertyName("rangeMin")]
    public double? RangeMin { get; set; }

    [JsonPropertyName("rangeMax")]
    public double? RangeMax { get; set; }

    /// <summary>From <c>[Min(value)]</c> when no Range is present.</summary>
    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }

    /// <summary>From <c>[Tooltip("…")]</c>.</summary>
    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }

    /// <summary>From <c>[HideInInspector]</c> — modder-facing UI should hide
    /// these fields by default.</summary>
    [JsonPropertyName("hideInInspector")]
    public bool? HideInInspector { get; set; }

    /// <summary>True when the field is marked with the game's
    /// <c>Stem.SoundIDAttribute</c>. Field type is <c>Stem.ID</c>; UI can
    /// render a sound-aware indicator.</summary>
    [JsonPropertyName("isSoundId")]
    public bool? IsSoundId { get; set; }
}

public sealed class InterfaceImplementation
{
    [JsonPropertyName("concreteFullName")]
    public required string ConcreteFullName { get; set; }

    [JsonPropertyName("interfaceFullName")]
    public required string InterfaceFullName { get; set; }
}

public sealed class NamedArrayPairing
{
    [JsonPropertyName("templateTypeFullName")]
    public required string TemplateTypeFullName { get; set; }

    [JsonPropertyName("templateTypeShortName")]
    public required string TemplateTypeShortName { get; set; }

    [JsonPropertyName("fieldName")]
    public required string FieldName { get; set; }

    [JsonPropertyName("elementTypeFullName")]
    public string? ElementTypeFullName { get; set; }

    [JsonPropertyName("enumTypeShortName")]
    public required string EnumTypeShortName { get; set; }

    [JsonPropertyName("enumTypeFullName")]
    public string? EnumTypeFullName { get; set; }

    [JsonPropertyName("attributeFullName")]
    public string? AttributeFullName { get; set; }
}
