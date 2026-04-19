namespace Jiangyu.Shared.Templates;

/// <summary>
/// Shared path-syntax validator for compiled template patch field paths. Lives
/// in Jiangyu.Shared so both the loader-side catalogue and framework-agnostic
/// tests can reach it without pulling in IL2CPP references.
/// </summary>
public static class TemplatePatchPathValidator
{
    /// <summary>
    /// Returns true when <paramref name="fieldPath"/> is a non-empty sequence
    /// of dotted segments. Each segment must be a valid identifier optionally
    /// followed by a non-negative integer indexer (e.g. <c>Skills[0]</c>).
    /// Whitespace-only segments, leading/trailing dots, parentheses, and
    /// backslashes/forward-slashes are rejected.
    /// </summary>
    public static bool IsSupportedFieldPath(string? fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath))
            return false;
        if (fieldPath!.StartsWith('.') || fieldPath.EndsWith('.'))
            return false;

        foreach (var c in fieldPath)
        {
            if (c == '/' || c == '\\' || c == '(' || c == ')')
                return false;
        }

        foreach (var segment in fieldPath.Split('.'))
        {
            if (string.IsNullOrWhiteSpace(segment))
                return false;
            if (!IsValidSegment(segment))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> is fully populated for its
    /// declared <see cref="CompiledTemplateValueKind"/> — the matching
    /// typed field is non-null (scalar kinds) or the <c>Reference</c> payload
    /// has both templateType and templateId (TemplateReference kind).
    /// </summary>
    public static bool IsSupportedValue(CompiledTemplateValue value)
    {
        if (value == null)
            return false;

        return value.Kind switch
        {
            CompiledTemplateValueKind.Boolean => value.Boolean.HasValue,
            CompiledTemplateValueKind.Byte => value.Byte.HasValue,
            CompiledTemplateValueKind.Int32 => value.Int32.HasValue,
            CompiledTemplateValueKind.Single => value.Single.HasValue,
            CompiledTemplateValueKind.String => value.String != null,
            CompiledTemplateValueKind.Enum => !string.IsNullOrWhiteSpace(value.EnumValue),
            CompiledTemplateValueKind.TemplateReference =>
                value.Reference != null
                && !string.IsNullOrWhiteSpace(value.Reference.TemplateType)
                && !string.IsNullOrWhiteSpace(value.Reference.TemplateId),
            _ => false,
        };
    }

    private static bool IsValidSegment(string segment)
    {
        var bracketStart = segment.IndexOf('[');
        if (bracketStart < 0)
            return IsValidIdentifier(segment);

        var bracketEnd = segment.IndexOf(']', bracketStart + 1);
        if (bracketEnd != segment.Length - 1)
            return false;

        var name = segment[..bracketStart];
        if (!IsValidIdentifier(name))
            return false;

        var indexText = segment.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
        if (string.IsNullOrEmpty(indexText))
            return false;

        foreach (var c in indexText)
        {
            if (!char.IsDigit(c))
                return false;
        }

        // Reject indices that overflow Int32 at the validator, not at apply
        // time — the applier parses via int.TryParse for defence, but catching
        // it here gives modders a load-time warning instead of a runtime crash.
        return int.TryParse(indexText, out _);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (!(char.IsLetter(name[0]) || name[0] == '_'))
            return false;
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }
}
