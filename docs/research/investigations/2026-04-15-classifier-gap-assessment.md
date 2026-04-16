# Classifier Gap Assessment: Non-Template-Named Template-Like Types

Date: 2026-04-15

## Goal

Determine whether the `*TileEffect` non-Template naming pattern (discovered during the TileEffectTemplate audit) represents a broader category of template-like types invisible to `TemplateClassifier`, or is unique to the TileEffectTemplate family. Stay structural/discoverability-focused — no runtime behaviour or classifier redesign.

## Why This Target

The TileEffectTemplate audit discovered 7 concrete subtypes with 23 instances, all invisible to the classifier because their class names don't end with "Template". The SkillEventHandlerTemplate survey had already found a similar pattern (119+ concrete handler types, also non-Template-named). The question is whether these two are the full extent of the gap, or whether additional non-Template-named template-like types exist in the current game data.

This matters for template-patching design: if the template index is incomplete, patching logic cannot assume it is a complete inventory of patchable data objects.

## Method

Broad survey of all 13,448 MonoBehaviours in `resources.assets`, cross-referenced against the legacy schema (84 template types, 119 effect handlers, 35 embedded classes, 83 inheritance entries).

1. Extracted all unique name prefixes from the 13,448 MonoBehaviours (80+ distinct prefix groups)
2. Stratified sampling: resolved `m_Script` PPtr references on representative entries from each group to discover actual script class names
3. Sampled 23 anonymous "MonoBehaviour"-named entries (every 200th of 4,544) for script class diversity
4. Sampled 14 named MonoBehaviour groups spanning diverse prefixes (inlineStyle, Exposure, VisualEnvironment, Bloom, decorations, building_destroyed, animator, property, light_condition, special, mod_weapon, offmap, perk_tree, enemy_asset)
5. Traced all 9 legacy abstract template families for concrete subtypes
6. Cross-referenced Jiangyu template index (73 types) against legacy schema (84 types) to identify coverage gaps
7. Inspected specific anomalies: `TileEffectGroup`, `DecalCollection`/`DecalTemplate`, `MissionStrategicAssetTemplate`

Commands used:

```bash
# Script class resolution via m_Script PPtr
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id <pid> --max-depth 3

# TileEffectGroup structural inspection
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 114654 --max-depth 6

# ClearTileEffectGroup reference verification
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --collection resources.assets --path-id 106265 --max-depth 4
```

## Results

### Jiangyu index vs legacy schema cross-reference

73 types in Jiangyu template index. 84 types in legacy schema. 11 legacy-only:

| Legacy-only type | Abstract? | Explanation |
|---|---|---|
| DataTemplate | yes | Base class, no instances |
| BaseItemTemplate | yes | Base class, no instances |
| BasePlayerSettingTemplate | yes | Base class, no instances |
| EffectListTemplate | yes | Base class, no instances |
| ItemTemplate | yes | Base class, no instances |
| MissionTemplate | yes | Base class, no instances |
| OperationAssetTemplate | yes | Base class, no instances |
| SkillEventHandlerTemplate | yes | Abstract, concrete subtypes non-Template-named |
| TileEffectTemplate | yes | Abstract, concrete subtypes non-Template-named |
| DecalTemplate | no | Actually an inline struct inside DecalCollection, not a MonoBehaviour |
| MissionStrategicAssetTemplate | no | Base `IWeighted` (interface), zero instances found |

No Jiangyu-only types. Every type in the Jiangyu index has a corresponding legacy schema entry.

### Abstract template families: concrete subtype naming

| Abstract family | Concrete subtypes end with "Template"? | Gap? |
|---|---|---|
| DataTemplate | All concrete descendants are Template-named | No |
| BaseItemTemplate | BlueprintTemplate, CommodityTemplate, etc. | No |
| BasePlayerSettingTemplate | BoolPlayerSettingTemplate, IntPlayerSettingTemplate, etc. | No |
| EffectListTemplate | ConversationEffectsTemplate, ShipUpgradeTemplate, etc. | No |
| ItemTemplate | ArmorTemplate, WeaponTemplate, AccessoryTemplate, etc. | No |
| MissionTemplate | GenericMissionTemplate | No |
| OperationAssetTemplate | EnemyAssetTemplate, StrategicAssetTemplate | No |
| **SkillEventHandlerTemplate** | Attack, Damage, AddSkill, etc. (119+ types) | **Yes** |
| **TileEffectTemplate** | ApplySkillTileEffect, SpawnObjectTileEffect, etc. (7 types) | **Yes** |

