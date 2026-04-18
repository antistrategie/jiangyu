# Verified — Convention-First Sprite Replacement

Sprite replacement is validated for `Sprite` assets backed by a **unique** `Texture2D`. Atlas-backed sprites — `Sprite` assets whose backing texture is shared by more than one indexed sprite — are rejected at compile time and are not part of this contract.

## Contract

- Modder drops a replacement image at `assets/replacements/sprites/<target-name>--<pathId>.<ext>` in a mod project.
- `<target-name>--<pathId>` identifies a `Sprite` asset in the Jiangyu asset index.
- Jiangyu resolves the target sprite's backing `Texture2D` from the index. If that `Texture2D` backs more than one indexed `Sprite`, the compile fails with an atlas-rejection error naming the shared texture and listing its co-tenant sprites. The modder cannot ship an atlas-backed sprite replacement through Jiangyu.
- For unique-texture-backed sprites, Jiangyu compiles the replacement image into the mod's AssetBundle.
- At runtime, the loader mutates the sprite's backing `Texture2D` in place via the same `Graphics.ConvertTexture` + `Graphics.CopyTexture` path used for direct texture replacement. Every UI consumer referencing that `Sprite` — UGUI `Image`, UI Toolkit `Image`, `VisualElement.style.backgroundImage`, `SpriteRenderer`, cached references on `ScriptableObject` fields — picks up the new pixels because the `Sprite` object still references the same (now-mutated) `Texture2D`.

## Compile-time validation

- The sprite target must resolve to exactly one `Sprite` entry in the asset index (`ResolveReplacementSpriteTarget`).
- The sprite's runtime `name` must be globally unique across indexed `Sprite` assets (`ValidateUniqueRuntimeSpriteNames`).
- The sprite's backing `Texture2D` must not back any other indexed `Sprite` (`ValidateSpriteBackingTextureIsUnique`). This is the atlas-rejection check.

All three checks run inside `CompilationService.ResolveAndValidateSpriteTarget`. The backing-texture identity is recorded in the asset index during `jiangyu assets index`; re-index after upgrading Jiangyu so the atlas check has the data it needs.

## Validation

Atlas-rejection check verified by `SpriteTargetAtlasValidationTests` in `tests/Jiangyu.Core.Tests/Compile/`:

- A clean unique-backed sprite (`menace_logo_main_menue`) resolves without error.
- An atlas-backed sprite (`icon_hitpoints` sharing `sactx-0-…-ui_sprite_atlas` with 6 other sprites) is rejected with an error that names the atlas and lists the first co-tenants.
- A sprite entry missing backing-texture identity (old index) is rejected with a re-index instruction.

Runtime mutation re-uses the in-place texture mutation path validated on 2026-04-18 (see [`texture-replacement.md`](texture-replacement.md) and [`../investigations/2026-04-18-sprite-audio-runtime-routing.md`](../investigations/2026-04-18-sprite-audio-runtime-routing.md) "Follow-up — 2026-04-18 evening"). Because `Sprite.texture` is a reference to a `Texture2D` object, mutating that object updates the sprite for every consumer without additional machinery.

## Out of scope for this contract

- **Atlas-backed sprites.** Compile-time rejected. A future Jiangyu contract may address per-atlas-entry replacement by rebuilding the atlas, but that is genuinely a different strategy and is not in the current supported path.
- **Runtime-created sprites** (`Sprite.Create` against a runtime-allocated `Texture2D`). Not targeted.
- **Sprite metadata changes** (pivot, pixels-per-unit, border). The replacement only mutates the backing texture's pixels; sprite rect, pivot, and pixels-per-unit of the game's `Sprite` object are unchanged.
- **Non-square or differently-sized replacements relative to the backing texture.** `Graphics.ConvertTexture` converts to the destination's dimensions; if the modder's replacement aspect or resolution differs, the result is resampled to the game texture's size. Provide replacements at the backing texture's native dimensions for best fidelity.
