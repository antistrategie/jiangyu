using System.Collections.Generic;
using System.Globalization;
using Jiangyu.Sdk;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

public class LocaleTests
{
    [Fact]
    public void Text_ReturnsFallback_WhenNothingInstalled()
    {
        Locale.Install(new Dictionary<string, string>());
        Assert.Equal("SWAP FORM", Locale.Text("MyMod::ui/swap_form", "SWAP FORM"));
    }

    [Fact]
    public void Text_ReturnsInstalledTranslation()
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/swap_form"] = "CHANGER DE FORME" });
        Assert.Equal("CHANGER DE FORME", Locale.Text("MyMod::ui/swap_form", "SWAP FORM"));
    }

    [Fact]
    public void Text_FallsBackOnEmptyTranslationOrMissingKey()
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/swap_form"] = "" });
        Assert.Equal("SWAP FORM", Locale.Text("MyMod::ui/swap_form", "SWAP FORM"));   // empty -> fallback
        Assert.Equal("OK", Locale.Text("MyMod::ui/missing", "OK"));                   // missing -> fallback
    }

    [Fact]
    public void Text_NullKeyReturnsFallback()
    {
        Assert.Equal("OK", Locale.Text(null, "OK"));
    }

    [Fact]
    public void Format_SubstitutesIntoTheTranslation()
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/lvl"] = "Stufe {0}" });
        Assert.Equal("Stufe 7", Locale.Format("MyMod::ui/lvl", "Level {0}", 7));
    }

    [Fact]
    public void Format_MissingKeyUsesTheFallback()
    {
        Locale.Install(null);
        Assert.Equal("Level 7", Locale.Format("MyMod::ui/lvl", "Level {0}", 7));
    }

    [Theory]
    [InlineData("Stufe {1}")]      // an index the caller never passes
    [InlineData("Stufe {0")]       // an unclosed brace
    [InlineData("Stufe")]          // placeholder dropped entirely: formats fine, must not throw
    public void Format_BadTranslationFallsBackWithoutThrowing(string translation)
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/lvl"] = translation });
        var result = Locale.Format("MyMod::ui/lvl", "Level {0}", 7);
        Assert.NotNull(result);
        Assert.DoesNotContain("{1}", result);
    }

    [Fact]
    public void Format_NullArgsDoesNotThrow()
    {
        // string.Format raises ArgumentNullException rather than FormatException here, which is
        // why the guard cannot be narrowed to FormatException.
        Locale.Install(null);
        Assert.Equal("Level {0}", Locale.Format("MyMod::ui/lvl", "Level {0}", null));
    }

    [Fact]
    public void Format_UsesInvariantCultureNotTheMachineCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Locale.Install(null);
            Assert.Equal("Range 12.5", Locale.Format("MyMod::ui/range", "Range {0}", 12.5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Install_NullClearsTheTable()
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/k"] = "x" });
        Locale.Install(null);
        Assert.Equal("fallback", Locale.Text("MyMod::ui/k", "fallback"));
    }
}