Only 2 of 9 abstract families have the gap.

### Anonymous MonoBehaviour population (4,544 entries)

Script classes resolved on 23 stratified samples (every 200th entry):

- `HDAdditionalLightData` (6 hits) — Unity HDRP lighting component
- `AnimatorSetLayerWeight` (4) — animation runtime component
- `SetAnimatorBool` (2) — animation runtime component
- `AttachmentMaterialReplacement` (2) — visual runtime component
- `Toggle`, `SetLightIntensity`, `SetAnimatorFloat`, `RotateSelf`, `Ragdoll`, `MeshDestroyedVariations`, `InitForestPrefab`, `Image`, `FireAnimatorEvent` (1 each)

All are runtime MonoBehaviour components attached to prefab GameObjects. None are template-like data objects. No game-specific data fields beyond Unity metadata.

### Named MonoBehaviour groups: script class resolution

| Name prefix | Count | Script class | Template-named? |
|---|---|---|---|
| active.* | 287 | SkillTemplate | Yes |
| Attack (no dot) | 225 | SkillEventHandlerTemplate subtype | No (known) |
| ChangeProperty (no dot) | 160 | SkillEventHandlerTemplate subtype | No (known) |
| inlineStyle | 151 | StyleSheet | N/A (UI toolkit) |
| perk.* | 121 | PerkTemplate | Yes |
| mission.* | 106 | GenericMissionTemplate | Yes |
| AddSkill (no dot) | 85 | SkillEventHandlerTemplate subtype | No (known) |
| enemy.* | 84 | EntityTemplate | Yes |
| accessory.* | 74 | AccessoryTemplate | Yes |
| effect.* | 71 | SkillTemplate | Yes |
| damage_effect.* | 33 | SkillTemplate | Yes |
| animator.* | 29 | ElementAnimatorTemplate | Yes |
| movement_type.* | 29 | SkillGroup | No (known wrapper) |
| property.* | 27 | PropertyDisplayConfigTemplate | Yes |
| Exposure (no dot) | 43 | Exposure | N/A (HDRP volume) |
| VisualEnvironment | 36 | VisualEnvironment | N/A (HDRP volume) |
| Bloom | 26 | Bloom | N/A (HDRP volume) |
| building_destroyed | 26 | ConversationTemplate | Yes |
| decorations.* | 18 | PrefabListTemplate | Yes |
| special.* | 21 | SkillTemplate | Yes |
| mod_weapon.* | 25 | ModularVehicleWeaponTemplate | Yes |
| offmap.* | 18 | SkillTemplate | Yes |
| perk_tree.* | 17 | PerkTreeTemplate | Yes |
| enemy_asset.* | 17 | EnemyAssetTemplate | Yes |
| light_condition.* | 23 | LightConditionTemplate | Yes |

Every named group maps to either a known Template-named type, a known SkillEventHandlerTemplate subtype, a known wrapper type (SkillGroup), or a non-game type (HDRP/UI toolkit).

### New finding: TileEffectGroup

Script class: `TileEffectGroup`
Namespace: `Menace.Tactical.TileEffects`
Instances: 1 (`tile_effects_mines`, pathId=114654)

Structure:

```
m_Structure: Menace.Tactical.TileEffects.TileEffectGroup
  TileEffects: TileEffectTemplate[]
    [0] -> tile_effect.ap_mine (pathId=114656)
    [1] -> tile_effect.at_mine (pathId=114657)
```

Referenced by: all 50 `ClearTileEffectGroup` handler instances via `EffectsToClear: PPtr<TileEffectGroup>` (5 verified, all pointing to the same instance).

Structurally parallel to `SkillGroup` (wraps `Skills: List<SkillTemplate>`) and `DefectGroup` (wraps `Defects: List<DefectTemplate>`). All three are non-Template-named reference wrapper ScriptableObjects containing a single list-of-template-references field. No DataTemplate base fields, no Odin container.

`TileEffectGroup` is absent from the legacy schema's `embedded_classes` section (unlike `SkillGroup` and `DefectGroup`, which are listed).

