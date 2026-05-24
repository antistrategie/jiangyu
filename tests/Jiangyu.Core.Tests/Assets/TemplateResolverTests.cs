using Jiangyu.Core.Assets;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Tests.Assets;

public class TemplateResolverTests
{
    [Fact]
    public void Resolve_AmbiguousCandidates_AreSortedDeterministically()
    {
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes =
            [
                new TemplateTypeEntry { ClassName = "WeaponTemplate", Count = 3, ClassifiedVia = "suffix" },
            ],
            Instances =
            [
                new TemplateInstanceEntry
                {
                    Name = "rifle",
                    ClassName = "WeaponTemplate",
                    Identity = new TemplateIdentity { Collection = "sharedassets2.assets", PathId = 20 },
                },
                new TemplateInstanceEntry
                {
                    Name = "Rifle",
                    ClassName = "WeaponTemplate",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 15 },
                },
                new TemplateInstanceEntry
                {
                    Name = "rifle",
                    ClassName = "WeaponTemplate",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 10 },
                },
            ],
        };

        TemplateResolutionResult result = TemplateResolver.Resolve(index, "WeaponTemplate", "rifle");

        Assert.Equal(TemplateResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Collection(
            result.Candidates,
            candidate =>
            {
                Assert.Equal("rifle", candidate.Name, StringComparer.OrdinalIgnoreCase);
                Assert.Equal("sharedassets1.assets", candidate.Identity.Collection);
                Assert.Equal(10, candidate.Identity.PathId);
            },
            candidate =>
            {
                Assert.Equal("Rifle", candidate.Name, StringComparer.OrdinalIgnoreCase);
                Assert.Equal("sharedassets1.assets", candidate.Identity.Collection);
                Assert.Equal(15, candidate.Identity.PathId);
            },
            candidate =>
            {
                Assert.Equal("rifle", candidate.Name, StringComparer.OrdinalIgnoreCase);
                Assert.Equal("sharedassets2.assets", candidate.Identity.Collection);
                Assert.Equal(20, candidate.Identity.PathId);
            });
    }

    [Fact]
    public void Resolve_ReturnsIndexUnavailable_WhenIndexMissing()
    {
        TemplateResolutionResult result = TemplateResolver.Resolve(null, "EntityTemplate", "bunker");

        Assert.Equal(TemplateResolutionStatus.IndexUnavailable, result.Status);
    }
}
