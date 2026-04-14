using Jiangyu.Core.Assets;
using Jiangyu.Core.Glb;

namespace Jiangyu.Core.Tests.Glb;

public class StandardChannelMapTests
{
    [Theory]
    [InlineData("_BaseColorMap", "BaseColor")]
    [InlineData("_MainTex", "BaseColor")]
    [InlineData("_BaseMap", "BaseColor")]
    [InlineData("_NormalMap", "Normal")]
    [InlineData("_BumpMap", "Normal")]
    [InlineData("_MetallicGlossMap", "MetallicRoughness")]
    [InlineData("_EmissionMap", "Emissive")]
    [InlineData("_OcclusionMap", "Occlusion")]
    public void KnownProperties_MapToExpectedChannels(string property, string expectedChannel)
    {
        Assert.True(AssetPipelineService.StandardChannelMap.TryGetValue(property, out var channel));
        Assert.Equal(expectedChannel, channel);
    }

    [Theory]
    [InlineData("_MaskMap")]
    [InlineData("_Effect_Map")]
    [InlineData("_DetailAlbedoMap")]
    public void NonStandardProperties_NotMapped(string property)
    {
        Assert.False(AssetPipelineService.StandardChannelMap.ContainsKey(property));
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        Assert.True(AssetPipelineService.StandardChannelMap.ContainsKey("_basecolormap"));
        Assert.True(AssetPipelineService.StandardChannelMap.ContainsKey("_NORMALMAP"));
    }
}

public class IsMenaceTextureNamePatternTests
{
    [Theory]
    [InlineData("soldier_BaseMap")]
    [InlineData("soldier_BaseColorMap")]
    [InlineData("soldier_NormalMap")]
    [InlineData("soldier_Normal")]
    [InlineData("soldier_MaskMap")]
    [InlineData("soldier_EffectMap")]
    [InlineData("soldier_Emissive")]
    [InlineData("soldier_EmissiveColorMap")]
    [InlineData("soldier_OcclusionMap")]
    [InlineData("soldier_MetallicGlossMap")]
    public void KnownSuffixes_ReturnTrue(string name)
    {
        Assert.True(GlbMeshBundleCompiler.IsMenaceTextureNamePattern(name));
    }

    [Theory]
    [InlineData("soldier_diffuse")]
    [InlineData("random_texture")]
    [InlineData("soldier_ao")]
    [InlineData("soldier")]
    public void UnknownSuffixes_ReturnFalse(string name)
    {
        Assert.False(GlbMeshBundleCompiler.IsMenaceTextureNamePattern(name));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.True(GlbMeshBundleCompiler.IsMenaceTextureNamePattern("SOLDIER_BASEMAP"));
        Assert.True(GlbMeshBundleCompiler.IsMenaceTextureNamePattern("soldier_normalmap"));
    }
}

public class IsLinearTextureNameTests
{
    [Theory]
    [InlineData("soldier_MaskMap", true)]
    [InlineData("soldier_NormalMap", true)]
    [InlineData("soldier_Normal", true)]
    [InlineData("soldier_EffectMap", true)]
    [InlineData("soldier_BaseMap", false)]
    [InlineData("soldier_BaseColorMap", false)]
    [InlineData("soldier_Emissive", false)]
    [InlineData("soldier_OcclusionMap", false)]
    public void IdentifiesLinearTextures(string name, bool expected)
    {
        Assert.Equal(expected, GlbMeshBundleCompiler.IsLinearTextureName(name));
    }
}

public class FindCommonPrefixTests
{
    [Fact]
    public void SingleName_ReturnsName()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix(["soldier_Normal"]);

        Assert.Equal("soldier_Normal", result);
    }

    [Fact]
    public void SharedPrefix_ReturnsCommonPart()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix(
            ["soldier_Normal", "soldier_BaseMap", "soldier_MaskMap"]);

        Assert.Equal("soldier", result);
    }

    [Fact]
    public void TrailingUnderscore_StrippedFromPrefix()
    {
        // If the common prefix ends with _, it's trimmed
        var result = GlbMeshBundleCompiler.FindCommonPrefix(
            ["pfx_A", "pfx_B"]);

        Assert.Equal("pfx", result);
    }

    [Fact]
    public void NoCommonPrefix_ReturnsNull()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix(
            ["alpha_Normal", "beta_Normal"]);

        Assert.Null(result);
    }

    [Fact]
    public void EmptyList_ReturnsNull()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix([]);

        Assert.Null(result);
    }

    [Fact]
    public void IdenticalNames_ReturnsFullName()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix(
            ["soldier_Normal", "soldier_Normal"]);

        Assert.Equal("soldier_Normal", result);
    }

    [Fact]
    public void DifferentLengths_FindsShortestCommonPrefix()
    {
        var result = GlbMeshBundleCompiler.FindCommonPrefix(
            ["a_short", "a_longer_name", "a_medium"]);

        Assert.Equal("a", result);
    }
}
