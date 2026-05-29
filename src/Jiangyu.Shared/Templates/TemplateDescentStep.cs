using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Templates;

/// <summary>
/// One descent step along a template patch path: an edit into element
/// <see cref="Index"/> of collection <see cref="Field"/>, the KDL syntax
/// <c>set "Field" index=N { ... }</c>. The element's concrete subtype is
/// inferred from the live element at apply time, so no subtype is carried.
///
/// Descent steps live as a structural list on patch operations. They are
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

    /// <summary>Zero-based element index inside <see cref="Field"/>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }
}
