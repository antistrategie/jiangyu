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

    /// <summary>
    /// Inner-relative member path on the destination instance (the instance
    /// reached after walking <see cref="Descent"/>, or the top-level template
    /// when <see cref="Descent"/> is null/empty). A bare member name in the
    /// common case; never carries bracket notation, dotted segments only
    /// where the modder authored a deeper composite-member write.
    /// </summary>
    [JsonPropertyName("fieldPath")]
    public string FieldPath { get; set; } = string.Empty;

    /// <summary>
    /// Insert position for <see cref="CompiledTemplateOp.InsertAt"/>. Must be
    /// non-negative and no greater than the current collection length. Ignored
    /// for other ops.
    /// </summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>
    /// Outer descent prefix as a structural step list. Each step navigates
    /// into one polymorphic / collection-element slot before the inner
    /// <see cref="FieldPath"/> write applies. Produced by the KDL parser
    /// when modders write <c>set "Field" index=N type="X" { ... }</c> child
    /// blocks; nested descent appends further steps in outer-to-inner order.
    /// Null/empty when the directive writes a top-level member directly.
    /// </summary>
    [JsonPropertyName("descent")]
    public List<TemplateDescentStep>? Descent { get; set; }

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
    /// Remove the element at <see cref="CompiledTemplateSetOperation.Index"/>
    /// from the collection at fieldPath. No value is required.
    /// </summary>
    Remove,

    /// <summary>
    /// Empty the collection at fieldPath. No value, no index. Composes with
    /// subsequent <see cref="Append"/> ops on the same field for a
    /// "replace the whole list" pattern. A null collection at apply time is
    /// treated the same as a missing field; the loader's missing-field path
    /// surfaces it instead of silently materialising an empty list.
    /// </summary>
    Clear,
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

    [JsonPropertyName("asset")]
    public CompiledAssetReference? Asset { get; set; }

    [JsonPropertyName("composite")]
    public CompiledTemplateComposite? Composite { get; set; }

    /// <summary>
    /// Payload for <see cref="CompiledTemplateValueKind.HandlerConstruction"/>.
    /// Same field-bag shape as <see cref="Composite"/>, but signals
    /// "construct a new ScriptableObject" at apply time rather than
    /// "build an inline value". Used to add a freshly-instantiated handler
    /// (e.g. AddSkill) into a polymorphic-reference array such as
    /// <c>SkillTemplate.EventHandlers</c>.
    /// </summary>
    [JsonPropertyName("handlerConstruction")]
    public CompiledTemplateComposite? HandlerConstruction { get; set; }
}

/// <summary>
/// Payload for a <see cref="CompiledTemplateValueKind.Composite"/> value —
/// constructs a new instance of <see cref="TypeName"/> (resolved via the same
/// dispatch as <see cref="CompiledTemplateReference"/>: DataTemplate subtype
/// or ScriptableObject subtype or plain support type) and applies each entry
/// in <see cref="Operations"/> to the named member of the new instance using
/// the same op semantics as outer-level patch directives. This means
/// constructing a fresh handler can include not just scalar field assignments
/// but also <c>append</c>/<c>insert</c>/<c>remove</c>/<c>clear</c> ops on the
/// new instance's collection members.
/// </summary>
public sealed class CompiledTemplateComposite
{
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// Patch operations applied to the freshly-constructed instance, in
    /// declaration order. Same shape as <see cref="CompiledTemplatePatch.Set"/>
    /// at the outer level — every op (Set, Append, InsertAt, Remove, Clear) is
    /// supported. Modders writing
    /// <c>composite="X" { append "Items" composite="Y" { ... } }</c> get this
    /// list directly; older syntax <c>set "F" v</c> appears as a single Set op.
    /// </summary>
    [JsonPropertyName("operations")]
    public List<CompiledTemplateSetOperation> Operations { get; set; } = [];
}

/// <summary>
/// Payload for a <see cref="CompiledTemplateValueKind.TemplateReference"/>
/// value — identifies an existing live DataTemplate by
/// <c>(templateType?, templateId)</c>. The applier resolves this to an Il2Cpp
/// wrapper at apply time via <c>TemplateRuntimeAccess</c>; unknown types and
/// missing IDs fail loudly.
///
/// <para><c>TemplateType</c> is optional. The single source of truth for the
/// lookup type is the catalog: when the destination field's declared type
/// is concrete (e.g. <c>PerkTemplate</c>), the modder doesn't need to repeat
/// it and the applier derives the lookup type from the field. When the field
/// is polymorphic (declared as an abstract base like <c>DataTemplate</c>),
/// <c>TemplateType</c> must be specified — otherwise the lookup is
/// ambiguous. The compile-time validator enforces this.</para>
/// </summary>
public sealed class CompiledTemplateReference
{
    [JsonPropertyName("templateType")]
    public string? TemplateType { get; set; }

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

    /// <summary>
    /// Construct a new ScriptableObject of a named subclass and populate
    /// its fields. Used to add a freshly-instantiated handler (or other
    /// SerializedScriptableObject element) to a polymorphic-reference
    /// array. The runtime applier dispatches via
    /// <c>ScriptableObject.CreateInstance&lt;T&gt;()</c> reflectively;
    /// inline composites stay on the <see cref="Composite"/> path.
    /// </summary>
    HandlerConstruction,

    /// <summary>
    /// Reference to a Unity asset shipped by the mod under
    /// <c>assets/additions/&lt;category&gt;/&lt;name&gt;.&lt;ext&gt;</c> or to a
    /// game-indexed asset of the same name. The category is inferred from the
    /// destination field's declared Unity type (Sprite, Texture2D, AudioClip,
    /// Material, Mesh) at compile and apply time, so the modder writes the
    /// name only. The applier resolves to the live Unity Object via the mod's
    /// loaded AssetBundle, falling back to the game-asset registry.
    /// </summary>
    AssetReference,
}

/// <summary>
/// Payload for a <see cref="CompiledTemplateValueKind.AssetReference"/> value.
/// <para><c>Name</c> is the asset's logical key: the path under
/// <c>assets/additions/&lt;category&gt;/</c> with the file extension stripped
/// and directory separators preserved as <c>/</c>. The category is derived
/// from the destination field's declared Unity type, not stored here.</para>
/// </summary>
public sealed class CompiledAssetReference
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
