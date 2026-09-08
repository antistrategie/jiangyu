using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests <see cref="TemplateCloneApplier.ChainLoops"/>, which separates a clone chain that can
/// never register (it leads back to one of its own clones) from one that waits on a source
/// another loader has not registered yet. Both come out of
/// <see cref="TemplateCloneApplier.OrderBySourceAvailability"/> as unresolved.
/// </summary>
public class TemplateCloneLateSourceTests
{
    private static LoadedCloneDirective Clone(string sourceId, string cloneId)
        => new("WeaponTemplate", sourceId, cloneId, "test");

    private static Dictionary<string, LoadedCloneDirective> Directives(params LoadedCloneDirective[] directives)
    {
        var byCloneId = new Dictionary<string, LoadedCloneDirective>(StringComparer.Ordinal);
        foreach (var directive in directives)
            byCloneId[directive.CloneId] = directive;
        return byCloneId;
    }

    [Fact]
    public void SourceOutsideTheMod_IsNotALoop()
    {
        var directives = Directives(Clone("modkit.weapon", "weapon.variant"));

        Assert.False(TemplateCloneApplier.ChainLoops(directives["weapon.variant"], directives));
    }

    [Fact]
    public void ChainBehindAMissingSource_IsNotALoop()
    {
        // B clones A, A clones an id nothing has registered yet. Both come out unresolved, and
        // neither is a loop: they register once the external source appears.
        var directives = Directives(
            Clone("modkit.weapon", "weapon.a"),
            Clone("weapon.a", "weapon.b"));

        TemplateCloneApplier.OrderBySourceAvailability(directives.Values, _ => false, out var unresolved);
        Assert.Equal(2, unresolved.Count);

        Assert.False(TemplateCloneApplier.ChainLoops(directives["weapon.a"], directives));
        Assert.False(TemplateCloneApplier.ChainLoops(directives["weapon.b"], directives));
    }

    [Fact]
    public void SelfClone_IsALoop()
    {
        var directives = Directives(Clone("weapon.self", "weapon.self"));

        Assert.True(TemplateCloneApplier.ChainLoops(directives["weapon.self"], directives));
    }

    [Fact]
    public void TwoCycle_IsALoopFromEitherEnd()
    {
        var directives = Directives(
            Clone("weapon.b", "weapon.a"),
            Clone("weapon.a", "weapon.b"));

        Assert.True(TemplateCloneApplier.ChainLoops(directives["weapon.a"], directives));
        Assert.True(TemplateCloneApplier.ChainLoops(directives["weapon.b"], directives));
    }

    [Fact]
    public void ChainIntoACycle_IsALoop()
    {
        // C clones B, and B and A clone each other. C's chain never reaches a live root.
        var directives = Directives(
            Clone("weapon.b", "weapon.a"),
            Clone("weapon.a", "weapon.b"),
            Clone("weapon.b", "weapon.c"));

        Assert.True(TemplateCloneApplier.ChainLoops(directives["weapon.c"], directives));
    }

    [Fact]
    public void ChainEndingInACreate_IsNotALoop()
    {
        var directives = Directives(
            Clone(string.Empty, "weapon.fresh"),
            Clone("weapon.fresh", "weapon.derived"));

        Assert.False(TemplateCloneApplier.ChainLoops(directives["weapon.derived"], directives));
    }

    [Fact]
    public void HasChangedChain_TrueWhenAChainedCloneOrItsSourceLandedLate()
    {
        // A is a clone of an outside id, B is chained on A.
        var directives = Directives(
            Clone("modkit.weapon", "weapon.a"),
            Clone("weapon.a", "weapon.b"));

        var changedA = new HashSet<string> { LateTemplateSet.Key("WeaponTemplate", "weapon.a") };
        Assert.True(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, changedA, Same));

        var changedB = new HashSet<string> { LateTemplateSet.Key("WeaponTemplate", "weapon.b") };
        Assert.True(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, changedB, Same));
    }

    [Fact]
    public void HasChangedChain_FalseWhenNothingChainedTouchesTheChange()
    {
        var directives = Directives(
            Clone("modkit.weapon", "weapon.a"),
            Clone("weapon.a", "weapon.b"),
            Clone("vanilla.weapon", "weapon.lone"));

        // An unrelated id, and a non-chained clone of this type.
        var changed = new HashSet<string>
        {
            LateTemplateSet.Key("ItemTemplate", "item.elsewhere"),
            LateTemplateSet.Key("WeaponTemplate", "weapon.lone"),
        };
        Assert.False(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, changed, Same));
        Assert.False(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, new HashSet<string>(), Same));
    }

    [Fact]
    public void HasChangedChain_MatchesTheIdUnderAnAncestorTypeName()
    {
        // A clone is registered in every ancestor map, so a patch may address it as
        // BaseItemTemplate:weapon.a. The chain built on it still counts as changed.
        var directives = Directives(
            Clone("modkit.weapon", "weapon.a"),
            Clone("weapon.a", "weapon.b"));

        static bool Hierarchy(string a, string b) => a == b
            || (a, b) is ("WeaponTemplate", "BaseItemTemplate") or ("BaseItemTemplate", "WeaponTemplate");
        var changed = new HashSet<string> { LateTemplateSet.Key("BaseItemTemplate", "weapon.a") };
        Assert.True(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, changed, Hierarchy));

        // An unrelated type reusing the id is another template.
        var unrelated = new HashSet<string> { LateTemplateSet.Key("PerkTemplate", "weapon.a") };
        Assert.False(TemplateCloneApplier.HasChangedChain("WeaponTemplate", directives, unrelated, Hierarchy));

        Assert.True(TemplateCloneApplier.ChangedHas(changed, "WeaponTemplate", "weapon.a", Hierarchy));
        Assert.False(TemplateCloneApplier.ChangedHas(changed, "WeaponTemplate", "weapon", Hierarchy));
        Assert.False(TemplateCloneApplier.ChangedHas(changed, "WeaponTemplate", "", Hierarchy));
        // Without the hierarchy, only the same spelling matches.
        Assert.False(TemplateCloneApplier.ChangedHas(changed, "WeaponTemplate", "weapon.a", Same));
    }

    private static bool Same(string a, string b) => a == b;
}
