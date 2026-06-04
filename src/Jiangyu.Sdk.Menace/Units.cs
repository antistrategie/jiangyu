using Il2CppMenace.Tactical;
using Jiangyu.Sdk;

namespace Jiangyu.Game;

/// <summary>
/// The outcome of a <see cref="Units.Spawn"/> call. A struct rather than a bool
/// because spawn can be refused for several reasons, and the set of reasons will
/// grow as occupancy/faction-validity rules are characterised.
/// </summary>
public readonly struct SpawnOutcome
{
    public bool Ok { get; }

    /// <summary>The spawned actor when <see cref="Ok"/>, otherwise null.</summary>
    public Actor Unit { get; }

    /// <summary>Why the spawn was refused when not <see cref="Ok"/>, otherwise null.</summary>
    public string Reason { get; }

    private SpawnOutcome(bool ok, Actor unit, string reason)
    {
        Ok = ok;
        Unit = unit;
        Reason = reason;
    }

    public static SpawnOutcome Spawned(Actor unit) => new(true, unit, null);
    public static SpawnOutcome Rejected(string reason) => new(false, null, reason);
}

/// <summary>
/// Unit lifecycle verbs. These mutate the battlefield, so they are guarded:
/// outside a mission, on an unavailable tile, or while a faction is mid-evaluation
/// they refuse and report rather than corrupting game state. Backed by the
/// verified <c>TrySpawnUnit</c> / <c>Die</c> sequences.
/// </summary>
public static class Units
{
    /// <summary>
    /// Spawn a unit of <paramref name="template"/> for <paramref name="faction"/>
    /// onto <paramref name="tile"/>. Refuses (never throws) when there is no
    /// mission, the tile is null / occupied / blocked, or a faction is mid-turn.
    /// <paramref name="faction"/> is unconstrained (any faction may be spawned).
    /// The occupied- and blocked-tile refusals are Jiangyu safeties, not game limits:
    /// <c>TrySpawnUnit</c> validates neither, so a mod that wants a stacked or
    /// wall-embedded unit calls <c>TacticalManager.TrySpawnUnit</c> directly.
    /// </summary>
    [MutatingVerb]
    public static SpawnOutcome Spawn(EntityTemplate template, FactionType faction, Tile tile)
    {
        if (template == null)
            return SpawnOutcome.Rejected("template is null");
        if (!Tactical.InMission)
            return SpawnOutcome.Rejected("not in a tactical mission");
        if (tile == null)
            return SpawnOutcome.Rejected("tile is null");
        if (tile.HasActor())
            return SpawnOutcome.Rejected("tile is occupied");
        if (tile.IsBlocked())
            return SpawnOutcome.Rejected("tile is blocked");
        if (GameGuard.AnyFactionThinking())
        {
            Log.Warn("Units.Spawn refused: a faction is mid-evaluation. Spawn from a committed-action dispatch point, not a predicate or AI-context callback.");
            return SpawnOutcome.Rejected("a faction is mid-evaluation");
        }

        // InMission and Manager are separate calls; the manager can be torn down
        // between them, so re-check rather than trust the InMission gate.
        var manager = Tactical.Manager;
        if (manager == null)
            return SpawnOutcome.Rejected("no tactical manager");

        Actor unit = null;
        var ok = manager.TrySpawnUnit(faction, template, tile, out unit);
        return ok && unit != null
            ? SpawnOutcome.Spawned(unit)
            : SpawnOutcome.Rejected("TrySpawnUnit returned false");
    }

    /// <summary>
    /// Remove <paramref name="actor"/> from the field. Returns false for a null or
    /// already-released actor. <paramref name="quiet"/> suppresses death effects.
    /// <c>Die</c> is a clean removal: the unit leaves its faction roster, its tile is
    /// freed, and its HP goes to 0. Deeper on-death triggers (loot, conversations,
    /// objective counting) are not separately verified, so treat it as a removal
    /// rather than a guaranteed side-effect-free teleport-out.
    /// </summary>
    [MutatingVerb]
    public static bool Despawn(Actor actor, bool quiet = true)
    {
        if (!actor.IsAlive())
            return false;
        actor.Die(quiet);
        return true;
    }
}
