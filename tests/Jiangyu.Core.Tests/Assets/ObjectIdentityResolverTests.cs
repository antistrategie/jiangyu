using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public sealed class ObjectIdentityResolverTests
{
    [Fact]
    public void Resolve_ReturnsAmbiguousCandidatesInDeterministicOrder()
    {
        var resolver = new ObjectIdentityResolver(new AssetIndex
        {
            Assets =
            [
                new AssetEntry { Name = "WeaponTemplate", ClassName = "MonoBehaviour", Collection = "sharedassets2.assets", PathId = 500 },
                new AssetEntry { Name = "WeaponTemplate", ClassName = "MonoBehaviour", Collection = "resources.assets", PathId = 100 },
                new AssetEntry { Name = "WeaponTemplate", ClassName = "MonoBehaviour", Collection = "resources.assets", PathId = 300 },
            ],
        });

        ObjectResolutionResult result = resolver.Resolve("WeaponTemplate", "MonoBehaviour");

        Assert.Equal(ObjectResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Equal("resources.assets", candidate.Collection);
                Assert.Equal(100, candidate.PathId);
            },
            candidate =>
            {
                Assert.Equal("resources.assets", candidate.Collection);
                Assert.Equal(300, candidate.PathId);
            },
            candidate =>
            {
                Assert.Equal("sharedassets2.assets", candidate.Collection);
                Assert.Equal(500, candidate.PathId);
            });
    }

    [Fact]
    public void Resolve_ReturnsIndexUnavailableWhenNoIndexIsPresent()
    {
        var resolver = new ObjectIdentityResolver(index: null);

        ObjectResolutionResult result = resolver.Resolve("WeaponTemplate", null);

        Assert.Equal(ObjectResolutionStatus.IndexUnavailable, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Resolved);
    }
}
