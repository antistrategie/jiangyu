using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class SanitizeAssetPathSegmentTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("Hello_World-1.0", "Hello_World-1.0")]
    public void PreservesAlphanumericDotUnderscoreDash(string input, string expected)
    {
        Assert.Equal(expected, AssetPipelineService.SanitizeAssetPathSegment(input));
    }

    [Theory]
    [InlineData("hello world", "hello_world")]
    [InlineData("foo/bar\\baz", "foo_bar_baz")]
    [InlineData("a@b#c$d", "a_b_c_d")]
    public void ReplacesDisallowedCharactersWithUnderscore(string input, string expected)
    {
        Assert.Equal(expected, AssetPipelineService.SanitizeAssetPathSegment(input));
    }

    [Fact]
    public void EmptyInputCollapsesToUnderscore()
    {
        Assert.Equal("_", AssetPipelineService.SanitizeAssetPathSegment(""));
    }

    [Fact]
    public void PreservesUnicodeLettersAndDigits()
    {
        // char.IsLetterOrDigit is true for CJK characters
        var result = AssetPipelineService.SanitizeAssetPathSegment("装甲車01");
        Assert.Equal("装甲車01", result);
    }
}
