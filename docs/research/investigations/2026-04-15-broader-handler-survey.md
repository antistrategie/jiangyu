# Legacy Support-Type Spot-Check: SkillEventHandlerTemplate Broader Concrete Handler Survey

Date: 2026-04-15

## Goal

Broaden the structural validation of concrete `SkillEventHandlerTemplate` subclass types beyond the initial 5-type sample. The first pass (see `2026-04-15-skilleventhandlertemplate-structural-spot-check.md`) established the polymorphic reference array pattern and sampled `Attack`, `ChangePropertyConditional`, `SpawnTileEffect`, `SwitchBetweenSkills`, and `SynchronizeItemUses`. This survey adds 10 more types spanning all legacy size bands, to determine whether the Odin substitution pattern, field-set alignment, and `serializationData` universality hold across the handler family.

## Why This Pass

- the initial 5-type sample was representative but covered only ~4% of the legacy 119-type inventory
- the next-support-type-candidates document lists this as the top priority
- the user wants answers to four specific structural questions:
  1. how broad the Odin-substitution pattern really is
  2. whether legacy inventory remains narrower than observed reality
  3. whether field-count/field-set alignment stays strong outside the first sample
  4. whether `serializationData` remains the only universal shared field

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo.

### Selection criteria

Types chosen to maximise cross-section quality over raw count:

- **1 large** (16+ fields): the only type in this band besides the already-validated `Attack`
- **4 medium** (6–15 fields): spanning combat, utility, stat-modification, and detection domains
- **5 small** (1–5 fields): spanning vehicle, morale, defence, suppression, cooldown, and death-trigger domains

### Parent SkillTemplate instances used for handler discovery

| SkillTemplate | Handlers found | Reason chosen |
|---|---|---|
| `effect.bleeding` | DamageOverTime, LifetimeLimit, AttachObject, PlaySound | DoT / damage-over-time domain |
| `active.shoot_flamethrower` | Attack, AttackProc ×2, ClearTileEffectGroup, Cooldown, AttackMorale | combat + cooldown |
| `active.acid_damage_short` | Attack, AddSkill | skill-adding on damage |
| `active.use_motion_scanner` | Scanner, DisplayText | detection/utility |
| `effect.sprint` | LifetimeLimit, ChangeMovementCost, ChangeProperty ×2, AddSkill | stat modification |
| `special.vehicle_movement` | VehicleMovement | vehicle domain |
| `special.suppression` | Suppression | suppression control |
| `effect.aura.high_spirits` | MoraleOverMaxEffect | morale/aura |
| `racial.carrier_explosion_death` | Deathrattle | death trigger |
| `passive.vehicle_era_armor` | LimitedPassiveUses, IgnoreDamage, DisplayText | defensive vehicle |

### Concrete handler instances inspected (10 distinct types + 2 stability checks)

| Instance name | pathId | Parent skill | Size band | Why chosen |
|---|---|---|---|---|
| DamageOverTime | 107150 | effect.bleeding | large (16) | only other large handler besides Attack |
| AddSkill | 115850 | acid_damage_short | medium (12) | buff/skill-adding domain, highest medium field count |
| ChangeProperty | 107502 | effect.sprint | medium (7) | core stat modification, widely used |
| AttackProc | 116256 | shoot_flamethrower | medium (7) | combat proc mechanism |
| Scanner | 106462 | use_motion_scanner | medium (6) | detection/utility, likely unique fields |
| Cooldown | 107398 | shoot_flamethrower | small (2) | universal game mechanic |
| Deathrattle | 107391 | carrier_explosion_death | small (2) | death-trigger mechanism |
| VehicleMovement | 106329 | vehicle_movement | small (1) | vehicle domain, minimal handler |
| Suppression | 115738 | suppression | small (3) | suppression control domain |
| IgnoreDamage | 114962 | vehicle_era_armor | small (3) | defensive mechanism |
| ChangeProperty (stability) | 107192 | effect.sprint | — | within-type stability check |
| AttackProc (stability) | 107187 | throw_incendiary_grenade | — | within-type stability check |

## Method

For each selected handler:

1. identify the parent SkillTemplate and discover the handler's pathId from its `EventHandlers` array
2. inspect the handler directly via `jiangyu templates inspect --collection resources.assets --path-id <id>`
3. record the `m_Structure` field set (excluding base MonoBehaviour fields)
4. compare against the legacy `effect_handlers` definition from `../MenaceAssetPacker/generated/schema.json`
5. classify any delta

For within-type stability, inspect a second instance of `ChangeProperty` and `AttackProc` and compare field names.

Commands used:

