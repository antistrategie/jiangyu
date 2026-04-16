# Legacy Support-Type Spot-Check: PrefabAttachment

Date: 2026-04-15

## Goal

Do a structural validation pass on `Menace.Tactical.PrefabAttachment`, an embedded array element type used under `EntityTemplate.AttachedPrefabs`, using Jiangyu-native template inspection on templates with populated arrays.

## Why This Type

`PrefabAttachment` is the next target because:

- it is the first array element type to be structurally validated (previous passes covered direct nested objects only)
- it directly touches the "template -> visual/prefab linkage" questions Jiangyu will care about for future prefab cloning and redirection work
- the legacy schema defines it in `embedded_classes`, so there is concrete field-level legacy evidence to compare against
- it is a relatively sparse type in practice: only 2 out of 16+ inspected EntityTemplates had populated `AttachedPrefabs` arrays, making these two samples the main evidence

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `EntityTemplate`
  - `player_vehicle.modular_ifv` — `AttachedPrefabs` count `2` (both with real prefab references)
  - `player_vehicle.generic_heavy_attack_walker` — `AttachedPrefabs` count `1` (null prefab reference)

Additional templates checked with count `0`: `player_squad.darby`, `enemy.pirate_scavengers`, `building_military_1x1_bunker`, `enemy.pirate_vehicle.chaingun_guntruck`, `enemy.vehicle_rogue_army_heavy_tank`, `player_vehicle.cveh_heavy_tank`, `player_vehicle.modular_walker_medium`, `player_vehicle.modular_piratetruck`, `enemy.pirate_vehicle.rocket_guntruck`, `enemy.vehicle_rogue_army_auto_autocannon_walker`, `enemy.vehicle_rogue_army_auto_laser_walker`, `enemy.vehicle_rogue_army_mercenary_medium_walker`, `player_vehicle.modular_walker_light`, `player_vehicle.modular_atv`, `player_vehicle.modular_light_troop_carrier`, `bunker`.

## Method

For each sample:

1. run `jiangyu templates inspect`
2. navigate to `m_Structure` → `AttachedPrefabs`
3. inspect the array `elements` for each populated entry
4. record the field set with kinds and type names
5. compare across Jiangyu samples for stability
6. compare against legacy `embedded_classes.PrefabAttachment`

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_vehicle.modular_ifv
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name player_vehicle.generic_heavy_attack_walker
```

## Results

### Array container

Both samples share the same array type declaration:

- `name = "AttachedPrefabs"`
- `kind = "array"`
- `fieldTypeName = "Menace.Tactical.PrefabAttachment[]"`

### Element shape

Each element in both samples has:

- `fieldTypeName = "PrefabAttachment"`
- 3 fields, stable across all 3 observed elements:

| Field | Kind | Type |
|---|---|---|
| `IsLight` | bool | `Boolean` |
| `AttachmentPointName` | string | `String` |
| `Prefab` | reference | `UnityEngine.GameObject` |

### Per-element values

**IFV element 0:**

- `IsLight = true`
- `AttachmentPointName = "bone_body"`
- `Prefab` → `apc_headlight_container_left` (GameObject, pathId 10065)

**IFV element 1:**

- `IsLight = true`
- `AttachmentPointName = "bone_body"`
- `Prefab` → `apc_headlight_container_right` (GameObject, pathId 10067)

**Walker element 0:**

- `IsLight = true`
- `AttachmentPointName = "mount02"`
- `Prefab` → null reference (pathId 0)

Values are meaningfully distinct: the IFV attaches two headlight prefabs to `bone_body`, while the walker has a single null-reference entry on `mount02`.

### Jiangyu-vs-legacy comparison

Legacy `embedded_classes.PrefabAttachment`:

```json
{
  "base_class": "2763",
  "fields": [
    { "name": "IsLight", "type": "bool", "offset": "0x10", "category": "primitive" },
    { "name": "AttachmentPointName", "type": "string", "offset": "0x18", "category": "string" },
    { "name": "Prefab", "type": "GameObject", "offset": "0x20", "category": "unity_asset" }
  ]
}
```

Comparison:

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `IsLight` | bool / Boolean | bool | **matches** |
| `AttachmentPointName` | string / String | string | **matches** |
| `Prefab` | reference / UnityEngine.GameObject | GameObject / unity_asset | **matches** |

All 3 fields match in name, type, and order.

The legacy schema also records `base_class: "2763"` — this is a managed base class identifier from the decompiled metadata, not a serialized field. Its absence from Jiangyu's output is expected (Jiangyu surfaces the serialized contract, not managed type hierarchy metadata).

Classification: **legacy broader than serialized contract** — the only legacy-only detail (`base_class`) is managed metadata, not a serialized field. The serialized field set itself is an exact match.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialized `PrefabAttachment` contract from live game data
- the shape is stable across all 3 observed array elements in 2 templates
- Jiangyu correctly surfaces `PrefabAttachment[]` array elements with their individual field values
- the legacy `embedded_classes` definition is an exact match at the serialized field level
- `PrefabAttachment` is the first validated array element type — previous passes covered only direct nested objects

What this does **not** validate:

- runtime behaviour (how the game processes attached prefabs, bone attachment logic, light registration)
- the managed base class layout referenced by legacy `base_class: "2763"`
- memory offsets recorded in the legacy definition
- why most EntityTemplates have empty `AttachedPrefabs` (could be editor-populated only for specific vehicle variants, or could indicate the attachment system is sparsely used)
- whether the null reference in the walker is intentional data or a broken asset

## Conclusion

This is a successful structural validation pass for the first array element type.

The main results are:

- `PrefabAttachment` is a compact 3-field embedded type with an exact serialized match to the legacy definition
- the field set is stable across all observed elements
- this pass proves that Jiangyu's template inspection correctly surfaces populated array element structures, not just direct nested objects
- the type is sparse in practice (2 of 16+ templates inspected had populated arrays), which limits sample diversity but does not invalidate the structural finding

## Next Step

Good next targets:

1. `SkillTemplate` nested support types — 501 instances, likely rich structure, not blocked by empty arrays
2. Other array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
3. WeaponTemplate deeper nested types beyond the already-validated `LocalizedLine`/`LocalizedMultiLine`/`OperationResources`
