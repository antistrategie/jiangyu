using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public class TemplatePatchPathValidatorTests
{
    [Theory]
    [InlineData("Accuracy")]
    [InlineData("Properties")]
    [InlineData("Properties.Accuracy")]
    [InlineData("Properties.Armor")]
    [InlineData("a.b.c.d")]
    [InlineData("_underscore_start")]
    [InlineData("m_ID")]
    [InlineData("Properties2")]
    [InlineData("Skills[0]")]
    [InlineData("Skills[12]")]
    [InlineData("Properties.MoraleEvents[3]")]
    [InlineData("Skills[0].DamageBonus")]
    [InlineData("a.b[0].c.d[12].e")]
    public void Accepts_ValidPaths(string path)
    {
        Assert.True(TemplatePatchPathValidator.IsSupportedFieldPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData(".Properties")]
    [InlineData("Properties.")]
    [InlineData("Properties..Accuracy")]
    [InlineData("Properties.Accuracy.")]
    [InlineData("Properties/Accuracy")]
    [InlineData("Properties\\Accuracy")]
    [InlineData("Skills(0)")]
    [InlineData("Properties.Skills[0)")]
    [InlineData("Skills[]")]
    [InlineData("Skills[-1]")]
    [InlineData("Skills[0xA]")]
    [InlineData("Skills[ 0 ]")]
    [InlineData("Skills[0]extra")]
    [InlineData("Skills[0][1]")]
    [InlineData("2InvalidStart")]
    [InlineData("Properties.2Invalid")]
    [InlineData("Properties.Inv-alid")]
    [InlineData("Properties.Inv ali d")]
    public void Rejects_InvalidPaths(string path)
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedFieldPath(path));
    }

    [Fact]
    public void Accepts_Null_And_ReturnsFalse()
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedFieldPath(null));
    }

    [Theory]
    [MemberData(nameof(SupportedScalarValues))]
    public void Accepts_FullyPopulatedScalarValues(CompiledTemplateScalarValue value)
    {
        Assert.True(TemplatePatchPathValidator.IsSupportedScalarValue(value));
    }

    public static TheoryData<CompiledTemplateScalarValue> SupportedScalarValues =>
        new()
        {
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Boolean, Boolean = true },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Boolean, Boolean = false },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Byte, Byte = 0 },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Byte, Byte = 255 },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Int32, Int32 = 0 },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Int32, Int32 = -1 },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Single, Single = 0f },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Single, Single = 3.14f },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.String, String = "" },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.String, String = "hello" },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Enum, EnumValue = "SomeMember" },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Enum, EnumType = "SomeEnum", EnumValue = "SomeMember" },
            new CompiledTemplateScalarValue
            {
                Kind = CompiledTemplateScalarValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = "SkillTemplate",
                    TemplateId = "skill.some_skill",
                },
            },
        };

    [Theory]
    [MemberData(nameof(UnsupportedScalarValues))]
    public void Rejects_IncompleteScalarValues(CompiledTemplateScalarValue value)
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedScalarValue(value));
    }

    public static TheoryData<CompiledTemplateScalarValue> UnsupportedScalarValues =>
        new()
        {
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Boolean, Boolean = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Byte, Byte = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Int32, Int32 = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Single, Single = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.String, String = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Enum, EnumValue = null },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Enum, EnumValue = "" },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.Enum, EnumValue = "  " },
            new CompiledTemplateScalarValue { Kind = CompiledTemplateScalarValueKind.TemplateReference, Reference = null },
            new CompiledTemplateScalarValue
            {
                Kind = CompiledTemplateScalarValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = "", TemplateId = "skill.foo" },
            },
            new CompiledTemplateScalarValue
            {
                Kind = CompiledTemplateScalarValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = "SkillTemplate", TemplateId = "" },
            },
            new CompiledTemplateScalarValue
            {
                Kind = CompiledTemplateScalarValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = " ", TemplateId = "   " },
            },
        };

    [Fact]
    public void Rejects_NullScalarValue()
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedScalarValue(null!));
    }

    [Fact]
    public void Rejects_MismatchedKindFields()
    {
        // Only the field matching Kind is consulted; a filled non-matching
        // field doesn't count as populated.
        var value = new CompiledTemplateScalarValue
        {
            Kind = CompiledTemplateScalarValueKind.Int32,
            Single = 3.14f,
        };
        Assert.False(TemplatePatchPathValidator.IsSupportedScalarValue(value));
    }
}
