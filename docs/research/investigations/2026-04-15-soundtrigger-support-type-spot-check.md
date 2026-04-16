# Legacy Support-Type Spot-Check: AnimationSoundTemplate.SoundTrigger

Date: 2026-04-15

## Goal

Do a structural validation pass on `AnimationSoundTemplate.SoundTrigger`, the array element type used under `AnimationSoundTemplate.SoundTriggers`, using Jiangyu-native template inspection on templates with populated arrays.

## Why This Type

`SoundTrigger` is the next target because:

- it was identified during WeaponTemplate deeper validation reconnaissance as the useful pivot out of WeaponTemplate's otherwise exhausted inline nested-object landscape
- it is a standalone template type (`AnimationSoundTemplate`) referenced by WeaponTemplate, not inlined — validating its inner structure bridges the weapon → sound linkage
- it exercises a new template type family (`AnimationSoundTemplate`, 7 instances) not previously inspected
- the legacy schema has an interesting naming inconsistency: the template's field is typed `AnimationSoundTemplate.SoundTrigger[]` but the struct definition is keyed as just `AnimationSoundTemplate` under `structs`, not `SoundTrigger` or `AnimationSoundTemplate.SoundTrigger`

## Samples

Jiangyu-native inspection, using the built CLI DLL already present in the repo:

- `AnimationSoundTemplate`
  - `alien_warrior_animation_sounds` — SoundTriggers count `1`, alien unit
  - `infantry_weapon_animation_sounds` — SoundTriggers count `1`, weapon-related
  - `construct_soldier_animation_sounds` — SoundTriggers count `4`, construct unit (best data)
  - `sounds_test` — SoundTriggers count `1`, test data (Sounds count `2`, useful for confirming ID[] depth)

Total: 7 SoundTrigger elements observed across 4 templates.

## Method

For each sample:

1. run `jiangyu templates inspect --type AnimationSoundTemplate --name <name>`
2. navigate to `m_Structure` → `SoundTriggers`
3. inspect the array `elements` for each populated entry
4. record the field set with kinds and type names
5. compare across Jiangyu samples for stability
6. compare against legacy `structs.AnimationSoundTemplate` (the legacy struct definition for this element type)

Commands used:

```bash
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates list --type AnimationSoundTemplate
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AnimationSoundTemplate --name alien_warrior_animation_sounds
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AnimationSoundTemplate --name infantry_weapon_animation_sounds
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AnimationSoundTemplate --name construct_soldier_animation_sounds
dotnet src/Jiangyu.Cli/bin/Debug/net10.0/jiangyu.dll templates inspect --type AnimationSoundTemplate --name sounds_test
```

## Results

### Array container

All 4 samples share the same array type declaration:

- `name = "SoundTriggers"`
- `kind = "array"`
- `fieldTypeName = "SoundTrigger[]"`

### Element shape

Each element across all 4 samples has:

- `fieldTypeName = "SoundTrigger"`
- 2 fields, stable across all 7 observed elements:

| Field | Kind | Type |
|---|---|---|
| `Key` | string | `String` |
| `Sounds` | array | `ID[]` |

`ID` is already validated from the SkillOnSurfaceDefinition pass as a 2-field struct (`bankId: Int32`, `itemId: Int32`).

### Sample diversity

| Template | Elements | Key values | Max Sounds per element |
|---|---|---|---|
| `alien_warrior_animation_sounds` | 1 | `alien_idle_sfx` | 1 |
| `infantry_weapon_animation_sounds` | 1 | `weapon_team_load_through_01` | 1 |
| `construct_soldier_animation_sounds` | 4 | `idle_var_01`, `idle_var_02`, `idle_var_03`, `walk_hydraulic` | 1 |
| `sounds_test` | 1 | `fart` | 2 |

The `sounds_test` template is useful: its single SoundTrigger has `Sounds` count `2`, confirming the ID[] can hold multiple entries. All other observed elements have count `1`.

### Jiangyu-vs-legacy comparison

