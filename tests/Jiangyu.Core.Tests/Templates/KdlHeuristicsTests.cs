using Jiangyu.Core.Templates;

namespace Jiangyu.Core.Tests.Templates;

public sealed class KdlHeuristicsTests
{
    [Theory]
    [InlineData("patch \"T\" \"x\"")]
    [InlineData("  patch \"T\" \"x\"")]
    [InlineData("\tpatch \"T\" \"x\"")]
    [InlineData("patch\t\"T\" \"x\"")]
    [InlineData("patch{")]
    public void IsNodeHeader_RecognisesMatchingNodes(string line)
    {
        Assert.True(KdlHeuristics.IsNodeHeader(line, "patch"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("clone \"T\" from=\"x\"")]
    [InlineData("patchwork \"T\"")] // prefix match alone is not enough
    [InlineData("patch")] // bare node name with no following args/brace
    public void IsNodeHeader_RejectsNonMatches(string line)
    {
        Assert.False(KdlHeuristics.IsNodeHeader(line, "patch"));
    }

    [Theory]
    [InlineData("// patch \"T\"")]
    [InlineData("  //patch \"T\"")]
    [InlineData("/- patch \"T\"")]
    [InlineData("  /-patch \"T\"")]
    public void IsNodeHeader_SkipsCommentedLines(string line)
    {
        Assert.False(KdlHeuristics.IsNodeHeader(line, "patch"));
    }
}
