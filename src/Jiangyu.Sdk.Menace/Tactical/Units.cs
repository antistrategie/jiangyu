using Il2CppMenace.Tactical;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Unit lifecycle verbs. Per-actor state reads (Hitpoints, Morale, ...) are generated
/// from the verb manifest into this partial class.
/// </summary>
public static partial class Units
{
    /// <summary>
    /// Spawn a unit of <paramref name="template"/> for <paramref name="faction"/> on
    /// <paramref name="tile"/>. Returns the spawned actor, or null when the game refuses.
    /// </summary>
    [MutatingVerb]
    public static Actor Spawn(EntityTemplate template, FactionType faction, Tile tile)
    {
        TacticalManager.Get().TrySpawnUnit(faction, template, tile, out var unit);
        return unit;
    }

    /// <summary>Remove <paramref name="actor"/> from the field. <paramref name="quiet"/> suppresses death effects.</summary>
    [MutatingVerb]
    public static void Despawn(Actor actor, bool quiet = true) => actor.Die(quiet);

    /// <summary>
    /// Move <paramref name="actor"/> to <paramref name="dest"/>. Returns true once the
    /// move is accepted (it animates over the following frames). The game validates
    /// reachability.
    /// </summary>
    [MutatingVerb]
    public static bool Move(Actor actor, Tile dest, MovementFlags flags = MovementFlags.None)
    {
        var action = default(MovementAction);
        return actor.MoveTo(dest, ref action, flags);
    }

    /// <summary>
    /// Refill <paramref name="actor"/>'s ammo across every skill by
    /// <paramref name="refillFactor"/> (0..1 of capacity), granting at least
    /// <paramref name="minAmount"/>. Returns true when any skill was refilled.
    /// </summary>
    [MutatingVerb]
    public static bool RefillAmmo(Actor actor, float refillFactor = 1f, int minAmount = 0)
        => actor.RefillAmmo(refillFactor, minAmount, null);
}
