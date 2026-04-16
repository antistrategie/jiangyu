# Next Nested Support-Type Candidates

Date: 2026-04-15

Purpose:

- capture candidate shared support types for the next Jiangyu-native nested structural validation passes
- keep the choice visible outside chat history

Context:

- `LocalizedLine` / `LocalizedMultiLine` have already been structurally spot-checked
- `RoleData` has been structurally spot-checked under `EntityTemplate.AIRole`
- `EntityProperties` has been structurally spot-checked (102 fields, stable across 4 entity categories)
- `OperationResources` has been confirmed too trivial (single field `m_Supplies: Int32`) but matches legacy exactly
- the EntityTemplate top-level field set and its three main nested support types are now validated
- all three EntityTemplate array element types (`EntityLootEntry`, `SkillGroup`, `DefectGroup`) are now validated
- the next target should move beyond EntityTemplate entirely, into other polymorphic reference arrays, unaudited template families, or deeper support type validation
- polymorphic reference array cross-template survey is complete: 3 new polymorphic fields confirmed, 10 monomorphic fields classified, pattern confirmed as cross-cutting
- `SkillOnSurfaceDefinition` has been structurally spot-checked under `SkillTemplate.ImpactOnSurface`
- `SkillTemplate` top-level field audit is complete: 120 vs 128 delta fully classified, no real mismatches
- new classification discovered: Odin-serialised interface/abstract-typed fields (5 fields routed through `serializationData` blob)

## Completed

### `Menace.Tactical.EntityProperties`

- 102-field nested struct, stable across infantry/enemy/structure/vehicle
- all 72 legacy EntityPropertyType enum values confirmed present
- see `2026-04-15-entityproperties-support-type-spot-check.md`

### `Menace.Strategy.OperationResources`

- single field (`m_Supplies: Int32`), matches legacy struct exactly
- too trivial for a standalone pass; validated as a side result of the EntityProperties pass

### `Menace.Tactical.PrefabAttachment`

- 3-field array element type (`IsLight`, `AttachmentPointName`, `Prefab`), exact match to legacy `embedded_classes`
- stable across 3 elements in 2 populated templates
- first validated array element type
- see `2026-04-15-prefabattachment-support-type-spot-check.md`

### `Menace.Tactical.Skills.SkillOnSurfaceDefinition`

- 6-field array element type (`ImpactEffect`, `Decals`, `DecalChance`, `RicochetChance`, `SoundOnRicochet`, `SoundOnImpact`), exact match to legacy `embedded_classes`
- stable across 24 elements in 3 templates spanning active combat, active utility, and passive skill categories
- first nested support type validated under `SkillTemplate`
- documented legacy category error: `SoundOnRicochet`/`SoundOnImpact` labelled `enum` in legacy but are `ID` structs
- see `2026-04-15-skillonsurfacedefinition-support-type-spot-check.md`

### `SkillTemplate` top-level field audit

- 120 Jiangyu fields vs 128 legacy fields: 119 shared, 9 legacy-only, 1 Jiangyu-only, 0 real mismatches
- 4 legacy-only fields are the same base class / managed-only pattern as EntityTemplate/WeaponTemplate
- 5 legacy-only fields are a new classification: Odin-serialised interface/abstract-typed fields (`CustomAoEShape`, `AoEFilter`, `ProjectileData`, `SecondaryProjectileData`, `AIConfig`) routed through the `serializationData` blob
- `serializationData` is the 1 Jiangyu-only field — the Odin container the legacy schema does not list
- first template type where the delta includes Odin-routed fields, not just base class exclusions
- see `2026-04-15-skilltemplate-top-level-field-audit.md`

### `Menace.Tactical.Skills.SkillEventHandlerTemplate` (polymorphic reference array)