### Legacy anomalies resolved

**DecalTemplate**: Legacy schema lists it as non-abstract with base `2691` and 6 fields. In current game data, `DecalTemplate` is an inline struct inside `DecalCollection.SelectedDecals: List<DecalTemplate>` — it is NOT a standalone MonoBehaviour. The 4 `DecalCollection` instances (tiretracks_earth_decals, etc.) are the actual ScriptableObjects. `DecalCollection` is a non-template lookup/config type, not structurally template-like. Not a classifier gap.

**MissionStrategicAssetTemplate**: Legacy schema lists it with base `IWeighted` (an interface). Zero instances found in `resources.assets`. May be an inline struct, an interface type, or removed. Not assessable; not a classifier gap.

## Interpretation

### What this validates

Three categories of non-Template-named template-like types exist in the surveyed data:

**Category A: Concrete subtypes of abstract Template-named bases**

| Abstract base | Concrete subtypes | Naming pattern | Instance count |
|---|---|---|---|
| TileEffectTemplate | 7 types | `*TileEffect` | 23 |
| SkillEventHandlerTemplate | 119+ types | Diverse (Attack, Damage, etc.) | ~4,500 |

These are the only two abstract template families with non-Template-named concrete subtypes out of 9 abstract families checked.

**Category B: Reference wrapper ScriptableObjects**

| Wrapper type | Contents | Instance count | In legacy? |
|---|---|---|---|
| SkillGroup | `Skills: List<SkillTemplate>` | Multiple | Yes |
| DefectGroup | `Defects: List<DefectTemplate>` | Multiple | Yes |
| TileEffectGroup | `TileEffects: TileEffectTemplate[]` | 1 | No |

These are grouping constructs, not directly patchable data objects. They exist as PPtr reference targets from template fields (EntityTemplate.SkillGroups, EntityTemplate.DefectGroups, ClearTileEffectGroup.EffectsToClear).

**No additional non-Template-named template-like types were found in the current Jiangyu/legacy cross-check of `resources.assets` and the surveyed template families.** The anonymous MonoBehaviour population is entirely runtime components. All named MonoBehaviour groups resolve to known Template-named types, known handler subtypes, known wrapper types, or non-game engine types. This does not guarantee completeness across the entire game forever — it bounds the finding to the current survey scope.

### What this does not validate

- Whether additional non-Template-named types exist in collections other than `resources.assets`
- Whether future game updates could introduce new abstract families with non-Template-named subtypes
- Runtime behaviour of any discovered type
- Whether TileEffectGroup or the reference wrappers need to be patchable targets (a design question, not a structural one)
- Completeness of the SkillEventHandlerTemplate concrete type inventory (119+ in legacy, 15 validated by Jiangyu, survey did not enumerate all)

## Conclusion

`*TileEffect` is **not a one-off**. It belongs to a consistent two-family pattern: abstract template base → non-Template-named concrete subtypes. The two affected families are:

1. **TileEffectTemplate** (7 concrete types, 23 instances) — abstract base is Template-named, concrete subtypes are `*TileEffect`
2. **SkillEventHandlerTemplate** (119+ concrete types, ~4,500 instances) — abstract base is Template-named, concrete subtypes have diverse names (Attack, Damage, AddSkill, etc.)

Together, these account for approximately 4,500+ MonoBehaviours in `resources.assets` that are structurally template-like but invisible to `TemplateClassifier`. This is roughly one third of all MonoBehaviours in the collection.

A third gap — **TileEffectGroup** (1 instance) — is a non-Template-named reference wrapper ScriptableObject, structurally parallel to the already-known SkillGroup and DefectGroup wrappers. It is absent from the legacy schema entirely.

All other abstract template families (7 of 9) have exclusively Template-named concrete subtypes within the surveyed data. The pattern is confined to the two families above, not a pervasive naming problem.

## Next Step

1. **Record TileEffectGroup** in the next-support-type-candidates file alongside SkillGroup/DefectGroup as a validated reference wrapper type.
2. **OperationTemplate conversation arrays** — still the next structural target if populated instances can be found.
3. **Deeper EntityTemplate.Items validation** — inspect ArmorTemplate/AccessoryTemplate field sets against legacy, similar to handler concrete type passes.
