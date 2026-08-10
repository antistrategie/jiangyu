# Structural Spot-Check: EntityTemplate and SkillTemplate (2026-08-10 game update re-audit)

Date: 2026-08-10

## Goal

Re-verify the two template types flagged by `templates baseline audit`, so the committed
structural baseline can be regenerated, and rebind the verb surface the same update moved.

## Why These Types

The audit flagged exactly two CHANGED types:

- `EntityTemplate`: `+ OccupyWholeBoundingBox`
- `SkillTemplate`: `- TargetFaction`

## Samples

The curated baseline samples for both types:

- `EntityTemplate`: `player_squad.darby`, `enemy.pirate_scavengers`
- `SkillTemplate`: `active.change_plates`, `passive.ammo_armor_piercing`

## Method

`jiangyu templates index` (8847 instances across 251 template types), then
`jiangyu templates inspect --type <T> --name <sample>` per sample, reading the field
entry directly out of the inspector output.

## Results

`EntityTemplate.OccupyWholeBoundingBox` is present on both samples:

```
"name": "OccupyWholeBoundingBox",
"kind": "bool",
"fieldTypeName": "Boolean",
"value": false
```

`SkillTemplate.TargetFaction` is absent from both samples. Its recorded shape in the
previous baseline is `enum Menace.Tactical.FactionType`. The sibling targeting fields
remain: `IsTargeted`, `TargetingCursor`, and `TargetsAllowed`
(`enum Menace.Tactical.Skills.SkillTarget`, `EmptyTile` on `active.change_plates`).

## Interpretation

Both changes are membership only. `bool` is an already-handled kind, so the addition
needs no parser work, and the removal drops a field with no replacement on the same
axis: `TargetsAllowed` selects what a skill may target (tile, actor), not which faction,
so it is not a rename of `TargetFaction`. Skill faction targeting is no longer part of
the serialised template contract.

Neither field is referenced anywhere in Jiangyu or in mod content.

## Related API drift fixed in the same pass

`Map` now derives from a generic `BaseMap<Tile>`. Six bindings moved:

- `IsInBounds(int, int)` is the instance member on `BaseMap`. `Map` keeps static
  overloads that take the map's width and height as trailing arguments. `Tiles.InBounds`
  routes through `GetMap()`.
- `GetElevation(float, float)` becomes `GetElevation(Vector3, bool _performRaycast)`.
  `Tiles.ElevationAt` takes a world-space `Vector3` and exposes the raycast flag,
  defaulting to true.
- `GetTerrainHeight()` becomes the property `MaxPossibleTerrainY`.
- `GetSizeX()` and `GetSizeZ()` become the properties `BaseMap.Width` and
  `BaseMap.Height`.
- `IsValidPosition(Vector3)` is removed. `Tiles.IsValidPosition` composes
  `WorldToTilePos` and `IsValidTile`.

The four property-backed and multi-call cases leave `manifests/tiles.json` for the
hand-written partial in `Tactical/Tiles.cs`, since the manifest schema binds methods
only. `Tactical.Tiles.g.cs` regenerates to 49 verbs. The modder-facing verb names are
unchanged, and `ElevationAt` is the one changed signature.

## Surface baselines

Both regenerated. All 48 hooks still resolve and `HookCatalog.g.cs` is byte-identical.

The verb surface baseline reports 4 of 23 `Map` removals as artefacts of the new generic
base: `IsInBounds(int, int)`, `IsInBounds(Vector2Int)`, `ClampToBounds(RectInt)` and
`Resize(int, int)` all still exist on `BaseMap` or `BaseMap<Tile>`. The baseline compares
declared members, so a base-class extraction reads as a removal.

Genuine removals on `Map`: `GetTerrainData`, `UpdateWalkability` (both overloads),
`UpdatePrimaryTextureMap`, `UpdateBlockedTiles`, `UpdateGpuiGrass`, `Clear`,
`ClearDetailsAt`, `ClearDetailsOnTile`, `MakeMountainsBlockLineOfSight`, `SetTerrainSize`,
plus signature changes on `UpdateElevation` and `GenerateMap`.

`TacticalManager` drops `GetDecals()`, `GetPathfinding()` and `GetTerrain()`. Nothing in
Jiangyu binds them, and all three targets stay reachable:

- pathfinding: `PathfindingManager.Get()`, the static singleton accessor matching
  `TacticalManager.Get()`. `TacticalManager.m_PathfindingManager` is a public property
  reaching the same instance.
- decals: the property `TacticalManager.Decals`, a method-to-property conversion.
- terrain: `Map.GetTerrain()`. No terrain accessor remains on `TacticalManager`.

`Squaddies` gains `AreCorruptedByCheats()` and drops `GetNextSquaddieId()`, so the
corruption check's alive-count input has no public accessor.

Candidate new verbs and hooks: `Actor.GetMaster()`,
`TacticalManager.GetActorByID(int)`, `Tile.SetElevation`, `Tile.SetWorldPos`,
`Tile.GetPos`, `Tile.GetPosWithoutElevation`, `Map.WorldToTilePos`,
`Map.TileToWorldPos`, `Map.TileCenterToWorldPos`, `Map.GetTerrain`, `Map.SetTerrain`,
`StrategyState.CalcPromotionCostMult`, `OperationsManager.FixNoAvailableOperations`.

## Conclusion

All three committed baselines match the current game build. `templates baseline audit`
reports no drift, and both codegen generators report no drift on the bound game types.

## Next Step

The candidate list above is the pool for new verbs. `Map.WorldToTilePos` and
`Map.TileToWorldPos` are the strongest additions, being the world-to-grid conversions
`Tiles` currently makes callers derive.

Tactical movement work reaches pathfinding through `PathfindingManager.Get()`, which
`RequestProcess()` hangs off. No verb binds that surface.
