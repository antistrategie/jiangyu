using Jiangyu.Loader.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests <see cref="LateTemplateSet"/>, the bookkeeping behind the retry of template ids
/// another loader registers after Jiangyu's pass for their type has run.
/// </summary>
public class LateTemplateSetTests
{
    [Fact]
    public void KeyedUnderAnyName_MatchesTheIdUnderAnyNameOfTheType()
    {
        static bool Hierarchy(string a, string b) => a == b
            || (a, b) is ("WeaponTemplate", "BaseItemTemplate") or ("BaseItemTemplate", "WeaponTemplate");
        var keys = new HashSet<string> { LateTemplateSet.Key("BaseItemTemplate", "weapon.a"), "no-separator" };

        Assert.True(LateTemplateSet.KeyedUnderAnyName(keys, "BaseItemTemplate", "weapon.a", Hierarchy));
        Assert.True(LateTemplateSet.KeyedUnderAnyName(keys, "WeaponTemplate", "weapon.a", Hierarchy));
        Assert.False(LateTemplateSet.KeyedUnderAnyName(keys, "PerkTemplate", "weapon.a", Hierarchy));
        Assert.False(LateTemplateSet.KeyedUnderAnyName(keys, "WeaponTemplate", "weapon", Hierarchy));
        Assert.False(LateTemplateSet.KeyedUnderAnyName(keys, "WeaponTemplate", "weapon.a.b", Hierarchy));
        Assert.False(LateTemplateSet.KeyedUnderAnyName(keys, "WeaponTemplate", "", Hierarchy));
        Assert.False(LateTemplateSet.KeyedUnderAnyName(null, "WeaponTemplate", "weapon.a", Hierarchy));
    }

    [Fact]
    public void Contains_ByPredicate_MatchesAnyAcceptedType()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "weapon.a", "mod", 1);

        Assert.True(set.Contains(type => type is "WeaponTemplate" or "BaseItemTemplate", "weapon.a"));
        Assert.False(set.Contains(type => type == "BaseItemTemplate", "weapon.a"));
        Assert.False(set.Contains(type => type == "WeaponTemplate", "weapon.b"));
    }

    [Fact]
    public void Add_KeepsTheFirstEntryForAnId()
    {
        var set = new LateTemplateSet();

        Assert.True(set.Add("WeaponTemplate", "weapon.late", "mod-a", 3));
        Assert.False(set.Add("WeaponTemplate", "weapon.late", "mod-b", 9));

        var entry = Assert.Single(set.Snapshot());
        Assert.Equal("mod-a", entry.OwnerLabel);
        Assert.Equal(3, entry.OpCount);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void SameIdOnAnotherType_IsASeparateEntry()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "shared.id", "mod-a", 1);
        set.Add("ItemTemplate", "shared.id", "mod-a", 2);

        Assert.Equal(2, set.Count);
        Assert.True(set.Contains("WeaponTemplate", "shared.id"));
        Assert.True(set.Contains("ItemTemplate", "shared.id"));
        Assert.Equal(["WeaponTemplate", "ItemTemplate"], set.Snapshot().Select(entry => entry.TemplateType).Distinct());
    }

    [Fact]
    public void PendingOps_SumsEveryHeldEntry()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "a", "mod", 3);
        set.Add("WeaponTemplate", "b", "mod", 4);
        set.Add("ItemTemplate", "c", "mod", 5);

        Assert.Equal(12, set.PendingOps);
        Assert.Equal(2, set.ForType("WeaponTemplate").Count());

        set.Remove("WeaponTemplate", "b");
        Assert.Equal(8, set.PendingOps);
        Assert.False(set.Contains("WeaponTemplate", "b"));
    }

    [Fact]
    public void Remove_OfAnUnknownId_IsFalse()
    {
        var set = new LateTemplateSet();
        Assert.False(set.Remove("WeaponTemplate", "never.held"));
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void TakeUnreported_ReturnsEachEntryOnceAndKeepsItHeld()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "a", "mod", 1, "detail a");
        set.Add("WeaponTemplate", "b", "mod", 1);

        var first = set.TakeUnreported();
        Assert.Equal(2, first.Count);
        Assert.Equal("detail a", first.Single(e => e.TemplateId == "a").Detail);
        Assert.Null(first.Single(e => e.TemplateId == "b").Detail);

        // A second schedule end reports nothing new, and the ids are still retried.
        Assert.Empty(set.TakeUnreported());
        Assert.Equal(2, set.Count);

        // An id added after the first report is reported at the next one.
        set.Add("WeaponTemplate", "c", "mod", 1);
        var second = set.TakeUnreported();
        Assert.Equal("c", Assert.Single(second).TemplateId);
    }

    [Fact]
    public void Snapshot_IsDetachedFromLaterRemovals()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "a", "mod", 1);
        set.Add("WeaponTemplate", "b", "mod", 1);

        var snapshot = set.Snapshot();
        foreach (var entry in snapshot)
            set.Remove(entry.TemplateType, entry.TemplateId);

        Assert.Equal(2, snapshot.Count);
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void ResetReported_ReportsEveryEntryAgain()
    {
        var set = new LateTemplateSet();
        set.Add("WeaponTemplate", "a", "mod", 1);
        Assert.Single(set.TakeUnreported());
        Assert.Empty(set.TakeUnreported());

        set.ResetReported();
        Assert.Single(set.TakeUnreported());
    }
}
