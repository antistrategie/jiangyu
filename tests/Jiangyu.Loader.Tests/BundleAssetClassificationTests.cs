using Jiangyu.Loader.Bundles;
using Xunit;

namespace Jiangyu.Loader.Tests;

// The catalog indexes a bundle from its asset table alone, so what an asset is has to
// follow from its path. These pin that reading, and the stem the object inside answers to.
public class BundleAssetClassificationTests
{
    [Theory]
    [InlineData("assets/jiangyu/staging/meshreplacement/audio/lenna__click_bark.wav", "Audio")]
    [InlineData("assets/audio/theme.OGG", "Audio")]
    [InlineData("assets/jiangyu/staging/meshreplacement/spriteadditions/asteria__armor__default__icon.png", "Image")]
    [InlineData("assets/textures/skin.tga", "Image")]
    [InlineData("assets/jiangyu/staging/meshreplacement/generated/klukai__stand_look_left.asset", "Serialised")]
    [InlineData("assets/prefabs/klukai/default/main.prefab", "Prefab")]
    [InlineData("assets/models/rig.fbx", "Prefab")]
    [InlineData("assets/models/rig.psd", "Prefab")]
    [InlineData("assets/ui/transmog/outfit-modal.uxml", "Other")]
    [InlineData("assets/ui/transmog/outfit-modal", "Other")]
    public void ClassifiesByExtension(string assetName, string expected)
        => Assert.Equal(expected, BundleReplacementCatalog.ClassifyAssetName(assetName).ToString());

    [Theory]
    [InlineData("assets/jiangyu/staging/meshreplacement/audio/weapons__ar__ar_burst_01.wav", "weapons__ar__ar_burst_01")]
    [InlineData("assets/jiangyu/staging/meshreplacement/generated/sprite_source__portrait.asset", "sprite_source__portrait")]
    [InlineData("main.prefab", "main")]
    [InlineData("assets/ui/outfit-modal", "outfit-modal")]
    public void StemIsTheLeafWithoutItsExtension(string assetName, string expected)
        => Assert.Equal(expected, BundleReplacementCatalog.AssetStem(assetName));

    [Fact]
    public void AdditionBundleLoadsByItsPrefabWhenItListsOne()
    {
        var names = new[] { "assets/prefabs/klukai/default/main.prefab", "assets/prefabs/klukai/default/extra.png" };
        Assert.Equal("assets/prefabs/klukai/default/main.prefab", BundleReplacementCatalog.PrefabEntryName(names));

        var reordered = new[] { "assets/prefabs/klukai/default/extra.png", "assets/prefabs/klukai/default/main.prefab" };
        Assert.Equal("assets/prefabs/klukai/default/main.prefab", BundleReplacementCatalog.PrefabEntryName(reordered));
    }

    [Fact]
    public void AdditionBundleWithoutAPrefabClaimsNoPrefabKey()
    {
        var ui = new[] { "assets/ui/transmog/outfit-modal.uxml", "assets/ui/transmog/outfit-modal.uss" };
        Assert.Null(BundleReplacementCatalog.PrefabEntryName(ui));
        Assert.Null(BundleReplacementCatalog.PrefabEntryName(Array.Empty<string>()));
    }
}
