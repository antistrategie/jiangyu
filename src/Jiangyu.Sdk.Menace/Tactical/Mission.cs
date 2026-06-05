using System.Collections.Generic;
using Il2CppMenace.Tactical;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Reads and acquisition for the live Tactical mission. Check <see cref="InMission"/>
/// before reading the manager-derived members: outside a mission the manager is null
/// and they fault, as any direct game call would.
/// </summary>
public static partial class Mission
{
    /// <summary>Whether a Tactical mission is currently running.</summary>
    public static bool InMission => TacticalManager.IsMissionRunning();

    /// <summary>The tactical manager singleton, or null outside a mission.</summary>
    public static TacticalManager Manager => TacticalManager.Get();

    /// <summary>The live tactical map.</summary>
    public static Map Map => Manager.GetMap();

    /// <summary>The actor whose turn it currently is.</summary>
    public static Actor ActiveActor => Manager.m_ActiveActor;

    /// <summary>The current round number.</summary>
    public static int Round => Manager.GetRound();

    /// <summary>The tile at a map coordinate, or null if out of bounds.</summary>
    public static Tile TileAt(int x, int z) => Map.GetTile(x, z);

    /// <summary>
    /// Every actor on the field, optionally filtered to one faction. Flattens the
    /// per-faction actor lists.
    /// </summary>
    public static IReadOnlyList<Actor> Actors(FactionType? faction = null)
    {
        var result = new List<Actor>();
        var factions = Manager.GetFactions();
        for (var i = 0; i < factions.Length; i++)
        {
            var f = factions[i];
            if (faction.HasValue && f.GetFactionType() != faction.Value)
                continue;

            var actors = f.GetActors();
            for (var j = 0; j < actors.Count; j++)
                result.Add(actors[j]);
        }
        return result;
    }

    /// <summary>
    /// Count actors matching the given side and liveness flags, optionally restricted to
    /// one <paramref name="actorType"/>.
    /// </summary>
    public static int ActorCount(bool playerActors, bool enemyActors, bool alive, bool dead, ActorType? actorType = null)
        => Manager.GetActorCount(playerActors, enemyActors, alive, dead, Optional(actorType));

    /// <summary>The number of dead enemy actors, optionally restricted to one <paramref name="faction"/>.</summary>
    public static int DeadEnemyCount(FactionType? faction = null) => Manager.GetDeadEnemyCount(Optional(faction));

    /// <summary>The total enemy actor count, optionally restricted to one <paramref name="faction"/>.</summary>
    public static int TotalEnemyCount(FactionType? faction = null) => Manager.GetTotalEnemyCount(Optional(faction));

    /// <summary>The number of dead actors of <paramref name="type"/>.</summary>
    public static int DeadCount(ActorType type) => Manager.GetDeadCount(type);

    /// <summary>The number of destroyed structures of <paramref name="type"/>.</summary>
    public static int DeadStructureCount(StructureType type) => Manager.GetDeadCount(type);

    private static Il2CppSystem.Nullable<T> Optional<T>(T? value) where T : struct
        => value.HasValue ? new Il2CppSystem.Nullable<T>(value.Value) : new Il2CppSystem.Nullable<T>();
}
