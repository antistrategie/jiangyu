# Legacy Top-Level Field Audit: SkillTemplate

Date: 2026-04-15

## Goal

Classify the 120 vs 128 field delta between Jiangyu's observed `SkillTemplate` serialized contract and the legacy schema at `../MenaceAssetPacker/generated/schema.json`.

## Why This Target

The SkillOnSurfaceDefinition spot-check (same date) found all 3 inspected `SkillTemplate` samples showing 120 serialized fields under `m_Structure`, while the legacy schema lists 128. The 8-field net delta was flagged as a candidate for a top-level field audit. With the first `SkillTemplate` nested type now validated, the top-level audit is the natural next step.

This also extends the top-level field audit pattern beyond `EntityTemplate` (109 vs 112) and `WeaponTemplate` (50 vs 54) — both of which showed only base class / managed-only deltas. The question is whether `SkillTemplate` introduces any new delta categories.

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `active.fire_assault_rifle_tier1_556` — standard projectile combat skill
- `passive.ammo_armor_piercing` — passive buff skill
- `active.deploy_explosive_charge` — thrown/deployed utility skill

These span three distinct skill categories: active combat, passive, and active utility/deploy.

## Method

For each sample:

1. run `jiangyu templates inspect --type SkillTemplate --name <name>`
2. extract `m_Structure` field names, kinds, and type names
3. compare across all 3 Jiangyu samples for stability
4. extract the 128 legacy field names from `../MenaceAssetPacker/generated/schema.json` → `templates.SkillTemplate.fields`
5. compute set intersection and symmetric difference
6. classify each delta field

## Results

### Jiangyu-side stability

All 3 samples show **120 serialized fields** under `m_Structure` with an identical field set (names, kinds, type names). The field set is stable across all 3 skill categories.

### Delta computation

| Metric | Count |
|---|---|
| Jiangyu fields | 120 |
| Legacy fields | 128 |
| Shared fields | 119 |
| Legacy-only fields | 9 |
| Jiangyu-only fields | 1 |
| Net delta | 8 (128 − 120) |

### Legacy-only fields (9)

#### Group 1: base class / managed-only (4 fields)

Same pattern already confirmed in EntityTemplate and WeaponTemplate passes. The inheritance chain is `SerializedScriptableObject` → `DataTemplate` → `SkillTemplate`.

| Field | Legacy type | Legacy category | Reason absent from Jiangyu |
|---|---|---|---|
| `m_ID` | string | string | `NotSerialized` attribute (base class) |
| `m_IsGarbage` | bool | primitive | `NotSerialized` attribute (base class) |
| `m_IsInitialized` | bool | primitive | `NotSerialized` attribute (base class) |
| `m_LocalizedStrings` | BaseLocalizedString[] | collection | private, no `SerializeField` attribute |

These are the same 4 base class fields that appeared as legacy-only in EntityTemplate (where `m_LocalizedStrings` was also absent) and WeaponTemplate (where all 4 plus `ItemType` were absent). The explanation is consistent: Unity's serialization rules exclude them from the current serialized contract.

#### Group 2: Odin-serialized fields (5 fields)

This is a **new classification** not seen in prior passes. These 5 fields have interface or abstract types that Unity's native serialization cannot handle.

| Field | Legacy type | Legacy category | Reason absent from Jiangyu |
|---|---|---|---|
| `CustomAoEShape` | ICustomAoEShape | reference | **interface** type — excluded by Unity serialization |
| `AoEFilter` | ITacticalCondition | reference | **interface** type — excluded by Unity serialization |
| `ProjectileData` | BaseProjectileData | reference | **abstract** base class — excluded by Unity serialization |
| `SecondaryProjectileData` | BaseProjectileData | reference | **abstract** base class — excluded by Unity serialization |
| `AIConfig` | SkillBehavior | reference | likely Odin-routed — not confirmed as abstract/interface directly, but absent from Unity's serialized type tree and consistent with the Odin pattern |

AssetRipper's `FieldSerializer.Logic.cs` explicitly checks `!resolvedTypeDeclaration.IsAbstract` and `!resolvedTypeDeclaration.IsInterface` before including fields in the serializable type. Fields that fail these checks are excluded from the type tree entirely.

The data for these fields is instead routed through Odin Serializer (Sirenix) and stored inside the `serializationData` blob — a concrete field that Jiangyu does observe.

#### Evidence for Odin routing

The `serializationData` field contains a `SerializedBytes` array and a `ReferencedUnityObjects` list. Both vary across skill categories in ways consistent with the 5 missing fields:

| Sample | SerializedBytes | ReferencedUnityObjects | Expected content |
|---|---|---|---|
| fire (assault rifle) | 1267 bytes | 1 ("Tracer" GameObject) | projectile data with tracer |
| passive (ammo) | 456 bytes | 0 | no projectile/AoE, minimal Odin payload |
| deploy (explosive charge) | 1536 bytes | 1 ("grenade" GameObject) | projectile data with grenade |

- combat skills with projectiles have more Odin-serialized data and Unity object references (tracers, grenades)
- passive skills with no projectiles or AoE have minimal Odin payload
- the referenced GameObjects correspond to what `ProjectileData` would be expected to point at

This is circumstantial but strongly consistent. The actual data is present in the serialized asset — it is just stored in the Odin blob, not as separate Unity-native fields.

### Jiangyu-only field (1)

| Field | Kind | Type |
|---|---|---|
| `serializationData` | object | Sirenix.Serialization.SerializationData |

This is the Odin Serializer container. It is a concrete public field that passes Unity's serialization filters, so AssetRipper includes it in the type tree. The legacy schema does not list it because it is a serialization framework field, not a SkillTemplate-specific gameplay field.

The `serializationData` field has a stable internal structure across all 3 samples:

- `SerializedFormat` (enum)
- `SerializedBytes` (byte array — the Odin-encoded payload)
- `ReferencedUnityObjects` (list of Unity object references used by the Odin payload)
- `SerializedBytesString` (empty string)
- `Prefab` (null reference)
- `PrefabModificationsReferencedUnityObjects` (empty list)
- `PrefabModifications` (empty list)
- `SerializationNodes` (empty list)

### Shared field ordering

The 119 shared fields appear in the same relative order in both Jiangyu and legacy. No field reordering was observed.

## Interpretation

What this validates:

- Jiangyu independently reproduces 119 of 128 legacy `SkillTemplate` fields from live game data
- the 120-field serialized contract is stable across 3 skill categories (active combat, passive, active utility)
- the 4 base class legacy-only fields follow the same pattern already confirmed in EntityTemplate and WeaponTemplate — `NotSerialized` or non-`SerializeField` exclusions
- the 5 Odin-routed legacy-only fields are a new classification: interface/abstract-typed fields that Unity cannot natively serialize, routed through Odin Serializer into the `serializationData` blob
- `serializationData` is the Jiangyu-only field that compensates for the 5 missing Odin fields — the data exists, just stored differently than the legacy schema assumes

What this does **not** validate:

- Odin blob contents or decoding — the `SerializedBytes` payload was not parsed
- runtime behaviour of the 5 Odin-routed fields
- whether `AIConfig` is specifically abstract or interface (classified as "likely Odin-routed" based on indirect evidence)
- field semantics or formulas
- `SkillEventHandlerTemplate` array element shape (that is a separate nested support type)

## Conclusion

The 120 vs 128 delta is fully explained. No real mismatches.

| Classification | Count | Fields |
|---|---|---|
| matches | 119 | (all shared fields) |
| legacy broader than serialized contract (base class / managed-only) | 4 | `m_ID`, `m_IsGarbage`, `m_IsInitialized`, `m_LocalizedStrings` |
| legacy broader than serialized contract (Odin-routed) | 5 | `CustomAoEShape`, `AoEFilter`, `ProjectileData`, `SecondaryProjectileData`, `AIConfig` |
| Jiangyu-only (serialization framework) | 1 | `serializationData` |
| real mismatch | 0 | — |

The new finding is that `SkillTemplate` is the first template type where the Jiangyu-vs-legacy delta includes Odin-serialized fields, not just base class exclusions. This means the legacy schema includes fields from both Unity's native serialization and Odin's parallel serialization, without distinguishing between them. Jiangyu's serialized type tree correctly reflects only the Unity-native layer, while the Odin layer lives inside the `serializationData` blob.

This classification pattern may apply to other template types that use interface/abstract-typed fields. Future top-level audits should check for `serializationData` as a signal.

## Next Step

Good next targets:

1. Array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
2. WeaponTemplate deeper nested types beyond `LocalizedLine`/`LocalizedMultiLine`/`OperationResources`
3. `SkillEventHandlerTemplate` array element shape — large and likely rich, nested under `SkillTemplate.EventHandlers`
4. Check other template types for the Odin serialization pattern (look for `serializationData` + interface/abstract-typed legacy fields)
