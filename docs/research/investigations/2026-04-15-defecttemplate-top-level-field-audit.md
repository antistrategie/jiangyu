# Legacy Top-Level Field Audit: DefectTemplate

Date: 2026-04-15

## Goal

Audit the `DefectTemplate` top-level serialised field set using Jiangyu-native inspection, and compare against the legacy schema's 5-field definition at `../MenaceAssetPacker/generated/schema.json`.

## Why This Target

DefectTemplate has a non-standard inheritance chain: `SerializedScriptableObject → DefectTemplate`. It does **not** inherit from `DataTemplate`. All 6 previously audited template families (EntityTemplate, WeaponTemplate, SkillTemplate, PerkTemplate, AnimationSoundTemplate, TileEffectTemplate) inherit from DataTemplate and share a universal 4+1 delta pattern (4 DataTemplate base class exclusions as legacy-only, 1 Odin container as Jiangyu-only).

The question is whether the broader structural rule holds outside the DataTemplate lineage — and if so, what shape the delta takes when the DataTemplate base class exclusions are removed from the equation.

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo. 33 DefectTemplate instances exist. 5 inspected in detail, 10 surveyed for field stability and Odin blob variation:

**Detailed inspection (5):**

| Sample | Severity | Category |
|---|---|---|
| `defect.armor_damaged` | Medium (1) | standard vehicle defect |
| `defect.shocked` | Light (0) | light status defect |
| `defect.critical_hit` | Heavy (2) | heavy combat defect |
| `building.burning_medium_fire` | Medium (1) | building fire (different naming prefix) |
| `defect.big_fire` | Heavy (2) | heavy fire defect |

These span all 3 severity levels, both naming prefixes (`defect.*` and `building.*`), and a mix of combat/status/environmental categories.

**Broader survey (10):**

All 5 detailed samples plus `defect.fuel_leak`, `defect.walker_knocked_over`, `defect.emp_discharge`, `defect.accidental_discharge`, `defect.engine_destroyed`.

## Method

1. Listed all DefectTemplate instances via `jiangyu templates list --type DefectTemplate` (33 found)
2. Inspected 5 samples across severity levels and naming prefixes
3. Compared field sets across all Jiangyu samples for stability
4. Surveyed 10 samples for Odin blob size variation
5. Extracted legacy `DefectTemplate` fields from `../MenaceAssetPacker/generated/schema.json` → `templates.DefectTemplate`
6. Computed set intersection and symmetric difference
7. Classified each delta field

## Results

### Jiangyu-vs-Jiangyu: field set stability

All 10 surveyed instances show **4 serialised fields** under `m_Structure` with an identical field set (names, kinds, type names):

| # | Name | Kind | Type |
|---|---|---|---|
| 0 | `serializationData` | object | Sirenix.Serialization.SerializationData |
| 1 | `DamageEffect` | reference | Menace.Tactical.Skills.SkillTemplate |
| 2 | `Severity` | enum | Menace.Tactical.DefectSeverity |
| 3 | `Chance` | int | Int32 |

The field set is perfectly stable. Only the Odin blob contents vary.

### Odin blob variation

| Sample | SerializedBytes | ReferencedUnityObjects |
|---|---|---|
| defect.armor_damaged | 464 | 0 |
| defect.fuel_leak | 464 | 0 |
| defect.shocked | 464 | 0 |
| defect.critical_hit | 464 | 0 |
| defect.walker_knocked_over | 464 | 0 |
| defect.emp_discharge | 464 | 0 |
| defect.accidental_discharge | 464 | 0 |
| defect.engine_destroyed | 469 | 1 |
| defect.big_fire | 474 | 2 |
| building.burning_medium_fire | 829 | 1 |

7 of 10 instances have identical 464-byte Odin payloads with no Unity object references — likely baseline (empty `DisqualifierConditions` and empty `SkillsRemoved`).

3 outliers have larger payloads and populated `ReferencedUnityObjects`. The referenced objects are MonoBehaviours (e.g. `damage_effect.big_fire`), consistent with populated interface-typed or collection-typed Odin fields that reference SkillTemplate instances.

### Jiangyu-vs-legacy: delta computation

| Metric | Count |
|---|---|
| Jiangyu fields (incl. Odin container) | 4 |
| Legacy fields | 5 |
| Shared fields | 3 |
| Legacy-only fields | 2 |
| Jiangyu-only fields | 1 |
| Net delta | 1 (5 − 4) |

### Legacy-only fields (2) — Odin-routed

| Field | Legacy type | Legacy category | Reason absent from Jiangyu |
|---|---|---|---|
| `DisqualifierConditions` | ITacticalCondition[] | collection | **interface-typed array** — Unity cannot natively serialise interface references; routed through Odin |
| `SkillsRemoved` | HashSet\<SkillTemplate\> | unknown | **HashSet** — not Unity-serialisable; routed through Odin |

Both fields use types that Unity's native serialisation excludes. `ITacticalCondition` is the same interface already confirmed as Odin-routed in 5 of 15 validated `SkillEventHandlerTemplate` concrete types. `HashSet<T>` is a .NET collection type Unity does not serialise natively.

