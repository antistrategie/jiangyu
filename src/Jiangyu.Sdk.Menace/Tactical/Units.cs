using Il2CppMenace.States;
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
    /// move is accepted (it animates over the following frames), false when no path
    /// reaches the tile.
    /// </summary>
    /// <remarks>
    /// The active actor travels the way a double click does. The tactical state's hovered
    /// tile is set to the destination, <c>ComputeActorPath</c> builds the path preview
    /// (<c>m_MovementResult</c>, and it copies the hovered tile into <c>m_TargetTile</c>),
    /// and <c>ExecuteActorTravel</c> then travels it: that method only moves when a result
    /// exists and the target equals the hovered tile, otherwise it merely previews. The
    /// travel stands a deployed unit up through its stance skill, calls <c>Actor.MoveTo</c>,
    /// then clears the visualiser and refreshes the tactical UI. Calling
    /// <c>Actor.MoveTo</c> directly, outside that sequence, leaves the state's action
    /// machine out of step with a unit that is already moving and the main thread spins on
    /// the next frame with the bridge unresponsive. The direct call is kept only for an
    /// actor that is not the active one, where the state has no travel to execute;
    /// <paramref name="flags"/> applies to that path alone.
    /// </remarks>
    [MutatingVerb]
    public static bool Move(Actor actor, Tile dest, MovementFlags flags = MovementFlags.None)
    {
        if (actor == null || dest == null)
            return false;
        var state = TacticalState.Get();
        var active = TacticalManager.Get()?.GetActiveActor();
        if (state != null && active != null && active.Pointer == actor.Pointer)
        {
            state.m_CurrentTile = dest;
            state.ComputeActorPath(actor);
            if (state.m_MovementResult == null || state.m_TargetTile?.Pointer != dest.Pointer)
                return false;
            state.ExecuteActorTravel(actor);
            return true;
        }
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
