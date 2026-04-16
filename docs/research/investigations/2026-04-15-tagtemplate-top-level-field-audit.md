# Legacy Top-Level Field Audit: TagTemplate

Date: 2026-04-15

## Goal

Audit the `TagTemplate` top-level serialised field set using Jiangyu-native inspection, and compare against the legacy schema's 12-field definition at `../MenaceAssetPacker/generated/schema.json`.

## Why This Target

TagTemplate is the last unaudited template type. 7 template families are already audited (EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, AnimationSoundTemplate, TileEffectTemplate, DefectTemplate). TagTemplate inherits from DataTemplate, so should show the standard 4+1 delta pattern. This pass closes the full template type audit sweep.

TagTemplate is useful as the "boring confirms-the-rule" case:

- DataTemplate descendant, so the standard base class exclusions should apply
- no Odin-routed fields in practice (all tag-specific fields are Unity-serialisable types)
- structurally simple: enums, a localised name, a bool, and two self-referential list fields
- the reference arrays (`GoodAgainst`, `BadAgainst`) are straightforward monomorphic `List<TagTemplate>` — no polymorphism, no nesting complexity

If the universal delta rule holds here, it holds across the entire template type inventory.

## Samples

Jiangyu-native inspection, using the built CLI DLL. 73 TagTemplate instances exist. 5 inspected in detail:

| Sample | TagType value | Category | Notes |
|---|---|---|---|
| `assault_rifle` | 34 | weapon type | IsVisible=false, Value=0, both arrays empty |
| `vehicle` | 47 | entity category | IsVisible=false, populated BadAgainst (→ anti_vehicle) |
| `deploy` | 9 | gameplay mechanic | IsVisible=true, Value=0, both arrays empty |
| `anti_vehicle` | 25 | counter tag | IsVisible=true, Value=3, populated GoodAgainst (→ vehicle) |
| `incendiary` | 21 | damage type | IsVisible=true, Value=0, both arrays empty |

These span weapon type, entity category, gameplay mechanic, counter, and damage type tags. The counter pair (`anti_vehicle` ↔ `vehicle`) exercises populated reference arrays in both directions.

## Method

1. Listed all TagTemplate instances via `jiangyu templates list --type TagTemplate` (73 found)
2. Inspected 5 samples across different tag categories
3. Compared field sets across all Jiangyu samples for stability
4. Extracted legacy `TagTemplate` fields from `../MenaceAssetPacker/generated/schema.json` → `templates.TagTemplate`
5. Computed set intersection and symmetric difference
6. Classified each delta field

## Results

### Jiangyu-vs-Jiangyu: field set stability

All 5 inspected instances show **9 serialised fields** under `m_Structure` with an identical field set (names, kinds, type names):

| # | Name | Kind | Type |
|---|---|---|---|
| 0 | `serializationData` | object | Sirenix.Serialization.SerializationData |
| 1 | `m_GameDesignComment` | string | String |
| 2 | `m_LocaState` | enum | Menace.Tools.LocaState |
| 3 | `TagType` | enum | Menace.Tags.TagType |
| 4 | `Name` | object | Menace.Tools.LocalizedLine |
| 5 | `IsVisible` | bool | Boolean |
| 6 | `Value` | enum | Menace.Tags.TagValue |
| 7 | `GoodAgainst` | array | List\<Menace.Tags.TagTemplate\> |
| 8 | `BadAgainst` | array | List\<Menace.Tags.TagTemplate\> |

The field set is perfectly stable across all 5 samples. Only primitive values, enum values, and array populations vary.

### Odin blob: empty across all samples

All 5 instances have a completely empty `serializationData`:

- `SerializedBytes`: count 0
- `ReferencedUnityObjects`: count 0
- `SerializationNodes`: count 0
- `SerializedBytesString`: empty

TagTemplate has no Odin-routed fields. The `serializationData` container is structurally present because the type inherits from `SerializedScriptableObject` (through DataTemplate), but contains no data. This makes TagTemplate the first audited DataTemplate descendant where the Odin blob is purely vestigial.

### Reference array observations

`GoodAgainst` and `BadAgainst` are monomorphic `List<TagTemplate>` fields containing PPtr references to other TagTemplate MonoBehaviours:

- `vehicle.BadAgainst` → `[anti_vehicle]` (pathId 114585)
- `anti_vehicle.GoodAgainst` → `[vehicle]` (pathId 114647)
- remaining 3 samples: both arrays empty

The reciprocal pairing (`anti_vehicle` is good against `vehicle`; `vehicle` is bad against `anti_vehicle`) confirms these are straightforward self-referential tag relationship lists.

### Jiangyu-vs-legacy: delta computation

| Metric | Count |
|---|---|
| Jiangyu fields (incl. Odin container) | 9 |
| Legacy fields | 12 |
| Shared fields | 8 |
| Legacy-only fields | 4 |
| Jiangyu-only fields | 1 |
| Net delta | 3 (12 − 9) |

### Legacy-only fields (4) — DataTemplate base class exclusions

