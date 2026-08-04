using Jiangyu.Shared.State;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

/// <summary>
/// A stranded sidecar is either state that belongs to a save still on disk under its folded name,
/// or dead weight from a save the game deleted. The plan must not confuse the two, and must never
/// overwrite state a save already has.
/// </summary>
public class ModStateSidecarRepairPlanTests
{
    private const string Saves = "/Saves";

    private static string Sidecar(string saveFile) => $"{Saves}/{saveFile}.jiangyu.WOMENACE.json";

    // Stands in for the game's fold: lowercases and turns spaces into underscores.
    private static string? Fold(string savePath) =>
        $"{Saves}/{System.IO.Path.GetFileNameWithoutExtension(savePath).Replace(' ', '_').ToLowerInvariant()}.save";

    private static Func<string, bool> Present(params string[] paths) => p => paths.Contains(p);

    [Fact]
    public void SidecarBesideItsSaveIsLeftAlone()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("my_save.save")], Present($"{Saves}/my_save.save"), Fold);

        Assert.Empty(plan);
    }

    [Fact]
    public void SidecarUnderTheTypedNameIsReattachedToTheFoldedSave()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("DETERMINISMTEST.save")], Present($"{Saves}/determinismtest.save"), Fold);

        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Reattach, repair.Action);
        Assert.Equal(Sidecar("determinismtest.save"), repair.TargetPath);
    }

    [Fact]
    public void SidecarForADeletedSaveIsDiscarded()
    {
        // What autosave rotation leaves behind: the fold is a no-op and the save is gone.
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("auto_20260618_185551.save")], Present($"{Saves}/other.save"), Fold);

        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Discard, repair.Action);
        Assert.Null(repair.TargetPath);
    }

    [Fact]
    public void ReattachNeverOverwritesStateTheSaveAlreadyHas()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("DETERMINISMTEST.save")],
            Present($"{Saves}/determinismtest.save", Sidecar("determinismtest.save")),
            Fold);

        // The state is stranded, not dead: the save it wants is still there, so it is parked
        // where it stays readable rather than deleted.
        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Retire, repair.Action);
        Assert.Equal($"{Sidecar("DETERMINISMTEST.save")}.orphan", repair.TargetPath);
    }

    [Fact]
    public void OnlyOneOfTwoSidecarsFoldingToOneSaveIsReattached()
    {
        // "My Save" and "MY SAVE" fold to the same file, and one target takes one sidecar.
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("My Save.save"), Sidecar("MY SAVE.save")],
            Present($"{Saves}/my_save.save"),
            Fold);

        Assert.Equal(2, plan.Count);
        Assert.Equal(SidecarRepairAction.Reattach, plan[0].Action);
        Assert.Equal(Sidecar("my_save.save"), plan[0].TargetPath);
        Assert.Equal(SidecarRepairAction.Retire, plan[1].Action);
        Assert.NotEqual(plan[0].TargetPath, plan[1].TargetPath);
    }

    [Fact]
    public void SidecarWhoseFoldedSaveIsAlsoGoneIsDiscarded()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("DETERMINISMTEST.save")], Present($"{Saves}/other.save"), Fold);

        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Discard, repair.Action);
        Assert.Null(repair.TargetPath);
    }

    [Fact]
    public void ModIdCarryingTheInfixStillResolvesToItsSave()
    {
        // The mod id is the only side of the name that can hold a '.', so the split must not
        // search from the right.
        var sidecar = $"{Saves}/DETERMINISMTEST.save.jiangyu.studio.jiangyu.helper.json";

        var plan = ModStateSidecarRepairPlan.Build(
            [sidecar], Present($"{Saves}/determinismtest.save"), Fold);

        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Reattach, repair.Action);
        Assert.Equal($"{Saves}/determinismtest.save.jiangyu.studio.jiangyu.helper.json", repair.TargetPath);
    }

    [Fact]
    public void FolderCarryingTheInfixDoesNotReachIntoTheSplit()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            ["/games/.jiangyu.saves/my_save.save.jiangyu.WOMENACE.json"],
            Present("/games/.jiangyu.saves/my_save.save"),
            Fold);

        Assert.Empty(plan);
    }

    [Fact]
    public void SidecarWhoseNameFoldsToNothingIsDiscarded()
    {
        var plan = ModStateSidecarRepairPlan.Build(
            [Sidecar("세이브.save")], Present($"{Saves}/manual_20260804_163705.save"), _ => null);

        var repair = Assert.Single(plan);
        Assert.Equal(SidecarRepairAction.Discard, repair.Action);
    }

    [Theory]
    [InlineData("/Saves/my_save.save")]
    [InlineData("/Saves/my_save.jpg")]
    [InlineData("/Saves/.jiangyu..json")]
    public void NonSidecarPathsYieldNothing(string path)
    {
        var plan = ModStateSidecarRepairPlan.Build([path], Present(), Fold);

        Assert.Empty(plan);
    }
}
