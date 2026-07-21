namespace Jiangyu.Shared.Net;

/// <summary>
/// One replicated command: the unit of the multiplayer simulation stream. A peer issues
/// a command as intent (unsequenced, <see cref="Seq"/> = <see cref="Unassigned"/>); the
/// host stamps it with a monotonic sequence number and broadcasts it, giving every peer
/// one identical total order to apply. The body is transport-agnostic and schema-opaque
/// to this layer: <see cref="Kind"/> names the command and <see cref="Payload"/> carries
/// its arguments as JSON the loader encodes and decodes, so a modder-defined command
/// needs no change here.
/// </summary>
public sealed class NetCommand
{
    /// <summary>Sentinel for a command that has not yet been ordered by the host.</summary>
    public const long Unassigned = -1;

    /// <summary>Host-assigned position in the total order, dense from zero. While a peer's
    /// local intent travels to the host it is <see cref="Unassigned"/>.</summary>
    public long Seq { get; set; } = Unassigned;

    /// <summary>The peer that issued the command (its transport id).</summary>
    public ulong Source { get; set; }

    /// <summary>The command discriminator: a built-in verb (<c>skill</c>, <c>move</c>,
    /// <c>endturn</c>) or a modder-defined name. Opaque to the replication core.</summary>
    public string? Kind { get; set; }

    /// <summary>The command arguments as JSON, produced and consumed by the loader-side
    /// command handlers. Opaque here so the core never needs a command's schema.</summary>
    public string? Payload { get; set; }

    /// <summary>Host-resolved roll outputs carried with the command so both peers apply
    /// the same combat result rather than each re-rolling a stream that wall-clock
    /// consumers have reordered (the combat-roll spike's mechanism). Null for commands
    /// with no roll-bearing outcome.</summary>
    public string? Outcome { get; set; }

    /// <summary>True once the host has ordered this command.</summary>
    public bool IsSequenced => Seq >= 0;

    /// <summary>Content identity for journal integrity: two entries at the same sequence
    /// must agree on everything but the sequence number, or the streams have diverged.</summary>
    public bool SameContent(NetCommand other) =>
        Source == other.Source && Kind == other.Kind && Payload == other.Payload && Outcome == other.Outcome;
}
