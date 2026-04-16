# Legacy Schema Spot-Check: EntityTemplate and WeaponTemplate

Date: 2026-04-14

## Goal

Use Jiangyu's own offline template inventory and object inspection to perform the first real structural validation pass against legacy `MenaceAssetPacker` schema entries.

## Method

Selected from Jiangyu's own `template-index.json`:

- `EntityTemplate`
  - `bunker`
  - `allied.local_forces_basic_infantry`
- `WeaponTemplate`
  - `turret.construct_gunslinger_twin_heavy_auto_repeater`
  - `specialweapon.generic_designated_marksman_rifle_tier1`

For each sample:

1. run `jiangyu templates inspect`
2. inspect the resolved `m_Structure` payload
3. compare the observed `m_Structure.fields[].name` set against the legacy schema field list for that type

This pass validates structure only, not semantics.

## Results

### EntityTemplate

Observed from both Jiangyu-native samples:

- `m_Structure.fieldTypeName = "EntityTemplate"`
- both samples produced the same top-level field set
- observed field count: `109`
- legacy schema field count: `112`

Fields present in legacy schema but not observed in Jiangyu:

- `m_ID`
- `m_IsGarbage`
- `m_IsInitialized`
- `m_LocalizedStrings`

Field present in Jiangyu but not in legacy schema:

- `serializationData`

Interpretation:

- the core structural field set is overwhelmingly aligned
- the missing legacy-only fields look like base/serialization-framework fields rather than `EntityTemplate`-specific gameplay fields
- Jiangyu's extra `serializationData` field appears to come from the imported serializable object layer rather than gameplay-specific template schema

### WeaponTemplate

Observed from both Jiangyu-native samples:

- `m_Structure.fieldTypeName = "WeaponTemplate"`
- both samples produced the same top-level field set
- observed field count: `50`
- legacy schema field count: `54`

Fields present in legacy schema but not observed in Jiangyu:

- `ItemType`
- `m_ID`
- `m_IsGarbage`
- `m_IsInitialized`
- `m_LocalizedStrings`

Field present in Jiangyu but not in legacy schema:

- `serializationData`

Follow-up investigation:

- Jiangyu's `MonoScript -> TypeDefinition` chain for `Menace.Items.WeaponTemplate` still contains inherited `ItemTemplate.ItemType`
- the generated AssetRipper `SerializableType` for `WeaponTemplate` does **not** include that field because `ItemTemplate.ItemType` is marked `NotSerialized` in the current managed metadata
- `m_ID`, `m_IsGarbage`, and `m_IsInitialized` are also `NotSerialized`
- `m_LocalizedStrings` is private and does not have a `SerializeField` attribute, so current Unity serialization rules also exclude it

Interpretation:

- again, the core structural field set is strongly aligned
- the `m_*` legacy-only fields look like base-layer metadata rather than gameplay-specific weapon fields
- the legacy-only fields are explained by current serialization rules rather than a Jiangyu extraction failure
- the legacy schema appears to include managed/in-editor fields that are not part of MENACE's current serialized asset contract

## Confidence

Moderate for structural alignment of these two types.

Why not higher:

- this compares Jiangyu-imported structures, not yet raw serialized type-tree reconstruction
- this pass still checks top-level field presence only, not nested field details or semantics

Note:

- Jiangyu inspection was later improved to recover enum names from the managed type chain for fields that are present in the serialized structure
- this helps validation readability while keeping the serialized field-set itself faithful to the current asset contract

## Conclusion

For `EntityTemplate` and `WeaponTemplate`, the legacy schema appears directionally trustworthy at the structural field-set level, but not exact.

What this validates:

- legacy template names are real
- legacy field sets are mostly real
- Jiangyu can now reproduce those structures independently from game data
- the first investigated legacy-only field mismatches are explained by current Unity serialization behavior, not by a remaining Jiangyu fidelity bug

What remains unverified:

- nested support-type structure
- enum/type precision
- field semantics
- formula/behavior claims

## Next Step

1. Repeat this pass for nested support types that Jiangyu will actually depend on.
2. Repeat the same managed-metadata cross-check whenever legacy schema includes fields Jiangyu does not observe.
3. Extend validation from top-level field sets into nested support types and selected field semantics.
