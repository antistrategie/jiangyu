using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public class TemplateSearchServiceTests
{
    private static TemplateIndex CreateIndex() => new()
    {
        Classification = TemplateClassifier.GetMetadata(),
        TemplateTypes =
        [
            new TemplateTypeEntry { ClassName = "WeaponTemplate", Count = 2, ClassifiedVia = "suffix" },
            new TemplateTypeEntry { ClassName = "UnitLeaderTemplate", Count = 1, ClassifiedVia = "suffix" },
        ],
        Instances =
        [
            new TemplateInstanceEntry
            {
                Name = "weapon.ifn_shotgun",
                ClassName = "WeaponTemplate",
                Identity = new TemplateIdentity { Collection = "resources.assets", PathId = 200 },
            },
            new TemplateInstanceEntry
            {
                Name = "weapon.ifn_fal",
                ClassName = "WeaponTemplate",
                Identity = new TemplateIdentity { Collection = "sharedassets2.assets", PathId = 100 },
            },
            new TemplateInstanceEntry
            {
                Name = "squad_leader.darby",
                ClassName = "UnitLeaderTemplate",
                Identity = new TemplateIdentity { Collection = "resources.assets", PathId = 300 },
            },
        ],
    };

    [Fact]
    public void Search_ReturnsTypeAndInstanceMatches_CaseInsensitively()
    {
        var result = new TemplateSearchService(CreateIndex()).Search("weapon");

        Assert.Equal(TemplateSearchStatus.Success, result.Status);
        Assert.Single(result.MatchingTypes);
        Assert.Equal("WeaponTemplate", result.MatchingTypes[0].ClassName);
        Assert.Equal(2, result.MatchingInstances.Count);
        Assert.All(result.MatchingInstances, instance => Assert.Equal("WeaponTemplate", instance.ClassName));
    }

    [Fact]
    public void Search_MatchesCollectionSubstring()
    {
        var result = new TemplateSearchService(CreateIndex()).Search("sharedassets2");

        Assert.Equal(TemplateSearchStatus.Success, result.Status);
        Assert.Empty(result.MatchingTypes);
        var match = Assert.Single(result.MatchingInstances);
        Assert.Equal("weapon.ifn_fal", match.Name);
    }

    [Fact]
    public void Search_RestrictsInstancesToOneType()
    {
        var result = new TemplateSearchService(CreateIndex()).Search("resources", "WeaponTemplate");

        Assert.Equal(TemplateSearchStatus.Success, result.Status);
        Assert.Empty(result.MatchingTypes);
        var match = Assert.Single(result.MatchingInstances);
        Assert.Equal("weapon.ifn_shotgun", match.Name);
    }

    [Fact]
    public void Search_ReturnsNotFound_WhenNothingMatches()
    {
        var result = new TemplateSearchService(CreateIndex()).Search("grenade");

        Assert.Equal(TemplateSearchStatus.NotFound, result.Status);
        Assert.Empty(result.MatchingTypes);
        Assert.Empty(result.MatchingInstances);
    }

    [Fact]
    public void Search_ReturnsIndexUnavailable_WhenIndexMissing()
    {
        var result = new TemplateSearchService(null).Search("weapon");

        Assert.Equal(TemplateSearchStatus.IndexUnavailable, result.Status);
    }
}
