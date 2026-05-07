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
            new() { Field = "EventHandlers", Index = 0, Subtype = "Attack" },
            new() { Field = "DamageFilterCondition", Index = null, Subtype = "MoraleStateCondition" },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "EventHandlers", Index = 0, Subtype = "Attack" },
            new() { Field = "DamageFilterCondition", Index = null, Subtype = "MoraleStateCondition" },
        };
        Assert.True(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void DescentEquals_DifferentIndex_NotEqual()
    {
        // The Phase 2f case: same fieldPath, same subtype, different
        // collection index — must NOT dedup.
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 0, Subtype = "EntityWithTagsCondition" },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 1, Subtype = "EntityWithTagsCondition" },
        };
        Assert.False(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void DescentEquals_DifferentSubtype_NotEqual()
    {
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 0, Subtype = "EntityWithTagsCondition" },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "Conditions", Index = 0, Subtype = "EntityWithOneOfTheTagsCondition" },
        };
        Assert.False(TemplatePatchCatalog.DescentEquals(a, b));
    }

    [Fact]
    public void DescentEquals_NullVsEmptySubtype_AreEqual()
    {
        // The wire format may encode "no subtype" as either null or
        // empty string; treat them identically so a parser quirk in one
        // version doesn't become a phantom override.
        var a = new List<TemplateDescentStep>
        {
            new() { Field = "Foo", Index = 0, Subtype = null },
        };
        var b = new List<TemplateDescentStep>
        {
            new() { Field = "Foo", Index = 0, Subtype = "" },
        };
        Assert.True(TemplatePatchCatalog.DescentEquals(a, b));
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
}
