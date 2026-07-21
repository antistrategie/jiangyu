using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Shared.Net;
using Jiangyu.Shared.Net.Commands;

namespace Jiangyu.Loader.Net;

/// <summary>
/// Applies a replicated <see cref="NetCommand"/> to the live tactical mission by calling
/// the game's own execution funnels (<c>Skill.Use</c>, <c>Actor.MoveTo</c>,
/// <c>TacticalState.EndTurn</c>, <c>Actor.SkipTurn</c>) with the decoded arguments. This
/// is the replay half of command replication: every peer runs it on the ordered stream,
/// so applying an identical sequence reproduces an identical mission. Actor references
/// resolve by <see cref="Entity.ID"/>, the sequential per-entity id verified stable across
/// processes, so the same id names the same actor on every peer. The command intent is
/// replicated; internal skill-effect cascades (counterattacks, procs) follow as
/// deterministic consequences and are not replicated.
/// </summary>
public sealed class CommandApplier
{
    private readonly TacticalManager _manager;

    public CommandApplier(TacticalManager manager) => _manager = manager;

    /// <summary>Apply one ordered command, returning a short result for the journal:
    /// <c>ok</c>, <c>rejected by game</c>, or an <c>error: ...</c> string. Never throws;
    /// a throw becomes an error result so one bad command cannot tear down the stream.</summary>
    public string Apply(NetCommand command)
    {
        try
        {
            switch (command.Kind)
            {
                case CommandKinds.Move:
                    return ApplyMove(command);
                case CommandKinds.Skill:
                    return ApplySkill(command);
                case CommandKinds.EndTurn:
                    Il2CppMenace.States.TacticalState.Get().EndTurn();
                    return "ok";
                case CommandKinds.Skip:
                    return ApplySkip(command);
                default:
                    return $"error: unknown command kind '{command.Kind}'";
            }
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Resolve an actor by its stable <see cref="Entity.ID"/>, or null if no live
    /// actor carries that id.</summary>
    public Actor ResolveActor(ulong id)
    {
        var factions = _manager.GetFactions();
        for (var i = 0; factions != null && i < factions.Length; i++)
        {
            var actors = factions[i]?.GetActors();
            for (var j = 0; actors != null && j < actors.Count; j++)
            {
                var actor = actors[j];
                if (actor != null && (ulong)actor.ID == id)
                    return actor;
            }
        }

        return null;
    }

    private string ApplyMove(NetCommand command)
    {
        var body = CommandCodec.Decode<MoveCommand>(command.Payload);
        if (body?.Tile == null)
            return "error: malformed move payload";
        if (!TryResolve(body.Actor, body.Tile, out var actor, out var tile, out var error))
            return error;
        var action = default(MovementAction);
        return actor.MoveTo(tile, ref action, MovementFlags.None) ? "ok" : "rejected by game";
    }

    private string ApplySkill(NetCommand command)
    {
        var body = CommandCodec.Decode<SkillCommand>(command.Payload);
        if (body?.Tile == null || string.IsNullOrEmpty(body.Skill))
            return "error: malformed skill payload";
        if (!TryResolve(body.Actor, body.Tile, out var actor, out var tile, out var error))
            return error;
        var skill = TacticalResolve.Skill(actor, body.Skill);
        if (skill == null)
            return $"error: no skill '{body.Skill}' on actor {body.Actor}";
        skill.Use(tile, (UsageParameter)body.Usage);
        return "ok";
    }

    // Resolve the actor (by Entity.ID) and target tile a move or skill command shares,
    // with the same error strings both used before.
    private bool TryResolve(ulong actorId, TileRef tileRef, out Actor actor, out Tile tile, out string error)
    {
        tile = null;
        actor = ResolveActor(actorId);
        if (actor == null)
        {
            error = $"error: no actor with id {actorId}";
            return false;
        }

        tile = TacticalResolve.Tile(_manager, tileRef.X, tileRef.Z);
        if (tile == null)
        {
            error = $"error: no tile at {tileRef.X},{tileRef.Z}";
            return false;
        }

        error = null;
        return true;
    }

    private string ApplySkip(NetCommand command)
    {
        var body = CommandCodec.Decode<SkipCommand>(command.Payload);
        var actor = body == null ? null : ResolveActor(body.Actor);
        if (actor == null)
            return "error: no actor to skip";
        actor.SkipTurn();
        return "ok";
    }
}
