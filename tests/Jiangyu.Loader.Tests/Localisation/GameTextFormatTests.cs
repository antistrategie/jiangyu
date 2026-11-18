using Jiangyu.Loader.Runtime.Localisation;
using Xunit;

namespace Jiangyu.Loader.Tests.Localisation;

/// <summary>
/// Covers whether a mod's replacement for one of the game's strings can stand where the game's text
/// did. The game fills the string's numbered placeholders with string.Format, so a replacement that
/// would throw there is refused and the game's own text stays.
/// </summary>
public sealed class GameTextFormatTests
{
    [Theory]
    [InlineData("Dummies", "Squaddies")]
    [InlineData("{0} Dummies", "{0} Squaddies")]
    [InlineData("Dummies", "{0} Squaddies")]
    [InlineData("{1} of {0}", "{0} / {1}")]
    [InlineData("{0:N0} dummies", "{0} squaddies")]
    [InlineData("{1}", "{0} and {2}")]
    [InlineData("{0,5}", "{0}")]
    [InlineData("{0}", "{0,5} squaddies")]
    [InlineData("a {{brace}} written as a literal", "no placeholders")]
    [InlineData("{{0}} stays literal", "{{0}} literal")]
    [InlineData("Dummies", null)]
    public void Fits_WhenTheGameCanFormatIt(string text, string? original) =>
        Assert.True(GameTextFormat.Fits(text, original));

    // The event and mission texts carry the game's own bracketed tokens, which string.Format cannot
    // take, so the game does not format them.
    [Theory]
    [InlineData("[FACTION_NAME:{temp_faction1}] holds", "[FACTION_NAME:{temp_faction1}] holds the district")]
    [InlineData("Dummies {", "[{temp_leader_role_id}.NICKNAME]")]
    public void Fits_AnyTextWhenTheGameDoesNotFormatTheOriginal(string text, string original) =>
        Assert.True(GameTextFormat.Fits(text, original));

    [Theory]
    [InlineData("{1} Dummies", "{0} Squaddies")]
    [InlineData("{0} Dummies {", "{0} Squaddies")]
    [InlineData("Dummies }", "{0} Squaddies")]
    [InlineData("{x} Dummies", "{0} Squaddies")]
    [InlineData("{0:Q} Dummies", "{0} Squaddies")]
    [InlineData("a {brace} the game may format", "no placeholders")]
    [InlineData("{0} Dummies", "no placeholders")]
    [InlineData("Use {0}", "{{0}} literal")]
    [InlineData("{5}", null)]
    public void DoesNotFit_WhenFormattingWouldThrow(string text, string? original) =>
        Assert.False(GameTextFormat.Fits(text, original));

    [Fact]
    public void DoesNotFit_NoText() =>
        Assert.False(GameTextFormat.Fits(null, "{0}"));
}
