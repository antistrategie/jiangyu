using System.Collections.Generic;
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
    public void Install_NullClearsTheTable()
    {
        Locale.Install(new Dictionary<string, string> { ["MyMod::ui/k"] = "x" });
        Locale.Install(null);
        Assert.Equal("fallback", Locale.Text("MyMod::ui/k", "fallback"));
    }
}
