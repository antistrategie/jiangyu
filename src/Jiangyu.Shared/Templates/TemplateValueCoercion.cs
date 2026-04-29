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

    /// <summary>
    /// True when the destination is an integer-family scalar (int, short,
    /// ushort, uint, long, ulong, sbyte) that can accept an Int32-kinded
    /// patch value via range-checked widening in <see cref="TryWidenInt32"/>.
    /// Excludes <see cref="byte"/> (handled separately as the canonical Byte
    /// kind) and <see cref="bool"/> (not numeric).
    /// </summary>
    public static bool IsIntegerFamilyTarget(Type t)
        => t == typeof(int)
        || t == typeof(short)
        || t == typeof(ushort)
        || t == typeof(uint)
        || t == typeof(long)
        || t == typeof(ulong)
        || t == typeof(sbyte);

    /// <summary>
    /// Widens (or narrows, with range checks) a canonical Int32 patch value
    /// into the destination integer field's CLR type. Mirrors the design
    /// where <see cref="TemplateMemberQuery"/> folds all integer widths onto
    /// the Int32 patch kind so the editor renders a single number control.
    /// </summary>
    public static bool TryWidenInt32(int v, Type targetType, out object? converted, out string? error)
    {
        converted = null;
        error = null;

        if (targetType == typeof(int)) { converted = v; return true; }
        if (targetType == typeof(long)) { converted = (long)v; return true; }
        if (targetType == typeof(ulong))
        {
            if (v < 0) { error = $"value {v} is negative; UInt64 is unsigned."; return false; }
            converted = (ulong)v;
            return true;
        }
        if (targetType == typeof(uint))
        {
            if (v < 0) { error = $"value {v} is negative; UInt32 is unsigned."; return false; }
            converted = (uint)v;
            return true;
        }
        if (targetType == typeof(ushort))
        {
            if (v is < ushort.MinValue or > ushort.MaxValue)
            {
                error = $"value {v} is out of UInt16 range (0..{ushort.MaxValue}).";
                return false;
            }
            converted = (ushort)v;
            return true;
        }
        if (targetType == typeof(short))
        {
            if (v is < short.MinValue or > short.MaxValue)
            {
                error = $"value {v} is out of Int16 range ({short.MinValue}..{short.MaxValue}).";
                return false;
            }
            converted = (short)v;
            return true;
        }
        if (targetType == typeof(sbyte))
        {
            if (v is < sbyte.MinValue or > sbyte.MaxValue)
            {
                error = $"value {v} is out of SByte range ({sbyte.MinValue}..{sbyte.MaxValue}).";
                return false;
            }
            converted = (sbyte)v;
            return true;
        }

        error = $"value kind Int32 but member type is {targetType.FullName}.";
        return false;
    }
}
