# Legacy Support-Type Spot-Check: EntityTemplate Array Element Types

Date: 2026-04-15

## Goal

Structurally validate the three array element types under `EntityTemplate` that were previously blocked by empty arrays: `EntityLootEntry`, `SkillGroup`, and `DefectGroup`. Determine which are inline embedded types and which are reference arrays, compare against legacy `embedded_classes` definitions, and record the results.

## Why These Types

These three types were identified as the top priority in `2026-04-15-next-support-type-candidates.md`:

- they are `embedded_classes` in the legacy schema, suggesting real structured content
- `SkillGroup` and `EntityLootEntry` are directly relevant to gameplay modding (skill loadouts and loot tables)
- all previous EntityTemplate inspections had empty arrays for these fields, so this pass requires finding populated samples
- validating these completes the remaining EntityTemplate array element types (the only previously validated one was `PrefabAttachment`)

## Samples

All samples discovered via Jiangyu-native template inspection using the built CLI DLL.

### EntityLootEntry (inline embedded)

| Template | Loot count | Content |
|---|---|---|
| `enemy.pirate_boarding_commandos` | 4 | armour, weapon, 2 accessories (all `DropChance: 0`, `OverrideDefaultDropChance: false`) |
| `enemy.alien_01_big_warrior_queen` | 4 | alien commodities (all `OverrideDefaultDropChance: true`, varied `DropChance`: 100, 35, 35, 35) |
| `enemy.construct_soldier_tier1` | 2 | construct commodities (`OverrideDefaultDropChance: true`, `DropChance`: 40, 30) |

### SkillGroup (ScriptableObject reference)

| Referenced object | pathId | Skills count | Content |
|---|---|---|---|
| `infantry_default` | 113892 | 6 | stance (deploy/get_up), morale (wavering/fleeing), suppression |
| `infantry_player` | 113894 | 3 | stop_bleedout, infantry_crawl, cut_fence |
| `morale_aliens` | 113895 | 2 | morale only (wavering, fleeing) |

### DefectGroup (ScriptableObject reference)

| Referenced object | pathId | Defects count | Content |
|---|---|---|---|
| `vehicle_generic` | 111807 | 10 | optics, weapons, armour, fire (small/medium/big), weapon destruction |
| `vehicle_tracked` | 111814 | 1 | steering_damaged only |

Populated DefectGroups found in 5 templates out of the full EntityTemplate inventory: `civilian.workers_on_heavy_truck` (5), `enemy.construct_guncrawler_tier1` (4), `enemy.construct_guncrawler_tier2` (4), `enemy.local_forces_artillery` (1), `enemy.pirate_boarding_commando_on_light_truck` (5). All are vehicle or vehicle-adjacent entities.

### Templates with empty arrays

Player squad entities (`darby`, `sy`, `vamplew`) have populated `SkillGroups` but empty `Loot` and `DefectGroups`. Non-vehicle enemies have populated `Loot` and `SkillGroups` but empty `DefectGroups`. Buildings and terrain have all three empty.

## Method

For each target type:

1. run `jiangyu templates inspect --type EntityTemplate --name <name>`
2. navigate to `m_Structure` → target array field
3. determine whether elements are inline objects or PPtr references
4. for inline types: record the element field set
5. for reference types: follow the PPtr by inspecting via `--collection resources.assets --path-id <id>`
6. compare across Jiangyu samples for stability
7. compare against legacy `embedded_classes` definition

Commands used:

```bash
# EntityTemplate inspection
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.pirate_boarding_commandos
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.alien_01_big_warrior_queen
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type EntityTemplate --name enemy.construct_soldier_tier1

# SkillGroup follow-through (not indexed as a template type — inspected by identity)
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 113892
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 113894
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 113895

# DefectGroup follow-through
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 111807
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 111814
```

## Results

### EntityLootEntry — inline embedded array element

Array container: `Loot`, kind `array`, type `System.Collections.Generic.List'1<Menace.Tactical.EntityLootEntry>`.

Each element is an inline object (`kind: "object"`, `fieldTypeName: "EntityLootEntry"`). Field set across all 10 elements in 3 templates:

| Field | Kind | Type |
|---|---|---|
| `Item` | reference | `Menace.Items.BaseItemTemplate` |
| `Count` | int | `Int32` |
| `OverrideDefaultDropChance` | bool | `Boolean` |
| `DropChance` | int | `Int32` |

