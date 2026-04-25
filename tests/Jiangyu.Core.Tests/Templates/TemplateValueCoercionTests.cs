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
}