- `SkillTemplate.EventHandlers` is a polymorphic ScriptableObject reference array, not an inline embedded-type array — new structural category
- base type is abstract with zero own fields; each element is a PPtr reference to a separate MonoBehaviour (concrete subclass)
- **15 concrete types validated** across two passes (initial 5 + broader survey of 10 more), covering all size bands and 8 gameplay domains
- initial pass (5 types): `Attack` (39), `ChangePropertyConditional` (4), `SpawnTileEffect` (6), `SwitchBetweenSkills` (6), `SynchronizeItemUses` (1)
- broader survey (10 types): `DamageOverTime` (17), `AddSkill` (12), `Scanner` (8), `ChangeProperty` (7), `AttackProc` (7), `Suppression` (4), `IgnoreDamage` (4), `Cooldown` (3), `Deathrattle` (3), `VehicleMovement` (2)
- Odin substitution confirmed in 6 of 15 types, across two distinct interfaces: `ITacticalCondition` (5 handlers) and `IValueProvider` (1 handler)
- legacy inventory narrower than observed in two ways: `SynchronizeItemUses` absent entirely (type-level gap), `Scanner.AIEffectToEnemies` absent as a field (field-level gap)
- `serializationData` is universal across all 15 types — the only shared field
- zero real mismatches across all 15 validated types
- see `2026-04-15-skilleventhandlertemplate-structural-spot-check.md` and `2026-04-15-broader-handler-survey.md`

### `AnimationSoundTemplate.SoundTrigger` (array element type)

- 2-field array element type (`Key: String`, `Sounds: ID[]`), exact match to legacy `structs` definition
- stable across 7 elements in 4 templates spanning alien, construct, weapon, and test data
- first validated support type from a non-EntityTemplate/non-SkillTemplate template family (`AnimationSoundTemplate`)
- documented legacy naming inconsistency: template definition references `AnimationSoundTemplate.SoundTrigger` as element type, but legacy struct is keyed as just `AnimationSoundTemplate` under `structs`
- standard template-level delta pattern (DataTemplate base class exclusions + Odin container) confirmed for `AnimationSoundTemplate`
- see `2026-04-15-soundtrigger-support-type-spot-check.md`

### `Menace.Tactical.EntityLootEntry`

- 4-field inline embedded array element type (`Item`, `Count`, `OverrideDefaultDropChance`, `DropChance`), exact match to legacy `embedded_classes`
- stable across 10 elements in 3 templates spanning pirate, alien, and construct factions
- second validated inline array element type after `PrefabAttachment`
- see `2026-04-15-entitytemplate-array-element-types-spot-check.md`

### `Menace.Tactical.Skills.SkillGroup`

- ScriptableObject reference array element (not inline) — each element is a PPtr to a separate MonoBehaviour
- single field (`Skills: List<SkillTemplate>`), exact match to legacy `embedded_classes`
- stable across 3 inspected instances (`infantry_default`, `infantry_player`, `morale_aliens`)
- confirms the reference-array pattern generalises beyond `SkillTemplate.EventHandlers`
- see `2026-04-15-entitytemplate-array-element-types-spot-check.md`

### `Menace.Tactical.DefectGroup`

- ScriptableObject reference array element (not inline) — same structural pattern as SkillGroup
- single field (`Defects: List<DefectTemplate>`), exact match to legacy `embedded_classes`
- sparse: only 5 of 190+ EntityTemplates have populated arrays, all vehicle or vehicle-adjacent entities
- stable across 2 inspected instances (`vehicle_generic`, `vehicle_tracked`)
- see `2026-04-15-entitytemplate-array-element-types-spot-check.md`

### Polymorphic reference array cross-template survey

- surveyed 31 template instances across 6 template types, all reference array fields flagged as polymorphic in the legacy schema
- **3 new polymorphic fields confirmed**: `PerkTemplate.EventHandlers` (same SkillEventHandlerTemplate pattern, 8 distinct handler types), `EntityTemplate.Items` (ArmorTemplate + WeaponTemplate + AccessoryTemplate in same array), `EntityTemplate.Skills` (PerkTemplate + SkillTemplate in same array)
- **10 monomorphic fields classified**: Tags, Decoration variants, faction references, equipment SkillsGranted, operation Durations — all same-concrete-type in sampled data, though declared types permit wider polymorphism (currently observed monomorphic, not universally monomorphic)
- **PerkTemplate inherits from SkillTemplate**: first direct evidence from serialised data
- 4 OperationTemplate conversation arrays empty on all checked operations — not assessable
- pattern confirmed as cross-cutting, not SkillTemplate-specific
- see `2026-04-15-polymorphic-reference-arrays-cross-template-survey.md`

### `PerkTemplate` top-level field audit

- PerkTemplate adds exactly 1 serialised field (`PerkIcon: Sprite`) to the SkillTemplate contract — no other perk-specific serialised fields exist
- 121 Jiangyu fields vs 129 legacy fields: 120 shared, 9 legacy-only, 1 Jiangyu-only — identical delta to SkillTemplate, entirely inherited
- first validated derived template type; confirms standard delta patterns carry through Unity inheritance
- field set stable across 3 perk categories (passive combat, active utility, unique character-specific)
- see `2026-04-15-perktemplate-top-level-field-audit.md`

