# ItemTemplate top-level audit

Date: 2026-04-16

## Scope

This note closes the remaining gap in the item hierarchy by documenting the shared
`ItemTemplate`-level serialized contract now that `WeaponTemplate`, `ArmorTemplate`,
and `AccessoryTemplate` have all been inspected through Jiangyu-native tooling.

This pass is structural only. It does **not** validate gameplay semantics.

## Samples used

- `weapon.generic_carbine_tier1_spc`
- `armor.player_fatigues`
- `accessory.ammo_backpack`
- `vehicle.chassis_ifv`

These four concrete item subtypes span the currently observed `EntityTemplate.Items`
family.

## Jiangyu-observed shared item base contract

Across all three concrete item subtypes, the shared serialized item base contains
**28 fields** before any subtype-specific additions:

- `serializationData`
- `m_GameDesignComment`
- `m_LocaState`
- `Title`
- `ShortName`
- `Description`
- `Icon`
- `Tags`
- `Rarity`
- `MinCampaignProgress`
- `TradeValue`
- `BlackMarketMaxQuantity`
- `IconEquipment`
- `IconEquipmentDisabled`
- `IconSkillBar`
- `IconSkillBarDisabled`
- `IconSkillBarAlternative`
- `IconSkillBarAlternativeDisabled`
- `SlotType`
- `OnlyEquipableBy`
- `ExclusiveCategory`
- `DeployCosts`
- `SkillsGranted`
- `Model`
- `VisualAlterationSlot`
- `ModelSecondary`
- `VisualAlterationSlotSecondary`
- `AttachLightAtNight`

This base contract is stable across all three inspected concrete item families.

## Concrete subtype relation

### AccessoryTemplate

`AccessoryTemplate` adds **zero** own serialized fields beyond the 28-field shared
item base. It is the purest currently validated subclass in the item hierarchy.

Observed count:

- `AccessoryTemplate`: **28** Jiangyu-observed serialized fields

### WeaponTemplate

`WeaponTemplate` adds its validated weapon-specific field family on top of the shared
item base.

Observed count:

- `WeaponTemplate`: **50** Jiangyu-observed serialized fields

### ArmorTemplate

`ArmorTemplate` adds **45** armor-specific serialized fields on top of the shared
item base. The field family previously validated in the armor/accessory pass matches
the prior schema structurally with zero gaps.

Observed count:

- `ArmorTemplate`: **73** Jiangyu-observed serialized fields

### VehicleItemTemplate

`VehicleItemTemplate` adds **2** own serialized fields beyond the shared item base:
`EntityTemplate` (PPtr reference to a vehicle `EntityTemplate`) and `AccessorySlots`
(`Int32`). This makes it the second-leanest ItemTemplate subtype after AccessoryTemplate.

Observed count:

- `VehicleItemTemplate`: **30** Jiangyu-observed serialized fields

## Delta classification

The shared item hierarchy refines the broader structural rule already established for
`DataTemplate` descendants:

- the standard `DataTemplate` base exclusions still apply
- `serializationData` is present on the Unity-serialized side
- `ItemType` is a managed-only field on the `ItemTemplate` base and is absent from the
  serialized contract across all three concrete subtypes

So the item hierarchy follows a **4 + 1 + 1** explanation:

- 4 legacy-only managed `DataTemplate` base fields
- 1 legacy-only managed `ItemTemplate.ItemType`
- 1 Jiangyu-only `serializationData` field

No real serialized mismatches were found.

## Odin

Odin is structurally present through `serializationData`, but the item hierarchy does
**not** currently show any validated Odin-routed gameplay fields:

- item hierarchy Odin blobs are empty in the sampled concrete types
- no interface/abstract-field substitution pattern was observed here

## Conclusion

Jiangyu now has field-level coverage for all 4 observed concrete `ItemTemplate` subtypes:

- `WeaponTemplate`
- `ArmorTemplate`
- `VehicleItemTemplate`
- `AccessoryTemplate`

The shared `ItemTemplate` base contract is a stable 28-field serialized structure.
`AccessoryTemplate` adds no serialized fields of its own, while `WeaponTemplate` and
`ArmorTemplate` extend that base with their validated subtype-specific fields.

## Provenance

- `2026-04-14-entity-weapon-schema-spot-check.md`
- `2026-04-16-armortemplate-accessorytemplate-field-level-spot-check.md`
- `2026-04-16-vehicleitemtemplate-field-level-spot-check.md`
