using Jiangyu.Loader.Replacements;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// The sub-assembly sentinel is a modder-typed GameObject name, so its
/// parse is the one part of the script mirror that runs on a string rather
/// than on live IL2CPP state. A silent misparse would send the mirror
/// looking for the wrong vanilla prefab and report "no scripts to restore"
/// with no hint why.
/// </summary>
public class SubassemblySentinelParseTests
{
    [Fact]
    public void ParsesBarePrefabName()
    {
        Assert.True(SubassemblyScriptMirror.TryParseSentinel(
            "__jiangyu_scripts:pv.assault_rifle_cqb", out var payload, out var reference, out var path));
        Assert.Equal("pv.assault_rifle_cqb", payload);
        Assert.Equal("pv.assault_rifle_cqb", reference);
        Assert.Null(path);
    }

    [Fact]
    public void ParsesExplicitCounterpartPath()
    {
        Assert.True(SubassemblyScriptMirror.TryParseSentinel(
            "__jiangyu_scripts:pv.assault_rifle_cqb@rifle_red_laser/halo_funky",
            out var payload, out var reference, out var path));
        Assert.Equal("pv.assault_rifle_cqb@rifle_red_laser/halo_funky", payload);
        Assert.Equal("pv.assault_rifle_cqb", reference);
        Assert.Equal("rifle_red_laser/halo_funky", path);
    }

    [Fact]
    public void TrailingSeparatorLeavesNoPath()
    {
        Assert.True(SubassemblyScriptMirror.TryParseSentinel(
            "__jiangyu_scripts:pv.assault_rifle_cqb@", out _, out var reference, out var path));
        Assert.Equal("pv.assault_rifle_cqb", reference);
        Assert.Null(path);
    }

    [Fact]
    public void RejectsAnUnnamedNode()
    {
        Assert.False(SubassemblyScriptMirror.TryParseSentinel(null, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("halo_funky")]
    [InlineData("__jiangyu_ref:el.construct_soldier_t1")]
    [InlineData("__jiangyu_scripts_done:pv.assault_rifle_cqb")]
    [InlineData("__jiangyu_scripts:")]
    [InlineData("__jiangyu_scripts:@rifle_red_laser")]
    public void RejectsAnythingThatIsNotAPendingSentinel(string name)
    {
        Assert.False(SubassemblyScriptMirror.TryParseSentinel(name, out _, out _, out _));
    }
}
