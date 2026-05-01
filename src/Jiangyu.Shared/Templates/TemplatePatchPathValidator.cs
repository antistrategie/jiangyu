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
    /// Returns true when the terminal segment of <paramref name="fieldPath"/>
    /// carries an <c>[N]</c> indexer. Append ops target a collection as a
    /// whole, so an indexed terminal is invalid authoring regardless of whether
    /// the check runs at compile time or load time.
    /// </summary>
    public static bool TerminalSegmentIsIndexed(string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath))
            return false;
        var lastDot = fieldPath.LastIndexOf('.');
        var terminal = lastDot < 0 ? fieldPath : fieldPath[(lastDot + 1)..];
        return terminal.Contains('[');
    }

    /// <summary>
    /// Op-shape invariants. Shared by the compile-time emitter and the
    /// loader-side catalogue so hand-authored mods that skip compilation get
    /// the same checks as compiled bundles.
    /// <list type="bullet">
    ///   <item><description><c>Set</c>: <c>index</c> optional (required when targeting a collection element; forbidden on scalars — enforced by the catalog-aware validator); terminal not indexed; value required.</description></item>
    ///   <item><description><c>Append</c>: no <c>index</c> field; terminal not indexed; value required.</description></item>
    ///   <item><description><c>InsertAt</c>: <c>index</c> required and non-negative; terminal not indexed; value required.</description></item>
    ///   <item><description><c>Remove</c>: <c>index</c> required and non-negative; terminal not indexed; no value.</description></item>
    ///   <item><description><c>Clear</c>: no <c>index</c>; terminal not indexed; no value.</description></item>
    /// </list>
    /// </summary>
    public static bool TryValidateOpShape(
        CompiledTemplateSetOperation op, string effectivePath, out string? error)
    {
        if (op == null)
        {
            error = "operation is null.";
            return false;
        }

        var terminalIndexed = TerminalSegmentIsIndexed(effectivePath);
        switch (op.Op)
        {
            case CompiledTemplateOp.Set:
                if (op.Index.HasValue && op.Index.Value < 0)
                {
                    error = $"op=Set has negative index {op.Index.Value}.";
                    return false;
                }
                if (terminalIndexed)
                {
                    error = "op=Set cannot have an indexed terminal segment; use 'set \"<Field>\" index=N' to write one collection element.";
                    return false;
                }
                return RequireValue(op.Value, out error);

            case CompiledTemplateOp.Append:
                if (op.Index.HasValue)
                {
                    error = "op=Append cannot carry an 'index' field; Append writes to the tail.";
                    return false;
                }
                if (terminalIndexed)
                {
                    error = "op=Append cannot have an indexed terminal segment; drop the [N] suffix.";
                    return false;
                }
                return RequireValue(op.Value, out error);

            case CompiledTemplateOp.InsertAt:
                if (!op.Index.HasValue)
                {
                    error = "op=InsertAt requires an 'index' field.";
                    return false;
                }
                if (op.Index.Value < 0)
                {
                    error = $"op=InsertAt has negative index {op.Index.Value}.";
                    return false;
                }
                if (terminalIndexed)
                {
                    error = "op=InsertAt cannot have an indexed terminal segment; the position comes from the 'index' field.";
                    return false;
                }
                return RequireValue(op.Value, out error);

            case CompiledTemplateOp.Remove:
                if (!op.Index.HasValue)
                {
                    error = "op=Remove requires an 'index' field.";
                    return false;
                }
                if (op.Index.Value < 0)
                {
                    error = $"op=Remove has negative index {op.Index.Value}.";
                    return false;
                }
                if (terminalIndexed)
                {
                    error = "op=Remove cannot have an indexed terminal segment; the position comes from the 'index' field.";
                    return false;
                }
                if (op.Value != null)
                {
                    error = "op=Remove cannot carry a value; Remove takes no value.";
                    return false;
                }
                error = null;
                return true;

            case CompiledTemplateOp.Clear:
                if (op.Index.HasValue)
                {
                    error = "op=Clear cannot carry an 'index' field; Clear empties the whole collection.";
                    return false;
                }
                if (terminalIndexed)
                {
                    error = "op=Clear cannot have an indexed terminal segment; drop the [N] suffix.";
                    return false;
                }
                if (op.Value != null)
                {
                    error = "op=Clear cannot carry a value; Clear takes no value.";
                    return false;
                }
                error = null;
                return true;

            default:
                error = $"unknown op '{op.Op}'.";
                return false;
        }
    }

    private static bool RequireValue(CompiledTemplateValue? value, out string? error)
    {
        if (value == null)
        {
            error = "value is required.";
            return false;
        }
        if (!IsSupportedValue(value))
        {
            error = $"value is unsupported or incomplete (kind={value.Kind}).";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="value"/> is fully populated for its
    /// declared <see cref="CompiledTemplateValueKind"/> — the matching
    /// typed field is non-null (scalar kinds) or the <c>Reference</c> payload
    /// has both templateType and templateId (TemplateReference kind).
    /// </summary>
    public static bool IsSupportedValue(CompiledTemplateValue value)
        => IsSupportedValue(value, depth: 0);

    private const int MaxCompositeDepth = 8;

    private static bool IsSupportedValue(CompiledTemplateValue value, int depth)
    {
        if (value == null || depth > MaxCompositeDepth)
            return false;

        return value.Kind switch
        {
            CompiledTemplateValueKind.Boolean => value.Boolean.HasValue,
            CompiledTemplateValueKind.Byte => value.Byte.HasValue,
            CompiledTemplateValueKind.Int32 => value.Int32.HasValue,
            CompiledTemplateValueKind.Single => value.Single.HasValue,
            CompiledTemplateValueKind.String => value.String != null,
            CompiledTemplateValueKind.Enum => !string.IsNullOrWhiteSpace(value.EnumValue),
            // TemplateType is optional. The compile-time validator coerces a
            // bare-string author into TemplateReference{TemplateType=null} for
            // monomorphic ref fields, and the runtime applier derives the
            // lookup type from the destination field at apply time. Only
            // TemplateId is load-bearing here.
            CompiledTemplateValueKind.TemplateReference =>
                value.Reference != null
                && !string.IsNullOrWhiteSpace(value.Reference.TemplateId),
            CompiledTemplateValueKind.Composite => IsSupportedComposite(value.Composite, depth, requireTypeName: true),
            // HandlerConstruction allows an empty TypeName (the runtime
            // applier substitutes the array element type when the modder
            // omitted handler="X" against a monomorphic destination).
            CompiledTemplateValueKind.HandlerConstruction => IsSupportedComposite(value.HandlerConstruction, depth, requireTypeName: false),
            _ => false,
        };
    }

    private static bool IsSupportedComposite(CompiledTemplateComposite? composite, int depth, bool requireTypeName)
    {
        if (composite == null) return false;
        if (requireTypeName && string.IsNullOrWhiteSpace(composite.TypeName))
            return false;
        if (composite.Fields == null || composite.Fields.Count == 0)
            return false;

        foreach (var (fieldName, fieldValue) in composite.Fields)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return false;
            if (!IsSupportedValue(fieldValue, depth + 1))
                return false;
        }

        return true;
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
