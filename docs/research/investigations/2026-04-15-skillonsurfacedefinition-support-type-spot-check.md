# Legacy Support-Type Spot-Check: SkillOnSurfaceDefinition

Date: 2026-04-15

## Goal

Do a structural validation pass on `Menace.Tactical.Skills.SkillOnSurfaceDefinition`, an embedded array element type used under `SkillTemplate.ImpactOnSurface`, using Jiangyu-native template inspection across three structurally diverse `SkillTemplate` categories.

## Why This Type

`SkillOnSurfaceDefinition` is the next target because:

- it is the first nested support type to be structurally validated under `SkillTemplate`, moving validation beyond `EntityTemplate`/`WeaponTemplate`
- it is a populated embedded array in all 3 inspected samples (count=14 each), so there is no empty-array blocker
- the legacy schema defines it in `embedded_classes` with 6 fields, providing concrete field-level legacy evidence
- it nests the already-validated `ID` struct and references both `UnityEngine.GameObject` and `Menace.Tactical.DecalCollection`, making it structurally richer than the previously validated `PrefabAttachment` (3 fields)

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `SkillTemplate`
  - `active.fire_assault_rifle_tier1_556` — standard projectile combat skill, `ImpactOnSurface` count `14` (populated with distinct impact effects per surface)
  - `active.change_plates` — non-fire active utility skill, `ImpactOnSurface` count `14` (all null references — no surface effects)
  - `passive.ammo_armor_piercing` — passive buff skill, `ImpactOnSurface` count `14` (all null references — no surface effects)

The fire skill has populated references (stone, metal, sand, earth, snow impacts), while the utility and passive skills have 14 structurally identical elements with null/zero values. This is a meaningful value-level divergence despite identical structure.

## Method

For each sample:

1. run `jiangyu templates inspect --type SkillTemplate --name <name>`
2. navigate to `m_Structure` → `ImpactOnSurface`
3. inspect the array `elements` for each visible entry (maxArraySampleLength=8)
4. record the field set with kinds and type names
5. compare across Jiangyu samples for stability
6. compare against legacy `embedded_classes.SkillOnSurfaceDefinition` from `../MenaceAssetPacker/generated/schema.json`

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.fire_assault_rifle_tier1_556
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.change_plates
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name passive.ammo_armor_piercing
```

## Results

### Array container

All 3 samples share the same array type declaration:

- `name = "ImpactOnSurface"`
- `kind = "array"`
- `fieldTypeName = "System.Collections.Generic.List`1<Menace.Tactical.Skills.SkillOnSurfaceDefinition>"`

All 3 have `count = 14` elements.

### Element shape

Every visible element across all 3 samples (24 elements total: 8 per sample) shares a single structural signature:

- `fieldTypeName = "SkillOnSurfaceDefinition"`
- 6 fields, stable across all 24 observed elements:

| Field | Kind | Type |
|---|---|---|
| `ImpactEffect` | reference | `UnityEngine.GameObject` |
| `Decals` | reference | `Menace.Tactical.DecalCollection` |
| `DecalChance` | int | `Int32` |
| `RicochetChance` | int | `Int32` |
| `SoundOnRicochet` | object | `ID` |
| `SoundOnImpact` | object | `ID` |

`SoundOnRicochet` and `SoundOnImpact` are nested `ID` objects. These appear as `truncated=true` in the array elements due to the inspection depth limit (maxDepth=4), but the `ID` shape (`bankId: Int32`, `itemId: Int32`) is already validated from prior passes and is visible non-truncated in the same template's top-level `DefaultSoundOnRicochet` and `DefaultSoundOnImpact` fields.

### Per-element values (representative)

**Fire skill element 0 (stone surface):**

- `ImpactEffect` → `impact_bullet_stone_small_01` (GameObject, pathId 33926)
- `Decals` → `impacts_bullets_concrete` (MonoBehaviour, pathId 111726)
- `DecalChance = 100`
- `RicochetChance = 40`
- `SoundOnRicochet` → ID (truncated)
- `SoundOnImpact` → ID (truncated)

**Utility/passive element 0 (empty surface):**

- `ImpactEffect` → null (pathId 0)
- `Decals` → null (pathId 0)
- `DecalChance = 0`
- `RicochetChance = 0`
- `SoundOnRicochet` → ID (truncated)
- `SoundOnImpact` → ID (truncated)

The fire skill has meaningfully populated surface definitions (distinct effects per stone, metal, sand, earth, snow, etc.), while the non-combat skills carry 14 structurally identical but value-empty elements. All share the same 6-field structure regardless of whether the data is populated.

### Jiangyu-vs-legacy comparison

Legacy `embedded_classes.SkillOnSurfaceDefinition` from `../MenaceAssetPacker/generated/schema.json`:

```json
{
  "base_class": "3259",
  "fields": [
    { "name": "ImpactEffect", "type": "GameObject", "offset": "0x10", "category": "unity_asset" },
    { "name": "Decals", "type": "DecalCollection", "offset": "0x18", "category": "reference" },
    { "name": "DecalChance", "type": "int", "offset": "0x20", "category": "primitive" },
    { "name": "RicochetChance", "type": "int", "offset": "0x24", "category": "primitive" },
    { "name": "SoundOnRicochet", "type": "ID", "offset": "0x28", "category": "enum" },
    { "name": "SoundOnImpact", "type": "ID", "offset": "0x30", "category": "enum" }
  ]
}
```

Comparison:

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `ImpactEffect` | reference / UnityEngine.GameObject | GameObject / unity_asset | **matches** |
| `Decals` | reference / Menace.Tactical.DecalCollection | DecalCollection / reference | **matches** |
| `DecalChance` | int / Int32 | int / primitive | **matches** |
| `RicochetChance` | int / Int32 | int / primitive | **matches** |
| `SoundOnRicochet` | object / ID | ID / enum | **matches** (legacy `enum` category is a misnomer — `ID` is a struct, not an enum; Jiangyu correctly surfaces it as `object`) |
| `SoundOnImpact` | object / ID | ID / enum | **matches** (same as above) |

All 6 fields match in name, type, and order.

The legacy schema also records `base_class: "3259"` — this is a managed base class identifier from decompiled metadata, not a serialised field. Its absence from Jiangyu's output is expected.

The legacy schema categorises `SoundOnRicochet` and `SoundOnImpact` as `enum`, but `ID` is actually a 2-field struct (`bankId`, `itemId`). Jiangyu correctly surfaces these as `object` with nested fields. This is a legacy category error, not a structural mismatch.

Classification: **matches** — the serialised field set is an exact match. The only legacy-only detail (`base_class`) is managed metadata, and the category mismatch (`enum` vs `object` for `ID`) is a legacy labelling error.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialised `SkillOnSurfaceDefinition` contract from live game data
- the shape is stable across all 24 observed array elements in 3 templates spanning 3 distinct skill categories (active combat, active utility, passive)
- the legacy `embedded_classes` definition is an exact match at the serialised field level
- this is the first nested support type validated under `SkillTemplate`, extending structural validation coverage beyond `EntityTemplate`/`WeaponTemplate`
- all inspected `SkillTemplate` instances have exactly 14 `ImpactOnSurface` elements, suggesting the array is keyed by a fixed surface type index (probably matching the number of surface types in the game)

What this does **not** validate:

- runtime behaviour (how the game selects which surface entry to use, surface type indexing)
- the meaning of the 14-element count (whether it corresponds to a known surface type enum)
- the `ID` struct contents within truncated elements (shape is validated, specific bankId/itemId values in surface entries are not)
- whether the managed base class `3259` carries any additional inherited fields beyond what Unity serialises
- `SkillTemplate` top-level field set (120 Jiangyu fields vs 128 legacy fields — observed as a side finding but out of scope for this pass)

## Side finding: SkillTemplate top-level field stability

All 3 samples show **120 serialised fields** under `m_Structure` with an identical field set (names, kinds, type names). The legacy schema lists **128 fields**. The 8-field delta is a candidate for a future `SkillTemplate` top-level field audit pass. This does not affect the `SkillOnSurfaceDefinition` result.

## Conclusion

This is a successful structural validation pass for the first `SkillTemplate` nested support type.

The main results are:

- `SkillOnSurfaceDefinition` is a 6-field embedded type with an exact serialised match to the legacy `embedded_classes` definition
- the field set is stable across all 24 observed elements in 3 templates
- the legacy category error (`enum` for `ID`-type fields) is now documented — Jiangyu's `object` categorisation is correct
- this pass extends structural validation beyond `EntityTemplate`/`WeaponTemplate` into `SkillTemplate` for the first time

## Next Step

Good next targets:

1. `SkillTemplate` top-level field audit — compare the 120 Jiangyu-observed fields against the 128 legacy fields to classify the 8-field delta
2. Other array element types under `EntityTemplate` (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
3. `WeaponTemplate` deeper nested types beyond `LocalizedLine`/`LocalizedMultiLine`/`OperationResources`
