# Array Element Contracts

Verified inline embedded array-element types. These are struct-like objects whose data lives
directly inside the parent template's serialised blob, as elements of a `List<T>` or `T[]`
array field.

This is structurally distinct from reference arrays, where each element is a PPtr to a
separate MonoBehaviour asset. See [polymorphic reference arrays](polymorphic-reference-arrays.md)
for the reference-array pattern.

## PrefabAttachment

`Menace.Tactical.PrefabAttachment` — embedded array element under `EntityTemplate.AttachedPrefabs`.

### Fields

| Field | Kind | Type |
|---|---|---|
| `IsLight` | bool | `Boolean` |
| `AttachmentPointName` | string | `String` |
| `Prefab` | reference | `UnityEngine.GameObject` |

3 fields, stable across all 3 observed elements in 2 templates. Exact match to legacy
`embedded_classes` definition in field names, types, and order.

### Observations

- sparse in practice: 2 of 16+ inspected EntityTemplates had populated arrays
- populated on vehicle entities (IFV headlight containers, walker mount points)
- the `Prefab` reference can be null (observed on `player_vehicle.generic_heavy_attack_walker`)
- `AttachmentPointName` references bone names on the entity's skeleton (`bone_body`, `mount02`)

### Samples

- `player_vehicle.modular_ifv` — 2 elements (headlight containers on `bone_body`)
- `player_vehicle.generic_heavy_attack_walker` — 1 element (null prefab on `mount02`)

## EntityLootEntry

`Menace.Tactical.EntityLootEntry` — embedded array element under `EntityTemplate.Loot`.

### Fields

| Field | Kind | Type |
|---|---|---|
| `Item` | reference | `Menace.Items.BaseItemTemplate` |
| `Count` | int | `Int32` |
| `OverrideDefaultDropChance` | bool | `Boolean` |
| `DropChance` | int | `Int32` |

4 fields, stable across all 10 observed elements in 3 templates. Exact match to legacy
`embedded_classes` definition in field names, types, and order.

### Observations

- populated on enemy entities (pirate, alien, construct factions)
- `Item` references span armour, weapons, accessories, and commodities via polymorphic
  `BaseItemTemplate` PPtr (ArmorTemplate, WeaponTemplate, AccessoryTemplate observed)
- `OverrideDefaultDropChance` takes both `true` and `false` values
- `DropChance` ranges from 0 to 100
- `Count` is 1 in all observed samples
- player entities and buildings have empty `Loot` arrays

### Samples

- `enemy.pirate_boarding_commandos` — 4 elements (armour, weapon, 2 accessories)
- `enemy.alien_01_big_warrior_queen` — 4 elements (alien commodities, varied drop chances)
- `enemy.construct_soldier_tier1` — 2 elements (construct commodities)

## SkillOnSurfaceDefinition

`Menace.Tactical.Skills.SkillOnSurfaceDefinition` — embedded array element under
`SkillTemplate.ImpactOnSurface`.

### Fields

| Field | Kind | Type |
|---|---|---|
| `ImpactEffect` | reference | `UnityEngine.GameObject` |
| `Decals` | reference | `Menace.Tactical.DecalCollection` |
| `DecalChance` | int | `Int32` |
| `RicochetChance` | int | `Int32` |
| `SoundOnRicochet` | object | `ID` |
| `SoundOnImpact` | object | `ID` |

6 fields, stable across all 24 observed elements in 3 templates. Exact match to legacy
`embedded_classes` definition in field names, types, and order.

The legacy schema categorises `SoundOnRicochet` and `SoundOnImpact` as `enum`, but they are
`ID` structs (`bankId: Int32`, `itemId: Int32`). Jiangyu correctly surfaces them as `object`.
This is a legacy labelling error, not a structural mismatch.

### Observations

- all inspected SkillTemplate instances have exactly 14 elements, suggesting the array is
  keyed by a fixed surface type index
- combat skills have populated references (distinct effects per stone, metal, sand, earth, snow)
- non-combat skills (utility, passive) have 14 structurally identical elements with null
  references and zero values
- the structure is stable regardless of whether the data is populated

### Samples

- `active.fire_assault_rifle_tier1_556` — 14 elements (populated with distinct surface effects)
- `active.change_plates` — 14 elements (all null/zero)
- `passive.ammo_armor_piercing` — 14 elements (all null/zero)

## Validation method

Each type was validated by inspecting multiple Jiangyu-native samples using
`jiangyu templates inspect`, comparing field sets across all observed array elements for
stability, and cross-referencing against the legacy `embedded_classes` definitions. All three
types show exact serialised-field matches to legacy.

## Investigation notes

- `legacy/2026-04-15-prefabattachment-support-type-spot-check.md`
- `legacy/2026-04-15-entitytemplate-array-element-types-spot-check.md`
- `legacy/2026-04-15-skillonsurfacedefinition-support-type-spot-check.md`
