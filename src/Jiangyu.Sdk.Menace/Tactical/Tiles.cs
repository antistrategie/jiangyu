using System.Collections.Generic;
using Il2CppMenace.Tactical;
using UnityEngine;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Tile and map queries. Per-tile reads and mutations (X, Z, Actor, IsBlocked, ...) and
/// the single-overload map accessors are generated from the verb manifest into this
/// partial class. The members below pin a specific overload, hide an out-parameter,
/// supply a constant argument, or flatten an IL2CPP list the generator cannot model.
/// </summary>
public static partial class Tiles
{
    /// <summary>The tile at grid coordinate (<paramref name="x"/>, <paramref name="z"/>), or null when off-map.</summary>
    public static Tile At(int x, int z) => TacticalManager.Get().GetMap().GetTile(x, z);

    /// <summary>The tile under a world-space position, or null when off-map.</summary>
    public static Tile At(Vector3 pos) => TacticalManager.Get().GetMap().GetTileAtPos(pos);

    /// <summary>Whether grid coordinate (<paramref name="x"/>, <paramref name="z"/>) lies within the map's bounds.</summary>
    public static bool InBounds(int x, int z) => Map.IsInBounds(x, z);

    /// <summary>The terrain elevation at grid coordinate (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public static float ElevationAt(float x, float z) => TacticalManager.Get().GetMap().GetElevation(x, z);

    /// <summary>The tile adjacent to <paramref name="tile"/> in <paramref name="dir"/>, or null at the map edge.</summary>
    public static Tile Next(Tile tile, Direction dir) => tile.GetNextTile(dir);

    /// <summary>The cover <paramref name="tile"/> provides facing <paramref name="dir"/>.</summary>
    public static CoverType Cover(Tile tile, Direction dir) => tile.GetCover(dir);

    /// <summary>The grid distance from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static int Distance(Tile from, Tile to) => from.GetDistanceTo(to);

    /// <summary>
    /// The tiles inside <paramref name="area"/>, copied into a managed list. Empty when the
    /// game returns no tiles.
    /// </summary>
    public static IReadOnlyList<Tile> Within(RectInt area)
    {
        var result = new List<Tile>();
        var raw = TacticalManager.Get().GetMap().GetTiles(area);
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }
}
