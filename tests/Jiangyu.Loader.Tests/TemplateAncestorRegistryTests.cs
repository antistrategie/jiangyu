using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

public class TemplateAncestorRegistryTests
{
    [Fact]
    public void UnloadedAncestorReceivesAllRegisteredClonesOnItsFirstAndLaterLoads()
    {
        var registry = new TemplateAncestorRegistry<object>();
        var first = new object();
        var second = new object();
        registry.Remember(1, "Data/Items/", "weapon.first", first);
        registry.Remember(1, "Data/Items/", "weapon.second", second);

        for (var load = 0; load < 2; load++)
        {
            Assert.True(registry.TryGet(1, "Data/Items/", out var clones));
            Assert.Equal(new[] { first, second }, TemplateAncestorRegistry<object>.Missing(clones, ["weapon.vanilla"]));
        }
    }

    [Fact]
    public void ExistingIdsKeepPrecedenceAndRepeatedRegistrationDoesNotDuplicate()
    {
        var registry = new TemplateAncestorRegistry<object>();
        var first = new object();
        var second = new object();
        registry.Remember(1, "Data/Items/", "weapon.first", first);
        registry.Remember(1, "Data/Items/", "weapon.first", first);
        registry.Remember(1, "Data/Items/", "weapon.second", second);

        Assert.True(registry.TryGet(1, "Data/Items", out var clones));
        Assert.Equal(new[] { second }, TemplateAncestorRegistry<object>.Missing(clones, ["weapon.first"]));
        Assert.Empty(TemplateAncestorRegistry<object>.Missing(clones, ["weapon.first", "weapon.second"]));
    }

    [Fact]
    public void ASubfolderOrDifferentTypeDoesNotGainUnrelatedClones()
    {
        var registry = new TemplateAncestorRegistry<object>();
        registry.Remember(1, "Data/", "oci.fairy", new object());

        Assert.False(registry.TryGet(2, "Data/", out _));
        Assert.False(registry.TryGet(1, "Data/Items/", out _));
        Assert.False(registry.TryGet(1, null!, out _));
        Assert.True(registry.TryGet(1, "data", out _));
    }
}
