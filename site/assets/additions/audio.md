# Audio additions

Ship a new `AudioClip` and reference it from a template clone. Use this when a cloned skill, weapon, or unit needs its own sound that doesn't exist in the vanilla game.

## File layout

```text
assets/additions/audio/<logical-name>.<ext>
```

`<ext>` is `.wav`, `.ogg`, or `.mp3`. Subdirectories are allowed, and the path under `audio/` (with the extension stripped) is the logical name.

## KDL syntax

```kdl
clone "SkillTemplate" from="active.shoot" id="active.shoot_custom" {
    set "FireSound" asset="weapons/lrm5/launch"
}
```

## Compile-time errors

Same as [sprite additions](/assets/additions/sprites#compile-time-errors).

## Additions vs replacements

An audio addition is played by whatever code path the game uses for the field you wrote it into. If the cloned skill plays `FireSound`, your clip plays whenever that skill fires its sound.

An addition does **not** substitute for some vanilla sound everywhere it's referenced. Use an [audio replacement](/assets/replacements/audio) for that.

## CLI alternative

```sh
jiangyu assets export audio <vanilla-clip-name> --output assets/additions/audio/weapons/lrm5/launch.wav
jiangyu compile
```
