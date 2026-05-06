# Audio additions

Ship a new `AudioClip` and reference it from a template clone. Use this when a cloned skill, weapon, or unit needs its own sound that doesn't exist in the vanilla game.

## File layout

```text
assets/additions/audio/<logical-name>.<ext>
```

`<ext>` is `.wav`, `.ogg`, or `.mp3`. Subdirectories are allowed; the path under `audio/` (with the extension stripped) is the logical name.

## KDL syntax

```kdl
clone "SkillTemplate" from="active.shoot" id="active.shoot_custom" {
    set "FireSound" asset="weapons/lrm5/launch"
}
```

## Compile-time errors

Same as [sprite additions](/assets/additions/sprites#compile-time-errors).

## Runtime resolution

Audio additions ride the existing template-apply path: at apply time the loader resolves the asset reference and writes the resulting `AudioClip` into the cloned template's field via reflection. This is **not** the same as audio replacement, which Harmony-patches `AudioSource.Play` to substitute clips by name at playback time.

In practice that means:

- An audio addition is played by whatever code path the game uses for the field you wrote it into. If the cloned skill plays `FireSound` via `AudioSource.PlayOneShot(skill.FireSound)`, your clip plays.
- An audio addition does **not** automatically substitute for some vanilla sound everywhere it's referenced. Use an [audio replacement](/assets/replacements/audio) for that.

## CLI alternative

```sh
jiangyu assets export audio <vanilla-clip-name> --output assets/additions/audio/weapons/lrm5/launch.wav
jiangyu compile
```
