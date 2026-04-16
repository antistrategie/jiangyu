# EntityTemplate Contract

Verified top-level serialised contract for `Menace.Tactical.EntityTemplate`, Jiangyu's most
heavily validated template type.

## Shape

- **109 serialised fields** under `m_Structure`
- `fieldTypeName = "EntityTemplate"`
- stable across infantry (`player_squad.darby`), enemy (`enemy.pirate_scavengers`),
  structure (`building_military_1x1_bunker`), and vehicle (`player_vehicle.modular_ifv`)
  entity categories
- zero intra-type structural variation across all inspected samples

The delta vs legacy (112 fields) is fully explained by the
[universal delta rule](universal-delta-rule.md): 4 base class exclusions (`m_ID`, `m_IsGarbage`,
`m_IsInitialized`, `m_LocalizedStrings`) as legacy-only, `serializationData` as Jiangyu-only.
No real mismatches.

## Nested structure

EntityTemplate's fields include both inline embedded objects and reference arrays. The
distinction matters for patching: inline data lives inside the parent template's serialised
blob, while reference data lives in separate MonoBehaviour assets linked by PPtr.

### Inline embedded objects

| Field | Type | Validated? |
|---|---|---|
| `Properties` | `Menace.Tactical.EntityProperties` | yes — [102-field contract](entityproperties-contract.md) |
| `AIRole` | `Menace.Tactical.RoleData` | yes — simple struct, validated in earlier passes |
| `DisplayName`, `Description`, etc. | `LocalizedLine` / `LocalizedMultiLine` | yes — localisation wrappers |

### Inline array-element contracts

These arrays contain inline objects — the element data is embedded directly in the parent
template's serialised blob.

| Field | Element type | Fields | Validated? |
|---|---|---|---|
| `AttachedPrefabs` | `PrefabAttachment` | 3 (`IsLight`, `AttachmentPointName`, `Prefab`) | yes — [array element contracts](array-element-contracts.md) |
| `Loot` | `EntityLootEntry` | 4 (`Item`, `Count`, `OverrideDefaultDropChance`, `DropChance`) | yes — [array element contracts](array-element-contracts.md) |

### Reference-array relationships

These arrays contain PPtr references to separate MonoBehaviour assets. Each element is an
independent ScriptableObject — the parent template holds only the reference, not the data.

| Field | Declared element type | Polymorphic? | Notes |
|---|---|---|---|
| `Items` | `List<ItemTemplate>` | **yes** — ArmorTemplate, WeaponTemplate, AccessoryTemplate observed | widest polymorphic array; see [polymorphic reference arrays](polymorphic-reference-arrays.md) |
| `Skills` | `List<SkillTemplate>` | **yes** — SkillTemplate and PerkTemplate observed | sparse; primarily vehicle entities |
| `SkillGroups` | `List<SkillGroup>` | no (wrapper type) | each SkillGroup wraps `Skills: List<SkillTemplate>` |
| `DefectGroups` | `List<DefectGroup>` | no (wrapper type) | each DefectGroup wraps `Defects: List<DefectTemplate>`; vehicles only |
| `Tags` | `List<TagTemplate>` | observed monomorphic | all TagTemplate in sampled data |
| `Decoration` | `List<PrefabListTemplate>` | observed monomorphic | buildings only |
| `SmallDecoration` | `List<PrefabListTemplate>` | observed monomorphic | buildings only |
| `DestroyedDecoration` | `List<PrefabListTemplate>` | observed monomorphic | buildings only |

The inline vs reference distinction is one of Jiangyu's key structural lessons from this
validation phase. Inline types (PrefabAttachment, EntityLootEntry) can be patched by modifying
the parent template's data. Reference types (Items, Skills, SkillGroups, DefectGroups) require
patching the independent referenced assets.

## Validation method

Inspected via `jiangyu templates inspect --type EntityTemplate --name <name>` across 4+ entity
categories. Field stability confirmed by comparing the full field set (names, kinds, type names,
order) across all samples. Delta classification performed against legacy schema with managed
metadata cross-checks for every legacy-only field.

## Investigation notes

- `legacy/2026-04-14-entity-weapon-schema-spot-check.md` — initial top-level validation
- `legacy/2026-04-15-entitytemplate-array-element-types-spot-check.md` — array element type classification
- `legacy/2026-04-15-entityproperties-support-type-spot-check.md` — EntityProperties nested validation
- `legacy/2026-04-15-prefabattachment-support-type-spot-check.md` — PrefabAttachment inline validation