```bash
# Parent skill discovery (representative subset)
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name effect.bleeding
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.shoot_flamethrower
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name active.use_motion_scanner
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name special.vehicle_movement
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name special.suppression
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name racial.carrier_explosion_death
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type SkillTemplate --name passive.vehicle_era_armor

# Direct handler inspection
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107150
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 115850
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107502
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 116256
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106462
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107398
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107391
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106329
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 115738
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114962

# Stability checks
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107192
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 107187
```

## Results

### Concrete handler field sets

#### DamageOverTime — 17 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
ElementsHit: int (Int32)
ElementsHitPercentage: float (Single)
DamagePerTurn: float (Single)
DamagePctCurrentHitpoints: float (Single)
DamagePctCurrentHitpointsMin: float (Single)
DamagePctMaxHitpoints: float (Single)
DamagePctMaxHitpointsMin: float (Single)
DamagesArmorFirst: bool (Boolean)
DamageArmorFlatAmount: float (Single)
DamageArmorPctOfMax: float (Single)
DamageArmorPctOfCurrent: float (Single)
ArmorPenetration: float (Single)
DamageToArmorDurability: float (Single)
FatalityType: enum (FatalityType)
DamageVisualizationType: enum (DamageVisualizationType)
InflictDefects: bool (Boolean)
```

#### AddSkill — 12 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
Event: enum (AddEvent)
SkillToAdd: reference (SkillTemplate)
OnlyApplyOnHit: bool (Boolean)
OnlyUsableOnTargetsWithoutSkill: bool (Boolean)
OnlyApplyWhenNoElementWasDestroyed: bool (Boolean)
OnlyApplyOnHitpointDamage: bool (Boolean)
OnlyWhenSkillNotPresent: bool (Boolean)
ShowHUDText: bool (Boolean)
TargetRequiresOneOfTheseTags: array (List<TagTemplate>)
TargetCannotHaveOneOfTheseTags: array (List<TagTemplate>)
TagsCanPreventSkillUse: bool (Boolean)
```

#### ChangeProperty — 7 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
Trigger: enum (UpdateEvent)
PropertyType: enum (EntityPropertyType)
Amount: int (Int32)
AmountMult: float (Single)
TooltipPlaceholderIndex: int (Int32)
IncludePlusSign: bool (Boolean)
```

#### AttackProc — 7 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
SkillToAdd: reference (SkillTemplate)
OnlyApplyWhenNoElementWasDestroyed: bool (Boolean)
OnlyApplyOnHitpointDamage: bool (Boolean)
CanBeTriggeredByAnySkill: bool (Boolean)
Chance: int (Int32)
ShowHUDText: bool (Boolean)
```

#### Scanner — 8 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
Range: int (Int32)
Blip: reference (GameObject)
Scanline: reference (GameObject)
ScanSpeed: float (Single)
DetectsInfantry: bool (Boolean)
DetectsVehicles: bool (Boolean)
AIEffectToEnemies: reference (SkillTemplate)
```

#### Cooldown — 3 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
RoundsToCoolDown: int (Int32)
AIOnly: bool (Boolean)
```

#### Deathrattle — 3 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
Skill: reference (SkillTemplate)
Chance: int (Int32)
```

#### VehicleMovement — 2 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
Concealment: int (Int32)
```

