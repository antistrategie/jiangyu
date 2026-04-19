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
    /// Returns true when <paramref name="value"/> is a fully-populated
    /// scalar-kind entry (the expected typed field for the kind is non-null).
    /// </summary>
    public static bool IsSupportedScalarValue(CompiledTemplateScalarValue value)
    {
        if (value == null)
            return false;

        return value.Kind switch
        {
            CompiledTemplateScalarValueKind.Boolean => value.Boolean.HasValue,
            CompiledTemplateScalarValueKind.Int32 => value.Int32.HasValue,
            CompiledTemplateScalarValueKind.Single => value.Single.HasValue,
            CompiledTemplateScalarValueKind.String => value.String != null,
            CompiledTemplateScalarValueKind.Enum => !string.IsNullOrWhiteSpace(value.EnumValue),
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

        return true;
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
