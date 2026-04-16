# Legacy Support-Type Spot-Check: LocalizedLine and LocalizedMultiLine

Date: 2026-04-15

## Goal

Do the first nested support-type structural validation pass using Jiangyu-native template inspection, starting with a shared support type that appears under both `EntityTemplate` and `WeaponTemplate`.

## Why This Type

`Menace.Tools.LocalizedLine` and `Menace.Tools.LocalizedMultiLine` are a good first support-type target because:

- they appear directly inside both `EntityTemplate` and `WeaponTemplate`
- the old legacy research clearly treats them as real wrapper objects, not plain strings
- Jiangyu is already surfacing them as nested serialized structures rather than opaque blobs

Related legacy evidence:

- `schema.json` references `LocalizedLine`, `LocalizedMultiLine`, and `LocaState` throughout template definitions
- `docs/reverse-engineering/localization-system.md` in the old modkit describes `LocalizedLine` and `LocalizedMultiLine` as wrappers over a common localization base type
- `docs/reverse-engineering/localization-patterns.md` documents `m_LocaState`, `LocalizedLine`, and `LocalizedMultiLine` as core localization patterns across many template types

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `EntityTemplate`
  - `bunker`
  - `player_squad.darby`
- `WeaponTemplate`
  - `turret.construct_gunslinger_twin_heavy_auto_repeater`
  - `specialweapon.generic_designated_marksman_rifle_tier1`

## Method

For each sample:

1. run `jiangyu templates inspect`
2. inspect `m_Structure`
3. find nested localization fields:
   - `Title`
   - `ShortName`
   - `Description`
   - `m_LocaState`
4. compare Jiangyu-observed nested field shape against legacy expectations

This pass validates serialized structure only, not localization runtime behavior.

## Results

### LocalizedLine

Observed shape in all `Title` / `ShortName` occurrences checked:

- `fieldTypeName = "Menace.Tools.LocalizedLine"`
- nested fields:
  - `m_DefaultTranslation`
  - `m_Placeholders`

Observed examples:

- `EntityTemplate.bunker`
  - `Title`: `LocalizedLine`
- `EntityTemplate.player_squad.darby`
  - `Title`: `LocalizedLine`
- `WeaponTemplate.turret.construct_gunslinger_twin_heavy_auto_repeater`
  - `Title`: `LocalizedLine`
  - `ShortName`: `LocalizedLine`
- `WeaponTemplate.specialweapon.generic_designated_marksman_rifle_tier1`
  - `Title`: `LocalizedLine`
  - `ShortName`: `LocalizedLine`

### LocalizedMultiLine

Observed shape in all `Description` occurrences checked:

- `fieldTypeName = "Menace.Tools.LocalizedMultiLine"`
- nested fields:
  - `m_DefaultTranslation`
  - `m_Placeholders`

Observed examples:

- `EntityTemplate.bunker`
  - `Description`: `LocalizedMultiLine`
- `EntityTemplate.player_squad.darby`
  - `Description`: `LocalizedMultiLine`
- `WeaponTemplate.turret.construct_gunslinger_twin_heavy_auto_repeater`
  - `Description`: `LocalizedMultiLine`
- `WeaponTemplate.specialweapon.generic_designated_marksman_rifle_tier1`
  - `Description`: `LocalizedMultiLine`

### LocaState

Observed as:

- `fieldTypeName = "Menace.Tools.LocaState"`
- enum values observed in samples:
  - `1`
  - `2`

Legacy `schema.json` defines:

- `Unknown = 0`
- `DoNotTranslate = 1`
- `ReadyForTranslation = 2`
- `Translated = 3`
- `Any = 10`

So the observed values are consistent with the legacy enum definition.

## Interpretation

What this validates:

- Jiangyu is consistently surfacing `LocalizedLine` and `LocalizedMultiLine` as nested serialized wrapper objects, not plain strings
- across the sampled entity and weapon types, the current serialized field shape is stable:
  - `m_DefaultTranslation`
  - `m_Placeholders`
- Jiangyu's observed `LocaState` enum identity also aligns with the legacy enum definition

What this does **not** validate:

- the full managed base-class layout described by the old reverse-engineering docs
- whether additional managed-only fields exist but are not serialized
- runtime translation lookup behavior
- whether old offset/memory-layout claims are fully correct

## Conclusion

This is a successful first nested support-type structural validation pass.

The main result is:

- Jiangyu can independently reproduce the current serialized contract for `LocalizedLine` and `LocalizedMultiLine`
- the old modkit's localization research appears directionally correct about these being wrapper objects, but should still be treated as broader managed/runtime interpretation rather than exact serialized schema

## Next Step

Repeat the same style of nested support-type validation for another high-leverage shared support type observed under `EntityTemplate` / `WeaponTemplate`, preferably one that is structurally richer than the localization wrappers.
