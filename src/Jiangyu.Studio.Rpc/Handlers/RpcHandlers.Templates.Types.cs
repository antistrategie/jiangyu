using System.Text.Json.Serialization;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;
using Jiangyu.Core.Rpc;

namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Response DTOs returned by <see cref="RpcHandlers"/>'s templates surface.
/// Split out from <c>RpcHandlers.Templates.cs</c> so the handler file holds
/// orchestration and this file holds wire shapes — same <c>[RpcType]</c>
/// generator output, smaller files.
/// </summary>
public static partial class RpcHandlers
{
    [RpcType]
    internal sealed class TemplateIndexStatus
    {
        [JsonPropertyName("state")]
        public required string State { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("instanceCount")]
        public int? InstanceCount { get; set; }

        [JsonPropertyName("typeCount")]
        public int? TypeCount { get; set; }

        [JsonPropertyName("indexedAt")]
        public DateTimeOffset? IndexedAt { get; set; }
    }

    [RpcType]
    internal sealed class TemplateSearchResult
    {
        [JsonPropertyName("types")]
        public required List<TemplateTypeEntry> Types { get; set; }

        [JsonPropertyName("instances")]
        public required List<TemplateInstanceEntry> Instances { get; set; }

        [JsonPropertyName("referencedBy")]
        public Dictionary<string, List<TemplateReferenceEntry>>? ReferencedBy { get; set; }
    }

    [RpcType]
    internal sealed class TemplateQueryResult
    {
        [JsonPropertyName("kind")]
        public required string Kind { get; set; }

        [JsonPropertyName("resolvedPath")]
        public string? ResolvedPath { get; set; }

        [JsonPropertyName("typeName")]
        public string? TypeName { get; set; }

        [JsonPropertyName("typeFullName")]
        public string? TypeFullName { get; set; }

        [JsonPropertyName("isWritable")]
        public bool IsWritable { get; set; } = true;

        [JsonPropertyName("patchScalarKind")]
        public string? PatchScalarKind { get; set; }

        [JsonPropertyName("enumMemberNames")]
        public List<string>? EnumMemberNames { get; set; }

        /// <summary>{name, value} pairs for the leaf enum type. Set whenever
        /// the terminal type is an enum OR the leaf is a
        /// <c>[NamedArray(typeof(T))]</c> primitive-element field (in which
        /// case the members come from the paired enum, not from the leaf
        /// element type). Lets agents and the visual editor render dropdowns
        /// without a follow-up <c>templatesEnumMembers</c> call.</summary>
        [JsonPropertyName("enumMembers")]
        public List<EnumMemberEntry>? EnumMembers { get; set; }

        /// <summary>Short name of the enum paired with a
        /// <c>[NamedArray(typeof(T))]</c> primitive-element leaf. Null
        /// otherwise. Mirrors the same field on member entries.</summary>
        [JsonPropertyName("namedArrayEnumTypeName")]
        public string? NamedArrayEnumTypeName { get; set; }

        [JsonPropertyName("referenceTargetTypeName")]
        public string? ReferenceTargetTypeName { get; set; }

        [JsonPropertyName("isLikelyOdinOnly")]
        public bool? IsLikelyOdinOnly { get; set; }

        [JsonPropertyName("members")]
        public List<TemplateMember>? Members { get; set; }
    }