Legacy `structs.AnimationSoundTemplate` (note: not keyed as `SoundTrigger`):

```json
{
  "size_bytes": 12,
  "fields": [
    { "name": "Key", "type": "string", "offset": "0x0" },
    { "name": "Sounds", "type": "ID[]", "offset": "0x8" }
  ]
}
```

Comparison:

| Field | Jiangyu | Legacy | Classification |
|---|---|---|---|
| `Key` | string / String | string | **matches** |
| `Sounds` | array / ID[] | ID[] | **matches** |

All 2 fields match in name, type, and order.

### Legacy naming inconsistency

The legacy schema has a naming mismatch for this type:

- The template definition (`templates.AnimationSoundTemplate`) types the field as `AnimationSoundTemplate.SoundTrigger[]` with `element_type: "AnimationSoundTemplate.SoundTrigger"`
- But the struct definition is keyed as `structs.AnimationSoundTemplate`, not `structs.AnimationSoundTemplate.SoundTrigger` or `structs.SoundTrigger`
- Jiangyu surfaces the element `fieldTypeName` as just `SoundTrigger`

This is a legacy schema naming inconsistency — the struct key does not match the element type name used in the template definition. Not a serialised contract mismatch.

### Template-level delta (secondary context)

The `AnimationSoundTemplate` template itself follows the same delta pattern seen in EntityTemplate, WeaponTemplate, and SkillTemplate:

- Legacy lists `m_ID`, `m_IsGarbage`, `m_IsInitialized` as template-level fields — these are DataTemplate base class managed-only fields not present in serialised data
- Jiangyu has `serializationData` (Odin container, empty in all 4 samples) — legacy omits this
- Both share `m_GameDesignComment`, `m_LocaState`, `SoundTriggers`

No real mismatches at the template level.

## Interpretation

What this validates:

- Jiangyu independently reproduces the current serialised `SoundTrigger` contract from live game data
- the shape is stable across all 7 observed elements in 4 templates spanning alien, construct, weapon, and test data
- the legacy struct definition is an exact match at the serialised field level
- `ID[]` arrays inside embedded struct elements work correctly (consistent with prior `ID` validation from SkillOnSurfaceDefinition)
- this is the first validated support type from a non-EntityTemplate/non-SkillTemplate template family

What this does **not** validate:

- runtime behaviour (how the game resolves SoundTrigger keys to actual audio playback)
- the semantic meaning of `Key` values (they appear to be animation event names, but this is interpretation)
- memory offsets recorded in the legacy struct definition
- the full `AnimationSoundTemplate` template-level contract (only briefly noted as secondary context, not audited in depth)

## Conclusion

This is a successful structural validation pass for the `SoundTrigger` array element type.

The main results are:

- `SoundTrigger` is a compact 2-field embedded type (`Key: String`, `Sounds: ID[]`) with an exact serialised match to the legacy struct definition
- the field set is stable across 7 elements in 4 templates
- the legacy schema has a naming inconsistency: the struct is keyed as `AnimationSoundTemplate` rather than `SoundTrigger` or `AnimationSoundTemplate.SoundTrigger`, despite the template definition referencing `AnimationSoundTemplate.SoundTrigger` as the element type
- unlike the SkillOnSurfaceDefinition pass (where legacy miscategorised `SoundOnRicochet`/`SoundOnImpact` as `enum` when they're `ID` structs), here legacy correctly types `Sounds` as `ID[]`
- the standard template-level delta pattern (DataTemplate base class exclusions + Odin container) holds for `AnimationSoundTemplate`

## Next Step

Good next targets:

1. Broader concrete handler survey — spot-check more of the 119+ SkillEventHandlerTemplate concrete types to confirm the Odin substitution pattern holds broadly
2. Array element types under EntityTemplate (`DefectGroup`, `EntityLootEntry`, `SkillGroup`) — still need samples with populated arrays
3. Other polymorphic reference array fields across template types — determine if the SkillTemplate.EventHandlers pattern is cross-cutting
