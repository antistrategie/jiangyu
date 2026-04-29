using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class TemplateValueCoercionTests
{
    [Fact]
    public void ByteTarget_FromInt32InRange_Coerces()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Int32,
            Int32 = 42,
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Byte, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CompiledTemplateValueKind.Byte, value.Kind);
        Assert.Equal((byte)42, value.Byte);
        Assert.Null(value.Int32);
    }

    [Fact]
    public void ByteTarget_FromInt32OutOfRange_Rejects()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Int32,
            Int32 = 300,
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Byte, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("out of Byte range", error);
        Assert.Equal(CompiledTemplateValueKind.Int32, value.Kind);
    }

    [Fact]
    public void Int32Target_FromWholeSingle_Coerces()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Single,
            Single = 123.0f,
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Int32, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CompiledTemplateValueKind.Int32, value.Kind);
        Assert.Equal(123, value.Int32);
        Assert.Null(value.Single);
    }

    [Fact]
    public void Int32Target_FromFractionalSingle_Rejects()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Single,
            Single = 10.5f,
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Int32, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("whole Int32", error);
        Assert.Equal(CompiledTemplateValueKind.Single, value.Kind);
    }

    [Fact]
    public void SingleTarget_FromByte_Coerces()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Byte,
            Byte = 7,
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Single, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(CompiledTemplateValueKind.Single, value.Kind);
        Assert.Equal(7f, value.Single);
        Assert.Null(value.Byte);
    }

    [Fact]
    public void NumericTarget_FromNonNumericKind_Rejects()
    {
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.String,
            String = "hello",
        };

        var ok = TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Int32, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("cannot coerce String", error);
    }

    // --- TryWidenInt32 (integer-family widening) ---

    [Theory]
    [InlineData(typeof(int), 42)]
    [InlineData(typeof(long), 42L)]
    [InlineData(typeof(uint), (uint)42)]
    [InlineData(typeof(ulong), (ulong)42)]
    [InlineData(typeof(ushort), (ushort)42)]
    [InlineData(typeof(short), (short)42)]
    [InlineData(typeof(sbyte), (sbyte)42)]
    public void TryWidenInt32_InRange_Widens(Type targetType, object expected)
    {
        var ok = TemplateValueCoercion.TryWidenInt32(42, targetType, out var converted, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(converted);
        Assert.Equal(expected.GetType(), converted!.GetType());
        Assert.Equal(expected, converted);
    }

    [Theory]
    [InlineData(typeof(ushort), 70_000, "UInt16 range")]
    [InlineData(typeof(ushort), -1, "UInt16 range")]
    [InlineData(typeof(short), 40_000, "Int16 range")]
    [InlineData(typeof(short), -40_000, "Int16 range")]
    [InlineData(typeof(sbyte), 200, "SByte range")]
    [InlineData(typeof(sbyte), -200, "SByte range")]
    [InlineData(typeof(uint), -1, "negative")]
    [InlineData(typeof(ulong), -1, "negative")]
    public void TryWidenInt32_OutOfRange_Rejects(Type targetType, int value, string errorFragment)
    {
        var ok = TemplateValueCoercion.TryWidenInt32(value, targetType, out var converted, out var error);

        Assert.False(ok);
        Assert.Null(converted);
        Assert.NotNull(error);
        Assert.Contains(errorFragment, error);
    }

    [Fact]
    public void TryWidenInt32_BoundaryValues_AcceptedAtEdges()
    {
        Assert.True(TemplateValueCoercion.TryWidenInt32(ushort.MaxValue, typeof(ushort), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(ushort.MinValue, typeof(ushort), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(short.MaxValue, typeof(short), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(short.MinValue, typeof(short), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(sbyte.MaxValue, typeof(sbyte), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(sbyte.MinValue, typeof(sbyte), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(0, typeof(uint), out _, out _));
        Assert.True(TemplateValueCoercion.TryWidenInt32(0, typeof(ulong), out _, out _));
    }

    [Fact]
    public void TryWidenInt32_UnsupportedTarget_Rejects()
    {
        var ok = TemplateValueCoercion.TryWidenInt32(42, typeof(string), out var converted, out var error);

        Assert.False(ok);
        Assert.Null(converted);
        Assert.NotNull(error);
        Assert.Contains("System.String", error);
    }

    // --- IsIntegerFamilyTarget ---

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(short))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(long))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(sbyte))]
    public void IsIntegerFamilyTarget_TrueForIntegerWidths(Type t)
    {
        Assert.True(TemplateValueCoercion.IsIntegerFamilyTarget(t));
    }

    [Theory]
    [InlineData(typeof(byte))] // canonical Byte kind path, not the family widener
    [InlineData(typeof(bool))]
    [InlineData(typeof(float))]
    [InlineData(typeof(double))]
    [InlineData(typeof(string))]
    public void IsIntegerFamilyTarget_FalseForOthers(Type t)
    {
        Assert.False(TemplateValueCoercion.IsIntegerFamilyTarget(t));
    }
}
