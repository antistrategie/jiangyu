using System.Collections.Generic;
using Jiangyu.Loader.Runtime;
using Xunit;

namespace Jiangyu.Loader.Tests.Runtime;

public sealed class GameVersionGateTests
{
    [Fact]
    public void Warns_only_for_a_stamped_mod_built_against_a_different_version()
    {
        var warnings = new List<string>();
        var mods = new (string, string)[]
        {
            ("matches", "6000.0.72f1"),
            ("stale", "6000.0.50f1"),
            ("unstamped", null!),
        };

        var warned = GameVersionGate.Check("6000.0.72f1", mods, warnings.Add);

        Assert.Equal(1, warned);
        Assert.Contains(warnings, w => w.Contains("stale") && w.Contains("6000.0.50f1") && w.Contains("6000.0.72f1"));
        Assert.DoesNotContain(warnings, w => w.Contains("matches"));
        Assert.DoesNotContain(warnings, w => w.Contains("unstamped"));
    }

    [Fact]
    public void Says_nothing_when_the_running_version_is_unknown()
    {
        var warnings = new List<string>();
        var mods = new (string, string)[] { ("stale", "6000.0.50f1") };

        var warned = GameVersionGate.Check("", mods, warnings.Add);

        Assert.Equal(0, warned);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Matches_on_the_version_token_despite_surrounding_format_differences()
    {
        var warnings = new List<string>();
        var mods = new (string, string)[]
        {
            ("wrapped", "Unity 6000.0.72f1 (abc123)"),
            ("really_stale", "6000.0.50f1"),
        };

        var warned = GameVersionGate.Check("6000.0.72f1", mods, warnings.Add);

        // The same build rendered with extra surrounding text still matches on its
        // version token, so only the genuinely different build warns.
        Assert.Equal(1, warned);
        Assert.Contains(warnings, w => w.Contains("really_stale"));
        Assert.DoesNotContain(warnings, w => w.Contains("wrapped"));
    }
}
