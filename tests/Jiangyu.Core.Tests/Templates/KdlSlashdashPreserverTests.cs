using Jiangyu.Core.Templates;
using Jiangyu.Core.Templates.Kdl;

namespace Jiangyu.Core.Tests.Templates;

public class KdlSlashdashPreserverTests
{
    [Fact]
    public void Extract_NoSlashdashNodes_ReturnsEmptyAndPreservesText()
    {
        var src = "clone \"X\" \"a\" \"b\" {\n    set \"K\" 1\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Empty(blocks);
        Assert.Equal(src, stripped);
    }

    [Fact]
    public void Extract_SingleSlashdashBeforeActiveNode_AnchorsAtRootIndexZero()
    {
        var src =
            "/-clone \"PerkTemplate\" from=\"a\" id=\"b\" {\n" +
            "    set \"K\" 1\n" +
            "}\n" +
            "clone \"PerkTreeTemplate\" from=\"a\" id=\"b\" {\n" +
            "    clear \"Perks\"\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Single(blocks);
        Assert.Equal([0], blocks[0].Path);
        Assert.Contains("/-clone \"PerkTemplate\"", blocks[0].Text);
        Assert.Contains("set \"K\" 1", blocks[0].Text);
        Assert.Contains("}", blocks[0].Text);
        Assert.DoesNotContain("/-clone", stripped);
        Assert.Contains("clone \"PerkTreeTemplate\"", stripped);
    }

    [Fact]
    public void Extract_SlashdashAfterActiveNode_AnchorsAtRootIndexOne()
    {
        var src =
            "clone \"A\" \"a\" \"a\" {\n}\n" +
            "/-clone \"B\" \"b\" \"b\" {\n    set \"K\" 2\n}\n" +
            "clone \"C\" \"c\" \"c\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([1], blocks[0].Path);
    }

    [Fact]
    public void Extract_TrailingSlashdashAfterAllActiveNodes_AnchorsBeyondLastIndex()
    {
        var src =
            "clone \"A\" \"a\" \"a\" {\n}\n" +
            "/-clone \"Z\" \"z\" \"z\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([1], blocks[0].Path);
    }

    [Fact]
    public void Extract_PrecedingLineCommentsTravelWithSlashdash()
    {
        var src =
            "// parked example, here for reference\n" +
            "// shows how a marathoner perk would look\n" +
            "/-clone \"PerkTemplate\" \"a\" \"b\" {\n}\n" +
            "clone \"X\" \"x\" \"x\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Single(blocks);
        Assert.Contains("parked example, here for reference", blocks[0].Text);
        Assert.Contains("shows how a marathoner perk", blocks[0].Text);
        Assert.Contains("/-clone \"PerkTemplate\"", blocks[0].Text);
        Assert.DoesNotContain("parked example", stripped);
        Assert.DoesNotContain("/-clone", stripped);
        Assert.Contains("clone \"X\"", stripped);
    }

    [Fact]
    public void Extract_NestedBracesInSlashdashBody_FindsCorrectMatchingBrace()
    {
        var src =
            "/-clone \"A\" \"a\" \"b\" {\n" +
            "    set \"Outer\" {\n" +
            "        set \"Inner\" {\n" +
            "            set \"Deepest\" 42\n" +
            "        }\n" +
            "    }\n" +
            "}\n" +
            "clone \"X\" \"x\" \"x\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Single(blocks);
        Assert.Contains("Deepest", blocks[0].Text);
        // The block's text must end at the matching outermost brace, not
        // a nested one. Confirms brace matching is depth-aware.
        Assert.EndsWith("}", blocks[0].Text.TrimEnd());
        Assert.DoesNotContain("/-clone", stripped);
        Assert.DoesNotContain("Deepest", stripped);
    }

    [Fact]
    public void Extract_SlashdashAfterClosingBraceLine_LeavesPrecedingActiveNodeIntact()
    {
        // Regression: when /- followed a closing-brace line, the rollback
        // anchor sat at the start of that } line and ate it into the parked
        // block, leaving stripped KDL malformed (clone "A" missing its `}`).
        var src =
            "clone \"A\" \"a\" \"a\" {\n}\n" +
            "/-clone \"B\" \"b\" \"b\" {\n}\n" +
            "clone \"C\" \"c\" \"c\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Single(blocks);
        Assert.Equal([1], blocks[0].Path);
        Assert.Contains("clone \"A\" \"a\" \"a\" {\n}", stripped);
        Assert.Contains("clone \"C\"", stripped);
        Assert.DoesNotContain("clone \"B\"", stripped);
        Assert.DoesNotContain("/-clone", stripped);
    }