### `TileEffectTemplate` top-level field audit

- abstract template type — zero directly-indexed instances, concrete subtypes discovered via PPtr traversal from SpawnTileEffect handlers
- 7 concrete subtypes in `Menace.Tactical.TileEffects` namespace, none ending with "Template": ApplySkillTileEffect (9), RecoverableObjectTileEffect (5), ApplyStatusEffectTileEffect (3), RefillAmmoTileEffect (3), BleedOutTileEffect (1), AddItemTileEffect (1), SpawnObjectTileEffect (1)
- 12 base fields stable across all 23 instances and all 7 concrete types
- standard delta: 4 legacy-only (DataTemplate base exclusions), 1 Jiangyu-only (Odin container), 12 shared — same pattern as all other audited families
- legacy schema has no knowledge of any concrete subtype (type-level blind spot)
- new structural category: abstract Template-named base with non-Template-named concrete subtypes
- see `2026-04-15-tileeffecttemplate-top-level-field-audit.md`

### `DefectTemplate` top-level field audit

- first audited template type outside the DataTemplate lineage: `SerializedScriptableObject → DefectTemplate` (skips DataTemplate)
- 4 Jiangyu fields vs 5 legacy fields: 3 shared (`DamageEffect`, `Severity`, `Chance`), 2 legacy-only (Odin-routed), 1 Jiangyu-only (`serializationData`), 0 real mismatches
- legacy-only fields are `DisqualifierConditions` (`ITacticalCondition[]`, interface-typed) and `SkillsRemoved` (`HashSet<SkillTemplate>`, non-Unity-serialisable) — both Odin-routed
- **no DataTemplate base class exclusions** — correctly absent because DefectTemplate doesn't inherit from DataTemplate
- Odin blob variation: 7/10 samples have baseline 464-byte payload, 3 outliers have larger blobs with populated `ReferencedUnityObjects`
- `DefectSeverity` enum matches legacy exactly (Light=0, Medium=1, Heavy=2)
- refined the universal delta rule: the broader principle is about Unity serialisation boundaries (Jiangyu sees native contract, legacy includes Odin-routed fields, `serializationData` is the bridge), not specifically about DataTemplate base class exclusions
- see `2026-04-15-defecttemplate-top-level-field-audit.md`

### `Menace.Tags.TagTemplate` (top-level field audit)

- 8th and final template type audited at the top level — completes the full audit sweep
- DataTemplate descendant, standard 4+1 delta: 9 Jiangyu fields vs 12 legacy, 8 shared, 4 legacy-only (DataTemplate base class exclusions), 1 Jiangyu-only (`serializationData`)
- the "boring confirms-the-rule" case: all tag-specific fields are natively Unity-serialisable, Odin blob present but entirely empty (no Odin-routed fields)
- reference arrays `GoodAgainst`/`BadAgainst` are monomorphic `List<TagTemplate>` PPtr references, reciprocal pairing confirmed (`anti_vehicle` ↔ `vehicle`)
- zero real mismatches
- see `2026-04-15-tagtemplate-top-level-field-audit.md`

### Classifier gap assessment (complete)

- surveyed all 13,448 MonoBehaviours in `resources.assets` via stratified sampling and legacy cross-check
- the non-Template-named gap is confined to two abstract families: TileEffectTemplate (7 concrete `*TileEffect` subtypes, 23 instances) and SkillEventHandlerTemplate (119+ concrete handler subtypes, ~4,500 instances)
- no additional non-Template-named template-like types found in the current survey scope
- discovered TileEffectGroup (1 instance, `tile_effects_mines`) — non-Template-named reference wrapper parallel to SkillGroup/DefectGroup, absent from legacy schema
- see `2026-04-15-classifier-gap-assessment.md`

### `Menace.Tactical.TileEffects.TileEffectGroup` (reference wrapper)

- discovered during classifier gap assessment
- 1 instance (`tile_effects_mines`, pathId=114654), wraps `TileEffects: TileEffectTemplate[]` (2 elements: ap_mine, at_mine)
- referenced by all 50 `ClearTileEffectGroup` handler instances via `EffectsToClear` PPtr
- structurally parallel to SkillGroup and DefectGroup (single list-of-references field, no DataTemplate base, no Odin)
- not in legacy schema

### OperationTemplate conversation arrays (complete inventory sweep)

