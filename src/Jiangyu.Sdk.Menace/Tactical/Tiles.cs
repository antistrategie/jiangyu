using System.Collections.Generic;
using Il2CppMenace.Tactical;
using UnityEngine;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Tile and map queries. Per-tile reads and mutations (X, Z, Actor, IsBlocked, ...) and
/// the single-overload map accessors are generated from the verb manifest into this
/// partial class. The members below pin a specific overload, hide an out-parameter,
/// supply a constant argument, read a property, compose two calls, or flatten an
/// IL2CPP list the generator cannot model.
/// </summary>
public static partial class Tiles
{
    /// <summary>The tile at grid coordinate (<paramref name="x"/>, <paramref name="z"/>), or null when off-map.</summary>
    public static Tile At(int x, int z) => TacticalManager.Get().GetMap().GetTile(x, z);

    /// <summary>The tile under a world-space position, or null when off-map.</summary>
    public static Tile At(Vector3 pos) => TacticalManager.Get().GetMap().GetTileAtPos(pos);

    /// <summary>Whether grid coordinate (<paramref name="x"/>, <paramref name="z"/>) lies within the map's bounds.</summary>
    // The two-argument IsInBounds is the instance member on BaseMap. Map's own
    // static overloads take the map's width and height as trailing arguments.
    public static bool InBounds(int x, int z) => TacticalManager.Get().GetMap().IsInBounds(x, z);

    /// <summary>
    /// The terrain elevation at world-space <paramref name="pos"/>. Raycasting reads
    /// through terrain the heightmap alone does not describe, at the cost of a physics
    /// query per call: pass false to sample the heightmap only.
    /// </summary>
    public static float ElevationAt(Vector3 pos, bool raycast = true)
        => TacticalManager.Get().GetMap().GetElevation(pos, raycast);

    /// <summary>The map's maximum possible terrain elevation.</summary>
    public static float TerrainHeight() => TacticalManager.Get().GetMap().MaxPossibleTerrainY;

    /// <summary>The map's tile count along X.</summary>
    public static int MapSizeX() => TacticalManager.Get().GetMap().Width;

    /// <summary>The map's tile count along Z.</summary>
    public static int MapSizeZ() => TacticalManager.Get().GetMap().Height;

    /// <summary>Whether the given world-space position lies on a valid tile of the map.</summary>
    public static bool IsValidPosition(Vector3 pos)
    {
        var map = TacticalManager.Get().GetMap();
        var tilePos = map.WorldToTilePos(pos);
        return map.IsValidTile(tilePos.x, tilePos.y);
    }

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
