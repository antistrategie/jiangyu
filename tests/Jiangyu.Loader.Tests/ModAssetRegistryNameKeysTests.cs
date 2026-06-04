using System.Linq;
using Jiangyu.Loader.Bundles;
using Xunit;

namespace Jiangyu.Loader.Tests;

public class ModAssetRegistryNameKeysTests
{
    [Fact]
    public void IndexesFullPathLeafAndCategoryRelative()
    {
        var keys = ModAssetRegistry.NameKeys("Assets/UI/strategy/relationship_bar.uxml").ToArray();

        Assert.Contains("assets/ui/strategy/relationship_bar.uxml", keys); // full path
        Assert.Contains("strategy/relationship_bar", keys); // category-relative
        Assert.Contains("relationship_bar", keys); // leaf
    }

    [Fact]
    public void TopLevelAssetHasNoSubfolderSpelling()
    {
        var keys = ModAssetRegistry.NameKeys("Assets/UI/relationship_bar.uxml").ToArray();

        Assert.Contains("assets/ui/relationship_bar.uxml", keys);
        Assert.Contains("relationship_bar", keys);
        // The only key with a slash is the full path; there is no subfolder to name.
        Assert.DoesNotContain(keys, k => k.Contains('/') && k != "assets/ui/relationship_bar.uxml");
    }

    [Fact]
    public void NestedSubfoldersKeepTheirRelativePath()
    {
        var keys = ModAssetRegistry.NameKeys("Assets/UI/a/b/c.uxml").ToArray();

        Assert.Contains("a/b/c", keys);
        Assert.Contains("c", keys);
    }

    [Fact]
    public void AppliesToAnyCategoryRoot()
    {
        var keys = ModAssetRegistry.NameKeys("Assets/Prefabs/dir/test_cube.prefab").ToArray();

        Assert.Contains("dir/test_cube", keys);
        Assert.Contains("test_cube", keys);
    }

    [Fact]
    public void LowercasesEveryKey()
    {
        var keys = ModAssetRegistry.NameKeys("Assets/UI/Strategy/Bar.uxml").ToArray();

        Assert.Contains("strategy/bar", keys);
        Assert.Contains("bar", keys);
        Assert.All(keys, k => Assert.Equal(k.ToLowerInvariant(), k));
    }
}