#### Suppression — 4 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
SoundWhenSuppressed: object (ID)
SoundWhenPinnedDown: object (ID)
PinnedDownEffect: reference (SkillTemplate)
```

#### IgnoreDamage — 4 Jiangyu own fields

```
serializationData: object (Sirenix.Serialization.SerializationData)
ChanceToApply: int (Int32)
AbsorbDamagePct: int (Int32)
RequiredTags: array (TagType[])
```

### Within-type stability

Two stability checks performed:

- `ChangeProperty` (pathId 107502 vs 107192, both from `effect.sprint`): **identical** — 7 fields, same names, kinds, and type names
- `AttackProc` (pathId 116256 from `shoot_flamethrower` vs 107187 from `throw_incendiary_grenade`): **identical** — 7 fields, same names, kinds, and type names

### Jiangyu-vs-legacy comparison

#### DamageOverTime (Jiangyu 17 vs legacy 16)

| Metric | Count |
|---|---|
| Shared | 16 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. `serializationData` is inherited from SerializedScriptableObject but has no corresponding interface-typed legacy field. Classification: **serializationData inherited, no real mismatch**.

#### AddSkill (Jiangyu 12 vs legacy 12)

| Metric | Count |
|---|---|
| Shared | 11 |
| Legacy-only | 1 (`Condition`: ITacticalCondition — interface, Odin-routed) |
| Jiangyu-only | 1 (`serializationData`) |

**Odin substitution pattern**. Same as `Attack` and `ChangePropertyConditional` from the initial pass.

#### ChangeProperty (Jiangyu 7 vs legacy 7)

| Metric | Count |
|---|---|
| Shared | 6 |
| Legacy-only | 1 (`ValueProvider`: IValueProvider — interface, Odin-routed) |
| Jiangyu-only | 1 (`serializationData`) |

**Odin substitution pattern** — and the first instance of an `IValueProvider` interface being Odin-routed, not just `ITacticalCondition`.

#### AttackProc (Jiangyu 7 vs legacy 7)

| Metric | Count |
|---|---|
| Shared | 6 |
| Legacy-only | 1 (`Condition`: ITacticalCondition — interface, Odin-routed) |
| Jiangyu-only | 1 (`serializationData`) |

**Odin substitution pattern**. Same `ITacticalCondition` field as `Attack`, `ChangePropertyConditional`, and `AddSkill`.

#### Scanner (Jiangyu 8 vs legacy 6)

| Metric | Count |
|---|---|
| Shared | 6 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 2 (`serializationData` + `AIEffectToEnemies`) |

**Legacy narrower than observed reality** — `AIEffectToEnemies` (a `PPtr<SkillTemplate>` reference) is present in live game data but absent from the legacy schema. This is a field-level gap, as distinct from the type-level gap found in the initial pass (`SynchronizeItemUses` absent entirely).

#### Cooldown (Jiangyu 3 vs legacy 2)

| Metric | Count |
|---|---|
| Shared | 2 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. Classification: **serializationData inherited, no real mismatch**.

#### Deathrattle (Jiangyu 3 vs legacy 2)

| Metric | Count |
|---|---|
| Shared | 2 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. Classification: **serializationData inherited, no real mismatch**.

#### VehicleMovement (Jiangyu 2 vs legacy 1)

| Metric | Count |
|---|---|
| Shared | 1 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. Classification: **serializationData inherited, no real mismatch**.

#### Suppression (Jiangyu 4 vs legacy 3)

| Metric | Count |
|---|---|
| Shared | 3 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. Classification: **serializationData inherited, no real mismatch**.

#### IgnoreDamage (Jiangyu 4 vs legacy 3)

| Metric | Count |
|---|---|
| Shared | 3 (all legacy fields) |
| Legacy-only | 0 |
| Jiangyu-only | 1 (`serializationData`) |

No Odin-routed fields. Classification: **serializationData inherited, no real mismatch**.

### Summary table

| Concrete type | Jiangyu fields | Legacy fields | Shared | Legacy-only (Odin) | Jiangyu-only (sD) | Jiangyu-only (other) | Classification |
|---|---|---|---|---|---|---|---|
| DamageOverTime | 17 | 16 | 16 | 0 | 1 | 0 | serializationData inherited, no mismatch |
| AddSkill | 12 | 12 | 11 | 1 (Condition: ITacticalCondition) | 1 | 0 | Odin substitution, no real mismatch |
| ChangeProperty | 7 | 7 | 6 | 1 (ValueProvider: IValueProvider) | 1 | 0 | Odin substitution, no real mismatch |
| AttackProc | 7 | 7 | 6 | 1 (Condition: ITacticalCondition) | 1 | 0 | Odin substitution, no real mismatch |
| Scanner | 8 | 6 | 6 | 0 | 1 | 1 (AIEffectToEnemies) | legacy narrower, no real mismatch |
| Cooldown | 3 | 2 | 2 | 0 | 1 | 0 | serializationData inherited, no mismatch |
| Deathrattle | 3 | 2 | 2 | 0 | 1 | 0 | serializationData inherited, no mismatch |
| VehicleMovement | 2 | 1 | 1 | 0 | 1 | 0 | serializationData inherited, no mismatch |
| Suppression | 4 | 3 | 3 | 0 | 1 | 0 | serializationData inherited, no mismatch |
| IgnoreDamage | 4 | 3 | 3 | 0 | 1 | 0 | serializationData inherited, no mismatch |

### Combined view: all 15 validated concrete handler types

Including the 5 types from the initial pass for full family coverage:

| Concrete type | Fields (Jiangyu) | Delta pattern | Odin-routed interface |
|---|---|---|---|
| Attack | 39 | Odin substitution | DamageFilterCondition: ITacticalCondition |
| DamageOverTime | 17 | serializationData inherited | — |
| Damage (legacy 15) | not inspected | — | — |
| AddSkill | 12 | Odin substitution | Condition: ITacticalCondition |
| ChangeProperty | 7 | Odin substitution | ValueProvider: IValueProvider |
| AttackProc | 7 | Odin substitution | Condition: ITacticalCondition |
| Scanner | 8 | legacy narrower | — (extra field: AIEffectToEnemies) |
| SpawnTileEffect | 6 | serializationData inherited | — |
| SwitchBetweenSkills | 6 | serializationData inherited | — |
| ChangePropertyConditional | 4 | Odin substitution | Condition: ITacticalCondition |
| Suppression | 4 | serializationData inherited | — |
| IgnoreDamage | 4 | serializationData inherited | — |
| Cooldown | 3 | serializationData inherited | — |
| Deathrattle | 3 | serializationData inherited | — |
| VehicleMovement | 2 | serializationData inherited | — |
| SynchronizeItemUses | 1 | legacy type absent | — |

## Interpretation

This pass answers the four target questions:

### 1. Odin-substitution breadth

The Odin substitution pattern is confirmed in **3 additional types** (AddSkill, ChangeProperty, AttackProc), bringing the total to **6 out of 15** validated handler types. The pattern is consistent:

- legacy lists an interface-typed field (`ITacticalCondition` or `IValueProvider`)
- Jiangyu excludes that field from the Unity-native type tree
- `serializationData` contains the Odin-serialised payload for it

Two distinct interface types are now confirmed as Odin-routed: `ITacticalCondition` (5 instances across handlers) and `IValueProvider` (1 instance in ChangeProperty). The remaining 9 types have no interface-typed fields and `serializationData` is present but presumably empty or minimal.

### 2. Legacy inventory vs observed reality

The legacy schema is narrower than observed reality in **two distinct ways**:

- **Type-level**: `SynchronizeItemUses` exists in game data but is absent from the legacy 119-type inventory (initial pass finding, still the only known case)
- **Field-level**: `Scanner.AIEffectToEnemies` is a serialised `PPtr<SkillTemplate>` reference present in live game data but absent from the legacy `Scanner` definition. This is a new category of legacy gap — a real serialised field the legacy extraction missed.

### 3. Field-count/field-set alignment

Across all 10 new types, every legacy field was found in Jiangyu output. The only deltas are:

- **Odin-routed fields**: legacy lists the interface field, Jiangyu replaces it with `serializationData` (3 types)
- **`serializationData` inherited**: present in Jiangyu but not legacy, no corresponding interface field (7 types)
- **Extra Jiangyu field**: `Scanner.AIEffectToEnemies` is real serialised data the legacy schema missed (1 type)

Zero real mismatches across all 10 types.

### 4. `serializationData` universality

Confirmed universal. All 15 validated concrete handler types have `serializationData` as an own field. It remains the only field shared across all concrete types (inherited from `SerializedScriptableObject`). No other base-class field leaks into the type-specific field sets.

### Additional observations

- **Within-type stability holds**: ChangeProperty and AttackProc both show identical field sets across instances from different parent skills, consistent with the Attack stability finding from the initial pass.
- **New Odin-routed interface**: `IValueProvider` in ChangeProperty is structurally identical to the `ITacticalCondition` pattern but is a different interface. This means the Odin routing is not tied to one interface — it applies to any interface/abstract-typed field in the SerializedScriptableObject family.

What this does **not** validate:

- runtime behaviour of any handler type
- Odin blob contents or decoding
- the remaining 104 concrete handler types not inspected (15 of 119+ now covered)
- whether additional concrete types beyond the legacy 119 exist (only `SynchronizeItemUses` confirmed so far)
- managed inheritance chain details
- semantic meaning of fields

## Conclusion

This is a successful broader structural survey that strengthens confidence in the handler family's structural model.

The main results are:

- **Zero real mismatches** across 10 newly validated concrete handler types, spanning all size bands and 8 gameplay domains
- **Odin substitution pattern is broad and consistent**: confirmed in 6 of 15 validated types, applying to both `ITacticalCondition` and `IValueProvider` interfaces. Types without interface fields simply have an inherited `serializationData` with presumably empty payload.
- **Legacy schema has field-level gaps**: `Scanner.AIEffectToEnemies` is a real serialised field absent from legacy, a new category beyond the type-level gap (`SynchronizeItemUses`) found in the initial pass
- **`serializationData` is universal**: the only field shared across all 15 validated concrete types, confirming SerializedScriptableObject inheritance as the single structural invariant of the handler family
- **Within-type stability confirmed** for ChangeProperty and AttackProc, consistent with the initial pass's Attack stability finding

Combined with the initial 5-type pass, Jiangyu has now validated **15 of 119+ concrete handler types** (~13% of the legacy inventory). The structural model is consistent across all validated types. The handler family can be treated as a structurally understood category for template-patching design purposes, with the caveat that Odin blob contents remain opaque and ~87% of concrete types are uninspected.

## Next Step

Good next targets:

1. Array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
2. Other polymorphic reference array fields across template types — check whether `EventHandlers`-style patterns exist in other template families
3. Template types not yet audited at the top level — `TileEffectTemplate`, `TagTemplate`, or other non-Entity/non-Weapon/non-Skill types
