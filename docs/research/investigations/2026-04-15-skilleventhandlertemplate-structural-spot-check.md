# Legacy Support-Type Spot-Check: SkillEventHandlerTemplate

Date: 2026-04-15

## Goal

Do a structural validation pass on `Menace.Tactical.Skills.SkillEventHandlerTemplate`, the array element type used under `SkillTemplate.EventHandlers`, using Jiangyu-native template inspection to determine the structural pattern of this field.

## Why This Type

`SkillEventHandlerTemplate` is the next target because:

- it is the top-priority candidate from the next-support-type-candidates list
- the `SkillTemplate` top-level field audit is complete, making this the natural next nested type
- the legacy schema flags it as abstract with zero own fields and `base_class: SerializedScriptableObject`, suggesting it may introduce a structural pattern not seen in prior passes
- the legacy schema lists 119 concrete subclass types in `effect_handlers`, making the handler family the largest nested structure under any validated template type

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo.

### SkillTemplate parents (4 templates, 3 skill categories)

- `active.fire_assault_rifle_tier1_556` — standard projectile combat skill, `EventHandlers` count `2`
- `passive.ammo_armor_piercing` — passive buff skill, `EventHandlers` count `1`
- `active.deploy_explosive_charge` — thrown/deployed utility skill, `EventHandlers` count `2`
- `active.alien_stab_attack` — melee alien attack, `EventHandlers` count `1` (stability check for `Attack` type)

### Concrete handler instances inspected (5 distinct types + 1 duplicate)

| Instance name | pathId | Parent skill | Concrete type |
|---|---|---|---|
| `Attack` | 106858 | fire_assault_rifle_tier1_556 | Attack |
| `SynchronizeItemUses` | 116091 | fire_assault_rifle_tier1_556 | SynchronizeItemUses |
| `ChangePropertyConditional` | 116127 | ammo_armor_piercing | ChangePropertyConditional |
| `SwitchBetweenSkills` | 116323 | deploy_explosive_charge | SwitchBetweenSkills |
| `SpawnTileEffect` | 106221 | deploy_explosive_charge | SpawnTileEffect |
| `Attack` | 106932 | alien_stab_attack | Attack (stability check) |

## Method

For each SkillTemplate sample:

