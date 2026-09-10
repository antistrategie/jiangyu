using Jiangyu.Core.Compile;
using Jiangyu.Core.Glb;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Compile;

public sealed class PortraitTexturePolicyTests
{
    [Theory]
    [InlineData("SpeakerTemplate", "StandLookLeftImage", "character/art", true)]
    [InlineData("SpeakerTemplate", "StandLookRightImage", "character/art", true)]
    [InlineData("SpeakerTemplate", "StandLookRightInactiveImage", "character/art", true)]
    [InlineData("Il2CppMenace.Conversations.SpeakerTemplate", "StandLookLeftImage", "character/art", true)]
    [InlineData("SpeakerTemplate", "StandLookLeftImage", "character/other", false)]
    [InlineData("SpeakerTemplate", "SmallImage", "character/art", false)]
    [InlineData("UnitLeaderTemplate", "StandLookLeftImage", "character/art", false)]
    public void SamplingUsesTheSpeakerFieldAndLogicalAssetReference(
        string templateType, string field, string asset, bool expected)
    {
        var texture = Texture(isAddition: true);
        PortraitTexturePolicy.Apply([texture], [Patch(templateType, field, asset)]);

        Assert.Equal(expected, texture.IsStandingPortrait);
    }

    [Fact]
    public void ReplacementTexturesKeepTheirSampling()
    {
        var replacement = Texture(isAddition: false);
        PortraitTexturePolicy.Apply([replacement], [Patch("SpeakerTemplate", "StandLookLeftImage", "character/art")]);

        Assert.False(replacement.IsStandingPortrait);
    }

    [Fact]
    public void RemovingPortraitUsageResetsThePolicy()
    {
        var texture = Texture(isAddition: true);
        PortraitTexturePolicy.Apply([texture], [Patch("SpeakerTemplate", "StandLookLeftImage", "character/art")]);
        Assert.True(texture.IsStandingPortrait);

        PortraitTexturePolicy.Apply([texture], null);

        Assert.False(texture.IsStandingPortrait);
    }

    private static GlbMeshBundleCompiler.CompiledTexture Texture(bool isAddition)
        => new() { Name = "character__art", Content = [1, 2, 3], Linear = false, IsAddition = isAddition };

    private static CompiledTemplatePatch Patch(string type, string field, string asset)
        => new()
        {
            TemplateType = type,
            TemplateId = "speaker.custom",
            Set =
            [
                new()
                {
                    FieldPath = field,
                    Value = new()
                    {
                        Kind = CompiledTemplateValueKind.AssetReference,
                        Asset = new() { Name = asset },
                    },
                },
            ],
        };
}
