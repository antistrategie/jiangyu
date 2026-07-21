namespace Jiangyu.Shared.Net;

/// <summary>The outcome of offering a command to the journal.</summary>
public enum JournalResult
{
    /// <summary>Appended at the expected next position.</summary>
    Recorded,

    /// <summary>A command already sits at this sequence with identical content: a
    /// harmless re-delivery, ignored.</summary>
    Duplicate,

    /// <summary>A command already sits at this sequence with different content: the two
    /// peers' command streams have diverged. A desync signal, not a normal event.</summary>
    Conflict,

    /// <summary>The sequence is beyond the next expected position: it arrived before an
    /// earlier command. The caller buffers it until the gap fills.</summary>
    Gap,
}

/// <summary>
/// The append-only, sequence-indexed record of the command stream, kept identically on
/// every peer. It is the ordering authority (entries are dense from zero, so the index
/// is the sequence number), the integrity check (a differing command at a known sequence
/// is a <see cref="JournalResult.Conflict"/>), and the forensic log for desync bisection
/// and resync replay. Out-of-order arrivals are the replicator's concern, not the
/// journal's: the journal only ever accepts the next expected sequence.
/// </summary>
public sealed class CommandJournal
{
    private readonly List<NetCommand> _entries = [];

    /// <summary>Every recorded command in order, index equal to sequence.</summary>
    public IReadOnlyList<NetCommand> Entries => _entries;

    /// <summary>The number of commands recorded.</summary>
    public long Count => _entries.Count;

    /// <summary>The sequence the journal will accept next.</summary>
    public long NextSeq => _entries.Count;

    /// <summary>Offer a sequenced command to the journal. Only a command whose sequence
    /// equals <see cref="NextSeq"/> is recorded; earlier sequences are classified as a
    /// duplicate or a conflict, later ones as a gap for the caller to buffer.</summary>
    public JournalResult Record(NetCommand command)
    {
        if (!command.IsSequenced)
            throw new ArgumentException("cannot journal an unsequenced command", nameof(command));

        if (command.Seq == NextSeq)
        {
            _entries.Add(command);
            return JournalResult.Recorded;
        }

        if (command.Seq < NextSeq)
        {
            var existing = _entries[(int)command.Seq];
            return existing.SameContent(command) ? JournalResult.Duplicate : JournalResult.Conflict;
        }

        return JournalResult.Gap;
    }
}