1. run `jiangyu templates inspect --type SkillTemplate --name <name>`
2. navigate to `m_Structure` → `EventHandlers`
3. record the array container shape and element kinds
4. for each referenced handler, inspect directly via `--collection resources.assets --path-id <id>`
5. record the `m_Structure` field set of each concrete handler type
6. compare field stability across two instances of the same concrete type (`Attack`)
7. compare each concrete type's field set against the legacy `effect_handlers` definition

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.fire_assault_rifle_tier1_556
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name passive.ammo_armor_piercing
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.deploy_explosive_charge
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.alien_stab_attack
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106858
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 116091
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 116127
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 116323
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106221
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106932
```

## Results

### Array container shape

All 4 SkillTemplate samples share the same array type declaration:

- `name = "EventHandlers"`
- `kind = "array"`
- `fieldTypeName = "System.Collections.Generic.List`1<Menace.Tactical.Skills.SkillEventHandlerTemplate>"`

Each element is a **PPtr reference** (`kind = "reference"`, `fieldTypeName = "PPtr<IObject>"`) to a separate MonoBehaviour asset. This is fundamentally different from prior array element passes (`PrefabAttachment`, `SkillOnSurfaceDefinition`), which contained inline struct elements with directly embedded fields.

### New structural category: polymorphic ScriptableObject reference array

The `EventHandlers` array is polymorphic:

- the base type `SkillEventHandlerTemplate` is abstract with zero own fields
- each referenced MonoBehaviour is a concrete subclass instance with its own distinct field set
- the legacy schema lists 119 concrete subclass types in `effect_handlers`
- all concrete subclasses inherit from `SerializedScriptableObject`, so all include a `serializationData` field (the Odin Serializer container)

This is the first validated instance of this structural pattern. All prior array element passes involved homogeneous inline structs.

### Concrete handler field sets

#### Attack (39 fields)

```
serializationData: object (Sirenix.Serialization.SerializationData)
ApplyMode: enum (ApplicationMode)
ElementsHit: int (Int32)
ElementsHitPercentage: float (Single)
FatalityType: enum (Menace.Tactical.FatalityType)
DismemberChance: int (Int32)
DismemberArea: enum (Menace.Tactical.RagdollHitArea)
DamageVisualizationType: enum (Menace.Tactical.DamageVisualizationType)
DestroyHalfCover: enum (Menace.Tactical.HalfCoverClass)
IsHalfCoverDestroyedOnAOECenterTileOnly: bool (Boolean)
Damage: float (Single)
DamageMult: float (Single)
DamageDropoff: float (Single)
DamageDropoffMult: float (Single)
DamageDropoffAOE: float (Single)
DamagePctCurrentHitpoints: float (Single)
DamagePctCurrentHitpointsMin: float (Single)
DamagePctMaxHitpoints: float (Single)
DamagePctMaxHitpointsMin: float (Single)
ArmorPenetration: float (Single)
ArmorPenetrationMult: float (Single)
ArmorPenetrationDropoff: float (Single)
ArmorPenetrationDropoffMult: float (Single)
ArmorPenetrationDropoffAOE: float (Single)
DamageToArmorDurability: float (Single)
DamageToArmorDurabilityMult: float (Single)
DamageToArmorDurabilityDropoff: float (Single)
DamageToArmorDurabilityDropoffMult: float (Single)
DamageToArmorDurabilityDropoffAOE: float (Single)
AccuracyBonus: float (Single)
AccuracyMult: float (Single)
AccuracyDropoff: float (Single)
AccuracyDropoffMult: float (Single)
Suppression: float (Single)
SuppressionDealtMult: float (Single)
SuppressionDropoffAOE: float (Single)
EntityFlagsRequired: enum (Menace.Tactical.EntityFlags)
TargetRequiresOneOfTheseTags: array (List<Menace.Tags.TagTemplate>)
TargetCannotHaveOneOfTheseTags: array (List<Menace.Tags.TagTemplate>)
```

#### ChangePropertyConditional (4 fields)

```
serializationData: object (Sirenix.Serialization.SerializationData)
Properties: array (Menace.Tactical.Skills.Effects.PropertyChange[])
Event: enum (EventType)
HideIfNotActive: bool (Boolean)
```

#### SpawnTileEffect (6 fields)

```
serializationData: object (Sirenix.Serialization.SerializationData)
Event: enum (ApplyOnEvent)
EffectToSpawn: reference (Menace.Tactical.TileEffects.TileEffectTemplate)
ChanceAtCenter: int (Int32)
ChancePerTileFromCenter: int (Int32)
DelayWithDistance: float (Single)
```

#### SwitchBetweenSkills (6 fields)

```
serializationData: object (Sirenix.Serialization.SerializationData)
IsVisibleAtStart: bool (Boolean)
SwitchToAlternativeItemIcon: bool (Boolean)
DisplayAOEAreaOfAlternateSkill: bool (Boolean)
SwitchWithSkill: reference (Menace.Tactical.Skills.SkillTemplate)
Mode: enum (DeactivateMode)
```

#### SynchronizeItemUses (1 field)

```
serializationData: object (Sirenix.Serialization.SerializationData)
```

No Unity-native fields at all. All meaningful data, if any, lives inside the Odin blob.

### Within-type stability

Two `Attack` instances were inspected (pathId 106858 from the assault rifle, pathId 106932 from the alien stab attack). Both show identical field names, kinds, and type names — 39 fields, all matching. The concrete type's field set is stable across instances.

### Jiangyu-vs-legacy comparison

#### Attack (Jiangyu 39 vs legacy 39)

| Metric | Count |
|---|---|
| Shared | 38 |
| Legacy-only | 1 (`DamageFilterCondition`: ITacticalCondition — interface, Odin-routed) |
| Jiangyu-only | 1 (`serializationData`) |

The Odin substitution pattern: legacy lists the interface-typed field directly, Jiangyu excludes it from the Unity-native type tree and stores its data in `serializationData`. This is the same pattern found in the SkillTemplate top-level audit.

#### ChangePropertyConditional (Jiangyu 4 vs legacy 4)

| Metric | Count |
|---|---|
| Shared | 3 (`Properties`, `Event`, `HideIfNotActive`) |
| Legacy-only | 1 (`Condition`: ITacticalCondition — interface, Odin-routed) |
| Jiangyu-only | 1 (`serializationData`) |

Same Odin substitution pattern.

#### SpawnTileEffect (Jiangyu 6 vs legacy 5)

| Metric | Count |
|---|---|
| Shared | 5 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields in legacy. `serializationData` is present because it is inherited from `SerializedScriptableObject`, but presumably contains minimal or empty Odin payload.

#### SwitchBetweenSkills (Jiangyu 6 vs legacy 5)

| Metric | Count |
|---|---|
| Shared | 5 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

Same pattern as SpawnTileEffect: no Odin-routed fields, `serializationData` is inherited.

#### SynchronizeItemUses (Jiangyu 1 vs legacy absent)

This concrete type is not defined in the legacy schema's `effect_handlers` section. Classification: **legacy narrower than observed concrete handler inventory**. The legacy schema's 119-type inventory does not fully cover the concrete handler types that exist in current game data.

### Summary table

| Concrete type | Jiangyu fields | Legacy fields | Shared | Legacy-only (Odin) | Jiangyu-only (serializationData) | Classification |
|---|---|---|---|---|---|---|
| Attack | 39 | 39 | 38 | 1 | 1 | Odin substitution, no real mismatch |
| ChangePropertyConditional | 4 | 4 | 3 | 1 | 1 | Odin substitution, no real mismatch |
| SpawnTileEffect | 6 | 5 | 5 | 0 | 1 | serializationData inherited, no real mismatch |
| SwitchBetweenSkills | 6 | 5 | 5 | 0 | 1 | serializationData inherited, no real mismatch |
| SynchronizeItemUses | 1 | — | — | — | — | legacy narrower than observed inventory |

No real mismatches in any inspected type.

## Interpretation

What this validates:

- `SkillTemplate.EventHandlers` is a **polymorphic ScriptableObject reference array**, not an inline embedded-type array. This is a new structural category not seen in prior passes (`PrefabAttachment`, `SkillOnSurfaceDefinition` were inline structs).
- the base type `SkillEventHandlerTemplate` is abstract with zero own fields, confirmed by both Jiangyu observation and legacy schema
- concrete handler subclasses have completely distinct field sets — the only shared field across all inspected types is `serializationData` (inherited from SerializedScriptableObject)
- the Odin substitution pattern (interface-typed legacy fields replaced by `serializationData` on the Jiangyu side) applies at the concrete subclass level, not only at the parent template level. This was confirmed in `Attack` (where `DamageFilterCondition: ITacticalCondition` is Odin-routed) and `ChangePropertyConditional` (where `Condition: ITacticalCondition` is Odin-routed).
- within-type stability is confirmed: two `Attack` instances from different skill categories have identical field sets
- the legacy schema's 119-type handler inventory is not exhaustive: at least one observed concrete type (`SynchronizeItemUses`) is absent

What this does **not** validate:

- runtime behaviour of any handler type (damage formulas, event triggers, skill switching logic)
- Odin blob contents or decoding within any handler
- the remaining 114 concrete handler types not inspected — this is a representative sample of 5 types, not exhaustive handler-family coverage
- whether additional concrete types beyond the legacy 119 exist in current game data
- managed inheritance chain details (base class layouts, non-serialised fields beyond what the Odin pattern explains)
- semantic meaning of fields (what `ApplyMode` values mean, how `DamageDropoffAOE` is applied, etc.)

## Conclusion

This is a successful structural validation pass that identifies a new structural category.

The main results are:

- **New pattern discovered**: polymorphic ScriptableObject reference array. `EventHandlers` elements are PPtr references to separate script-backed MonoBehaviour assets, each being a concrete subclass of the abstract `SkillEventHandlerTemplate`. This is structurally different from all previously validated array element types.
- **Odin substitution confirmed at concrete subclass level**: the pattern first found in the SkillTemplate top-level audit (interface/abstract-typed fields excluded from Unity serialisation, routed through `serializationData`) applies identically to the concrete handler types. This is a consistent cross-cutting pattern in the SerializedScriptableObject family.
- **Representative sample validates the pattern**: 5 concrete types inspected out of 119+ in the legacy inventory, spanning combat damage, conditional property changes, tile effects, skill switching, and item synchronisation. All follow the same structural rules. No real mismatches.
- **Legacy inventory is narrower than observed reality**: at least one concrete type (`SynchronizeItemUses`) exists in current game data but is absent from the legacy schema.

## Next Step

Good next targets:

1. Array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
2. WeaponTemplate deeper nested types — explore what nested types exist beyond the already-validated localisation wrappers
3. Broader concrete handler survey — spot-check a few more of the 119 legacy handler types to confirm the Odin substitution pattern holds across the family, and identify any additional legacy-absent types
4. Check other template types for the polymorphic reference array pattern — do any other template fields use the same abstract ScriptableObject reference list?
