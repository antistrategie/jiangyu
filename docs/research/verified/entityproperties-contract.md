# EntityProperties Contract

Verified support contract for `Menace.Tactical.EntityProperties`, the main gameplay-stat
container nested inside every `EntityTemplate` as the `Properties` field.

## Shape

- **102 serialised fields**
- `fieldTypeName = "Menace.Tactical.EntityProperties"`
- stable across infantry, enemy, structure, and vehicle entity categories
- zero structural variation — all 4 inspected samples produce the identical field set

### Field kind breakdown

| Kind | Count |
|---|---|
| float | 57 |
| int | 30 |
| object (ID) | 9 |
| enum | 5 |
| reference | 1 |

### Nested types

**ID struct** (9 occurrences — all sound fields):

- `bankId: Int32`
- `itemId: Int32`

The `ID` shape was independently confirmed against the legacy struct definition. All 9
occurrences follow the same 2-field structure.

**Enum types** (5):

| Enum | Pattern |
|---|---|
| `Menace.Tactical.MoraleEvent` | bitmask (observed value `0x7FFFFFFF` = all flags) |
| `Menace.Tactical.EntityFlags` | bitmask (observed values `0` and `96`) |
| `Menace.Tactical.RagdollHitArea` | bitmask |
| `Menace.Tactical.FatalityType` | sequential |
| `Menace.Tactical.DamageVisualizationType` | sequential |

**Reference** (1):

- `SoundOnMovementStepOverrides2: Menace.Tactical.SurfaceSoundsTemplate`

### Value diversity

The struct carries distinct values per entity category, confirming it is real populated data:

| Field | Infantry | Enemy | Structure | Vehicle |
|---|---|---|---|---|
| MaxElements | 5 | 8 | 1 | 1 |
| HitpointsPerElement | 10 | 10 | 270 | 350 |
| Armor | 0 | 0 | 120 | 150 |
| ActionPoints | 100 | 100 | 10 | 100 |
| Accuracy | 70 | 45 | 60 | 70 |
| Vision | 9 | 9 | 8 | 8 |
| Flags | 0 | 0 | 0 | 96 (ImmuneToSuppression, ImmuneToIndirectSuppression) |

## Legacy cross-reference

The only field-level legacy evidence for EntityProperties is the `EntityPropertyType` runtime
enum (72 values), which indexes numeric stat properties for the `ChangeProperty` effect handler
system.

- All 72 legacy enum values map to real Jiangyu-observed fields — zero missing
- 30 Jiangyu-observed fields are not in the legacy enum

The 30 additional fields break down as:

| Category | Count | Explanation |
|---|---|---|
| Enum/flag fields | 5 | non-numeric kinds the legacy accessor was never designed to cover |
| Sound/reference fields | 10 | object and reference fields, not numeric stats |
| Additional numeric fields | 15 | real serialised int/float fields the legacy enum does not index |

No mismatch suggests a serialised discrepancy or extraction failure. The legacy enum is a
runtime accessor index for modifiable numeric stats, not a complete field inventory.

## Validation method

Inspected via `jiangyu templates inspect --type EntityTemplate --name <name>` on 4 samples:
`player_squad.darby`, `enemy.pirate_scavengers`, `building_military_1x1_bunker`,
`player_vehicle.modular_ifv`. Field stability confirmed by comparing the full 102-field set
across all samples.

## Scope and limits

This validates the serialised field set — which fields exist, their types, and their structural
stability. It does not validate:

- runtime behaviour or formulas for any field
- field semantics beyond name/type
- managed inheritance or base layout
- memory offsets
- whether the 15 additional numeric fields were added after the legacy schema or intentionally
  excluded from the `ChangeProperty` system

## Investigation notes

- `legacy/2026-04-15-entityproperties-support-type-spot-check.md` — primary validation pass
