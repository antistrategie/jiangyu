# Verified: Audio Bank Routing and SoundBank Patching

MENACE routes weapon, skill, UI, and ambient audio through opaque `ID{bankId, itemId}` integer pairs into per-category `Stem.SoundBank` assets, not via direct `AudioClip` references on templates. Modders extend the audio surface by patching the relevant bank, then routing references at it.

## Architecture

The chain that resolves "skill X plays audio Y" at runtime:

```
SkillTemplate.SoundsOnAttack[i]                 // also OnUse, OnHit, etc.
  → ID{bankId, itemId}                          // opaque integer pair
  → SoundBank where bankId matches              // Stem.SoundBank : Stem.BaseBank : ScriptableObject
  → Sound where Sound.id == itemId              // 164 entries in weapons_soundbank
  → variations[]                                // List<SoundVariation>
      (picked by RetriggerMode, typically RandomNoRepeat)
  → variation.clip                              // the Unity AudioClip
```

Per-bank summary:

| Bank | bankId | Notes |
| --- | --- | --- |
| `weapons_soundbank` | 7 | 164 sounds, 6 buses, parallel `busIndices` array per sound |
| `ui_soundbank` | (per-bank) | UI clicks, hovers |
| `human_soundbank` | (per-bank) | Voice lines |
| `ambience_soundbank` | (per-bank) | Wind, rain, environment |
| `explosions_soundbank`, `impacts_soundbank`, `vehicles_soundbank`, ... | (per-bank) | Other categories |

All weapon firing audio routes through `weapons_soundbank` regardless of weapon class. Different weapons reference different `itemId`s; for example, the BR's `active.fire_battle_rifle_tier_1` references `Sound.id 69690468` ("battle_rifle_t1") while the DMR's `active.fire_dmr` references `Sound.id 1610679466` ("designated_marksman_rifle_tier_1_01").

## Where the templates live

`Stem.SoundBank`, `Stem.Sound`, `Stem.SoundVariation`, and the related runtime types live in `Assembly-CSharp-firstpass.dll`. The template type resolver searches both `Assembly-CSharp.dll` (primary, where `DataTemplateLoader` lives) and adjacent `Assembly-CSharp*.dll` siblings, so `patch "SoundBank" "weapons_soundbank"` resolves correctly without explicit namespace hints.

`Sound` and `SoundVariation` are concrete `Il2CppSystem.Object` reference types with public fields. The existing patch applier's composite construction path handles them via `TryAllocateIl2CppInstance`.

## Surface for `asset=` on AudioClip fields

No template field in MENACE is typed `AudioClip` directly. The only `AudioClip` slots in data live inside `SoundBank.sounds[].variations[].clip`. Audio additions (`set "clip" asset="..."`) therefore land on `SoundVariation.clip` inside a bank patch, not on any `SkillTemplate` or `WeaponTemplate` field.

The catalogue's "no `AudioClip`-typed field on any template" property holds across all 230 indexed template types and is verified by the structural sweep run during this design.

## Authoring shape

A complete authoring example: add a new `Sound` to the weapons bank, then route a cloned skill to it.

```kdl
patch "SoundBank" "weapons_soundbank" {
    append "sounds" composite="Sound" {
        set "id" 99999001
        set "name" "custom_rifle_fire"
        set "minDistance" 1.0
        set "maxDistance" 500.0
        append "variations" composite="SoundVariation" {
            set "clip" asset="weapons/custom_rifle/fire_01"
        }
    }
}

clone "SkillTemplate" from="active.fire_battle_rifle_tier_1" id="active.fire_custom_rifle" {
    set "SoundsOnAttack" index=0 { set "itemId" 99999001 }
}

clone "WeaponTemplate" from="weapon.generic_battle_rifle_tier1_crowbar" id="weapon.custom_rifle" {
    set "SkillsGranted" index=0 ref="SkillTemplate" "active.fire_custom_rifle"
}
```

Each block uses the standard `patch`/`clone` grammar. The bank patch uses recursive nested construction (`composite="Sound" { ... append "variations" composite="SoundVariation" { ... } }`); the parser and runtime applier both walk the construction tree at arbitrary depth.

## Compile-time validation

Three classes of validation apply:

1. **Type resolution**: `SoundBank`, `Sound`, `SoundVariation` resolve from `Assembly-CSharp-firstpass.dll` via the multi-assembly catalogue.
2. **Asset references**: `clip` is typed `AudioClip`; the validator accepts `asset="..."` on it and resolves the asset name against the mod's `assets/additions/audio/` directory or the vanilla game asset index.
3. **Path coherence**: the writable backing field is `sounds` (lowercase), not the read-only `Sounds` property. The Studio Asset Browser and `jiangyu templates query SoundBank --include-readonly` both show the writable surface.

Out of scope at this layer (caught at runtime instead):

- **`Sound.id` uniqueness within the bank**: a new entry's `id` collides silently with an existing sound's `id` if reused. Read the bank's existing ids via `jiangyu assets inspect object --name weapons_soundbank` before picking a value.
- **Sample rate / channel match**: Unity resamples mismatches at runtime, which pitch-shifts the result. Match the bank's other variations' values.
- **`busIndices` parallel-array coherence**: `SoundBank` carries a parallel `busIndices: Int32[]` keyed by `sounds[]` position. Appending to `sounds` without a matching `busIndices` append may route the new sound to the default bus.

## Runtime application

The loader's `TemplatePatchApplier` resolves the bank by Unity object name via `Resources.FindObjectsOfTypeAll(typeof(SoundBank))` filtered by `name == "weapons_soundbank"`, then walks the patch operations against the live wrapper. The construction path uses `TryAllocateIl2CppInstance` to create fresh `Sound` and `SoundVariation` instances and the existing collection-mutation helpers to append into `List<Sound>` and `List<SoundVariation>`.

## Scope limits

- **New sound buses**: appending to `buses: List<SoundBus>` is structurally supported but the runtime side effects (bus initialisation, mixer routing) are not characterised. The verified path is appending to `sounds` and pointing variations at existing buses (or letting the default bus apply).
- **Cross-bank moves**: changing a `Sound`'s `Bank` reference after construction is not part of the verified surface; clone the sound into the target bank rather than re-pointing.
- **Variation arithmetic**: the bank's `RetriggerMode` and per-sound playback parameters apply to the variation collection as a whole. Adding a new variation to an existing sound changes its playback distribution.

## See also

- [Audio additions](/assets/additions/audio): the modder-facing how-to.
- [Audio replacements](/assets/replacements/audio): name-based `AudioClip` substitution for changing the *bytes* of an existing clip everywhere it plays.
- [Audio replacement architecture](audio-replacement.md): the Harmony-prefix mechanism that powers replacements.
