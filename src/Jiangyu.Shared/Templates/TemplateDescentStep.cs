using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

/// <summary>
/// One descent step along a template patch path. An edit navigates into
/// <see cref="Field"/> and applies inner ops in place, so the field's
/// concrete subtype is inferred from the live value at apply time (no subtype
/// is carried). Two shapes:
/// <list type="bullet">
/// <item>
/// Collection-element descent (<see cref="Index"/> non-null): KDL
/// <c>set "Field" index=N { ... }</c>. Navigate into element <c>N</c> of
/// collection <c>Field</c>.
/// </item>
/// <item>
/// Object-field descent (<see cref="Index"/> null): KDL
/// <c>set "Field" { ... }</c>. Navigate into the non-collection object/struct
/// at <c>Field</c>. Editing a struct field rides the applier's struct
/// write-back chain so sibling fields survive.
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
    /// Zero-based element index inside <see cref="Field"/> for collection
    /// descent. Null for object-field descent into a non-collection member.
    /// </summary>
    [JsonPropertyName("index")]
    public int? Index { get; set; }
}
