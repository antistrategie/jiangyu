using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the descent / indexPath-aware dedup invariants the loader's
/// patch catalog uses to decide whether a later-loaded Set op overrides
/// an earlier one. The bug Phase 2f surfaced was a too-coarse dedup key
/// (fieldPath only) that collapsed
/// <c>Conditions[0].Negated</c> and <c>Conditions[1].Negated</c> into
/// the same slot. The helpers under test are the comparators those
/// keys are built from.
/// </summary>
public class TemplatePatchCatalogDedupTests
{
    [Fact]
    public void DescentEquals_BothNullOrEmpty_AreEqual()
    {
        Assert.True(TemplatePatchCatalog.DescentEquals(null, null));
        Assert.True(TemplatePatchCatalog.DescentEquals(
            new List<TemplateDescentStep>(), null));
        Assert.True(TemplatePatchCatalog.DescentEquals(
            new List<TemplateDescentStep>(), new List<TemplateDescentStep>()));
    }

    [Fact]
    public void DescentEquals_DifferentLength_NotEqual()
    {
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "Foo", Index = 0 },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "Foo", Index = 0 },
            new() { Field = "Bar", Index = 1 },
        };
        Assert.False(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void DescentEquals_SameStructure_AreEqual()
    {
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "EventHandlers", Index = 0 },
            new() { Field = "Conditions", Index = 1 },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "EventHandlers", Index = 0 },
            new() { Field = "Conditions", Index = 1 },
        };
        Assert.True(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void DescentEquals_DifferentIndex_NotEqual()
    {
        // Same field, different collection index: must NOT dedup.
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 0 },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 1 },
        };
        Assert.False(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void IndexPathEquals_BothNullOrEmpty_AreEqual()
    {
        Assert.True(TemplatePatchCatalog.IndexPathEquals(null, null));
        Assert.True(TemplatePatchCatalog.IndexPathEquals(new List<int>(), null));
    }

    [Fact]
    public void IndexPathEquals_DifferentCells_NotEqual()
    {
        // Phase 2d cell writes: cell="2,6" vs cell="6,5" must NOT dedup.
        Assert.False(TemplatePatchCatalog.IndexPathEquals(
            new List<int> { 2, 6 }, new List<int> { 6, 5 }));
    }

    [Fact]
    public void IndexPathEquals_SameCells_AreEqual()
    {
        Assert.True(TemplatePatchCatalog.IndexPathEquals(
            new List<int> { 4, 4 }, new List<int> { 4, 4 }));
    }

    [Fact]
    public void IndexPathEquals_DifferentRank_NotEqual()
    {
        Assert.False(TemplatePatchCatalog.IndexPathEquals(
            new List<int> { 0, 0 }, new List<int> { 0, 0, 0 }));
    }

    [Fact]
    public void SetOpsCollide_DifferentIndex_DoNotCollide()
    {
        // The InitialAttributes case: two Set ops on the same field with
        // different collection indexes target different slots and must
        // NOT dedup. The regression that prompted this test had
        // `InitialAttributes` index=0 and index=6 collapsing to a single
        // write — Voymastina ended up with Positioning set but Agility,
        // WeaponSkill, etc. left at the source clone's values.
        var existing = new LoadedPatchOperation(
            CompiledTemplateOp.Set,
            "InitialAttributes",
            index: 0,
            indexPath: null,
            descent: null,
            value: null,
            ownerLabel: "mod-a");

        Assert.False(TemplatePatchCatalog.SetOpsCollide(
            existing,
            newFieldPath: "InitialAttributes",
            newIndex: 6,
            newDescent: null,
            newIndexPath: null));
    }

    [Fact]
    public void SetOpsCollide_SameIndex_DoCollide()
    {
        // Genuine same-slot collision: two Sets on InitialAttributes[0]
        // are the case the dedup is designed to resolve — later wins
        // with a warning.
        var existing = new LoadedPatchOperation(
            CompiledTemplateOp.Set,
            "InitialAttributes",
            index: 0,
            indexPath: null,
            descent: null,
            value: null,
            ownerLabel: "mod-a");

        Assert.True(TemplatePatchCatalog.SetOpsCollide(
            existing,
            newFieldPath: "InitialAttributes",
            newIndex: 0,
            newDescent: null,
            newIndexPath: null));
    }

    [Fact]
    public void SetOpsCollide_DifferentFieldPath_DoNotCollide()
    {
        var existing = new LoadedPatchOperation(
            CompiledTemplateOp.Set,
            "QualityLevel",
            index: null,
            indexPath: null,
            descent: null,
            value: null,
            ownerLabel: "mod-a");

        Assert.False(TemplatePatchCatalog.SetOpsCollide(
            existing,
            newFieldPath: "GrowthPotential",
            newIndex: null,
            newDescent: null,
            newIndexPath: null));
    }

    [Fact]
    public void SetOpsCollide_AppendVsSet_DoNotCollide()
    {
        // Only Set ops dedup. An Append targeting the same field path
        // adds a new element; later Set targeting the same field is a
        // separate operation, not an override.
        var existing = new LoadedPatchOperation(
            CompiledTemplateOp.Append,
            "Perks",
            index: null,
            indexPath: null,
            descent: null,
            value: null,
            ownerLabel: "mod-a");

        Assert.False(TemplatePatchCatalog.SetOpsCollide(
            existing,
            newFieldPath: "Perks",
            newIndex: null,
            newDescent: null,
            newIndexPath: null));
    }
}
