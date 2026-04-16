# Legacy Top-Level Field Audit: TileEffectTemplate

Date: 2026-04-15

## Goal

Audit the `TileEffectTemplate` top-level serialized field set using Jiangyu-native inspection, and compare against the legacy schema's 16-field definition at `../MenaceAssetPacker/generated/schema.json`.

## Why This Target

TileEffectTemplate is one of three template types listed in TODO as unaudited at the top level (alongside TagTemplate and DefectTemplate). All previous audits have been in the EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, or AnimationSoundTemplate families. Auditing a different family tests whether the standard delta patterns (base class exclusions, Odin container) are universal.

The legacy schema marks TileEffectTemplate as `is_abstract: true` with `base_class: "DataTemplate"`. This is the first audit of an abstract template type — all previous targets had direct serialized instances discoverable via the template index.

## Samples

TileEffectTemplate has **zero instances** in Jiangyu's template index. The template classifier (`TemplateClassifier.cs`) matches MonoBehaviour script names ending with `"Template"` — concrete subtypes of TileEffectTemplate don't end with "Template", so they are invisible to the classifier.

Instances were discovered by following PPtr references from SpawnTileEffect skill event handler instances. All 23 concrete instances inspected via pathId-based lookup:

**Discovery path:**
1. `EnvironmentFeatureTemplate.TileEffect` — all 4 instances have null PPtrs (dead end)
2. `SpawnTileEffect.EffectToSpawn` — 33 handler instances, populated PPtrs lead to concrete TileEffectTemplate objects

**Samples inspected for base field verification (4, spanning 3 concrete types):**
- `tile_effect.explosive_charge` (pathId=114662) — `SpawnObjectTileEffect`, simplest subtype (0 own fields)
- `tile_effect.acid` (pathId=114655) — `ApplySkillTileEffect`, most common subtype
- `tile_effect.fire` (pathId=114663) — `ApplySkillTileEffect`, second sample of most common subtype
- `tile_effect.smoke_grenade` (pathId=114673) — `ApplyStatusEffectTileEffect`, different subtype

**Full census of all 23 instances for concrete type catalogue.**

## Method

1. Searched Jiangyu template index for `TileEffectTemplate` — zero results
2. Inspected all 4 `EnvironmentFeatureTemplate` instances — `TileEffect` PPtr null on all 4
3. Inspected `SpawnTileEffect` handler instances by pathId, extracted `EffectToSpawn` PPtr references
4. Followed references to concrete TileEffectTemplate objects
5. Inspected 4 concrete instances across 3 types for base field verification
6. Ran full census of all 23 `tile_effect.*` assets to catalogue all concrete types
7. Compared base field set across concrete types for stability
8. Compared against legacy schema

Commands used:

```bash
# discovery via SpawnTileEffect handler
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106221

# concrete instance inspection
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114662 --max-depth 5
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114655 --max-depth 5
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114663 --max-depth 5
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114673 --max-depth 5
```

## Results

### Jiangyu-vs-Jiangyu: base field set stability

All 23 instances share the same 13 base fields (12 named + 1 Odin container), regardless of concrete type. Field names, order, kinds, and type names are identical across all 7 concrete types.

**TileEffectTemplate base fields (Jiangyu-observed):**

| # | Name | Kind | Type |
|---|---|---|---|
| 0 | `serializationData` | object | Sirenix.Serialization.SerializationData |
| 1 | `m_GameDesignComment` | string | String |
| 2 | `m_LocaState` | enum | Menace.Tools.LocaState |
| 3 | `Title` | object | Menace.Tools.LocalizedLine |
| 4 | `Description` | object | Menace.Tools.LocalizedMultiLine |
| 5 | `Type` | enum | Menace.Tactical.TileEffects.TileEffectType |
| 6 | `HasTimeLimit` | bool | Boolean |
| 7 | `RemoveAfterRounds` | int | Int32 |
| 8 | `SpawnObjectOrEffect` | reference | UnityEngine.GameObject |
| 9 | `AmountOfPrefabsSpawned` | int | Int32 |
| 10 | `RandomlyRotateObjectOrEffect` | bool | Boolean |
| 11 | `ObjectOrEffectDestroyDelay` | float | Single |
| 12 | `BlockLineOfSight` | bool | Boolean |