| Field | Legacy type | Legacy category | Reason absent from Jiangyu |
|---|---|---|---|
| `m_ID` | string | string | DataTemplate base class — excluded by Unity serialisation rules |
| `m_IsGarbage` | bool | primitive | DataTemplate base class — excluded by Unity serialisation rules |
| `m_IsInitialized` | bool | primitive | DataTemplate base class — excluded by Unity serialisation rules |
| `m_LocalizedStrings` | BaseLocalizedString[] | collection | DataTemplate base class — excluded by Unity serialisation rules |

These are exactly the same 4 DataTemplate base class fields that appeared as legacy-only in all 6 previously audited DataTemplate descendants: EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, AnimationSoundTemplate, TileEffectTemplate.

### Jiangyu-only field (1) — Odin container

| Field | Kind | Type |
|---|---|---|
| `serializationData` | object | Sirenix.Serialization.SerializationData |

Standard Odin Serializer container. Present on all instances but entirely empty — no data is routed through it. TagTemplate has no interface-typed, abstract-typed, or non-Unity-serialisable fields that would require Odin.

### Shared field type matching

All 8 shared fields match between Jiangyu and legacy:

| Field | Jiangyu kind/type | Legacy type/category | Match |
|---|---|---|---|
| `m_GameDesignComment` | string / String | string / string | exact |
| `m_LocaState` | enum / Menace.Tools.LocaState | LocaState / enum | exact |
| `TagType` | enum / Menace.Tags.TagType | TagType / enum | exact |
| `Name` | object / Menace.Tools.LocalizedLine | LocalizedLine / localization | exact |
| `IsVisible` | bool / Boolean | bool / primitive | exact |
| `Value` | enum / Menace.Tags.TagValue | TagValue / enum | exact |
| `GoodAgainst` | array / List\<TagTemplate\> | List\<TagTemplate\> / collection | exact |
| `BadAgainst` | array / List\<TagTemplate\> | List\<TagTemplate\> / collection | exact |

Zero type mismatches.

### Shared field ordering

The 8 shared fields appear in the same relative order in both Jiangyu and legacy.

## Interpretation

What this validates:

- Jiangyu independently reproduces 8 of 12 legacy `TagTemplate` fields from live game data
- the 9-field serialised contract is perfectly stable across 5 instances spanning 5 different tag categories
- the 4 legacy-only fields are the exact same DataTemplate base class exclusions seen in every other DataTemplate descendant audit
- the Odin container is structurally present but contains no data — TagTemplate is the cleanest possible DataTemplate descendant (standard base class delta, zero Odin-routed fields)
- all 8 shared fields match legacy types exactly, with no type mismatches or ordering differences
- the reference arrays (`GoodAgainst`, `BadAgainst`) are structurally straightforward monomorphic PPtr lists

TagTemplate serves as the "boring confirms-the-rule" case. Every structural pattern established across the prior 7 audits holds without exception:

- DataTemplate descendant → 4 base class exclusions as legacy-only
- inherits from SerializedScriptableObject → `serializationData` as Jiangyu-only
- all tag-specific fields are Unity-serialisable → empty Odin blob
- no polymorphism, no abstract fields, no interface types

What this does **not** validate:

- runtime behaviour of the tag relationship system (GoodAgainst/BadAgainst)
- the full `TagType` or `TagValue` enum value sets (only individual values observed)
- whether all 73 instances share the same field stability (5 inspected, not all 73)
- semantics of the tag system in gameplay

## Conclusion

The 9 vs 12 field delta is fully explained. No real mismatches.

| Classification | Count | Fields |
|---|---|---|
| matches | 8 | m_GameDesignComment, m_LocaState, TagType, Name, IsVisible, Value, GoodAgainst, BadAgainst |
| legacy broader than serialised contract (base class) | 4 | m_ID, m_IsGarbage, m_IsInitialized, m_LocalizedStrings |
| Jiangyu-only (serialisation framework) | 1 | serializationData |
| legacy broader than serialised contract (Odin-routed) | 0 | — |
| real mismatch | 0 | — |

TagTemplate is the 8th and final template type audited at the top level. The standard 4+1 delta pattern holds across the entire template type inventory — every DataTemplate descendant shows the same 4 base class exclusions, every template type shows the `serializationData` container. TagTemplate is the simplest case: all tag-specific fields are natively serialisable, so the Odin blob is vestigial. No surprises, no exceptions.

**The full template type top-level audit sweep is now complete.**

## Next Step

1. **Classifier gap assessment** — determine whether non-Template-named template-like types (TileEffectTemplate concrete subtypes, SkillEventHandlerTemplate concrete subtypes) represent a broader category the classifier misses, or are isolated naming patterns.
2. **OperationTemplate conversation arrays** — find populated instances to assess polymorphism (all checked instances had empty arrays).
3. **Deeper EntityTemplate.Items validation** — inspect concrete subtypes (ArmorTemplate, AccessoryTemplate) for field-level comparison against legacy.
