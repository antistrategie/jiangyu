# Legacy Support-Type Spot-Check: RoleData

Date: 2026-04-15

## Goal

Do a richer nested support-type structural validation pass using Jiangyu-native template inspection, starting with `Menace.Tactical.AI.Data.RoleData` as surfaced under `EntityTemplate.AIRole`.

## Why This Type

`RoleData` is a better next target than the already-validated localisation wrappers because:

- it appears in `EntityTemplate` as a named nested support object (`AIRole`)
- it is structurally richer than `LocalizedLine` / `LocalizedMultiLine`
- it is likely to matter later for tactical AI-related research, even if Jiangyu does not yet depend on its semantics

Related legacy evidence:

- the old schema records `EntityTemplate.AIRole` as type `RoleData`
- the old tactical AI reverse-engineering work treats role/weighting data as a real AI configuration surface

This pass validates serialised nested structure only, not tactical AI behaviour or formulas.

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `EntityTemplate`
  - `player_squad.darby`
  - `enemy.pirate_scavengers`

## Method

For each sample:

1. run `jiangyu templates inspect`
2. inspect `m_Structure`
3. find nested `AIRole`
4. compare Jiangyu-observed nested field shape across samples
5. compare the result against the limited legacy schema claim that `AIRole` is `RoleData`

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.pirate_scavengers
```

## Results

Observed in both samples:

- `AIRole`
  - `kind = "object"`
  - `fieldTypeName = "Menace.Tactical.AI.Data.RoleData"`

Observed nested field set in both samples:

- `TargetFriendlyFireValueMult`
- `UtilityScale`
- `UtilityThresholdScale`
- `SafetyScale`
- `DistanceScale`
- `FriendlyFirePenalty`
- `IsAllowedToEvadeEnemies`
- `AttemptToStayOutOfSight`
- `PeekInAndOutOfCover`
- `UseAoeAgainstSingleTargets`
- `Move`
- `InflictDamage`
- `InflictSuppression`
- `Stun`
- `AvoidOpponents`
- `ConsiderSurroundings`
- `CoverAgainstOpponents`
- `DistanceToCurrentTile`
- `ConsiderZones`
- `ThreatFromOpponents`
- `ExistingTileEffects`
- `IgnoreTileEffects`

Primitive kinds were also stable across both samples:

- scale/penalty weights surfaced as `float`
- toggles surfaced as `bool`
- `IgnoreTileEffects` surfaced as enum `Menace.Tactical.TileEffects.TileEffectType`

## Interpretation

What this validates:

- Jiangyu is consistently surfacing `AIRole` as a nested serialised `RoleData` object under `EntityTemplate`
- across the sampled entity types, the current serialised field set is stable
- the old schema was directionally correct that `AIRole` is backed by `RoleData`, even though it did not provide the nested serialised field layout directly

What this does **not** validate:

- the tactical AI formulas or how these values are consumed at runtime
- the full managed inheritance/base layout for `RoleData`
- whether additional managed-only fields exist but are not serialised
- whether old offset/memory-layout claims about AI settings are fully correct

## Conclusion

This is a successful richer nested support-type structural validation pass.

The main result is:

- Jiangyu can independently reproduce the current serialised `RoleData` contract exposed through `EntityTemplate.AIRole`
- the shape appears stable across at least two meaningfully different `EntityTemplate` samples

This gives Jiangyu a better foundation for future tactical AI research without overclaiming runtime semantics.

## Next Step

Repeat the same style of nested support-type validation for another high-leverage shared support type, preferably one that recurs broadly and is still richer than trivial engine structs.
