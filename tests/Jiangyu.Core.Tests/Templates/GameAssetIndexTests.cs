using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Replacements;
using Xunit;

namespace Jiangyu.Core.Tests.Templates;

public class GameAssetIndexTests
{
    [Fact]
    public void Contains_MapsKnownClassNamesToCategories()
    {
        var index = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "sprite_a", ClassName = "Sprite" },
                new() { Name = "tex_a", ClassName = "Texture2D" },
                new() { Name = "audio_a", ClassName = "AudioClip" },
                new() { Name = "mat_a", ClassName = "Material" },
                new() { Name = "prefab_a", ClassName = "GameObject" },
            },
        });

        Assert.True(index.Contains(AssetCategory.Sprites, "sprite_a"));
        Assert.True(index.Contains(AssetCategory.Textures, "tex_a"));
        Assert.True(index.Contains(AssetCategory.Audio, "audio_a"));
        Assert.True(index.Contains(AssetCategory.Materials, "mat_a"));
        Assert.True(index.Contains(AssetCategory.Prefabs, "prefab_a"));
    }

    [Fact]
    public void Contains_IsCaseSensitive()
    {
        var index = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "MyPrefab", ClassName = "GameObject" },
            },
        });

        Assert.True(index.Contains(AssetCategory.Prefabs, "MyPrefab"));
        Assert.False(index.Contains(AssetCategory.Prefabs, "myprefab"));
    }

    [Fact]
    public void Contains_RejectsCrossCategoryMatch()
    {
        // A texture named "soldier" should not satisfy a prefab lookup, even
        // though the name is the same. Category narrowing matches the
        // additions-catalog contract.
        var index = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "soldier", ClassName = "Texture2D" },
            },
        });

        Assert.True(index.Contains(AssetCategory.Textures, "soldier"));
        Assert.False(index.Contains(AssetCategory.Prefabs, "soldier"));
    }

    [Fact]
    public void Contains_SkipsAssetsWithUnsupportedClassName()
    {
        var index = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "x", ClassName = "MonoBehaviour" },
                new() { Name = "x", ClassName = "ScriptableObject" },
                new() { Name = "x", ClassName = "Mesh" }, // Mesh isn't a supported asset addition category
            },
        });

        Assert.False(index.Contains(AssetCategory.Prefabs, "x"));
        Assert.False(index.Contains(AssetCategory.Textures, "x"));
    }

    [Fact]
    public void Contains_NullAssetIndex_AlwaysReturnsFalse()
    {
        var index = new GameAssetIndex(null!);
        Assert.False(index.Contains(AssetCategory.Prefabs, "anything"));
    }

    [Fact]
    public void Contains_EmptyAssetIndex_AlwaysReturnsFalse()
    {
        var index = new GameAssetIndex(new AssetIndex { Assets = new() });
        Assert.False(index.Contains(AssetCategory.Prefabs, "anything"));
    }

    [Fact]
    public void Contains_NullOrEmptyArgsReturnFalse()
    {
        var index = new GameAssetIndex(new AssetIndex
        {
            Assets = new List<AssetEntry>
            {
                new() { Name = "x", ClassName = "GameObject" },
            },
        });

        Assert.False(index.Contains("", "x"));
        Assert.False(index.Contains(AssetCategory.Prefabs, ""));
        Assert.False(index.Contains(null!, "x"));
        Assert.False(index.Contains(AssetCategory.Prefabs, null!));
    }
}
