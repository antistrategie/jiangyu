# Legacy Support-Type Spot-Check: VehicleItemTemplate

Date: 2026-04-16

## Goal

Do a field-level structural validation pass on `VehicleItemTemplate`, the fourth and final concrete `ItemTemplate` subtype in the inventory. Compare its serialised field set against the shared ItemTemplate base contract (Jiangyu-vs-Jiangyu), and against the legacy schema.

## Why This Type

`VehicleItemTemplate` is the only remaining `ItemTemplate` subtype that had not been field-level audited. The prior passes validated WeaponTemplate, ArmorTemplate, and AccessoryTemplate. The TODO listed this as an optional pass contingent on finding populated instances. 9 instances exist in the inventory, all populated with meaningful data. This pass completes field-level coverage of the entire observed concrete `ItemTemplate` family.

## Samples

3 samples chosen from the 9 instances, spanning three distinct vehicle categories:

- `vehicle.chassis_ifv` (pathId 112440) — wheeled IFV chassis, player-obtainable
- `vehicle.captured_rogue_heavy_tank` (pathId 112437) — captured enemy heavy tank
- `vehicle.chassis_walker_heavy` (pathId 112442) — heavy walker chassis

## Method

1. Inspected all 3 samples with `jiangyu templates inspect --type VehicleItemTemplate --name <name>`.
2. Extracted the `m_Structure` field set (name + kind + fieldTypeName) for each sample.
3. Compared all 3 samples against each other for stability.
4. Compared against the shared 28-field ItemTemplate base contract established in the prior ArmorTemplate/AccessoryTemplate pass.
5. Compared against legacy schema (`../MenaceAssetPacker/generated/schema.json`).

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type VehicleItemTemplate --name vehicle.chassis_ifv
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type VehicleItemTemplate --name vehicle.captured_rogue_heavy_tank
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type VehicleItemTemplate --name vehicle.chassis_walker_heavy
```

## Results

### Jiangyu-vs-Jiangyu stability

All 3 samples have identical field sets — **30 fields**, same names, same types, same order. Perfectly stable across wheeled IFV, captured tank, and heavy walker categories.

### VehicleItemTemplate-vs-ItemTemplate base

The first 28 fields of VehicleItemTemplate are identical to the shared ItemTemplate base contract (same names, types, and order as AccessoryTemplate's full field set). VehicleItemTemplate adds exactly **2 own serialised fields** after `AttachLightAtNight`:

| Field | Kind | Type |
|---|---|---|
| `EntityTemplate` | reference | `Menace.Tactical.EntityTemplate` |
| `AccessorySlots` | int | `Int32` |

### Legacy comparison

30 Jiangyu fields vs 34 legacy fields.

| Category | Count | Fields |
|---|---|---|
| Shared | 29 | all ItemTemplate fields (minus 5 legacy-only) + both vehicle-specific fields |
| Jiangyu-only | 1 | `serializationData` (Odin container — standard) |
| Legacy-only | 5 | `m_ID`, `m_IsGarbage`, `m_IsInitialized`, `m_LocalizedStrings` (DataTemplate base class exclusions — standard), `ItemType` (ItemTemplate managed-only — established in prior pass) |

**Same 4+1+1 delta** as ArmorTemplate and AccessoryTemplate. **Zero real mismatches.**

### Odin serialisation data

All 3 samples have completely empty Odin blobs (SerializedBytes=0, ReferencedUnityObjects=0, SerializationNodes=0). No Odin-routed fields exist on VehicleItemTemplate or any of its base classes.

### Data observations

All 3 samples have populated `EntityTemplate` references pointing to `player_vehicle.*` EntityTemplates:

- `vehicle.chassis_ifv` → `player_vehicle.modular_ifv` (pathId 112148)
- `vehicle.captured_rogue_heavy_tank` → `player_vehicle.cveh_heavy_tank` (pathId 112145)
- `vehicle.chassis_walker_heavy` → `player_vehicle.modular_walker_medium` (pathId 112152)

`AccessorySlots` varies: IFV has 2, tank and walker have 1.

All samples have `SlotType` = 4, `Model` = null, `SkillsGranted` = empty. Vehicle chassis items carry no direct model or skills — they serve as chassis-to-entity bindings with an accessory slot count.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialised contract for VehicleItemTemplate from live game data
- the field set is perfectly stable across all 3 sampled vehicle categories
- **VehicleItemTemplate adds exactly 2 own serialised fields** beyond the shared ItemTemplate base: `EntityTemplate` (PPtr reference to a vehicle EntityTemplate) and `AccessorySlots` (Int32)
- the same 4+1+1 delta pattern (DataTemplate base exclusions + ItemType managed-only + serializationData) holds across all 4 concrete ItemTemplate subtypes
- **all 4 concrete `ItemTemplate` subtypes now have complete field-level coverage**: WeaponTemplate (50 fields), ArmorTemplate (73), VehicleItemTemplate (30), AccessoryTemplate (28)
- the Odin blob is empty, consistent with the rest of the item hierarchy

What this does **not** validate:

- runtime behaviour or semantic meaning of `EntityTemplate` or `AccessorySlots` fields (e.g. how the chassis-to-entity binding is resolved at runtime, how accessory slots constrain vehicle loadouts)
- whether `ModularVehicleWeaponTemplate` or `BlueprintTemplate` also appear in `EntityTemplate.Items` arrays (those subtypes were not observed in prior sampling and are out of scope for this pass)
- managed inheritance layout or memory offsets

## Conclusion

This is a successful field-level structural validation pass for VehicleItemTemplate.

The main results are:

- **VehicleItemTemplate adds exactly 2 own serialised fields** (`EntityTemplate`, `AccessorySlots`) to the 28-field shared ItemTemplate base. This makes it the second-leanest ItemTemplate subtype after AccessoryTemplate (0 own fields).
- **Zero real mismatches** against the legacy schema. The 4+1+1 delta is identical to all other ItemTemplate subtypes.
- **This completes field-level coverage of the entire observed concrete `ItemTemplate` family.** All 4 subtypes (WeaponTemplate, ArmorTemplate, AccessoryTemplate, VehicleItemTemplate) are now structurally validated with zero real mismatches across any of them.

## Next Step

Good next targets:

1. **TemplateClassifier extension decision** — determine whether the classifier needs to detect concrete subtypes of known abstract template families (TileEffectTemplate, SkillEventHandlerTemplate) before template-patching work begins, or whether PPtr traversal from the existing index is sufficient
2. **Formal ItemTemplate-level audit update** — add VehicleItemTemplate to the ItemTemplate top-level audit record now that all 4 concrete subtypes are validated
