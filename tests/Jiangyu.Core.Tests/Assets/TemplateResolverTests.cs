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

    [Fact]
    public void Resolve_AcceptsAFullClassNameForTheIndexShortName()
    {
        // The compiler writes a full name where a short one is shared. The index files
        // classes by short name, so the last segment is what it is asked for.
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = [new TemplateTypeEntry { ClassName = "WeaponTemplate", Count = 1, ClassifiedVia = "suffix" }],
            Instances =
            [
                new TemplateInstanceEntry
                {
                    Name = "rifle",
                    ClassName = "WeaponTemplate",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 1 },
                },
            ],
        };

        TemplateResolutionResult result = TemplateResolver.Resolve(index, "Il2CppMenace.Items.WeaponTemplate", "rifle");

        Assert.Equal(TemplateResolutionStatus.Success, result.Status);
        Assert.Equal("WeaponTemplate", result.Resolved!.ClassName);
    }

    [Fact]
    public void Resolve_UsesTheNamespaceOfAFullNameToPickBetweenTwins()
    {
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = [new TemplateTypeEntry { ClassName = "Attack", Count = 2, ClassifiedVia = "suffix" }],
            Instances =
            [
                new TemplateInstanceEntry
                {
                    Name = "fire",
                    ClassName = "Attack",
                    NamespaceName = "Menace.Tactical.Skills.Effects",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 1 },
                },
                new TemplateInstanceEntry
                {
                    Name = "fire",
                    ClassName = "Attack",
                    NamespaceName = "Menace.Tactical.AI.Behaviors",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 2 },
                },
            ],
        };

        TemplateResolutionResult effect = TemplateResolver.Resolve(index, "Il2CppMenace.Tactical.Skills.Effects.Attack", "fire");
        TemplateResolutionResult bare = TemplateResolver.Resolve(index, "Attack", "fire");

        Assert.Equal(TemplateResolutionStatus.Success, effect.Status);
        Assert.Equal(1, effect.Resolved!.Identity.PathId);
        Assert.Equal(TemplateResolutionStatus.Ambiguous, bare.Status);
    }

    [Fact]
    public void Resolve_TreatsAModCodeNameAsUndotted()
    {
        var index = new TemplateIndex
        {
            Classification = TemplateClassifier.GetMetadata(),
            TemplateTypes = [new TemplateTypeEntry { ClassName = "Handler", Count = 1, ClassifiedVia = "suffix" }],
            Instances =
            [
                // No namespace on the entry, so a split of the mod name into a namespace
                // and the class "Handler" would match it. The guard must keep it whole.
                new TemplateInstanceEntry
                {
                    Name = "x",
                    ClassName = "Handler",
                    Identity = new TemplateIdentity { Collection = "sharedassets1.assets", PathId = 1 },
                },
            ],
        };

        TemplateResolutionResult result = TemplateResolver.Resolve(index, "com.example.mod:My.Handler", "x");

        Assert.Equal(TemplateResolutionStatus.NotFound, result.Status);
    }
}
