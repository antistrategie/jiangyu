using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the chained-clone ordering in <see cref="TemplateCloneApplier.OrderBySourceAvailability"/>.
/// A clone whose source is itself a mod clone must apply after its source no matter where the two
/// are declared (file order made every SSR rank clone die at creation with "source template not
/// found" before this). Missing sources and cycles must surface as unresolved, never as a hang.
/// </summary>
public class TemplateCloneChainedOrderTests
{
    private static LoadedCloneDirective Clone(string sourceId, string cloneId)
        => new("WeaponTemplate", sourceId, cloneId, "test");

    private static List<string> Ids(List<LoadedCloneDirective> directives)
        => directives.ConvertAll(d => d.CloneId);

    [Fact]
    public void SourceDeclaredLater_AppliesAfterSource()
    {
        // The SSR rank case: the rank clone is declared before its source clone exists.
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("weapon.sextans_ssr", "weapon.sextans_ssr_r1"),
                Clone("weapon.sextans", "weapon.sextans_ssr"),
            },
            id => id == "weapon.sextans",
            out var unresolved);

        Assert.Equal(new[] { "weapon.sextans_ssr", "weapon.sextans_ssr_r1" }, Ids(ordered));
        Assert.Empty(unresolved);
    }

    [Fact]
    public void ThreeDeepChain_ResolvesBaseFirstRegardlessOfDeclarationOrder()
    {
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("b", "c"),
                Clone("a", "b"),
                Clone("vanilla.x", "a"),
            },
            id => id == "vanilla.x",
            out var unresolved);

        Assert.Equal(new[] { "a", "b", "c" }, Ids(ordered));
        Assert.Empty(unresolved);
    }

    [Fact]
    public void VanillaSourcesAndCreates_ApplyInDeclarationOrder()
    {
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("vanilla.a", "mod.a"),
                Clone("", "mod.created"),
                Clone("vanilla.b", "mod.b"),
            },
            id => id is "vanilla.a" or "vanilla.b",
            out var unresolved);

        Assert.Equal(new[] { "mod.a", "mod.created", "mod.b" }, Ids(ordered));
        Assert.Empty(unresolved);
    }

    [Fact]
    public void MissingSource_IsUnresolvedNotDeferred()
    {
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("vanilla.gone", "mod.a"),
                Clone("vanilla.here", "mod.b"),
            },
            id => id == "vanilla.here",
            out var unresolved);

        Assert.Equal(new[] { "mod.b" }, Ids(ordered));
        Assert.Equal(new[] { "mod.a" }, Ids(unresolved));
    }

    [Fact]
    public void Cycle_IsUnresolvedNotAHang()
    {
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("b", "a"),
                Clone("a", "b"),
            },
            _ => false,
            out var unresolved);

        Assert.Empty(ordered);
        Assert.Equal(2, unresolved.Count);
    }

    [Fact]
    public void SelfClone_IsUnresolvedNotAHang()
    {
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[] { Clone("a", "a") },
            _ => false,
            out var unresolved);

        Assert.Empty(ordered);
        Assert.Single(unresolved);
    }

    [Fact]
    public void ChainBehindAMissingSource_IsUnresolvedOnce()
    {
        // c clones b, b clones a source nothing provides: both are unresolvable, and the missing
        // root being dropped must not leave c deferred forever.
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            new[]
            {
                Clone("b", "c"),
                Clone("vanilla.gone", "b"),
            },
            _ => false,
            out var unresolved);

        Assert.Empty(ordered);
        Assert.Equal(2, unresolved.Count);
    }

    [Fact]
    public void SiblingOrderIsStableWithinOneRound()
    {
        // Several independent ranks of one source keep their declaration order.
        var directives = new List<LoadedCloneDirective>
        {
            Clone("base", "base_r1"),
            Clone("base", "base_r2"),
            Clone("vanilla.base", "base"),
        };
        var ordered = TemplateCloneApplier.OrderBySourceAvailability(
            directives,
            id => id == "vanilla.base",
            out var unresolved);

        Assert.Equal(new[] { "base", "base_r1", "base_r2" }, Ids(ordered));
        Assert.Empty(unresolved);
    }
}
