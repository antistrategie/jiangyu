# Polymorphic Reference Array Survey Across Template Types

Date: 2026-04-15

## Goal

Determine whether the polymorphic ScriptableObject reference array pattern (validated for `SkillTemplate.EventHandlers`) exists in other template types, and classify all observed reference array fields as polymorphic or monomorphic.

## Why This Target

The next-support-type-candidates list identified "other polymorphic reference array fields" as the top priority. Three reference array fields were already validated (`SkillTemplate.EventHandlers`, `EntityTemplate.SkillGroups`, `EntityTemplate.DefectGroups`), but all were discovered under only two template types. The question is whether the pattern is a cross-cutting design or specific to those families.

## Samples

### Templates inspected (6 template types, 31 instances)

**PerkTemplate** (121 instances total, 9 inspected):
- perk.ambush, perk.berserk, perk.call_out_target, perk.barrage, perk.buff, perk.commando, perk.critical_hits, perk.deploy_decoy, perk.candyman

**EntityTemplate** (260 instances total, 11 inspected):
- player_squad.darby, enemy.pirate_scavengers, building_military_1x1_bunker, player_vehicle.modular_ifv, enemy.pirate_vehicle.chaingun_guntruck, enemy.vehicle_rogue_army_heavy_tank, player_vehicle.modular_walker_medium, player_vehicle.modular_walker_light, building_civilian_energy_generator_01, building_civilian_energy_generator_01_raid_target, building_civilian_energy_storage_01

**FactionTemplate** (7 instances total, 2 inspected):
- faction.pirates, faction.constructs

**UnitLeaderTemplate** (18 instances total, 1 inspected):
- squad_leader.darby

**OperationTemplate** (8 instances total, 2 inspected):
- operation.pirates_counterinsurgency, operation.constructs_story1

**Equipment templates** (5 inspected across ArmorTemplate, AccessoryTemplate, WeaponTemplate):
- armor.pirate_boarding_commando_armor_jetsuit, accessory.ammo_armor_piercing, accessory.ammo_backpack, accessory.scrap_bomb_frag_grenade_tier1, specialweapon.rocket_launcher_tier1_pal

### Concrete type verification

All reference array element types were verified by looking up the referenced pathId in Jiangyu's `templates list` output for the expected concrete type. This confirms the actual template type of each referenced object, not just the declared array element type.

## Method