Odin `SerializedBytes` is empty (0 bytes) on the `SpawnObjectTileEffect` sample and all `ApplySkillTileEffect` and `ApplyStatusEffectTileEffect` samples inspected — no Odin-routed fields observed at the TileEffectTemplate base level.

### Concrete subtypes: full census

All 23 `tile_effect.*` assets catalogued:

| Concrete type | Namespace | Instances | Total fields | Own fields |
|---|---|---|---|---|
| ApplySkillTileEffect | Menace.Tactical.TileEffects | 9 | 17 | ApplyOn, ApplySkillToActorsOnTile, SoundOnApply, OneShot |
| RecoverableObjectTileEffect | Menace.Tactical.TileEffects | 5 | 17 | DropObjectSkill, SoundOnPickup, PickupableByInfantry, PickupableByVehicles |
| ApplyStatusEffectTileEffect | Menace.Tactical.TileEffects | 3 | 17 | ApplySkillToActorsInside, OnlyApplyIfTargetHasOneOfTheseTags, DontApplyIfTargetHasOneOfTheseTags, RemoveSkillOnLeavingTile |
| RefillAmmoTileEffect | Menace.Tactical.TileEffects | 3 | 16 | SoundOnPickup, RestorePct, RestoreMinimum |
| BleedOutTileEffect | Menace.Tactical.TileEffects | 1 | 17 | PermanentlyDieAfterRounds, SoundOnRoundStart, SoundOnSaved, SoundOnPermanentDeath |
| AddItemTileEffect | Menace.Tactical.TileEffects | 1 | 18 | ItemSelectionType, Item, Items, RewardTable, RewardRarityMult |
| SpawnObjectTileEffect | Menace.Tactical.TileEffects | 1 | 13 | (none) |

**Instance breakdown:**

- ApplySkillTileEffect (9): acid, ap_mine, at_mine, corrosive_cloud, fire, plasma_fire, smoke_large, smoke_small, wire_slings
- RecoverableObjectTileEffect (5): construct_sample, recoverable_obj_egg_minor_alien, recoverable_obj_egg_pristine_alien, recoverable_object, recoverable_object2
- ApplyStatusEffectTileEffect (3): carrier_smoke_screen, flare_red_smoke, smoke_grenade
- RefillAmmoTileEffect (3): laser_sentry_turret_drop, scavenger_drop, supply_drop
- BleedOutTileEffect (1): bleedout
- AddItemTileEffect (1): loot_test
- SpawnObjectTileEffect (1): explosive_charge

### Jiangyu-vs-legacy: delta computation

| Metric | Count |
|---|---|
| Jiangyu base fields (excl. Odin container) | 12 |
| Legacy fields | 16 |
| Shared fields | 12 |
| Legacy-only fields | 4 |
| Jiangyu-only fields | 1 |
| Net delta | 3 (16 − 13) |

The 12 shared fields appear in the same relative order in both Jiangyu and legacy.

### Legacy-only fields (4) — DataTemplate base class exclusions

| Field | Legacy type | Legacy category |
|---|---|---|
| `m_ID` | string | string |
| `m_IsGarbage` | bool | primitive |
| `m_IsInitialized` | bool | primitive |
| `m_LocalizedStrings` | BaseLocalizedString[] | collection |

Same `NotSerialized` / non-`SerializeField` base class exclusions confirmed in EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, and AnimationSoundTemplate. No new legacy-only fields.

### Jiangyu-only field (1) — Odin container

| Field | Kind | Type |
|---|---|---|
| `serializationData` | object | Sirenix.Serialization.SerializationData |

Standard Odin container. Empty (0 bytes) on inspected samples, consistent with TileEffectTemplate having no interface/abstract-typed fields that would require Odin routing.

### Legacy schema gaps

The legacy schema records TileEffectTemplate as `is_abstract: true` but:

1. **No concrete subtypes listed.** Neither the `templates` nor `inheritance` sections list any of the 7 observed concrete types. The legacy schema knows the abstract base exists but has no knowledge of its concrete instantiations.
2. **No concrete subtype fields recorded.** The 4 own fields on each concrete type (e.g. `ApplyOn`, `ApplySkillToActorsOnTile` on `ApplySkillTileEffect`) are not in the legacy schema at all.
3. **Concrete types don't end with "Template".** This is why both Jiangyu's template classifier and presumably the legacy schema's discovery mechanism miss them.

### Classifier gap

