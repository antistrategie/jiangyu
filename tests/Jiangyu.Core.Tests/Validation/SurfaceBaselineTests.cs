using Jiangyu.Core.Validation;
using Xunit;

namespace Jiangyu.Core.Tests.Validation;

/// <summary>
/// The diff and report are pure, so they exercise the game-update surface logic without
/// a game: synthetic surfaces in, drift out.
/// </summary>
public class SurfaceBaselineTests
{
    private static Surface Surface(params (string Type, string[] Members)[] types)
        => new(types.Select(t => new SurfaceType(t.Type, t.Members)).ToList());

    [Fact]
    public void Diff_of_identical_surfaces_is_empty()
    {
        var s = Surface(("Roster", new[] { "void HireLeader(L)", "void Dismiss(L)" }));
        var diff = SurfaceBaseline.Diff(s, s);
        Assert.True(diff.IsEmpty);
        Assert.Equal("Surface baseline: no drift on the bound game types.", SurfaceBaseline.FormatReport(diff));
    }

    [Fact]
    public void Diff_reports_added_and_removed_types()
    {
        var previous = Surface(("Roster", new[] { "void HireLeader(L)" }));
        var current = Surface(("BlackMarket", new[] { "void FillUp()" }));
        var diff = SurfaceBaseline.Diff(previous, current);
        Assert.Equal(new[] { "BlackMarket" }, diff.AddedTypes);
        Assert.Equal(new[] { "Roster" }, diff.RemovedTypes);
        Assert.Empty(diff.ChangedTypes);
    }

    [Fact]
    public void Diff_reports_added_and_removed_members_on_a_shared_type()
    {
        var previous = Surface(("Roster", new[] { "void HireLeader(L)", "void Old(L)" }));
        var current = Surface(("Roster", new[] { "void HireLeader(L)", "void New(L)" }));
        var diff = SurfaceBaseline.Diff(previous, current);
        var changed = Assert.Single(diff.ChangedTypes);
        Assert.Equal("Roster", changed.TypeName);
        Assert.Equal(new[] { "void New(L)" }, changed.AddedMembers);
        Assert.Equal(new[] { "void Old(L)" }, changed.RemovedMembers);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void FormatReport_flags_added_members_as_candidates_and_removed_as_drift()
    {
        var previous = Surface(("Roster", new[] { "void Old(L)" }));
        var current = Surface(("Roster", new[] { "void New(L)" }));
        var report = SurfaceBaseline.FormatReport(SurfaceBaseline.Diff(previous, current));
        Assert.Contains("+ void New(L)   (candidate verb/hook)", report);
        Assert.Contains("- void Old(L)   (drift)", report);
    }
}
