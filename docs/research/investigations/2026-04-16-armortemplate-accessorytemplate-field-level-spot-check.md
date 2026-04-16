# Legacy Support-Type Spot-Check: ArmorTemplate and AccessoryTemplate

Date: 2026-04-16

## Goal

Do a field-level structural validation pass on the two non-WeaponTemplate concrete subtypes of `ItemTemplate` observed in `EntityTemplate.Items`: `ArmorTemplate` and `AccessoryTemplate`. Compare their serialised field sets against each other (Jiangyu-vs-Jiangyu), against the shared ItemTemplate base contract, and against the legacy schema.

## Why These Types

`EntityTemplate.Items` was confirmed as the widest polymorphic reference array in the prior cross-template survey (3 distinct concrete subtypes in a single entity's array). WeaponTemplate was already audited at the top level. ArmorTemplate and AccessoryTemplate have not been inspected at the field level. This pass completes the concrete subtype coverage for the `Items` array.

The legacy schema predicts an interesting asymmetry: ArmorTemplate should add ~45 own fields (armor stats, squad leader models, sounds), while AccessoryTemplate should add zero own fields beyond ItemTemplate. Confirming whether that holds in the actual serialised contract is the primary structural question.

## Samples

### ArmorTemplate (3 samples — 45 instances total in inventory)

- `armor.player_fatigues` (pathId 112281) — basic starting armour, low stats
- `armor.player_crafted_elbams_exoskeleton` (pathId 112278) — high-end crafted exoskeleton
- `armor.pirate_tier1_scavenger_armor_enemy` (pathId 112296) — enemy faction, already confirmed as ArmorTemplate in prior polymorphic survey

### AccessoryTemplate (3 samples — 71 instances total in inventory)

- `accessory.scrap_bomb_frag_grenade_tier1` (pathId 112252) — grenade, already confirmed as AccessoryTemplate in prior survey
- `accessory.ammo_backpack` (pathId 112206) — ammo/utility type
- `accessory.vehicle_adaptive_camouflage` (pathId 112385) — vehicle accessory

## Method

1. Inspected all 6 samples with `jiangyu templates inspect --type <Type> --name <name>`.
2. Extracted the `m_Structure` field set (name + fieldTypeName) for each sample.
3. Compared ArmorTemplate samples against each other for stability.
4. Compared AccessoryTemplate samples against each other for stability.
5. Computed AccessoryTemplate-vs-ArmorTemplate to isolate ArmorTemplate's own fields.
6. Compared both types against legacy schema (`../MenaceAssetPacker/generated/schema.json`).
7. Cross-checked the one unexpected legacy-only field (`ItemType`) against a WeaponTemplate instance to determine whether it is type-specific or base-class-level.

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type ArmorTemplate --name armor.player_fatigues
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type ArmorTemplate --name armor.player_crafted_elbams_exoskeleton
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type ArmorTemplate --name armor.pirate_tier1_scavenger_armor_enemy
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AccessoryTemplate --name accessory.scrap_bomb_frag_grenade_tier1
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AccessoryTemplate --name accessory.ammo_backpack
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AccessoryTemplate --name accessory.vehicle_adaptive_camouflage
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type WeaponTemplate --name weapon.generic_carbine_tier1_spc
```

## Results

### Jiangyu-vs-Jiangyu stability

**ArmorTemplate**: all 3 samples have identical field sets — 73 fields, same names, same types, same order. Perfectly stable across basic fatigues, crafted exoskeleton, and enemy faction armour.

**AccessoryTemplate**: all 3 samples have identical field sets — 28 fields, same names, same types, same order. Perfectly stable across grenade, ammo utility, and vehicle accessory.

### AccessoryTemplate-vs-ArmorTemplate (ItemTemplate base isolation)

AccessoryTemplate has 28 fields. ArmorTemplate has 73 fields. The first 28 fields of ArmorTemplate are identical to AccessoryTemplate's 28 fields (same names, same types, same order). ArmorTemplate adds 45 additional fields after `AttachLightAtNight`.

This confirms:
- **AccessoryTemplate adds zero own serialised fields** beyond the ItemTemplate contract
- ArmorTemplate adds exactly **45 own serialised fields** beyond the same ItemTemplate contract
- The ItemTemplate contract (as observed through AccessoryTemplate) is 28 fields including `serializationData`

### ArmorTemplate own fields (45 fields)

The 45 armor-specific fields fall into distinct functional groups:

| Group | Fields | Types |
|---|---|---|
| Gender model system | HasSpecialFemaleModels, MaleModels, FemaleModels | Boolean, GameObject[], GameObject[] |
| Squad leader models | SquadLeaderMode, SquadLeaderModelMale{White,Brown,Black}, SquadLeaderModelFemale{White,Brown,Black}, SquadLeaderModelFixed | enum + 7 GameObject refs |
| Scale/animation | OverrideScale, Scale, AnimSize | Boolean, Vector2, enum |
| Armour stats | Armor, DurabilityPerElement, DamageResistance, HitpointsPerElement, HitpointsPerElementMult | Int32, Int32, Single, Int32, Single |
| Combat modifiers | Accuracy, AccuracyMult, DefenseMult, Discipline, DisciplineMult, Vision, VisionMult, Detection, DetectionMult, Concealment, ConcealmentMult, SuppressionImpactMult, GetDismemberedChanceBonus, GetDismemberedChanceMult, ActionPoints, ActionPointsMult, AdditionalMovementCost | Int32/Single pairs |
| Equipment slots | ItemSlots | UInt32[] |
| Sound IDs | SoundOnMovementStep, SoundOnMovementSymbolic, SoundOnArmorHit, SoundOnHitpointsHit, SoundOnHitpointsHitFemale, SoundOnDeath, SoundOnDeathFemale | ID (bankId + itemId struct) |
| Sound reference | SoundOnMovementStepOverrides2 | SurfaceSoundsTemplate (PPtr reference) |

### Legacy comparison

**ArmorTemplate**: 73 Jiangyu fields vs 77 legacy fields.

| Category | Count | Fields |
|---|---|---|
| Shared | 72 | all ItemTemplate fields (minus 5 legacy-only) + all 45 armor-specific fields |
| Jiangyu-only | 1 | `serializationData` (Odin container — standard) |
| Legacy-only | 5 | `m_ID`, `m_IsGarbage`, `m_IsInitialized`, `m_LocalizedStrings` (DataTemplate base class exclusions — standard), `ItemType` (new — see below) |

**AccessoryTemplate**: 28 Jiangyu fields vs 32 legacy fields.

| Category | Count | Fields |
|---|---|---|
| Shared | 27 | entire ItemTemplate serialised contract |
| Jiangyu-only | 1 | `serializationData` (Odin container — standard) |
| Legacy-only | 5 | same 4 DataTemplate base class exclusions + `ItemType` |

**Type name comparison on shared fields**: all "mismatches" are trivial notation differences between Jiangyu's .NET type names and legacy's shorthand (Int32 vs int, Single vs float, Boolean vs bool, fully qualified generic types vs short form). Zero semantic type mismatches.

**Zero real mismatches** on either type.

### ItemType: new legacy-only managed field

`ItemType` (type: `ItemType` enum, legacy offset 0xEC) appears in the legacy schema for all three ItemTemplate subtypes — ArmorTemplate, AccessoryTemplate, and WeaponTemplate — but is absent from Jiangyu's serialised output for all three.

In the legacy schema it sits between `SlotType` (offset 0xE8) and `OnlyEquipableBy` (offset 0xF0). In Jiangyu's output, `SlotType` is followed directly by `OnlyEquipableBy` — the field is cleanly absent, not relocated.

Cross-checked against `weapon.generic_carbine_tier1_spc` (WeaponTemplate): also absent. This confirms `ItemType` is a managed-only field on the `ItemTemplate` base class, not serialised by Unity across any concrete subtype.

Classification: **legacy broader than serialised contract**. Same classification as the DataTemplate base class exclusions, but at the ItemTemplate level. Likely a `[NonSerialized]` or property-backed field computed at runtime from other data.

### Odin serialisation data

All 6 samples have completely empty Odin blobs (SerializedBytes=0, ReferencedUnityObjects=0, SerializationNodes=0). No Odin-routed fields exist on ArmorTemplate, AccessoryTemplate, or any of their base classes (ItemTemplate, BaseItemTemplate). All serialised fields are natively Unity-serialisable.

### Delta pattern

Both types show a **4+1+1 delta**:

- 4 DataTemplate base class exclusions (standard, inherited from DataTemplate)
- 1 `ItemType` (new, managed-only field on ItemTemplate)
- 1 `serializationData` (standard, Jiangyu-only Odin container)

This extends the standard DataTemplate 4+1 pattern with one additional managed-only field at the ItemTemplate level.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialised contract for both ArmorTemplate and AccessoryTemplate from live game data
- both field sets are perfectly stable across all sampled instances
- **AccessoryTemplate adds zero own serialised fields** beyond ItemTemplate — it is a pure subclass with no additional serialised state. Confirmed in both Jiangyu and legacy.
- **ArmorTemplate adds exactly 45 own serialised fields** beyond ItemTemplate, all present and matching legacy at the field-name level. The armor-specific field family spans gender models, squad leader models, scale/animation, armour stats, combat modifiers, equipment slots, and sound IDs.
- **`ItemType` is a new managed-only field** discovered at the ItemTemplate level. It extends the known legacy-only field inventory for the item hierarchy. Absent from all 3 concrete subtypes' serialised data.
- the Odin blob is empty across all item types — no Odin-routed fields at any level of the item hierarchy
- the `EntityTemplate.Items` polymorphic array's concrete subtypes now have full field-level coverage: WeaponTemplate (prior audit), ArmorTemplate (this pass), AccessoryTemplate (this pass)

What this does **not** validate:

- runtime behaviour, formulas, or semantic meaning of any field (including armour stat calculations, item slot semantics, squad leader model selection logic)
- the remaining concrete subtypes that may appear in `Items` arrays but were not observed in prior sampling: VehicleItemTemplate, ModularVehicleWeaponTemplate, BlueprintTemplate
- the full `ItemType` enum values or what drives the field at runtime
- managed inheritance layout or memory offsets

## Conclusion

This is a successful field-level structural validation pass for both EntityTemplate.Items concrete subtypes.

The main results are:

- **AccessoryTemplate has zero own serialised fields.** It inherits the entire ItemTemplate contract unchanged. This is the clearest "pure subclass" finding in the validation series — even PerkTemplate added 1 field to SkillTemplate.
- **ArmorTemplate's 45 armor-specific fields are an exact match** to the legacy schema. All field names, all types, no gaps, no extras.
- **`ItemType` is a new managed-only field** at the ItemTemplate level, confirmed absent across all 3 concrete subtypes (ArmorTemplate, AccessoryTemplate, WeaponTemplate). Extends the known delta inventory.
- **All 3 concrete subtypes of EntityTemplate.Items now have full field-level coverage.** Together with the WeaponTemplate top-level audit, the item hierarchy's serialised contract is fully validated for the observed subtypes.

## Next Step

Good next targets:

1. **TemplateClassifier extension decision** — determine whether the classifier needs to detect concrete subtypes of known abstract template families (TileEffectTemplate, SkillEventHandlerTemplate) before template-patching work begins, or whether PPtr traversal from the existing index is sufficient
2. **VehicleItemTemplate field-level check** — the only remaining ItemTemplate subtype likely to appear in player-relevant `Items` arrays but not yet observed; requires finding an entity with a VehicleItemTemplate in its Items
3. **ItemTemplate top-level audit** — now that concrete subtypes are validated, a formal ItemTemplate-level audit entry would document the base contract (including the `ItemType` managed-only finding) as a standalone record