- all 4 conversation arrays (`IntroConversations`, `VictoryConversations`, `FailureConversations`, `AbortConversations`) are empty across 100% (8/8) of OperationTemplate instances
- fields are structurally present with correct type (`Menace.Conversations.ConversationTemplate[]`), matching legacy exactly
- **polymorphism not assessable** — no array elements to evaluate
- single-reference event fields (`VictoryEvent`, `FailureEvent`, `AbortEvent`) null on 7/8 operations; `constructs_story1` is the sole exception (all 3 point to `event_story_constructs_01_aftermath_cutscene`)
- 3,626 ConversationTemplate instances exist in the inventory — conversations are a rich system but not wired through OperationTemplate arrays
- side discovery: `ConversationCondition` is a single-field inline object (`ExpressionStr: String`); `OperationIntrosTemplate` (3 instances) may be the actual operation-conversation delivery mechanism
- see `2026-04-15-operationtemplate-conversation-arrays.md`

### `Menace.Items.ArmorTemplate` and `Menace.Items.AccessoryTemplate` (field-level)

- field-level structural validation of both non-WeaponTemplate concrete subtypes in `EntityTemplate.Items`
- **AccessoryTemplate adds zero own serialised fields** beyond ItemTemplate — purest subclass in the validation series. 28 Jiangyu fields vs 32 legacy, standard 4+1+1 delta (4 DataTemplate base + 1 ItemType + 1 serializationData)
- **ArmorTemplate adds 45 own serialised fields** beyond ItemTemplate, all an exact match to legacy. 73 Jiangyu fields vs 77 legacy, same 4+1+1 delta
- `ItemType` discovered as a new managed-only field on the ItemTemplate base class, absent from all 3 concrete subtypes (ArmorTemplate, AccessoryTemplate, WeaponTemplate)
- Odin blobs empty across all item types — no Odin-routed fields at any level of the item hierarchy
- both types perfectly stable across 3 samples each (player/enemy/crafted armour; grenade/ammo/vehicle accessories)
- all 3 confirmed concrete subtypes of EntityTemplate.Items now have full field-level coverage
- see `2026-04-16-armortemplate-accessorytemplate-field-level-spot-check.md`

### `Menace.Items.VehicleItemTemplate` (field-level)

- adds exactly 2 own serialised fields (`EntityTemplate: EntityTemplate`, `AccessorySlots: Int32`) beyond the 28-field ItemTemplate base
- 30 Jiangyu fields vs 34 legacy fields, same 4+1+1 delta as all other ItemTemplate subtypes, zero real mismatches
- stable across 3 samples (wheeled IFV, captured tank, heavy walker)
- Odin blobs empty — consistent with the entire item hierarchy
- all 3 samples have populated `EntityTemplate` references to `player_vehicle.*` EntityTemplates
- **completes field-level coverage of all 4 observed concrete `ItemTemplate` subtypes**: WeaponTemplate (50), ArmorTemplate (73), VehicleItemTemplate (30), AccessoryTemplate (28)
- see `2026-04-16-vehicleitemtemplate-field-level-spot-check.md`

## Good Next Candidates

### 1. TemplateClassifier extension decision

Why:

- the classifier gap assessment established the scope (2 families, ~4,500 invisible MonoBehaviours)
- before template-patching work begins, need to decide whether the classifier should be extended to detect concrete subtypes of known abstract template families, or whether those types should be discovered via PPtr traversal from the existing index
- design question, not a structural validation pass — but informed by the structural findings

### 2. Formal ItemTemplate-level audit update

Why:

- VehicleItemTemplate should be added to the ItemTemplate top-level audit record now that all 4 concrete subtypes are validated
- the base contract is well-characterised through the concrete subtype passes
- not urgent — the information is captured across the individual subtype notes

## Lower-Priority Candidates

### `UnityEngine.Vector2`, `UnityEngine.Vector2Int`, `UnityEngine.Color`

- too trivial — standard Unity engine types
- `ID` is now also validated as a side result (bankId/itemId, matches legacy)

## Selection Rule

Prefer the next target that is:

1. shared or recurring across important template types
2. richer than already-validated types
3. likely to matter for upcoming Jiangyu feature work
4. not blocked by empty array data

## Recommendation

Current preferred order:

1. TemplateClassifier extension decision — determine discovery strategy for non-Template-named concrete subtypes before template-patching design
2. Formal ItemTemplate-level audit update — add VehicleItemTemplate to the standalone ItemTemplate base contract record