Stable across all samples. No structural variation.

Value diversity confirmed:
- `OverrideDefaultDropChance` takes both `true` and `false` values
- `DropChance` ranges from 0 to 100
- `Item` references span armour, weapons, accessories, and commodities across alien, pirate, and construct factions
- `Count` is 1 in all observed samples

### SkillGroup — ScriptableObject reference array (not inline)

Array container: `SkillGroups`, kind `array`, type `System.Collections.Generic.List'1<Menace.Tactical.Skills.SkillGroup>`.

Each element is a **PPtr reference** (`kind: "reference"`, `fieldTypeName: "PPtr<IObject>"`), not an inline object. This means SkillGroup is a separate MonoBehaviour/ScriptableObject asset, not an embedded struct.

SkillGroup is **not a new inline support-type result**. It is a confirmation that the **reference-array pattern generalises beyond `SkillTemplate.EventHandlers`**. The structural category is the same: parent template holds a `List<T>` where `T` derives from ScriptableObject, and each array element is a PPtr to a separate MonoBehaviour asset.

Inspecting the referenced objects by identity reveals a consistent internal shape across all 3 instances:

| Field | Kind | Type |
|---|---|---|
| `Skills` | array | `System.Collections.Generic.List'1<Menace.Tactical.Skills.SkillTemplate>` |

Each `Skills` entry is itself a PPtr reference to a `SkillTemplate` MonoBehaviour. The SkillGroup object is a thin grouping wrapper — one field, no inline data beyond the reference list.

The `m_Script` reference resolves to `SkillGroup` (MonoScript), confirming the managed type. SkillGroup does not end in "Template", so it is not indexed by Jiangyu's template classifier, but it is inspectable by collection + pathId.

### DefectGroup — ScriptableObject reference array (not inline)

Array container: `DefectGroups`, kind `array`, type `System.Collections.Generic.List'1<Menace.Tactical.DefectGroup>`.

Same structural category as SkillGroup: each element is a **PPtr reference** to a separate MonoBehaviour asset.

Internal shape across both inspected instances:

| Field | Kind | Type |
|---|---|---|
| `Defects` | array | `System.Collections.Generic.List'1<Menace.Tactical.DefectTemplate>` |

Each `Defects` entry is a PPtr reference to a `DefectTemplate` MonoBehaviour. Same thin grouping wrapper pattern as SkillGroup — one field, one reference list.

DefectGroups are sparse: only 5 of 190+ EntityTemplates have populated arrays, and all are vehicle or vehicle-adjacent entities.

## Jiangyu-vs-Legacy Comparison

### EntityLootEntry

Legacy `embedded_classes.EntityLootEntry`:

```json
{
  "base_class": "2809",
  "fields": [
    { "name": "Item", "type": "BaseItemTemplate", "offset": "0x10", "category": "reference" },
    { "name": "Count", "type": "int", "offset": "0x18", "category": "primitive" },
    { "name": "OverrideDefaultDropChance", "type": "bool", "offset": "0x1C", "category": "primitive" },
    { "name": "DropChance", "type": "int", "offset": "0x20", "category": "primitive" }
  ]
}
```

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `Item` | reference / `Menace.Items.BaseItemTemplate` | `BaseItemTemplate` / reference | **matches** |
| `Count` | int / `Int32` | int / primitive | **matches** |
| `OverrideDefaultDropChance` | bool / `Boolean` | bool / primitive | **matches** |
| `DropChance` | int / `Int32` | int / primitive | **matches** |

All 4 fields match in name, type, and order. Exact serialised-field match.

Legacy `base_class: "2809"` is a managed base class identifier, not a serialised field. Its absence from Jiangyu output is expected.

Classification: **legacy broader than serialised contract** — the only legacy-only detail is managed metadata.

### SkillGroup

Legacy `embedded_classes.SkillGroup`:

```json
{
  "base_class": "ScriptableObject",
  "fields": [
    { "name": "Skills", "type": "List<SkillTemplate>", "offset": "0x18", "category": "collection", "element_type": "SkillTemplate" }
  ]
}
```

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `Skills` | array / `List'1<Menace.Tactical.Skills.SkillTemplate>` | `List<SkillTemplate>` / collection | **matches** |