    [Fact]
    public void Extract_IndentedSetLines_DoNotInflateActiveNodeCount()
    {
        // Regression: indented `set` lines (and the leading whitespace before
        // them) used to increment the active-node count, so a /- following a
        // clone body anchored far past the actual top-level position.
        var src =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    set \"K\" 1\n" +
            "    set \"L\" 2\n" +
            "}\n" +
            "/-clone \"Park\" \"p\" \"p\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([1], blocks[0].Path);
    }

    [Fact]
    public void Extract_SlashdashInsideStringLiteral_IsNotMistakenForNode()
    {
        // `/-` appears inside a string value, not at the start of a top-level
        // node. The extractor must not treat it as a slashdash.
        var src =
            "clone \"A\" \"a\" \"a\" {\n" +
            "    set \"Text\" \"escape /- and other slashes\"\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Empty(blocks);
        Assert.Equal(src, stripped);
    }

    [Fact]
    public void Extract_NestedSlashdashBeforeSibling_CapturesWithPath()
    {
        var src =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    /-set \"Y\" 1\n" +
            "    set \"Z\" 2\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);

        Assert.Single(blocks);
        Assert.Equal([0, 0], blocks[0].Path);
        Assert.Contains("/-set \"Y\" 1", blocks[0].Text);
        Assert.DoesNotContain("/-set", stripped);
        Assert.Contains("set \"Z\" 2", stripped);
    }

    [Fact]
    public void Extract_NestedSlashdashAfterSibling_AnchorsAtBodyIndexOne()
    {
        var src =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    set \"A\" 1\n" +
            "    /-set \"B\" 2\n" +
            "    set \"C\" 3\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([0, 1], blocks[0].Path);
    }

    [Fact]
    public void Extract_NestedSlashdashAtEndOfBody_AnchorsPastLastChild()
    {
        // The parked node is the LAST line in the body. Reinjection must
        // place it before the body's closing brace.
        var src =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    set \"A\" 1\n" +
            "    /-set \"B\" 2\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([0, 1], blocks[0].Path);
    }

    [Fact]
    public void Extract_DeeplyNestedSlashdash_CapturesFullPath()
    {
        var src =
            "patch \"EntityTemplate\" \"id\" {\n" +
            "    set \"Properties\" type=\"FixtureProperties\" {\n" +
            "        set \"A\" 1\n" +
            "        /-set \"B\" 2\n" +
            "        set \"C\" 3\n" +
            "    }\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out _);

        Assert.Single(blocks);
        Assert.Equal([0, 0, 1], blocks[0].Path);
    }

    [Fact]
    public void Reinject_PlacesBlocksBeforeAnchoredActiveNodes()
    {
        var formatted =
            "clone \"A\" \"a\" \"a\" {\n}\n" +
            "clone \"B\" \"b\" \"b\" {\n}\n";

        var blocks = new[]
        {
            new KdlSlashdashPreserver.Block("/-clone \"P0\" \"p\" \"q\" {\n}", [0]),
            new KdlSlashdashPreserver.Block("/-clone \"P1\" \"r\" \"s\" {\n}", [1]),
        };

        var result = KdlSlashdashPreserver.Reinject(formatted, blocks);

        var p0Pos = result.IndexOf("/-clone \"P0\"", StringComparison.Ordinal);
        var aPos = result.IndexOf("clone \"A\"", StringComparison.Ordinal);
        var p1Pos = result.IndexOf("/-clone \"P1\"", StringComparison.Ordinal);
        var bPos = result.IndexOf("clone \"B\"", StringComparison.Ordinal);

        Assert.True(p0Pos >= 0 && aPos >= 0 && p1Pos >= 0 && bPos >= 0);
        Assert.True(p0Pos < aPos, "/-P0 must appear before clone A");
        Assert.True(aPos < p1Pos, "clone A must appear before /-P1");
        Assert.True(p1Pos < bPos, "/-P1 must appear before clone B");
    }

    [Fact]
    public void Reinject_TrailingBlocksAppendAtEnd()
    {
        var formatted = "clone \"A\" \"a\" \"a\" {\n}\n";
        var blocks = new[]
        {
            new KdlSlashdashPreserver.Block("/-clone \"END\" \"e\" \"e\" {\n}", [1]),
        };

        var result = KdlSlashdashPreserver.Reinject(formatted, blocks);

        var aPos = result.IndexOf("clone \"A\"", StringComparison.Ordinal);
        var endPos = result.IndexOf("/-clone \"END\"", StringComparison.Ordinal);
        Assert.True(aPos >= 0 && endPos >= 0);
        Assert.True(aPos < endPos);
    }

    [Fact]
    public void Reinject_EmptyBlockList_ReturnsInputUnchanged()
    {
        var formatted = "clone \"A\" \"a\" \"a\" {\n}\n";
        var result = KdlSlashdashPreserver.Reinject(formatted, []);
        Assert.Equal(formatted, result);
    }

