namespace Jiangyu.Shared.Templates;

/// <summary>
/// Canonical numeric-kind coercion for template values. Keeps coercion rules
/// in one place so compile-time validation, host-side editor normalisation,
/// and loader apply-time conversion all agree on how numeric literals narrow
/// or widen between <see cref="CompiledTemplateValueKind.Byte"/>,
/// <see cref="CompiledTemplateValueKind.Int32"/>, and
/// <see cref="CompiledTemplateValueKind.Single"/>.
/// </summary>
public static class TemplateValueCoercion
{
    public static bool TryCoerceNumericKind(
        CompiledTemplateValue value,
        CompiledTemplateValueKind targetKind,
        out string? error)
    {
        if (value == null)
        {
            error = "value payload is null.";
            return false;
        }

        error = null;

        switch (targetKind)
        {
            case CompiledTemplateValueKind.Byte:
                if (value.Kind == CompiledTemplateValueKind.Byte)
                {
                    if (value.Byte.HasValue)
                        return true;
                    if (value.Int32 is { } i32FromByte)
                    {
                        if (i32FromByte is < byte.MinValue or > byte.MaxValue)
                        {
                            error = $"value {i32FromByte} is out of Byte range (0-255).";
                            return false;
                        }

                        value.Byte = (byte)i32FromByte;
                        value.Int32 = null;
                        value.Single = null;
                        return true;
                    }
                    error = "Byte value is missing.";
                    return false;
                }

                if (value.Kind == CompiledTemplateValueKind.Int32 && value.Int32 is { } i32)
                {
                    if (i32 is < byte.MinValue or > byte.MaxValue)
                    {
                        error = $"value {i32} is out of Byte range (0-255).";
                        return false;
                    }

                    value.Kind = CompiledTemplateValueKind.Byte;
                    value.Byte = (byte)i32;
                    value.Int32 = null;
                    value.Single = null;
                    return true;
                }

                if (value.Kind == CompiledTemplateValueKind.Single && value.Single is { } sByte)
                {
                    if (!IsFiniteWholeNumber(sByte)
                        || sByte < byte.MinValue
                        || sByte > byte.MaxValue)
                    {
                        error = $"value {sByte} is not a whole Byte in range 0-255.";
                        return false;
                    }

                    value.Kind = CompiledTemplateValueKind.Byte;
                    value.Byte = (byte)sByte;
                    value.Int32 = null;
                    value.Single = null;
                    return true;
                }

                error = $"cannot coerce {value.Kind} to Byte.";
                return false;

            case CompiledTemplateValueKind.Int32:
                if (value.Kind == CompiledTemplateValueKind.Int32)
                {
                    if (value.Int32.HasValue)
                        return true;
                    error = "Int32 value is missing.";
                    return false;
                }

                if (value.Kind == CompiledTemplateValueKind.Byte && value.Byte is { } b32)
                {
                    value.Kind = CompiledTemplateValueKind.Int32;
                    value.Int32 = b32;
                    value.Byte = null;
                    value.Single = null;
                    return true;
                }

                if (value.Kind == CompiledTemplateValueKind.Single && value.Single is { } s32)
                {
                    if (!IsFiniteWholeNumber(s32)
                        || s32 < int.MinValue
                        || s32 > int.MaxValue)
                    {
                        error = $"value {s32} is not a whole Int32 in range {int.MinValue}..{int.MaxValue}.";
                        return false;
                    }

                    value.Kind = CompiledTemplateValueKind.Int32;
                    value.Int32 = (int)s32;
                    value.Byte = null;
                    value.Single = null;
                    return true;
                }

                error = $"cannot coerce {value.Kind} to Int32.";
                return false;

            case CompiledTemplateValueKind.Single:
                if (value.Kind == CompiledTemplateValueKind.Single)
                {
                    if (value.Single.HasValue)
                        return true;
                    error = "Single value is missing.";
                    return false;
                }

                if (value.Kind == CompiledTemplateValueKind.Int32 && value.Int32 is { } iSingle)
                {
                    value.Kind = CompiledTemplateValueKind.Single;
                    value.Single = iSingle;
                    value.Int32 = null;
                    value.Byte = null;
                    return true;
                }

                if (value.Kind == CompiledTemplateValueKind.Byte && value.Byte is { } bSingle)
                {
                    value.Kind = CompiledTemplateValueKind.Single;
                    value.Single = bSingle;
                    value.Int32 = null;
                    value.Byte = null;
                    return true;
                }

                error = $"cannot coerce {value.Kind} to Single.";
                return false;

            default:
                return true;
        }
    }

    private static bool IsFiniteWholeNumber(float value)
        => !float.IsNaN(value)
           && !float.IsInfinity(value)
           && MathF.Floor(value) == value;
}
