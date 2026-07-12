using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the top-level-member extraction the chained-clone re-inheritance
/// relies on. A clone-of-clone re-inherits every field it did NOT author from
/// its patched source, and "did not author" is decided by which top-level
/// members its own patch ops write to. An op reaches its target either by
/// descending into an outer field or by an inner-relative fieldPath, so the
/// extraction has to resolve the outermost member in both shapes.
/// </summary>
public class TemplatePatchCatalogTouchedFieldsTests
{
    private static LoadedPatchOperation Op(string fieldPath, params TemplateDescentStep[] descent)
        => new(
            CompiledTemplateOp.Set,
            fieldPath,
            index: null,
            indexPath: null,
            descent: descent.Length == 0 ? null : descent,
            value: null,
            ownerLabel: "test");

    [Fact]
    public void TopLevelField_PlainMember_IsWholePath()
        => Assert.Equal("OnlyEquipableBy", TemplatePatchCatalog.TopLevelField(Op("OnlyEquipableBy")));

    [Fact]
    public void TopLevelField_DottedPath_IsFirstSegment()
        => Assert.Equal("DeployCosts", TemplatePatchCatalog.TopLevelField(Op("DeployCosts.m_Supplies")));

    [Fact]
    public void TopLevelField_IndexedPath_IsNameBeforeBracket()
        => Assert.Equal("InitialAttributes", TemplatePatchCatalog.TopLevelField(Op("InitialAttributes[4]")));

    [Fact]
    public void TopLevelField_IndexedThenDotted_IsFirstSegment()
        => Assert.Equal("EventHandlers", TemplatePatchCatalog.TopLevelField(Op("EventHandlers[0].Damage")));

    [Fact]
    public void TopLevelField_Descent_IsOutermostStepField()
    {
        // set "Title" { set "m_DefaultTranslation" ... }: descent carries the
        // outer field, fieldPath is the inner-relative member.
        var op = Op("m_DefaultTranslation", new TemplateDescentStep { Field = "Title" });
        Assert.Equal("Title", TemplatePatchCatalog.TopLevelField(op));
    }

    [Fact]
    public void TopLevelField_MultiStepDescent_IsFirstStepField()
    {
        var op = Op(
            "Damage",
            new TemplateDescentStep { Field = "EventHandlers", Index = 0 },
            new TemplateDescentStep { Field = "Attack" });
        Assert.Equal("EventHandlers", TemplatePatchCatalog.TopLevelField(op));
    }

    [Fact]
    public void TopLevelField_DescentWinsOverFieldPath()
    {
        // When both are present the descent's first step is the top-level
        // member, never the inner fieldPath.
        var op = Op("m_Supplies", new TemplateDescentStep { Field = "DeployCosts" });
        Assert.Equal("DeployCosts", TemplatePatchCatalog.TopLevelField(op));
    }

    [Fact]
    public void TopLevelField_EmptyPathNoDescent_IsNull()
        => Assert.Null(TemplatePatchCatalog.TopLevelField(Op("")));
}
