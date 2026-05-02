using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

/// <summary>
/// One descent step along a template patch path. Mirrors the KDL syntax
/// <c>set "Field" index=N type="Subtype" { ... }</c>: navigate into element
/// <see cref="Index"/> of collection <see cref="Field"/>, switching the
/// validated type to <see cref="Subtype"/> when the field's element is a
/// polymorphic-abstract base whose concrete instance class can't be inferred
/// from the field declaration alone.
///
/// Descent steps live as a structural list on patch operations — they are
/// not encoded as a bracketed string segment. A directive's
/// <c>FieldPath</c> means the inner-relative member name; descent context
/// (if any) is carried in the <c>Descent</c> list. Nested descent appends
/// further entries to the list in outer-to-inner order.
/// </summary>
public sealed class TemplateDescentStep
{
    /// <summary>The collection member being descended into at this step.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>Element index inside <see cref="Field"/> (zero-based).</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Concrete subtype name when the destination element is a polymorphic-
    /// abstract base; null when the element type is concrete (or the
    /// validator can otherwise infer the type from the field declaration).
    /// </summary>
    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }
}
