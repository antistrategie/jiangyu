namespace Jiangyu.Shared.Net.Commands;

/// <summary>
/// The <see cref="NetCommand.Kind"/> discriminators for the framework's built-in tactical
/// verbs. A mod adds its own kinds alongside these; the replication core treats all kinds
/// alike, so this is only the built-in set the loader knows how to capture and replay.
/// </summary>
public static class CommandKinds
{
    public const string Move = "move";
    public const string Skill = "skill";
    public const string EndTurn = "endturn";
    public const string Skip = "skip";
}

/// <summary>A tile coordinate on the tactical grid, matching the game's (x, z) addressing.</summary>
public sealed class TileRef
{
    public int X { get; set; }
    public int Z { get; set; }
}

/// <summary>
/// A stable actor identity carried in a command. Both peers must resolve the same value
/// to the same actor, so it binds to the game's deterministic per-entity id (assigned at
/// mission generation, identical across peers under the same seed), never to a faction
/// list position, which is not stable once actors have acted. The concrete binding is the
/// loader's, verified in game; this layer only carries the id.
/// </summary>
public sealed class MoveCommand
{
    public ulong Actor { get; set; }
    public TileRef? Tile { get; set; }
}

/// <summary>Use a skill, attack, or ability on a target tile. Mirrors the command shape
/// the determinism harness drives in game: acting actor, skill id, usage mode, target.</summary>
public sealed class SkillCommand
{
    public ulong Actor { get; set; }
    public string? Skill { get; set; }
    public int Usage { get; set; }
    public TileRef? Tile { get; set; }
}

/// <summary>End the acting faction's turn. Turn-level, so it names no actor.</summary>
public sealed class EndTurnCommand
{
}

/// <summary>Skip one actor's remaining turn.</summary>
public sealed class SkipCommand
{
    public ulong Actor { get; set; }
}