Single field, exact match. Legacy `base_class: ScriptableObject` is consistent with Jiangyu's observation that these are separate MonoBehaviour assets (ScriptableObject-derived).

Classification: **matches**.

### DefectGroup

Legacy `embedded_classes.DefectGroup`:

```json
{
  "base_class": "ScriptableObject",
  "fields": [
    { "name": "Defects", "type": "List<DefectTemplate>", "offset": "0x18", "category": "collection", "element_type": "DefectTemplate" }
  ]
}
```

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `Defects` | array / `List'1<Menace.Tactical.DefectTemplate>` | `List<DefectTemplate>` / collection | **matches** |

Single field, exact match. Same pattern as SkillGroup.

Classification: **matches**.

### EntityTemplate field-level comparison

Legacy defines the parent fields as:

| Field | Legacy type | Jiangyu type | Match |
|---|---|---|---|
| `DefectGroups` | `List<DefectGroup>` | `List'1<Menace.Tactical.DefectGroup>` | **yes** |
| `Loot` | `List<EntityLootEntry>` | `List'1<Menace.Tactical.EntityLootEntry>` | **yes** |
| `SkillGroups` | `List<SkillGroup>` | `List'1<Menace.Tactical.Skills.SkillGroup>` | **yes** |

All three parent fields match in name and declared element type.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialised contract for all three array element types from live game data
- **EntityLootEntry** is the second validated inline embedded array element type (after `PrefabAttachment`). 4 fields, stable across 10 elements in 3 templates spanning pirate, alien, and construct entity categories. Exact serialised match to legacy.
- **SkillGroup** and **DefectGroup** are not inline embedded types — they are ScriptableObject reference arrays. Each element is a PPtr to a separate MonoBehaviour asset. This is the same structural category as `SkillTemplate.EventHandlers`, but simpler: no polymorphism, each group is a thin wrapper around a single homogeneous reference list.
- The reference-array pattern now generalises beyond `SkillTemplate.EventHandlers` to at least two more fields (`SkillGroups`, `DefectGroups`) under `EntityTemplate`. The pattern is: parent template holds `List<T>` where T is ScriptableObject-derived, and each array element is a PPtr, not an inline struct.
- Legacy `embedded_classes` definitions are accurate for all three types at the serialised field level. Zero mismatches.

What this does **not** validate:

- runtime behaviour (loot drop logic, skill group resolution, defect application)
- the managed base class layout referenced by legacy `base_class: "2809"` (EntityLootEntry) or `base_class: "ScriptableObject"` (SkillGroup, DefectGroup)
- memory offsets recorded in the legacy definitions
- why DefectGroups are populated only on vehicle-type entities (could be a game design decision or an editor workflow pattern)
- the internal structure of referenced `DefectTemplate` or `SkillTemplate` objects beyond their existence as PPtr targets

## Conclusion

All three EntityTemplate array element types are now structurally validated:

- **EntityLootEntry**: inline embedded, 4 fields, exact legacy match. Second validated inline array element type after PrefabAttachment.
- **SkillGroup**: ScriptableObject reference array, 1 field (`Skills: List<SkillTemplate>`), exact legacy match. Confirms that the reference-array pattern seen in `SkillTemplate.EventHandlers` is not SkillTemplate-specific — it is a cross-cutting design pattern used under EntityTemplate as well.
- **DefectGroup**: ScriptableObject reference array, 1 field (`Defects: List<DefectTemplate>`), exact legacy match. Same structural pattern as SkillGroup. Sparse in practice (vehicle entities only).

The structural distinction matters for template-patching design:
- inline types (EntityLootEntry, PrefabAttachment) have their data embedded directly in the parent template's serialised blob
- reference types (SkillGroup, DefectGroup, EventHandlers) are separate assets linked by PPtr — patching them means patching independent MonoBehaviour objects, not fields within the parent

## Next Step

Good next targets:

1. Other polymorphic reference array fields across template types — the reference-array pattern is now confirmed in 3 distinct fields (`EventHandlers`, `SkillGroups`, `DefectGroups`); determine if any other template types use it
2. Template types not yet audited at the top level (`TileEffectTemplate`, `TagTemplate`, or other non-Entity/non-Weapon/non-Skill types) — would test whether the standard delta patterns are truly universal
3. Deeper validation of `DefectTemplate` internal structure if vehicle modding becomes a near-term feature need
