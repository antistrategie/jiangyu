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
    [InlineData("Skills[9999999999]")]
    [InlineData("Skills[99999999999999999999]")]
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
    public void Accepts_FullyPopulatedScalarValues(CompiledTemplateValue value)
    {
        Assert.True(TemplatePatchPathValidator.IsSupportedValue(value));
    }

    public static TheoryData<CompiledTemplateValue> SupportedScalarValues =>
        new()
        {
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = true },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = false },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Byte = 0 },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Byte = 255 },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 0 },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = -1 },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 0f },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = 3.14f },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "" },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = "hello" },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Enum, EnumValue = "SomeMember" },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Enum, EnumType = "SomeEnum", EnumValue = "SomeMember" },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = "SkillTemplate",
                    TemplateId = "skill.some_skill",
                },
            },
            // TemplateType=null: monomorphic ref field, lookup type derived
            // from the destination at apply time.
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = null,
                    TemplateId = "skill.some_skill",
                },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = "",
                    TemplateId = "skill.some_skill",
                },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = "  ",
                    TemplateId = "skill.some_skill",
                },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference { Name = "lrm5/icon" },
            },
            // Asset references in modder-facing logical form (slashes preserved)
            // pass the validator; the bundle-side translation to flat
            // `__` happens at compile staging and runtime resolution, not in
            // the wire-format check.
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference { Name = "flat" },
            },
        };

    [Theory]
    [MemberData(nameof(UnsupportedScalarValues))]
    public void Rejects_IncompleteScalarValues(CompiledTemplateValue value)
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedValue(value));
    }

    public static TheoryData<CompiledTemplateValue> UnsupportedScalarValues =>
        new()
        {
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Boolean, Boolean = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Byte, Byte = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Single, Single = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.String, String = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Enum, EnumValue = null },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Enum, EnumValue = "" },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Enum, EnumValue = "  " },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.TemplateReference, Reference = null },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = "SkillTemplate", TemplateId = "" },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = " ", TemplateId = "   " },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference { TemplateType = null, TemplateId = "" },
            },
            new CompiledTemplateValue { Kind = CompiledTemplateValueKind.AssetReference, Asset = null },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference { Name = "" },
            },
            new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference { Name = "   " },
            },
        };

    [Fact]
    public void Rejects_NullScalarValue()
    {
        Assert.False(TemplatePatchPathValidator.IsSupportedValue(null!));
    }

    [Fact]
    public void Rejects_MismatchedKindFields()
    {
        // Only the field matching Kind is consulted; a filled non-matching
        // field doesn't count as populated.
        var value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Int32,
            Single = 3.14f,
        };
        Assert.False(TemplatePatchPathValidator.IsSupportedValue(value));
    }

    // --- Op-shape: Clear ---

    [Fact]
    public void OpShape_Clear_AcceptsBareFieldPath()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Clear,
            FieldPath = "Skills",
        };

        var ok = TemplatePatchPathValidator.TryValidateOpShape(op, op.FieldPath, out var error);
        Assert.True(ok, error);
        Assert.Null(error);
    }

    [Fact]
    public void OpShape_Clear_RejectsIndex()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Clear,
            FieldPath = "Skills",
            Index = 0,
        };

        var ok = TemplatePatchPathValidator.TryValidateOpShape(op, op.FieldPath, out var error);
        Assert.False(ok);
        Assert.Contains("op=Clear cannot carry an 'index' field", error);
    }

    [Fact]
    public void OpShape_Clear_RejectsValue()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Clear,
            FieldPath = "Skills",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = 0,
            },
        };

        var ok = TemplatePatchPathValidator.TryValidateOpShape(op, op.FieldPath, out var error);
        Assert.False(ok);
        Assert.Contains("op=Clear cannot carry a value", error);
    }

    [Fact]
    public void OpShape_Clear_RejectsIndexedTerminal()
    {
        var op = new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Clear,
            FieldPath = "Skills[0]",
        };

        var ok = TemplatePatchPathValidator.TryValidateOpShape(op, op.FieldPath, out var error);
        Assert.False(ok);
        Assert.Contains("indexed terminal segment", error);
    }
}
