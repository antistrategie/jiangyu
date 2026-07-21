using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class CommandJournalTests
{
    private static NetCommand Cmd(long seq, string kind = "move", string payload = "{}", ulong source = 1) =>
        new() { Seq = seq, Source = source, Kind = kind, Payload = payload };

    [Fact]
    public void Record_AppendsInOrderAndAdvancesNextSeq()
    {
        var journal = new CommandJournal();
        Assert.Equal(0, journal.NextSeq);

        Assert.Equal(JournalResult.Recorded, journal.Record(Cmd(0)));
        Assert.Equal(JournalResult.Recorded, journal.Record(Cmd(1)));

        Assert.Equal(2, journal.Count);
        Assert.Equal(2, journal.NextSeq);
    }

    [Fact]
    public void Record_ReportsGapForOutOfOrderSequence()
    {
        var journal = new CommandJournal();
        journal.Record(Cmd(0));

        Assert.Equal(JournalResult.Gap, journal.Record(Cmd(2)));
        Assert.Equal(1, journal.Count);
    }

    [Fact]
    public void Record_ReportsDuplicateForIdenticalReplay()
    {
        var journal = new CommandJournal();
        journal.Record(Cmd(0, "move", "{\"tile\":[1,2]}"));

        Assert.Equal(JournalResult.Duplicate, journal.Record(Cmd(0, "move", "{\"tile\":[1,2]}")));
        Assert.Equal(1, journal.Count);
    }

    [Fact]
    public void Record_ReportsConflictForDifferentCommandAtSameSequence()
    {
        var journal = new CommandJournal();
        journal.Record(Cmd(0, "move", "{\"tile\":[1,2]}"));

        Assert.Equal(JournalResult.Conflict, journal.Record(Cmd(0, "move", "{\"tile\":[9,9]}")));
    }

    [Fact]
    public void Record_RejectsUnsequencedCommand()
    {
        var journal = new CommandJournal();
        Assert.Throws<ArgumentException>(() => journal.Record(Cmd(NetCommand.Unassigned)));
    }
}
