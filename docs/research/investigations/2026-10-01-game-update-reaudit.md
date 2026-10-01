# Structural Spot-Check: EntityTemplate, EntityProperties and SkillTemplate (2026-10-01 game update re-audit)

Date: 2026-10-01

## Goal

Re-verify the three types flagged by `templates baseline audit` after the MENACE v0.7.15
update, so the committed structural baseline can be regenerated, and record the verb, hook
and SDK surface the same update moved.

## Why These Types

The audit flagged exactly three CHANGED types:

- `EntityTemplate`: `+ DisableOutlines`, `- ElementsMax`, `- ElementsMin`
- `Menace.Tactical.EntityProperties`: `+ ArmorDamageSustainedMult`, `+ MinElements`
- `SkillTemplate`: `+ IgnoreLineOfSightCheckForAoE`

## Samples

The curated baseline samples from `validation/template-structure-baseline.sources.json`:

- `EntityTemplate`: `player_squad.darby`, `enemy.pirate_scavengers`
- `Menace.Tactical.EntityProperties` (support type, read through `EntityTemplate.Properties`):
  `player_squad.darby`, `enemy.pirate_scavengers`, `building_military_1x1_bunker`,
  `player_vehicle.modular_ifv`
- `SkillTemplate`: `active.change_plates`, `passive.ammo_armor_piercing`

## Method

`jiangyu templates index` (9446 instances across 270 template types), then
`jiangyu templates inspect --type <T> --name <sample>` per sample, reading each field entry
out of the inspector output under `m_Structure`, and under `m_Structure/Properties` for the
`EntityProperties` fields. The `EntityTemplate` inspection covers all four `EntityProperties`
samples.

## Results

`EntityTemplate.DisableOutlines` is present on all four entity samples:

```
"name": "DisableOutlines",
"kind": "bool",
"fieldTypeName": "Boolean",
"value": false
```

`EntityTemplate.ElementsMin` and `EntityTemplate.ElementsMax` are absent from all four
samples. Their recorded shape in the previous baseline is `int Int32` for both.

`EntityProperties.MinElements` is `int Int32`. `EntityProperties.ArmorDamageSustainedMult`
is `float Single`. `EntityProperties.MaxElements` was already in the previous baseline as
`int Int32` and is unchanged. Values per sample:

| Field | darby | pirate_scavengers | bunker | modular_ifv |
|---|---|---|---|---|
| MinElements | 5 | 6 | 1 | 1 |
| MaxElements | 5 | 8 | 1 | 1 |
| ArmorDamageSustainedMult | 1 | 1 | 1 | 1 |

`SkillTemplate.IgnoreLineOfSightCheckForAoE` is present on both skill samples:

```
"name": "IgnoreLineOfSightCheckForAoE",
"kind": "bool",
"fieldTypeName": "Boolean",
"value": false
```

## Interpretation

All changes are membership only. `bool`, `int` and `float` are already-handled kinds, so
no parser work is needed.

The element-count pair is a relocation, not a loss. The squad-size range now lives in
`EntityProperties` as `MinElements` and `MaxElements`, beside the other per-entity stats.
`MaxElements` was already there, and the new `MinElements` carries the lower bound the
removed `EntityTemplate.ElementsMin` held. The pirate sample's 6 to 8 range confirms the two
are distinct populated values, not a mirrored pair. KDL that set `ElementsMin` or
`ElementsMax` on an `EntityTemplate` now patches `Properties` with `MinElements` and
`MaxElements`.

`ArmorDamageSustainedMult` is the serialised counterpart of the `EntityPropertyType` member
of the same name that `site/reference/event-handlers.md` already lists, so `ChangeProperty`
handlers can address it.

No Jiangyu code, test, KDL sample or living doc references `ElementsMin`, `ElementsMax`,
`DisableOutlines` or `IgnoreLineOfSightCheckForAoE`. The only mentions of the removed pair
are in the dated investigation `2026-06-15-custom-unit-animation-feasibility.md`, which
records the contract as it stood then.

## Related API drift fixed in the same pass

- `Squaddies.OnAliveSquaddiesChanged(int)` is replaced by `OnSquaddiesChanged`, an argument
  free event that fires on any roster change, wounding included, and `GetAliveCount()` is
  gone. The `AliveSquaddiesChanged` hook now subscribes to `OnSquaddiesChanged`, reads the
  count from `m_AliveSquaddies`, and publishes only when that count changes.
- `Operation.GetLength()` is removed. `Operations.Length` moves from the verb manifest to
  `Strategy/Operations.cs` and reads the field `m_MissionCount`.
- `OperationsManager.OnOperationFinished` gains a `bool _cancel` parameter.
  `Operations.Finish(Operation, bool cancel = false)` moves to `Strategy/Operations.cs` and
  passes the flag through, and the `OperationFinished` hook payload gains `Cancelled`.
- `ItemContainer.TryUnequip` drops its `ItemEventFlags` argument. `Leaders.cs` calls the
  single-argument form.
- `Entity.IsPlayerControlled(bool)` is replaced by `IsPlayerOrPlayerAI()` in the
  diagnostics `MissionAutoWin`.
- `MissionPrepUIScreen.LaunchMission` is renamed `StartMission`, used by the diagnostics
  `NavDriver`.
- `Attack.DamageFilterCondition` no longer exists, so docs and comments use
  `AddSkill.Condition: ITacticalCondition` as the Odin-routed field example.
- `TextButton` hover now builds the native `.text-button-hover` child, a `Hover` overlay
  shown on pointer enter while the button is enabled, in place of `WireNativeHover`.
- The game's Unity version moves from 6000.0.72f1 to 6000.0.82f1. `BuildBundles.cs`
  `ExpectedUnityVersion`, the scaffolder message, `AGENTS.md`, `README.md` and the
  installation tutorial are bumped to match.

## Surface baselines

Both surface baselines were regenerated with `--update-surface` after the drift report was
reviewed. Among the recorded drift: `EndOperation()` becomes `EndOperation(bool)`,
`MissionDifficultyTemplate` becomes `MissionEnemyDifficultyTemplate` in the mission
generation signatures, `Squaddies.Kill(int)` becomes `SetKilled(int, EntityTemplate)` beside
a new `SetWounded(int)`, and `TacticalManager` gains `add_OnReachedFleeingThreshold`.

## Conclusion

All three committed baselines match the current game build. `templates baseline audit`
reports no drift after `templates baseline generate`. The structural baseline diff contains
only the six field changes above, the three field counts (`EntityProperties` 102 to 104,
`EntityTemplate` 112 to 111, `SkillTemplate` 124 to 125), the timestamp and the game
assembly hash.