    [RpcType]
    internal sealed class TemplateMember
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("typeName")]
        public required string TypeName { get; set; }

        [JsonPropertyName("typeFullName")]
        public string? TypeFullName { get; set; }

        [JsonPropertyName("isWritable")]
        public bool IsWritable { get; set; }

        [JsonPropertyName("isInherited")]
        public bool IsInherited { get; set; }

        [JsonPropertyName("isLikelyOdinOnly")]
        public bool? IsLikelyOdinOnly { get; set; }

        [JsonPropertyName("isCollection")]
        public bool? IsCollection { get; set; }

        [JsonPropertyName("isScalar")]
        public bool? IsScalar { get; set; }

        [JsonPropertyName("isTemplateReference")]
        public bool? IsTemplateReference { get; set; }

        /// <summary>True when the member's declared Unity type is a supported
        /// asset class (Sprite, Texture2D, AudioClip, Material). The visual
        /// editor uses this to surface an asset reference picker instead of
        /// the template-reference one. Null on non-asset members so the
        /// JSON wire stays compact.</summary>
        [JsonPropertyName("isAssetReference")]
        public bool? IsAssetReference { get; set; }

        [JsonPropertyName("patchScalarKind")]
        public string? PatchScalarKind { get; set; }

        [JsonPropertyName("elementTypeName")]
        public string? ElementTypeName { get; set; }

        [JsonPropertyName("enumTypeName")]
        public string? EnumTypeName { get; set; }

        [JsonPropertyName("referenceTypeName")]
        public string? ReferenceTypeName { get; set; }

        /// <summary>True when <see cref="ReferenceTypeName"/> is an abstract
        /// base. The editor keeps the ref-type combobox visible so the modder
        /// can pick a concrete subtype; null for monomorphic / non-reference
        /// fields so JSON omits the property.</summary>
        [JsonPropertyName("isReferenceTypePolymorphic")]
        public bool? IsReferenceTypePolymorphic { get; set; }

        /// <summary>Concrete subtype short-names the modder can pick when
        /// appending to an owned polymorphic-element collection. Populated for
        /// construction-style polymorphic collections only (e.g. EventHandlers
        /// → BaseEventHandlerTemplate); null otherwise so the visual editor
        /// keeps the standard composite/ref flow.</summary>
        [JsonPropertyName("elementSubtypes")]
        public List<string>? ElementSubtypes { get; set; }

        /// <summary>Concrete subtype short-names the modder can pick when
        /// constructing a value for a polymorphic scalar field (declared
        /// type is itself an interface or abstract base, e.g. Odin-routed
        /// <c>Attack.DamageFilterCondition: ITacticalCondition</c>). Drives
        /// the same picker UX as <see cref="ElementSubtypes"/> but the
        /// resulting patch is a Set rather than an Append, and the
        /// destination is the field itself rather than an element slot.</summary>
        [JsonPropertyName("scalarSubtypes")]
        public List<string>? ScalarSubtypes { get; set; }

        /// <summary>Friendly name of the polymorphic base type backing a
        /// tagged-string serialisation field (e.g.
        /// <c>BaseConversationNode</c> for
        /// <c>ConversationNodeContainer.m_SerializedNodes</c>). Non-null
        /// signals to the visual editor that the field stores
        /// <c>"DISCRIMINATOR|{json}"</c> entries authored via
        /// <c>type="X"</c>. The editor renders a discriminator picker
        /// from <see cref="TaggedDiscriminators"/> instead of a free-form
        /// type field. Null on non-tagged members.</summary>
        [JsonPropertyName("taggedPolymorphicBase")]
        public string? TaggedPolymorphicBase { get; set; }

        /// <summary>Discriminator strings the modder can pick when
        /// authoring a tagged-string composite (e.g.
        /// <c>["ACTION", "SAY", "VARIATION", ...]</c> for
        /// <c>BaseConversationNode</c>). Drawn from the catalog's
        /// per-subtype heuristic candidate set; the visual editor uses
        /// these directly to populate the discriminator dropdown. Null
        /// when <see cref="TaggedPolymorphicBase"/> is null.</summary>
        [JsonPropertyName("taggedDiscriminators")]
        public List<string>? TaggedDiscriminators { get; set; }

        /// <summary>Short name of the enum paired with a
        /// <c>[NamedArray(typeof(T))]</c> array member; null otherwise.</summary>
        [JsonPropertyName("namedArrayEnumTypeName")]
        public string? NamedArrayEnumTypeName { get; set; }

        /// <summary>{name, value} pairs for the member's enum (regular enum
        /// scalar/element OR the named-array's paired enum). Inlined so the
        /// visual editor can populate dropdowns without a follow-up
        /// <c>templatesEnumMembers</c> RPC. Null when the member doesn't
        /// touch an enum type.</summary>
        [JsonPropertyName("enumMembers")]
        public List<EnumMemberEntry>? EnumMembers { get; set; }

        [JsonPropertyName("numericMin")]
        public double? NumericMin { get; set; }

        [JsonPropertyName("numericMax")]
        public double? NumericMax { get; set; }

        [JsonPropertyName("tooltip")]
        public string? Tooltip { get; set; }

        [JsonPropertyName("isHiddenInInspector")]
        public bool? IsHiddenInInspector { get; set; }

        [JsonPropertyName("isSoundIdField")]
        public bool? IsSoundIdField { get; set; }

        /// <summary>True when this member is an Odin-routed multi-dim
        /// primitive array (e.g. <c>AOETiles</c>, <c>ChunkTileFlags</c>).
        /// The catalog's declared type is the catch-all
        /// <c>Il2CppObjectBase</c>; this flag plus the multi-dim* siblings
        /// are derived by scanning indexed instances for a kind=matrix
        /// node. Lets the visual editor surface a grid widget and
        /// FieldAdder list these otherwise-hidden fields.</summary>
        [JsonPropertyName("isOdinMultiDimArray")]
        public bool? IsOdinMultiDimArray { get; set; }

        /// <summary>Rank of the Odin multi-dim array (2 for [,], 3 for
        /// [,,]). Null when <see cref="IsOdinMultiDimArray"/> is false.</summary>
        [JsonPropertyName("multiDimRank")]
        public int? MultiDimRank { get; set; }

        /// <summary>Per-axis lengths from a representative populated
        /// instance. Acts as the editor's default grid shape when the
        /// patch target's vanilla value is null. Null when no instance
        /// of this template type carries a populated matrix for this
        /// field.</summary>
        [JsonPropertyName("multiDimDimensions")]
        public List<int>? MultiDimDimensions { get; set; }

        /// <summary>Element type short name (e.g. "Boolean",
        /// "ChunkTileFlags") parsed from the inspect-side
        /// <c>fieldTypeName</c>. The visual editor pairs this with
        /// <c>templatesEnumMembers</c> for [Flags] cells.</summary>
        [JsonPropertyName("multiDimElementType")]
        public string? MultiDimElementType { get; set; }

        /// <summary>Wire-format element kind ("bool", "int", "string",
        /// "scalar"). Mirrors the per-cell <c>InspectedFieldNode.Kind</c>
        /// so the editor can branch its render mode without having to
        /// re-inspect the matrix.</summary>
        [JsonPropertyName("multiDimElementKind")]
        public string? MultiDimElementKind { get; set; }

        /// <summary>True when the member's declared type is a
        /// <c>HashSet&lt;T&gt;</c>. Switches the editor and the loader
        /// applier into HashSet semantics: Append maps to <c>Add</c>
        /// (idempotent), Remove takes a value (not an index), and the
        /// validator rejects InsertAt / Set-with-index because HashSet
        /// has no order.</summary>
        [JsonPropertyName("isOdinHashSet")]
        public bool? IsOdinHashSet { get; set; }
    }

    [RpcType]
    internal sealed class ProjectCloneEntry
    {
        [JsonPropertyName("templateType")]
        public required string TemplateType { get; set; }

        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("file")]
        public required string File { get; set; }
    }

    [RpcType]
    internal sealed class TemplateValueResult
    {
        /// <summary>
        /// True when the (typeName, id) tuple resolved to a known template
        /// instance and its serialised values were loaded. False when the
        /// tuple does not match any vanilla template (e.g. a clone the modder
        /// just authored, or a misspelt id), in which case <see cref="Fields"/>
        /// is an empty list.
        /// </summary>
        [JsonPropertyName("found")]
        public required bool Found { get; set; }

        /// <summary>
        /// Top-level serialised fields of the matched template's m_Structure
        /// (or the whole inspection tree for non-MonoBehaviour templates).
        /// Empty when <see cref="Found"/> is false or when the values cache
        /// has not been built (caller should fall back to neutral defaults).
        /// </summary>
        [JsonPropertyName("fields")]
        public required List<InspectedFieldNode> Fields { get; set; }
    }
}
