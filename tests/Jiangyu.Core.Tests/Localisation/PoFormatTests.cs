using Jiangyu.Shared.Localisation;
using Xunit;

namespace Jiangyu.Core.Tests.Localisation;

public class PoFormatTests
{
    [Fact]
    public void Parse_ReadsContextIdStrAndComments()
    {
        const string po = """
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"

            #. Items · WeaponTemplate weapon.ak15 · Title
            #: templates/weapon/ak15.kdl
            msgctxt "WOMENACE::WeaponTemplate/weapon.ak15/Title"
            msgid "Kalashnikova-15"
            msgstr "Kalachnikova-15"
            """;

        var file = PoFormat.Parse(po);

        var entry = Assert.Single(file.Entries);
        Assert.Equal("WOMENACE::WeaponTemplate/weapon.ak15/Title", entry.Context);
        Assert.Equal("Kalashnikova-15", entry.Id);
        Assert.Equal("Kalachnikova-15", entry.Str);
        Assert.False(entry.Fuzzy);
        Assert.Contains("Items · WeaponTemplate weapon.ak15 · Title", entry.ExtractedComments);
        Assert.Contains("templates/weapon/ak15.kdl", entry.References);
    }

    [Fact]
    public void Parse_SkipsHeaderEntry()
    {
        const string po = """
            msgid ""
            msgstr "Content-Type: text/plain; charset=UTF-8\n"
            """;

        Assert.Empty(PoFormat.Parse(po).Entries);
    }

    [Fact]
    public void Parse_FlagsFuzzyAndConcatenatesMultiLine()
    {
        const string po = """
            #, fuzzy
            msgctxt "k"
            msgid ""
            "first line\n"
            "second line"
            msgstr "x"
            """;

        var entry = Assert.Single(PoFormat.Parse(po).Entries);
        Assert.True(entry.Fuzzy);
        Assert.Equal("first line\nsecond line", entry.Id);
        Assert.False(entry.HasUsableTranslation); // fuzzy is not usable
    }

    [Fact]
    public void WriteThenParse_RoundTripsMultiLineAndEscapes()
    {
        var file = new PoFile();
        file.Entries.Add(new PoEntry { Context = "k1", Id = "line1\nline2", Str = "trad1\ntrad2" });
        file.Entries.Add(new PoEntry { Context = "k2", Id = "say \"hi\"\tend", Str = "ok", Fuzzy = true });

        var round = PoFormat.Parse(PoFormat.Write(file));

        Assert.Equal(2, round.Entries.Count);
        Assert.Equal("k1", round.Entries[0].Context);
        Assert.Equal("line1\nline2", round.Entries[0].Id);
        Assert.Equal("trad1\ntrad2", round.Entries[0].Str);
        Assert.Equal("say \"hi\"\tend", round.Entries[1].Id);
        Assert.True(round.Entries[1].Fuzzy);
    }

    [Fact]
    public void HasUsableTranslation_RequiresNonEmptyNonFuzzy()
    {
        Assert.True(new PoEntry { Str = "x" }.HasUsableTranslation);
        Assert.False(new PoEntry { Str = "" }.HasUsableTranslation);
        Assert.False(new PoEntry { Str = "x", Fuzzy = true }.HasUsableTranslation);
    }
}
