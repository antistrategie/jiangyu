# Replacements

A **replacement** swaps an existing game asset by name. The new file ships in your mod's bundle; at load time Jiangyu finds every game asset of the same name and points it at the modded content.

The modder workflow is:

1. Find the target asset (e.g. via Studio's [Asset Browser](/studio#asset-browser) or `jiangyu assets search`).
2. Drop a file under `assets/replacements/<category>/<target-name>.<ext>` matching the asset's runtime name.
3. Compile and ship.

## File layout

```text
assets/replacements/
  textures/
  sprites/
  models/
  audio/
```

The basename (without extension) is the **target name** and must match the runtime `name` of the game asset you're replacing. The asset index (`jiangyu assets index`) is the source of truth for those names.

## Per-category contracts

Each asset class has its own runtime contract: textures mutate in place, atlas-backed sprites composite into a copy of the atlas at compile time, audio rides a Harmony hook on `AudioSource.Play`, models swap the shared mesh on every `SkinnedMeshRenderer`. The dedicated pages cover the specifics:

- [Textures](/assets/replacements/textures)
- [Sprites](/assets/replacements/sprites)
- [Models](/assets/replacements/models)
- [Audio](/assets/replacements/audio)

## Compile-time checks

The compiler walks `assets/replacements/` and the asset index together and refuses the build when:

- A replacement target name doesn't resolve to any matching game asset.
- The asset index is missing or out of date for the current game version. Rebuild with `jiangyu assets index`.
- Two replacement files in the project resolve to the same target.
- A replacement target name resolves to multiple game assets and the modder hasn't picked one (path-id disambiguation).

## Shared-name behaviour

Some game asset names cover multiple runtime objects (e.g. a sprite and its backing texture both sharing a name, or two unrelated sprites in different atlases). The contract on each [type-specific page](#per-category-contracts) explains how each category resolves shared names. Compile logs a warning enumerating every affected instance so you can confirm none of them are unintended targets.

## Replacement vs addition

Use a replacement when the modded content should appear everywhere the vanilla asset is used. Use an [addition](/assets/additions/) when only a specific cloned template should reference the modded content. Both routes can coexist in the same mod project.
