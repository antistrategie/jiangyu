# Legacy Support-Type Spot-Check: EntityProperties

Date: 2026-04-15

## Goal

Do a structural validation pass on `Menace.Tactical.EntityProperties`, the main gameplay-stat container nested inside every `EntityTemplate`, using Jiangyu-native template inspection across diverse entity categories.

## Why This Type

`EntityProperties` is the best next target because:

- it appears inside every `EntityTemplate` as the `Properties` field
- it is structurally the richest unvalidated nested support type (102 serialized fields)
- it contains multiple nested kinds: numeric stats (int/float), enums, embedded `ID` objects, and a reference
- the legacy schema references it by name but does not define its internal field layout — only an `EntityPropertyType` runtime accessor enum (72 values) provides field-level legacy evidence

The preferred first target `Menace.Strategy.OperationResources` was confirmed too trivial: a single-field struct (`m_Supplies: Int32`) appearing as `WeaponTemplate.DeployCosts`. Its Jiangyu-observed shape matches the legacy struct definition exactly, but the pass would only prove "Jiangyu handles embedded single-field numeric structs correctly."

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `EntityTemplate`
  - `player_squad.darby` — player infantry squad member
  - `enemy.pirate_scavengers` — enemy combatant
  - `building_military_1x1_bunker` — military structure
  - `player_vehicle.modular_ifv` — player vehicle

These span four distinct entity categories: infantry, enemy, structure, vehicle.

## Method

For each sample:

1. run `jiangyu templates inspect`
2. navigate to `m_Structure` → `Properties`
3. record the full nested field set with kinds and type names
4. compare across all four Jiangyu samples for stability
5. compare against legacy `EntityPropertyType` enum (the only field-level legacy evidence)
6. classify any mismatches

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_squad.darby
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.pirate_scavengers
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name building_military_1x1_bunker
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_vehicle.modular_ifv
```

## Results

### Jiangyu-vs-Jiangyu: field set stability

All four samples produced an identical `EntityProperties` field set:

- `fieldTypeName = "Menace.Tactical.EntityProperties"`
- **102 fields** in all four samples
- identical field names, field order, kinds, and type names across all four

The struct carries distinct values per entity category, confirming it is real populated data:

| Field | darby | pirate | bunker_mil | ifv |
|---|---|---|---|---|
| MaxElements | 5 | 8 | 1 | 1 |
| HitpointsPerElement | 10 | 10 | 270 | 350 |
| Armor | 0 | 0 | 120 | 150 |
| ActionPoints | 100 | 100 | 10 | 100 |
| Accuracy | 70 | 45 | 60 | 70 |
| Vision | 9 | 9 | 8 | 8 |

Enum values also vary meaningfully — e.g. the IFV has `Flags=96` (`ImmuneToSuppression | ImmuneToIndirectSuppression`), while infantry and building have `Flags=0`.

### Field kind breakdown

| Kind | Count |
|---|---|
| float | 57 |
| int | 30 |
| object (ID) | 9 |
| enum | 5 |
| reference | 1 |

### Nested type shapes

**ID** (9 occurrences — all sound fields):

- `bankId: Int32`
- `itemId: Int32`

Matches the legacy `ID` struct definition (`size_bytes: 8`, fields `bankId` at `0x0` and `itemId` at `0x4`).

**Enum types** (5 — all present in legacy schema):

- `Menace.Tactical.MoraleEvent` — bitmask, observed value `2147483647` (0x7FFFFFFF = all flags)
- `Menace.Tactical.EntityFlags` — bitmask, observed values `0` and `96`
- `Menace.Tactical.RagdollHitArea` — bitmask
- `Menace.Tactical.FatalityType` — sequential
- `Menace.Tactical.DamageVisualizationType` — sequential

**Reference** (1):

- `SoundOnMovementStepOverrides2: Menace.Tactical.SurfaceSoundsTemplate`

### Jiangyu-vs-legacy: cross-reference

The only field-level legacy evidence is the `EntityPropertyType` enum (72 values), which indexes EntityProperties fields by name for the `ChangeProperty` effect handler system. The legacy schema references `EntityProperties` as a type but does not define its internal field layout.

Comparison:

- **All 72 legacy enum values exist in Jiangyu's EntityProperties** — zero missing legacy fields
- **30 Jiangyu fields are not in the legacy enum**

### Classification of 30 Jiangyu-only fields

**Enum/flag fields (5) — legacy enum narrower than serialized contract:**

The legacy `EntityPropertyType` enum indexes only numeric stat properties for the `ChangeProperty` runtime system. Categorical and flag fields are not part of that accessor pattern.

- `MoraleEvents` (enum: `Menace.Tactical.MoraleEvent`)
- `Flags` (enum: `Menace.Tactical.EntityFlags`)
- `DismemberArea` (enum: `Menace.Tactical.RagdollHitArea`)
- `FatalityType` (enum: `Menace.Tactical.FatalityType`)
- `DamageVisualizationType` (enum: `Menace.Tactical.DamageVisualizationType`)

**Sound/reference fields (10) — legacy enum narrower than serialized contract:**

Object and reference fields are not numeric stats and would not be indexed by the property-modification system.

- `SoundOnMovementStart` (ID)
- `SoundOnMovementStop` (ID)
- `SoundOnMovementStep` (ID)
- `SoundOnMovementSymbolic` (ID)
- `SoundOnArmorHit` (ID)
- `SoundOnHitpointsHit` (ID)
- `SoundOnHitpointsHitFemale` (ID)
- `SoundOnDeath` (ID)
- `SoundOnDeathFemale` (ID)
- `SoundOnMovementStepOverrides2` (reference: `SurfaceSoundsTemplate`)

**Additional numeric fields (15) — legacy enum narrower than serialized contract:**

These are real serialized int/float fields that the legacy `EntityPropertyType` enum does not cover. They could be fields added after the legacy schema was frozen, or fields the `ChangeProperty` system intentionally did not expose.

- `ArmorSide` (int)
- `ArmorBack` (int)
- `ArmorDurabilityPerElementMult` (float)
- `AdditionalAttackCost` (int)
- `AttackCostMult` (float)
- `AdditionalMalfunctionChance` (int)
- `LowestModifiedMovementCosts` (int)
- `ArmorPenetrationDropoffAOE` (float)
- `DamageDropoffAOE` (float)
- `DamageToArmorDurabilityDropoffAOE` (float)
- `SuppressionDealtDropoffAOE` (float)
- `DamagePctCurrentHitpoints` (float)
- `DamagePctCurrentHitpointsMin` (float)
- `DamagePctMaxHitpoints` (float)
- `DamagePctMaxHitpointsMin` (float)

No fields in this group suggest a serialization mismatch or Jiangyu extraction failure. The pattern is consistent: the legacy enum is a runtime property accessor, not a full field inventory.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialized `EntityProperties` contract from live game data
- the shape is completely stable across four meaningfully different entity categories (infantry, enemy, structure, vehicle)
- all 72 legacy `EntityPropertyType` enum values map to real Jiangyu-observed fields
- the 30 Jiangyu-only fields are structurally explained: 15 are non-numeric kinds (enums, objects, references) that the legacy enum was never designed to cover, and 15 are numeric fields the legacy enum does not index
- the nested `ID` struct shape (`bankId`, `itemId`) matches the legacy struct definition
- all 5 enum types observed in EntityProperties exist in the legacy schema's enum definitions

What this does **not** validate:

- runtime behaviour or formulas for any EntityProperties field
- field semantics beyond name/type (e.g. whether `DamagePctMaxHitpoints` actually does percent-based damage)
- the full managed inheritance/base layout for EntityProperties
- memory offsets referenced in legacy effect handler descriptions
- whether the 15 numeric Jiangyu-only fields were added after the legacy schema or intentionally excluded from the `ChangeProperty` system

## Conclusion

This is a successful large-scale nested support-type structural validation pass.

The main results are:

- `EntityProperties` is Jiangyu's most structurally rich validated nested type so far (102 fields, stable across 4 entity categories)
- the legacy `EntityPropertyType` enum is best understood as a **runtime accessor index for modifiable numeric stats**, not as a complete field inventory — it covers 72 of 102 serialized fields
- every mismatch is classifiable as **legacy enum narrower than serialized contract** — no mismatches suggest a serialized discrepancy or a Jiangyu extraction failure
- the nested `ID` struct and all 5 enum types independently match their legacy definitions

## Also validated: OperationResources (trivial)

As a side result, `Menace.Strategy.OperationResources` was confirmed too trivial for a standalone pass but its observed shape matches the legacy definition exactly:

- Jiangyu: `DeployCosts` field under `WeaponTemplate`, `fieldTypeName = "Menace.Strategy.OperationResources"`, single nested field `m_Supplies: Int32`
- Legacy: `OperationResources` struct, `size_bytes: 4`, single field `m_Supplies: int` at offset `0x0`
- Stable across `specialweapon.generic_designated_marksman_rifle_tier1` and `specialweapon.rocket_launcher_tier1_pal`

## Next Step

The EntityTemplate top-level field set and its three main nested support types (`LocalizedLine`/`LocalizedMultiLine`, `RoleData`, `EntityProperties`) are now structurally validated.

Good next targets:

1. `Menace.Tactical.PrefabAttachment` — once a sample with non-empty `AttachedPrefabs` is identified (all four current samples had empty arrays)
2. Array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — same blocker: need samples with populated arrays
3. `WeaponTemplate`'s remaining unvalidated fields and nested types beyond the already-checked `LocalizedLine`/`LocalizedMultiLine`/`OperationResources`
4. A different template type entirely (e.g. `SkillTemplate` with 501 instances, likely rich nested structure)
