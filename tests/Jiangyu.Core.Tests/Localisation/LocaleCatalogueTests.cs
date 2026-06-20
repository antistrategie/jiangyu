using Jiangyu.Shared.Localisation;
using Xunit;

namespace Jiangyu.Core.Tests.Localisation;

public class LocaleCatalogueTests
{
    [Theory]
    [InlineData("fr", true)]
    [InlineData("zh_Hans", true)]
    [InlineData("pt_BR", true)]
    [InlineData("en", false)]   // the source language ships no file
    [InlineData("xx", false)]   // unknown code
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnownCode(string? code, bool expected)
        => Assert.Equal(expected, LocaleCatalogue.IsKnownCode(code!));

    [Theory]
    [InlineData("French", "fr")]
    [InlineData("ChineseSimplified", "zh_Hans")]
    [InlineData("PortugueseBrazil", "pt_BR")]
    [InlineData("English", null)]    // the source language maps to no code
    [InlineData("Klingon", null)]    // unknown member
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CodeForLanguageName(string? name, string? expected)
        => Assert.Equal(expected, LocaleCatalogue.CodeForLanguageName(name!));

    [Fact]
    public void CatalogueFileName_AppendsPotExtension()
        => Assert.Equal("WOMENACE.pot", LocaleLayout.CatalogueFileName("WOMENACE"));
}
