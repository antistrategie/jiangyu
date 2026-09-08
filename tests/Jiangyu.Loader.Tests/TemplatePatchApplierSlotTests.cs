using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Tests the slot paths behind the late-block override record: which recorded sets an
/// applied set, clear, insert or remove forgets (<see cref="TemplatePatchApplier.SlotPath"/>,
/// <see cref="TemplatePatchApplier.ResetPath"/>, <see cref="TemplatePatchApplier.IsUnder"/>).
/// </summary>
public class TemplatePatchApplierSlotTests
{
    private static LoadedPatchOperation Op(
        CompiledTemplateOp op, string fieldPath, int? index = null, TemplateDescentStep[]? descent = null, int[]? indexPath = null)
        => new(op, fieldPath, index, indexPath, descent, new CompiledTemplateValue { Kind = CompiledTemplateValueKind.Int32, Int32 = 1 }, "mod");

    [Fact]
    public void SlotPath_CoversDescentFieldPathIndexAndCell()
    {
        var descent = new[] { new TemplateDescentStep { Field = "EventHandlers", Index = 2 }, new TemplateDescentStep { Field = "Inner" } };
        Assert.Equal(["EventHandlers", "[2]", "Inner", "Whitelist", "[0]", "Name"], TemplatePatchApplier.SlotPath(Op(CompiledTemplateOp.Set, "Whitelist[0].Name", descent: descent)));
        Assert.Equal(["Attributes", "[6]"], TemplatePatchApplier.SlotPath(Op(CompiledTemplateOp.Set, "Attributes", index: 6)));
        Assert.Equal(["Grid", "[1]", "[2]"], TemplatePatchApplier.SlotPath(Op(CompiledTemplateOp.Set, "Grid", indexPath: [1, 2])));
        Assert.Equal(["Damage"], TemplatePatchApplier.SlotPath(Op(CompiledTemplateOp.Set, "Damage")));
    }

    [Fact]
    public void ResetPath_IsTheCollectionForInsertAndRemove_TheSlotOtherwise()
    {
        Assert.Equal(["EventHandlers"], TemplatePatchApplier.ResetPath(Op(CompiledTemplateOp.Remove, "EventHandlers", index: 1)));
        Assert.Equal(["EventHandlers"], TemplatePatchApplier.ResetPath(Op(CompiledTemplateOp.InsertAt, "EventHandlers", index: 0)));
        Assert.Equal(["EventHandlers"], TemplatePatchApplier.ResetPath(Op(CompiledTemplateOp.Clear, "EventHandlers")));
        Assert.Equal(["EventHandlers", "[1]"], TemplatePatchApplier.ResetPath(Op(CompiledTemplateOp.Set, "EventHandlers", index: 1)));
        Assert.Equal(["Stats", "Armour"], TemplatePatchApplier.ResetPath(Op(CompiledTemplateOp.Set, "Stats.Armour")));
    }

    [Fact]
    public void IsUnder_IsAProperPrefixMatch()
    {
        string[] inner = ["EventHandlers", "[1]", "Whitelist", "[0]"];
        Assert.True(TemplatePatchApplier.IsUnder(inner, ["EventHandlers"]));
        Assert.True(TemplatePatchApplier.IsUnder(inner, ["EventHandlers", "[1]"]));
        Assert.False(TemplatePatchApplier.IsUnder(inner, ["EventHandlers", "[2]"]));
        Assert.False(TemplatePatchApplier.IsUnder(inner, inner));
        Assert.False(TemplatePatchApplier.IsUnder(["Damage"], ["EventHandlers"]));
        Assert.False(TemplatePatchApplier.IsUnder([], []));
    }
}
