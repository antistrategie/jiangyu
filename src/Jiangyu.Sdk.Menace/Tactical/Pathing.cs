using System.Collections.Generic;
using Il2CppMenace.Tactical;
using UnityEngine;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Read-only path queries. Hides the request/return bracket the pathfinding manager
/// requires and copies the result into a managed list.
/// </summary>
public static partial class Pathing
{
    /// <summary>
    /// The path <paramref name="mover"/> would take from <paramref name="from"/> to
    /// <paramref name="dest"/>, as world-space waypoints. Empty when no path is found.
    /// </summary>
    public static IReadOnlyList<Vector3> To(Actor mover, Tile from, Tile dest)
    {
        var result = new List<Vector3>();
        var mgr = PathfindingManager.Get();
        var process = mgr.RequestProcess();
        try
        {
            var raw = new Il2CppSystem.Collections.Generic.List<Vector3>();
            if (process.FindPath(from, dest, mover, raw, default))
                for (var i = 0; i < raw.Count; i++)
                    result.Add(raw[i]);
        }
        finally
        {
            mgr.ReturnProcess(process);
        }
        return result;
    }
}
