using System;
using System.Linq;
using Jiangyu.Loader.Bundles;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// The probe order decides which Unity type a bundle asset is tried as first,
/// and the first type that loads is the one it registers as. These tests pin
/// the orderings that keep a multi-type asset landing where it belongs: Sprite
/// ahead of Texture2D everywhere, and GameObject ahead of Sprite for every
/// extension whose importer can emit a prefab.
/// </summary>
public class BundleAssetProbeOrderTests
{
    private static readonly string[] Samples =
    {
        "assets/audio/cheyanne/bark.wav",
        "assets/audio/x.ogg",
        "assets/sprites/portrait.png",
        "assets/textures/skin.tga",
        "assets/prefabs/main.prefab",
        "assets/authored/model.gltf",
        "assets/authored/model.glb",
        "assets/weird/no-extension",
        "assets/weird/trailing.",
        "assets/weird/thing.qqq",
        "UPPER/CASE/Portrait.PNG",
    };

    [Theory]
    [InlineData("assets/audio/bark.wav")]
    [InlineData("assets/sprites/portrait.png")]
    [InlineData("assets/prefabs/main.prefab")]
    [InlineData("assets/weird/no-extension")]
    public void EveryOrderingProbesSpriteBeforeTexture(string assetName)
    {
        var order = BundleReplacementCatalog.ProbeOrderFor(assetName);

        var sprite = Array.IndexOf(order, BundleReplacementCatalog.AssetProbeKind.Sprite);
        var texture = Array.IndexOf(order, BundleReplacementCatalog.AssetProbeKind.Texture);

        Assert.True(sprite >= 0 && texture >= 0);
        Assert.True(sprite < texture, $"Sprite must precede Texture2D for '{assetName}'.");
    }

    // A PSD imported in rig mode is a prefab main asset with Sprite
    // sub-assets, so demoting GameObject would register the rig as a sprite.
    [Theory]
    [InlineData("assets/art/rig.psd")]
    [InlineData("assets/art/rig.PSD")]
    [InlineData("assets/models/hull.fbx")]
    [InlineData("assets/prefabs/main.prefab")]
    public void PrefabCapableExtensionsProbeGameObjectBeforeSprite(string assetName)
    {
        var order = BundleReplacementCatalog.ProbeOrderFor(assetName);

        var gameObject = Array.IndexOf(order, BundleReplacementCatalog.AssetProbeKind.GameObject);
        var sprite = Array.IndexOf(order, BundleReplacementCatalog.AssetProbeKind.Sprite);

        Assert.True(gameObject >= 0 && sprite >= 0);
        Assert.True(gameObject < sprite, $"GameObject must precede Sprite for '{assetName}'.");
    }

    [Fact]
    public void EveryOrderingCoversEveryKindExactlyOnce()
    {
        var all = Enum.GetValues<BundleReplacementCatalog.AssetProbeKind>()
            .OrderBy(k => k)
            .ToArray();

        foreach (var sample in Samples)
        {
            var order = BundleReplacementCatalog.ProbeOrderFor(sample);
            Assert.Equal(all, order.OrderBy(k => k).ToArray());
            Assert.Equal(order.Length, order.Distinct().Count());
        }
    }

    // Expected kind passed by name: the enum is internal to the loader, so it
    // cannot appear in a public xUnit test signature.
    [Theory]
    [InlineData("a/b.wav", "Audio")]
    [InlineData("a/b.ogg", "Audio")]
    [InlineData("a/b.png", "Sprite")]
    [InlineData("a/b.PNG", "Sprite")]
    [InlineData("a/b.tga", "Sprite")]
    [InlineData("a/b.prefab", "GameObject")]
    [InlineData("a/b.gltf", "GameObject")]
    [InlineData("a/b.unknownext", "GameObject")]
    [InlineData("a/b", "GameObject")]
    public void ExtensionPicksTheFirstProbe(string assetName, string expected)
    {
        Assert.Equal(expected, BundleReplacementCatalog.ProbeOrderFor(assetName)[0].ToString());
    }
}