1. Identified all polymorphic reference array candidates from the legacy schema (`../MenaceAssetPacker/generated/schema.json`) by parsing template definitions for array fields whose element type has multiple concrete subtypes in the legacy `inheritance` section.
2. Listed all template types available in Jiangyu's inventory.
3. Inspected representative samples across 6 template types using `jiangyu templates inspect`.
4. For each populated reference array field, recorded the array container shape and each element's reference target.
5. Verified concrete types of referenced objects via `templates list --type <Type>` lookups.
6. Classified each field as **confirmed polymorphic** (different concrete subtypes observed in the same or equivalent arrays) or **observed monomorphic** (all elements same concrete type in sampled data).
7. Cross-referenced against the legacy schema's inheritance hierarchy.

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates list --type <Type>
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type <Type> --name <name>
```

## Results

### Confirmed polymorphic reference arrays

#### 1. `PerkTemplate.EventHandlers` → `List<SkillEventHandlerTemplate>`

Same base type and structural pattern as the already-validated `SkillTemplate.EventHandlers`. Each element is a PPtr reference to a concrete `SkillEventHandlerTemplate` subclass.

Observed across 8 perks, 8 distinct handler types:

| Perk | Handler(s) |
|---|---|
| perk.ambush | ChangePropertyConditional |
| perk.berserk | AddSkill |
| perk.call_out_target | AddSkill, DisplayText |
| perk.barrage | ChangeActionPointCost |
| perk.buff | ChangeProperty, SetEntityFlag |
| perk.commando | AddSkill |
| perk.critical_hits | ChangeProperty |
| perk.deploy_decoy | SpawnEntity |
| perk.candyman | ChangeSupplyCosts |

8 distinct concrete handler types confirmed, plus `DisplayText` and `SetEntityFlag` are new types not previously observed in `SkillTemplate.EventHandlers` (where 15 types were validated).

This is expected: the legacy schema defines `PerkTemplate` with the same `EventHandlers` field. `PerkTemplate` inherits from `SkillTemplate` (confirmed below), so the field is inherited.

#### 2. `EntityTemplate.Items` → `List<ItemTemplate>`

3 concrete subtypes confirmed in the same arrays:

| Entity | Items | Concrete types |
|---|---|---|
| player_squad.darby | armor.player_fatigues, weapon.generic_carbine_tier1_spc | ArmorTemplate, WeaponTemplate |
| enemy.pirate_scavengers | armor.pirate_tier1_scavenger_armor_enemy, weapon.pirate_assault_rifle, specialweapon.rpg_launcher_tier_1, accessory.scrap_bomb_frag_grenade_tier1 | ArmorTemplate, WeaponTemplate, WeaponTemplate, AccessoryTemplate |
| enemy.pirate_vehicle.chaingun_guntruck | turret.pirate_medium_chaingun | WeaponTemplate |
| enemy.vehicle_rogue_army_heavy_tank | turret.medium_machinegun_heavy_tank, turret.local_forces_heavy_tank_01_15cm_howitzer | WeaponTemplate, WeaponTemplate |

Concrete types verified:
- `armor.pirate_tier1_scavenger_armor_enemy` → ArmorTemplate (pathId 112296)
- `weapon.pirate_assault_rifle` → WeaponTemplate (pathId 112531)
- `specialweapon.rpg_launcher_tier_1` → WeaponTemplate (pathId 112481)
- `accessory.scrap_bomb_frag_grenade_tier1` → AccessoryTemplate (pathId 112252)
- `turret.pirate_medium_chaingun` → WeaponTemplate (pathId 112433)
- `turret.medium_machinegun_heavy_tank` → WeaponTemplate (pathId 112429)

The legacy schema declares the element type as `ItemTemplate` with inheritance: SerializedScriptableObject → DataTemplate → BaseItemTemplate → ItemTemplate. ArmorTemplate, WeaponTemplate, and AccessoryTemplate are all concrete subtypes of BaseItemTemplate/ItemTemplate. Additional subtypes (VehicleItemTemplate, ModularVehicleWeaponTemplate, BlueprintTemplate, etc.) may also appear but were not observed in sampled entities.

#### 3. `EntityTemplate.Skills` → `List<SkillTemplate>`

2 concrete subtypes confirmed in the same array:

| Entity | Skills | Concrete types |
|---|---|---|
| player_vehicle.modular_walker_medium | perk.deploy_vehicle, active.stance.walker.get_up | PerkTemplate, SkillTemplate |
| player_vehicle.modular_walker_light | perk.deploy_vehicle | PerkTemplate |

Concrete types verified:
- `perk.deploy_vehicle` → PerkTemplate (pathId 114358)
- `active.stance.walker.get_up` → SkillTemplate (pathId 114509)

This establishes that **PerkTemplate inherits from SkillTemplate** at the serialized level. The `Skills` field is typed as `List<SkillTemplate>` but can hold PerkTemplate instances because PerkTemplate is a subclass.

Sparse: only 2 of 11 checked EntityTemplates have populated `Skills` arrays. This field appears to be used primarily for vehicle-specific direct skill grants, while infantry entities get their skills via `SkillGroups` instead.

### Observed monomorphic reference arrays

These fields contained only one concrete type across all sampled data. The declared field type may permit subtypes, but none were observed.

| Field | Declared element type | Observed concrete type | Samples |
|---|---|---|---|
| EntityTemplate.Tags | List\<TagTemplate\> | all TagTemplate | 5 entities, 1-4 tags each |
| EntityTemplate.Decoration | List\<PrefabListTemplate\> | all PrefabListTemplate | 3 buildings |
| EntityTemplate.SmallDecoration | List\<PrefabListTemplate\> | all PrefabListTemplate | 1 building |
| EntityTemplate.DestroyedDecoration | List\<PrefabListTemplate\> | all PrefabListTemplate | 3 buildings |
| FactionTemplate.Operations | OperationTemplate[] | all OperationTemplate | 2 factions |
| FactionTemplate.EnemyAssets | EnemyAssetTemplate[] | all EnemyAssetTemplate | 1 faction, 7 elements |
| FactionTemplate.OperationRewardTables | RewardTableTemplate[] | all RewardTableTemplate | 2 factions |
| UnitLeaderTemplate.PerkTrees | PerkTreeTemplate[] | all PerkTreeTemplate | 1 leader |
| Equipment.SkillsGranted | List\<SkillTemplate\> | all SkillTemplate | 5 equipment items |
| OperationTemplate.Durations | OperationDurationTemplate[] | all OperationDurationTemplate | 2 operations |

**Note on `Equipment.SkillsGranted`**: the declared type is `List<SkillTemplate>`, which permits PerkTemplate instances (since PerkTemplate inherits from SkillTemplate). All 5 checked equipment items referenced only SkillTemplate instances (`passive.encumbered`, `active.pirate_jetpack`, `passive.ammo_armor_piercing`, `active.rogue_ammo_runner_refill_ammo`, `active.throw_scrap_bomb_frag_tier1`), but this does not rule out PerkTemplate appearing in other equipment. Currently observed monomorphic, not universally monomorphic.

The same caveat applies in principle to `Tags` (where the declared type permits DataTemplate subtypes) and other fields with inheritance hierarchies wider than the observed data. None showed mixed types in sampled data.

### Empty / not assessable

| Field | Reason |
|---|---|
| OperationTemplate.IntroConversations | empty on both checked operations |
| OperationTemplate.VictoryConversations | empty on both checked operations |
| OperationTemplate.FailureConversations | empty on both checked operations |
| OperationTemplate.AbortConversations | empty on both checked operations |

These conversation arrays are declared as `ConversationTemplate[]` with `ScriptableObject` in the inheritance hierarchy. They may be populated on specific story operations or loaded via a different mechanism. Cannot assess polymorphism from empty arrays.

### Legacy schema comparison

The legacy schema (`../MenaceAssetPacker/generated/schema.json`) declares 62 array fields with polymorphic inheritance hierarchies across 37 template types. The most common patterns:

| Pattern | Legacy field count | Observed in Jiangyu |
|---|---|---|
| `List<TagTemplate>` | 24 | yes, monomorphic in all samples |
| `List<SkillTemplate>` | 7 | yes, polymorphic on EntityTemplate.Skills, monomorphic on equipment |
| `List<BaseItemTemplate>` / `List<ItemTemplate>` | 4 | yes, polymorphic on EntityTemplate.Items |
| `List<SkillEventHandlerTemplate>` | 2 | yes, polymorphic (SkillTemplate + PerkTemplate share it) |
| `ConversationTemplate[]` | 4 | not assessable (empty) |
| `List<PrefabListTemplate>` | 3 | yes, monomorphic |
| `OperationTemplate[]` / `RewardTableTemplate[]` | 5 | yes, monomorphic |
| Other (PlanetTemplate, FactionTemplate, etc.) | 13 | not checked in this pass |

## Interpretation

What this validates:

- The polymorphic ScriptableObject reference array pattern is **cross-cutting**, not SkillTemplate-specific. It appears in at least 3 distinct field families: `EventHandlers` (SkillEventHandlerTemplate subtypes), `Items` (ItemTemplate subtypes), and `Skills` (SkillTemplate + PerkTemplate).
- **PerkTemplate inherits from SkillTemplate** at the serialized level. This is established by the `EntityTemplate.Skills` field containing both PerkTemplate and SkillTemplate instances, and by `PerkTemplate.EventHandlers` sharing the same `List<SkillEventHandlerTemplate>` field type.
- `PerkTemplate.EventHandlers` introduces 2 new concrete handler types not previously seen in the SkillTemplate-side validation (`DisplayText`, `SetEntityFlag`), bringing the total known handler type inventory to 17.
- `EntityTemplate.Items` is the widest polymorphic array observed: at least 3 distinct concrete subtypes (ArmorTemplate, WeaponTemplate, AccessoryTemplate) in a single entity's 4-element array.
- Most legacy-declared polymorphic fields are **monomorphic in practice** across sampled data. The `List<TagTemplate>` pattern (24 legacy fields) showed no mixed types, nor did decoration, faction, or equipment skill grant arrays. However, this is a sample-based observation — wider polymorphism may exist in unsampled instances.
- The OperationTemplate conversation arrays could not be assessed (all empty). These remain an open question for a future pass.

What this does **not** validate:

- Runtime behaviour, field semantics, or managed inheritance chain details for any field
- Whether the remaining 13 legacy polymorphic patterns (PlanetTemplate neighbours, FactionTemplate hostile factions, etc.) also manifest in Jiangyu
- Whether `Equipment.SkillsGranted` can contain PerkTemplate in unsampled data
- The full extent of concrete subtypes that can appear in `EntityTemplate.Items` (VehicleItemTemplate, ModularVehicleWeaponTemplate, BlueprintTemplate, etc. were not observed)
- Odin serialisation blob contents for any referenced objects

## Conclusion

This is a successful cross-template structural survey that answers the candidate-1 question from the next-support-type-candidates list.

The main results are:

- **3 new polymorphic reference array fields confirmed** beyond the already-validated `SkillTemplate.EventHandlers`: `PerkTemplate.EventHandlers`, `EntityTemplate.Items`, `EntityTemplate.Skills`
- **The polymorphic reference array is a cross-cutting pattern** used across entity composition (Items, Skills), skill/perk behaviour (EventHandlers), and potentially other template families
- **PerkTemplate inherits from SkillTemplate**: first direct evidence from serialized data
- **10 monomorphic reference array fields classified**: Tags, Decoration, faction references, equipment grants, operation durations — all observed as same-concrete-type in sampled data, though declared types permit wider polymorphism
- **4 fields not assessable** (OperationTemplate conversation arrays, all empty)
- Zero mismatches between Jiangyu observations and legacy inheritance declarations. Every observed polymorphic field matches a legacy-declared polymorphic type. Every observed monomorphic field is consistent with (but narrower than) its legacy inheritance hierarchy.

## Next Step

The polymorphic reference array pattern is now well-characterised across the main template families. Good next targets:

1. **Template types not yet audited at the top level** (`TileEffectTemplate`, `TagTemplate`, `PerkTemplate`, `DefectTemplate`) — test whether the standard delta patterns (base class exclusions, Odin container) are universal beyond Entity/Weapon/Skill/AnimationSound
2. **OperationTemplate conversation arrays** — find an operation with populated conversation references to assess polymorphism
3. **Deeper EntityTemplate.Items validation** — inspect the referenced ArmorTemplate/AccessoryTemplate objects to compare their field sets against legacy, similar to the SkillEventHandlerTemplate concrete handler passes
