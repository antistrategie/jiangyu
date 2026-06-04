using System.Collections.Generic;
using Il2CppMenace.Tactical;

namespace Jiangyu.Game;

/// <summary>
/// Reads and acquisition for the live Tactical layer. Every member is safe to
/// call from any context: outside a mission the manager-derived members return
/// null / empty / -1 rather than throwing, so a verb called from a strategy-layer
/// hook or a mistimed handler no-ops instead of crashing. Backed by the verified
/// call sequences in <c>docs/research/verified/tactical-game-api-verbs.md</c>.
/// </summary>
public static class Tactical
{
    /// <summary>Whether a Tactical mission is currently running.</summary>
    public static bool InMission => TacticalManager.IsMissionRunning();

    /// <summary>The tactical manager singleton, or null outside a mission.</summary>
    public static TacticalManager Manager => TacticalManager.Get();

    /// <summary>The live tactical map, or null outside a mission.</summary>
    public static Map Map
    {
        get
        {
            var tm = Manager;
            return tm != null ? tm.GetMap() : null;
        }
    }

    /// <summary>The actor whose turn it currently is, or null when none is active.</summary>
    public static Actor ActiveActor
    {
        get
        {
            var tm = Manager;
            return tm != null ? tm.m_ActiveActor : null;
        }
    }

    /// <summary>The current round number, or -1 outside a mission.</summary>
    public static int Round
    {
        get
        {
            var tm = Manager;
            return tm != null ? tm.GetRound() : -1;
        }
    }

    /// <summary>The tile at a map coordinate, or null if out of bounds / no map.</summary>
    public static Tile TileAt(int x, int z)
    {
        var map = Map;
        return map != null ? map.GetTile(x, z) : null;
    }

    /// <summary>
    /// Every actor on the field, optionally filtered to one faction. Flattens the
    /// per-faction actor lists (<see cref="TacticalManager.GetFactions"/> returns
    /// all faction slots, including empty ones). Empty outside a mission.
    /// </summary>
    public static IReadOnlyList<Actor> Actors(FactionType? faction = null)
    {
        var result = new List<Actor>();
        var tm = Manager;
        var factions = tm != null ? tm.GetFactions() : null;
        if (factions == null)
            return result;

        for (var i = 0; i < factions.Length; i++)
        {
            var f = factions[i];
            if (f == null)
                continue;
            if (faction.HasValue && f.GetFactionType() != faction.Value)
                continue;

            var actors = f.GetActors();
            if (actors == null)
                continue;
            for (var j = 0; j < actors.Count; j++)
                if (actors[j] != null)
                    result.Add(actors[j]);
        }
        return result;
    }
}
