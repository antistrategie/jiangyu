namespace Jiangyu.Shared.Net.Commands;

/// <summary>
/// One roll-derived state change the host resolved and ships in a command's
/// <see cref="NetCommand.Outcome"/>, so a client applies the host's result rather than
/// re-rolling a stream that wall-clock consumers have reordered (the combat-roll spike's
/// mechanism). The field set is small and known from the spike: suppression, armour,
/// morale, hit points. Values are carried verbatim so a client sets them exactly, leaving
/// no fractional difference that could cross a threshold differently across peers.
/// </summary>
public sealed class ActorDelta
{
    public ulong Actor { get; set; }

    /// <summary>The projected field this delta sets, e.g. <c>suppression</c>, <c>armour</c>,
    /// <c>morale</c>, <c>hp</c>.</summary>
    public string? Field { get; set; }

    /// <summary>The resolved absolute value for the field after the command.</summary>
    public double Value { get; set; }
}

/// <summary>The host-resolved outputs of one roll-bearing command: the set of actor field
/// values a client applies to stay identical to the host without re-rolling.</summary>
public sealed class CommandOutcome
{
    public List<ActorDelta> Deltas { get; set; } = [];
}
