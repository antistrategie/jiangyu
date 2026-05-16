# Audio additions

Ship a new `AudioClip` and add it to the appropriate `SoundBank`. The game routes every sound (weapon, skill, UI, ambient, voice) through `(bankId, itemId)` indirection, so a new clip lands by extending a bank rather than being assigned to a template field directly. Banks are namespaced by purpose: `weapons_soundbank`, `ui_soundbank`, `ambience_soundbank`, and so on. `jiangyu assets list --type SoundBank` lists them.

## File layout

```text
assets/additions/audio/<logical-name>.<ext>
```

`<ext>` is `.wav`, `.ogg`, or `.mp3`. Subdirectories are allowed, and the path under `audio/` (with the extension stripped) is the logical name.

## KDL syntax

Append a new `Sound` to an existing bank. Use `from="<existing>"` to inherit playback defaults (volume, pitch, distance falloff, retrigger mode) so you only have to set what differs.

```kdl
patch "SoundBank" "weapons_soundbank" {
    append "sounds" from="aimed_shot" {
        set "id" "custom_rifle_fire"
        set "name" "custom_rifle_fire"
        clear "variations"
        append "variations" {
            set "clip" asset="weapons/custom_rifle/fire_01"
        }
        append "variations" {
            set "clip" asset="weapons/custom_rifle/fire_02"
        }
    }
}
```

The two `asset=` references point at files the mod ships at `assets/additions/audio/weapons/custom_rifle/fire_01.wav` and `..._02.wav`. The same shape works for any bank: pick a `from=` entry whose playback feel you want to inherit, override `id`/`name`/`variations`, and the new sound is available for templates to reference.

To make a template use the new sound, point its bank-id field at the new name. The field name varies by template family; on `SkillTemplate` it's `SoundsOnAttack`:

```kdl
clone "SkillTemplate" from="active.fire_battle_rifle_tier_1" id="active.fire_custom_rifle" {
    set "SoundsOnAttack" index=0 {
        set "itemId" "custom_rifle_fire"
    }
}
```

(Other families have their own slots: UI buttons reference `ui_soundbank` entries through their own click-sound field, ambient zones reference `ambience_soundbank`, etc. Studio's template browser surfaces the exact field name on each template type.)

## Choosing a sound id

`Sound.id` accepts a string in source form, the compiler hashes it (FNV-1a 32-bit) to the underlying numeric id and the same string also routes `itemId` references from consumers. Pick a stable, descriptive name and reuse it on both sides. Different strings produce different hashes, so mod ids don't collide as long as the names differ.

## Reusing existing sounds

If a cloned template should sound *different* from its source without shipping new audio, point its sound field at another existing bank entry. For example, a cloned battle rifle that uses the DMR's audio:

```kdl
clone "SkillTemplate" from="active.fire_battle_rifle_tier_1" id="active.fire_battle_rifle_dmr_voice" {
    set "SoundsOnAttack" index=0 {
        set "itemId" "dmr_close"
    }
    set "SoundsOnAttackFar" index=0 {
        set "itemId" "dmr_distant"
    }
}
```

The `itemId` strings match an existing `Sound.name` in the bank. Read them via `jiangyu assets inspect object --name <bank>` and pick the entry that matches the audio family you want.