TileEffectTemplate concrete subtypes are invisible to `TemplateClassifier` because their script names don't end with `"Template"`:

- `SpawnObjectTileEffect`, `ApplySkillTileEffect`, `ApplyStatusEffectTileEffect`, `RecoverableObjectTileEffect`, `RefillAmmoTileEffect`, `BleedOutTileEffect`, `AddItemTileEffect`

These are all in the `Menace.Tactical.TileEffects` namespace and derive from `TileEffectTemplate`, which itself derives from `DataTemplate`. They are structurally template-like (carry the full DataTemplate base field set, live in resources.assets as named MonoBehaviours) but are not classified as templates by the current naming heuristic.

This is a new visibility pattern. Previous unclassified concrete types (SkillEventHandlerTemplate subtypes) also don't end with "Template" but their abstract base doesn't either — both sides are consistently outside the classifier. Here, the abstract base (`TileEffectTemplate`) is correctly named and would be classifiable, but has no instances; its concrete types are incorrectly named for classification and have all the instances.

## Interpretation

What this validates:

- **The standard delta pattern is universal.** TileEffectTemplate's 4 legacy-only fields and 1 Jiangyu-only field exactly match the DataTemplate base class exclusion + Odin container pattern seen in all 5 previously audited template families. No new delta categories.
- **TileEffectTemplate's base field set is stable and correct.** 12 named fields confirmed across all 7 concrete subtypes and all 23 instances. The legacy schema's 16 fields reduce to these 12 after the standard delta classification.
- **7 concrete subtypes exist** with 0–5 own fields each, all in the `Menace.Tactical.TileEffects` namespace. The legacy schema has no record of any of them.
- **TileEffectTemplate is genuinely abstract** — no instances are serialized under the base class name. All 23 instances use concrete subtype class names.
- **Odin container is present but empty** at the TileEffectTemplate base level (no interface/abstract-typed base fields). Concrete subtypes may carry their own Odin payloads but this was not verified.

What this does **not** validate:

- Odin blob contents or decoding on concrete subtypes
- runtime behaviour of any tile effect
- managed-only (non-serialized) fields on TileEffectTemplate or its subtypes
- field types and semantics on concrete subtype own fields (only names and existence verified)
- whether additional concrete subtypes exist that are not referenced by any SpawnTileEffect handler (discovery was via PPtr traversal, not exhaustive class enumeration)

## Conclusion

TileEffectTemplate is the first abstract template type audited. The audit required a different discovery path (PPtr reference traversal from skill event handlers) because the template classifier cannot see the concrete subtypes.

Key findings:

1. **Standard delta pattern confirmed as universal.** The same 4+1 delta (DataTemplate base exclusions + Odin container) holds across 6 template families now: EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, AnimationSoundTemplate, TileEffectTemplate. No exceptions.

2. **New template family structure: abstract base with non-Template-named concrete subtypes.** This is distinct from:
   - direct template types (EntityTemplate, WeaponTemplate) — concrete, directly indexed
   - derived template types (PerkTemplate) — concrete, directly indexed, inherits from another template
   - abstract handler types (SkillEventHandlerTemplate) — abstract, concrete subtypes also non-Template-named, but base is also non-Template-named
   
   TileEffectTemplate is the first case where the abstract base is correctly Template-named but all concrete subtypes are not.

3. **Legacy schema has a type-level blind spot.** It records the abstract base (16 fields) but none of the 7 concrete subtypes. The concrete subtype fields (3–5 per type) are entirely absent from the legacy schema.

4. **23 concrete instances across 7 subtypes.** The population is moderate (comparable to AnimationSoundTemplate's 7 instances) but the type diversity is high (7 subtypes vs AnimationSoundTemplate's 1).

## Next Step

1. **TagTemplate or DefectTemplate top-level audit** — the last two unaudited template types from the TODO list. DefectTemplate's inheritance chain (`SerializedScriptableObject → DefectTemplate`, not via DataTemplate) makes it structurally different from all audited types so far.
2. **Classifier gap assessment** — determine whether the `*TileEffect` naming pattern represents a broader category of non-Template-named template-like types that the classifier misses, or whether this is unique to the TileEffectTemplate family.
3. **Concrete subtype own field validation** — the own fields on each concrete type were enumerated but not type-verified against live data (only names observed from inspection output). A deeper pass could verify field types and Odin usage per concrete type.
