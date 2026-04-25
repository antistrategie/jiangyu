using Jiangyu.Core.Il2Cpp;

namespace Jiangyu.Core.Tests.Il2Cpp;

public class Il2CppMetadataSupplementTests
{
    [Fact]
    public void RoundTripJson_PreservesNamedArrayAndFieldMetadata()
    {
        var supplement = new Il2CppMetadataSupplement
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GameAssemblyMtime = DateTimeOffset.UtcNow.AddMinutes(-2),
            MetadataMtime = DateTimeOffset.UtcNow.AddMinutes(-1),
            NamedArrays =
            [
                new NamedArrayPairing
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
                    FieldName = "BoneIndices",
                    EnumTypeShortName = "FixtureAttribute",
                },
            ],
            Fields =
            [
                new FieldMetadata
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "Jiangyu.Core.Tests.Templates.Fixtures.Gameplay.FixtureEntity",
                    FieldName = "HudYOffsetScale",
                    RangeMin = 0.25,
                    RangeMax = 2.0,
                    Tooltip = "HUD Y offset scaling",
                    HideInInspector = true,
                    IsSoundId = true,
                },
            ],
        };

        var roundTripped = Il2CppMetadataSupplement.FromJson(supplement.ToJson());

        Assert.NotNull(roundTripped);
        Assert.Equal(Il2CppMetadataSupplement.CurrentSchemaVersion, roundTripped!.SchemaVersion);
        Assert.Single(roundTripped.NamedArrays);
        Assert.Single(roundTripped.Fields);
        Assert.Equal("FixtureAttribute", roundTripped.NamedArrays[0].EnumTypeShortName);
        Assert.Equal(0.25, roundTripped.Fields[0].RangeMin);
        Assert.Equal(2.0, roundTripped.Fields[0].RangeMax);
        Assert.Equal("HUD Y offset scaling", roundTripped.Fields[0].Tooltip);
        Assert.True(roundTripped.Fields[0].HideInInspector);
        Assert.True(roundTripped.Fields[0].IsSoundId);
    }

    [Fact]
    public void LookupHelpers_FindExpectedEntries()
    {
        var supplement = new Il2CppMetadataSupplement
        {
            NamedArrays =
            [
                new NamedArrayPairing
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "FixtureEntity",
                    FieldName = "BoneIndices",
                    EnumTypeShortName = "FixtureAttribute",
                },
            ],
            Fields =
            [
                new FieldMetadata
                {
                    TemplateTypeShortName = "FixtureEntity",
                    TemplateTypeFullName = "FixtureEntity",
                    FieldName = "HudYOffsetScale",
                },
            ],
        };

        var hasNamedArray = supplement.TryFindNamedArrayEnum("FixtureEntity", "BoneIndices", out var enumName);
        var fieldMeta = supplement.FindFieldMetadata("FixtureEntity", "HudYOffsetScale");

        Assert.True(hasNamedArray);
        Assert.Equal("FixtureAttribute", enumName);
        Assert.NotNull(fieldMeta);
        Assert.Null(supplement.FindFieldMetadata("FixtureEntity", "Missing"));
    }
}