The legacy schema marks `SkillsRemoved` as category `unknown`, which is consistent with a field the legacy extractor could not classify through standard Unity serialisation rules.

**No DataTemplate base class exclusions.** DefectTemplate does not inherit from DataTemplate, so the 4 standard base class fields (`m_ID`, `m_IsGarbage`, `m_IsInitialized`, `m_LocalizedStrings`) that appeared as legacy-only in all 6 previous audits are simply not in the picture.

### Jiangyu-only field (1) — Odin container

| Field | Kind | Type |
|---|---|---|
| `serializationData` | object | Sirenix.Serialization.SerializationData |

Standard Odin Serializer container. Present and non-empty on all 10 surveyed instances (minimum 464 bytes). The data for `DisqualifierConditions` and `SkillsRemoved` lives inside this blob.

### DefectSeverity enum

All 3 legacy enum values observed in live data:

| Value | Name | Samples |
|---|---|---|
| 0 | Light | defect.shocked, defect.walker_knocked_over, defect.emp_discharge |
| 1 | Medium | defect.armor_damaged, defect.fuel_leak, building.burning_medium_fire, defect.accidental_discharge |
| 2 | Heavy | defect.critical_hit, defect.big_fire, defect.engine_destroyed |

Exact match to legacy `DefectSeverity` enum definition.

### Shared field ordering

The 3 shared fields appear in the same relative order in both Jiangyu and legacy.

## Interpretation

What this validates:

- Jiangyu independently reproduces 3 of 5 legacy `DefectTemplate` fields from live game data
- the 4-field serialised contract is perfectly stable across 10 instances spanning all 3 severity levels, both naming prefixes, and multiple defect categories
- the 2 legacy-only fields are both Odin-routed (interface array + non-serialisable collection), consistent with the Odin pattern already established in SkillTemplate and its handlers
- the Odin blob variation across instances is consistent with some defects having populated `DisqualifierConditions` / `SkillsRemoved` and others having them empty
- the `DefectSeverity` enum matches legacy exactly (3 values)
- **the DataTemplate base class exclusions are absent** — correctly, because DefectTemplate does not inherit from DataTemplate

The broader structural rule that holds across all audited template types is not "4 DataTemplate base class exclusions + 1 Odin container." That was a lineage-specific instance. The actual universal rule is:

1. **Jiangyu sees the actual Unity-serialised contract.** Fields must pass Unity's serialisation rules (concrete types, `SerializeField` or public, not `NotSerialized`) to appear.
2. **Legacy may include additional managed or Odin-routed fields** that Unity's native serialisation excludes — interface types, abstract types, `NotSerialized` attributes, non-Unity collection types.
3. **`serializationData` is the Jiangyu-side marker when Odin is involved.** When the legacy schema lists fields with types Unity cannot serialise, Jiangyu will have `serializationData` instead, and the data for those fields lives inside the Odin blob.

For DataTemplate descendants, rule (2) manifests as both base class exclusions and Odin-routed fields. For non-DataTemplate types like DefectTemplate, only the Odin-routed component remains.

What this does **not** validate:

- Odin blob contents or decoding — the `SerializedBytes` payload was not parsed
- runtime behaviour of `DisqualifierConditions` or `SkillsRemoved`
- whether any additional managed-only fields exist on DefectTemplate beyond what the legacy schema lists
- field semantics or formulas

## Conclusion

The 4 vs 5 field delta is fully explained. No real mismatches.

| Classification | Count | Fields |
|---|---|---|
| matches | 3 | DamageEffect, Severity, Chance |
| legacy broader than serialised contract (Odin-routed) | 2 | DisqualifierConditions, SkillsRemoved |
| Jiangyu-only (serialisation framework) | 1 | serializationData |
| legacy broader than serialised contract (base class) | 0 | — |
| real mismatch | 0 | — |

DefectTemplate is the first audited template type outside the DataTemplate lineage. The absence of DataTemplate base class exclusions confirms that those exclusions are lineage-specific, not an artefact of the auditing method. The Odin-routing pattern persists independently — `serializationData` appears because DefectTemplate inherits from `SerializedScriptableObject` (the Odin base class), not because it inherits from DataTemplate.

This refines the universal delta rule from "4+1 pattern" to the more general principle: Jiangyu sees Unity's native serialised contract; legacy includes additional non-serialisable fields; `serializationData` is the bridge. The DataTemplate exclusions and the Odin substitutions are two independent effects that happen to co-occur in the DataTemplate lineage.

7 template families now audited at the top level. Only `TagTemplate` remains unaudited.

## Next Step

1. **TagTemplate top-level audit** — the last unaudited template type. Inherits from DataTemplate, so should show the standard 4+1 delta. Completes the full template type audit sweep.
2. **Classifier gap assessment** — determine whether non-Template-named template-like types extend beyond TileEffectTemplate and SkillEventHandlerTemplate families.