    [Fact]
    public void Reinject_NestedBlock_InsertsBetweenBodySiblings()
    {
        var formatted =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    set \"A\" 1\n" +
            "    set \"C\" 3\n" +
            "}\n";

        var blocks = new[]
        {
            new KdlSlashdashPreserver.Block("    /-set \"B\" 2", [0, 1]),
        };

        var result = KdlSlashdashPreserver.Reinject(formatted, blocks);

        var aPos = result.IndexOf("set \"A\" 1", StringComparison.Ordinal);
        var bPos = result.IndexOf("/-set \"B\" 2", StringComparison.Ordinal);
        var cPos = result.IndexOf("set \"C\" 3", StringComparison.Ordinal);

        Assert.True(aPos >= 0 && bPos >= 0 && cPos >= 0);
        Assert.True(aPos < bPos, "set A must appear before /-set B");
        Assert.True(bPos < cPos, "/-set B must appear before set C");
    }

    [Fact]
    public void Reinject_NestedBlockAtEndOfBody_InsertsBeforeClosingBrace()
    {
        var formatted =
            "clone \"X\" \"a\" \"b\" {\n" +
            "    set \"A\" 1\n" +
            "}\n";

        var blocks = new[]
        {
            new KdlSlashdashPreserver.Block("    /-set \"B\" 2", [0, 1]),
        };

        var result = KdlSlashdashPreserver.Reinject(formatted, blocks);

        var aPos = result.IndexOf("set \"A\" 1", StringComparison.Ordinal);
        var bPos = result.IndexOf("/-set \"B\" 2", StringComparison.Ordinal);
        var closeBracePos = result.IndexOf("\n}\n", StringComparison.Ordinal);

        Assert.True(aPos >= 0 && bPos >= 0 && closeBracePos >= 0);
        Assert.True(aPos < bPos, "set A must appear before /-set B");
        Assert.True(bPos < closeBracePos, "/-set B must appear before the closing brace");
    }

    [Fact]
    public void ExtractAndReinject_RoundTripPreservesSlashdashAtOriginalPosition()
    {
        // Full round-trip: a slashdash block with a `//` annotation should
        // re-emerge between the same active nodes after extract + reinject.
        var src =
            "clone \"First\" \"a\" \"a\" {\n}\n" +
            "// annotation describing the parked block\n" +
            "/-clone \"Parked\" \"p\" \"q\" {\n" +
            "    set \"X\" 7\n" +
            "}\n" +
            "clone \"Second\" \"b\" \"b\" {\n}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);
        // Simulate a no-op format pass: stripped text goes through unchanged.
        var rejoined = KdlSlashdashPreserver.Reinject(stripped, blocks);

        var firstPos = rejoined.IndexOf("clone \"First\"", StringComparison.Ordinal);
        var annoPos = rejoined.IndexOf("annotation describing", StringComparison.Ordinal);
        var parkedPos = rejoined.IndexOf("/-clone \"Parked\"", StringComparison.Ordinal);
        var secondPos = rejoined.IndexOf("clone \"Second\"", StringComparison.Ordinal);

        Assert.True(firstPos >= 0);
        Assert.True(annoPos >= 0);
        Assert.True(parkedPos >= 0);
        Assert.True(secondPos >= 0);
        Assert.True(firstPos < annoPos, "annotation should be after First");
        Assert.True(annoPos < parkedPos, "annotation should be above parked block");
        Assert.True(parkedPos < secondPos, "parked block should be before Second");
        Assert.Contains("set \"X\" 7", rejoined);
    }

    [Fact]
    public void NestedSlashdash_SurvivesFullFormatRoundTrip()
    {
        // Drives the full FormatDocument flow: extract /- → parse-and-serialise
        // through the KDL pipeline (which drops slashdash) → reinject. The
        // nested /-set must reappear at the same body position.
        var src =
            "clone \"EntityTemplate\" from=\"a\" id=\"b\" {\n" +
            "    set \"A\" 1\n" +
            "    /-set \"B\" 2\n" +
            "    set \"C\" 3\n" +
            "}\n";

        var blocks = KdlSlashdashPreserver.Extract(src, out var stripped);
        var doc = KdlTemplateParser.ParseText(stripped);
        var formatted = KdlTemplateSerialiser.Serialise(doc);
        var rejoined = KdlSlashdashPreserver.Reinject(formatted, blocks);

        var aPos = rejoined.IndexOf("set \"A\" 1", StringComparison.Ordinal);
        var bPos = rejoined.IndexOf("/-set \"B\" 2", StringComparison.Ordinal);
        var cPos = rejoined.IndexOf("set \"C\" 3", StringComparison.Ordinal);

        Assert.True(aPos >= 0 && bPos >= 0 && cPos >= 0, "all three set lines must be present after round-trip");
        Assert.True(aPos < bPos);
        Assert.True(bPos < cPos);
    }
}
