using Jiangyu.Shared;

namespace Jiangyu.Core.Tests.Shared;

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1", 1, 0, 0, null)]
    [InlineData("1.2.3-alpha.1", 1, 2, 3, "alpha.1")]
    [InlineData("1.2.3+build.5", 1, 2, 3, null)]
    [InlineData("0.5.0-1-g269ec83", 0, 5, 0, "1-g269ec83")]
    [InlineData("1.2.3-rc.1+meta", 1, 2, 3, "rc.1")]
    public void ParsesValidVersions(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(pre, version.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("-1.0.0")]
    public void RejectsInvalidVersions(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Fact]
    public void ParseThrowsOnInvalid()
    {
        Assert.Throws<FormatException>(() => SemVer.Parse("not-a-version"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.0.0", "1", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void OrdersCoreVersions(string left, string right, int expectedSign)
    {
        var l = SemVer.Parse(left);
        var r = SemVer.Parse(right);
        Assert.Equal(expectedSign, Math.Sign(l.CompareTo(r)));
    }

    [Fact]
    public void PreReleaseRanksBelowRelease()
    {
        Assert.True(SemVer.Parse("1.0.0-alpha") < SemVer.Parse("1.0.0"));
        Assert.True(SemVer.Parse("1.0.0") > SemVer.Parse("1.0.0-rc.1"));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]   // fewer identifiers rank lower
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")] // numeric identifiers numerically
    [InlineData("1.0.0-alpha.1", "1.0.0-beta")]    // alphanumeric ordinal
    [InlineData("1.0.0-1", "1.0.0-alpha")]         // numeric ranks below alphanumeric
    public void OrdersPreReleaseIdentifiers(string lower, string higher)
    {
        Assert.True(SemVer.Parse(lower) < SemVer.Parse(higher));
    }

    [Theory]
    [InlineData("1.2.0", ">=", "1.0.0", true)]
    [InlineData("1.0.0", ">=", "1.0.0", true)]
    [InlineData("0.9.0", ">=", "1.0.0", false)]
    [InlineData("1.0.0", ">", "1.0.0", false)]
    [InlineData("1.0.1", ">", "1.0.0", true)]
    [InlineData("1.0.0", "<=", "1.0.0", true)]
    [InlineData("2.0.0", "<", "2.0.0", false)]
    [InlineData("1.5.0", "<", "2.0.0", true)]
    [InlineData("1.0.0", "==", "1.0.0", true)]
    [InlineData("1.0.0", "=", "1.0.0", true)]
    [InlineData("1.0.0", "!=", "2.0.0", true)]
    [InlineData("1.0.0", "!=", "1.0.0", false)]
    [InlineData("1.0.0", "~>", "1.0.0", false)] // unknown operator never satisfies
    public void EvaluatesConstraints(string actual, string op, string required, bool expected)
    {
        Assert.Equal(expected, SemVer.Satisfies(SemVer.Parse(actual), op, SemVer.Parse(required)));
    }
}
