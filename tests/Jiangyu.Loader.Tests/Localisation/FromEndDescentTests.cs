using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Localisation;
using Xunit;

namespace Jiangyu.Loader.Tests.Localisation;

/// <summary>
/// Covers the from-end descent index the localisation coordinate mints for an element a patch
/// appended. The compiler cannot know where an append lands (the list it grows may already hold
/// anything), only how far back from the end it sits once the patch has run, so the coordinate says
/// <c>Field[^n]</c> and the loader resolves n against the live length here.
///
/// Tested against plain managed fixtures: descent navigation is pure reflection over Length/Count and
/// an int indexer, so the mechanism runs without a live IL2CPP game.
/// </summary>
public sealed class FromEndDescentTests
{
    private sealed class Line
    {
        public string m_DefaultTranslation { get; set; }
    }

    private sealed class Tooltip
    {
        public Line TooltipText { get; set; }
    }

    private sealed class Config
    {
        public List<Tooltip> Tooltips { get; set; }
        public Tooltip[] Fixed { get; set; }
    }

    private static Config ThreeTooltips() => new()
    {
        Tooltips =
        [
            new Tooltip { TooltipText = new Line { m_DefaultTranslation = "vanilla" } },
            new Tooltip { TooltipText = new Line { m_DefaultTranslation = "first appended" } },
            new Tooltip { TooltipText = new Line { m_DefaultTranslation = "second appended" } },
        ],
    };

    [Theory]
    [InlineData("Tooltips[^2]/TooltipText", "first appended")]
    [InlineData("Tooltips[^1]/TooltipText", "second appended")]
    [InlineData("Tooltips[^3]/TooltipText", "vanilla")]
    [InlineData("Tooltips[0]/TooltipText", "vanilla")]
    public void NavigatesToTheElementCountedBackFromTheEnd(string path, string expected)
    {
        var descent = LocaleCoordinate.ParseDescent(path);
        Assert.NotNull(descent);

        var navigated = TemplatePatchApplier.TryNavigateDescent(
            ThreeTooltips(), descent!, out var target, out var error);

        Assert.True(navigated, error);
        Assert.Equal(expected, Assert.IsType<Line>(target).m_DefaultTranslation);
    }

    [Fact]
    public void ResolvesAgainstAnArrayLengthToo()
    {
        var config = new Config
        {
            Fixed =
            [
                new Tooltip { TooltipText = new Line { m_DefaultTranslation = "one" } },
                new Tooltip { TooltipText = new Line { m_DefaultTranslation = "two" } },
            ],
        };

        var navigated = TemplatePatchApplier.TryNavigateDescent(
            config, LocaleCoordinate.ParseDescent("Fixed[^1]/TooltipText")!, out var target, out var error);

        Assert.True(navigated, error);
        Assert.Equal("two", Assert.IsType<Line>(target).m_DefaultTranslation);
    }

    [Fact]
    public void FailsRatherThanWrapsWhenTheListIsShorterThanTheCoordinateExpects()
    {
        // The collection lost elements since the POT was minted. Reaching past the start must report
        // a miss, not silently land on some other element and translate the wrong string.
        var config = new Config { Tooltips = [new Tooltip { TooltipText = new Line() }] };

        var navigated = TemplatePatchApplier.TryNavigateDescent(
            config, LocaleCoordinate.ParseDescent("Tooltips[^3]/TooltipText")!, out var target, out var error);

        Assert.False(navigated);
        Assert.Null(target);
        Assert.Contains("^3", error);
    }
}
