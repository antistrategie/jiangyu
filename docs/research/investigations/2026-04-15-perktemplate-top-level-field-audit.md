# Legacy Top-Level Field Audit: PerkTemplate

Date: 2026-04-15

## Goal

Audit the `PerkTemplate` top-level serialised field set using Jiangyu-native inspection, and determine whether PerkTemplate adds any own serialised fields beyond the inherited `SkillTemplate` set.

## Why This Target

PerkTemplate is the first template type known to inherit from another template type (SkillTemplate), confirmed by the polymorphic reference array survey. The SkillTemplate top-level audit is already complete (120 vs 128, fully classified). This pass answers two questions:

1. Does PerkTemplate's serialised contract add any fields beyond SkillTemplate's?
2. Does the standard delta pattern (base class exclusions + Odin container) hold for a derived template type?

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `perk.ambush` — passive combat perk (conditional property modification)
- `perk.first_aid` — active targeted utility perk (regeneration, costs AP)
- `perk.unique_darby_high_value_targets` — unique character-specific passive perk (adds a skill, stacking)

These span three distinct perk categories: passive combat, active utility, and unique character-specific.

## Method

For each sample:

1. run `jiangyu templates inspect --type PerkTemplate --name <name>`
2. extract `m_Structure` field names, kinds, and type names
3. compare across all 3 Jiangyu samples for stability
4. compare PerkTemplate field set directly against the validated SkillTemplate field set (from the SkillTemplate audit)
5. compare against legacy schema's `PerkTemplate` definition at `../MenaceAssetPacker/generated/schema.json`
6. classify any deltas

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type PerkTemplate --name perk.ambush
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type PerkTemplate --name perk.first_aid
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type PerkTemplate --name perk.unique_darby_high_value_targets
```

## Results

### Jiangyu-vs-Jiangyu: field set stability

All 3 samples produced an identical field set:

- `fieldTypeName = "Menace.Strategy.PerkTemplate"`
- **121 fields** in all 3 samples
- identical field names, field order, kinds, and type names across all 3

The data varies meaningfully across perk categories:

| Field | ambush | first_aid | unique_darby |
|---|---|---|---|
| IsActive | False | True | False |
| ActionPointCost | 0 | 10 | 0 |
| IsTargeted | False | True | False |
| IsStacking | False | False | True |
| PerkIcon | promotion_28 | promotion_38 | starting_perk_03 |
| EventHandlers | 1 (ChangePropertyConditional) | 2 (DisplayText, Regeneration) | 1 (AddSkill) |
| SerializedBytes | 456 bytes | 585 bytes | 456 bytes |
| ReferencedUnityObjects | 0 | 0 | 0 |

### PerkTemplate vs SkillTemplate: direct Jiangyu comparison

PerkTemplate's first 120 fields (indices 0–119) are **identical** to SkillTemplate's 120 fields — same names, same order, same kinds, same type names.

PerkTemplate adds exactly **1 own serialised field**:

| Index | Name | Kind | Type |
|---|---|---|---|
| 120 | `PerkIcon` | reference | `UnityEngine.Sprite` |

**PerkTemplate has no other perk-specific serialised fields.** The entire serialised contract is SkillTemplate + PerkIcon. There are no additional perk-only primitives, enums, objects, arrays, or references.

### Jiangyu-vs-legacy: delta computation

| Metric | Count |
|---|---|
| Jiangyu fields | 121 |
| Legacy fields | 129 |
| Shared fields | 120 |
| Legacy-only fields | 9 |
| Jiangyu-only fields | 1 |
| Net delta | 8 (129 − 121) |

The 120 shared fields appear in the same relative order in both Jiangyu and legacy.

### Legacy-only fields (9) — all inherited from SkillTemplate

#### Group 1: base class / managed-only (4 fields)

| Field | Legacy type | Legacy category |
|---|---|---|
| `m_ID` | string | string |
| `m_IsGarbage` | bool | primitive |
| `m_IsInitialized` | bool | primitive |
| `m_LocalizedStrings` | BaseLocalizedString[] | collection |

Same `NotSerialized` / non-`SerializeField` base class exclusions confirmed in EntityTemplate, WeaponTemplate, and SkillTemplate.

#### Group 2: Odin-serialised (5 fields)

| Field | Legacy type | Legacy category |
|---|---|---|
| `CustomAoEShape` | ICustomAoEShape | reference |
| `AoEFilter` | ITacticalCondition | reference |
| `ProjectileData` | BaseProjectileData | reference |
| `SecondaryProjectileData` | BaseProjectileData | reference |
| `AIConfig` | SkillBehavior | reference |

Same interface/abstract-typed fields excluded by Unity's native serialisation and routed through Odin, first classified in the SkillTemplate audit.

### Jiangyu-only field (1) — inherited from SkillTemplate

| Field | Kind | Type |
|---|---|---|
| `serializationData` | object | Sirenix.Serialization.SerializationData |

Same Odin Serializer container. Non-empty on all 3 samples (456–585 bytes), confirming PerkTemplate instances do carry Odin-serialised data — but via the inherited SkillTemplate fields, not via any perk-specific Odin fields.

### Inheritance arithmetic

| | SkillTemplate | PerkTemplate | Delta |
|---|---|---|---|
| Jiangyu fields | 120 | 121 | +1 (PerkIcon) |
| Legacy fields | 128 | 129 | +1 (PerkIcon) |
| Shared fields | 119 | 120 | +1 (PerkIcon) |
| Legacy-only | 9 | 9 | 0 |
| Jiangyu-only | 1 | 1 | 0 |

PerkTemplate adds exactly 1 field to both the Jiangyu-observed and legacy-recorded field sets. The 9 legacy-only and 1 Jiangyu-only fields are entirely inherited — PerkTemplate introduces zero new delta fields of its own.

### Odin payload observation

All 3 PerkTemplate samples have non-empty `SerializedBytes` (456–585 bytes) but 0 `ReferencedUnityObjects`. By comparison, SkillTemplate combat skills had 1+ referenced GameObjects (tracers, grenades). This is consistent with perks being primarily passive or support abilities that do not spawn projectile GameObjects — the Odin payload likely contains condition/filter data rather than Unity asset references.

## Interpretation

What this validates:

- **PerkTemplate's serialised contract is exactly SkillTemplate + PerkIcon** — confirmed by direct Jiangyu-vs-Jiangyu field comparison, not just by counting
- PerkTemplate has **no additional perk-specific serialised fields** beyond `PerkIcon`. This is a first-class conclusion: the inheritance is purely additive with a single field
- the entire 121 vs 129 legacy delta is inherited from SkillTemplate — no new delta categories, no perk-specific legacy-only or Jiangyu-only fields
- the standard delta patterns (base class exclusions + Odin container) hold for a derived template type, not just for direct `DataTemplate` descendants
- the Odin serialisation container is present and active on PerkTemplate instances, carrying the same inherited interface/abstract-typed fields as SkillTemplate
- legacy schema correctly records `base_class: "SkillTemplate"` and lists PerkIcon as the final field — the legacy inheritance claim matches observed serialised structure

What this does **not** validate:

- Odin blob contents or decoding
- runtime behaviour of any perk
- whether PerkTemplate has managed-only (non-serialised) fields beyond PerkIcon that affect gameplay
- the full set of concrete EventHandler types used under PerkTemplate (only 3 types observed: ChangePropertyConditional, DisplayText, Regeneration, AddSkill)
- semantics of PerkIcon vs the inherited Icon/IconDisabled fields

## Conclusion

PerkTemplate is the simplest top-level template audit so far. The entire result reduces to one statement:

**PerkTemplate adds exactly one serialised field (`PerkIcon: Sprite`) to the SkillTemplate contract. Everything else — all 120 other fields, all 9 legacy-only delta fields, and the Odin container — is inherited unchanged from SkillTemplate.**

This is the first validated derived template type. It confirms that:

- the SkillTemplate → PerkTemplate inheritance observed in the polymorphic reference array survey is faithfully reflected in the serialised contract
- the standard delta classification (base class exclusions, Odin-routed fields, serializationData container) carries through inheritance without introducing new categories
- template inheritance in MENACE follows standard Unity serialisation rules: derived types append fields after the base type's serialised set

The pass brings 4 template families to fully audited status at the top level: EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate (plus AnimationSoundTemplate as a lighter validation). The remaining unaudited types from the TODO list are: `TileEffectTemplate`, `TagTemplate`, `DefectTemplate`.

## Next Step

Good next targets:

1. `TileEffectTemplate` or `DefectTemplate` top-level audit — continue testing whether the standard delta patterns are universal across all template families
2. `TagTemplate` top-level audit — likely very small, may be similar to PerkTemplate in having few own fields
3. OperationTemplate conversation arrays — still need a populated instance to assess polymorphism
