using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

/// <summary>
/// One descent step along a template patch path.
/// <list type="bullet">
/// <item>
/// Collection element descent (<see cref="Index"/> non-null): KDL syntax
/// <c>set "Field" index=N type="Subtype" { ... }</c>. Navigate into element
/// <c>N</c> of collection <c>Field</c>, switching the validated type to
/// <c>Subtype</c> when the element's declared type is polymorphic.
/// </item>
/// <item>
/// Scalar polymorphic descent (<see cref="Index"/> null): KDL syntax
/// <c>set "Field" type="Subtype" { ... }</c>. Navigate into a non-collection
/// field whose declared type is polymorphic (e.g. an Odin-routed
/// <c>ITacticalCondition</c>), casting the runtime value to <c>Subtype</c>
/// so subsequent path segments can resolve subclass-specific members.
/// </item>
/// </list>
///
/// Descent steps live as a structural list on patch operations. They are
/// not encoded as a bracketed string segment. A directive's
/// <c>FieldPath</c> means the inner-relative member name; descent context
/// (if any) is carried in the <c>Descent</c> list. Nested descent appends
/// further entries to the list in outer-to-inner order.
/// </summary>
public sealed class TemplateDescentStep
{
    /// <summary>The member being descended into at this step.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Element index inside <see cref="Field"/> (zero-based) for collection
    /// descent. Null for scalar polymorphic descent into a non-collection
    /// field.
    /// </summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }

    /// <summary>
    /// Concrete subtype name when the destination is polymorphic-abstract;
    /// null when the destination type is concrete (or the validator can
    /// otherwise infer the type from the field declaration). Required for
    /// scalar descent (otherwise the descent has no purpose); optional for
    /// collection descent on monomorphic element types.
    /// </summary>
    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }
}
