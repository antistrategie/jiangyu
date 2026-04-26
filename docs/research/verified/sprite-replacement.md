# Verified — Convention-First Sprite Replacement

Sprite replacement is validated for `Sprite` assets across both unique-backed sprites and atlas-backed sprites. Unique-backed sprites mutate their backing `Texture2D` in place. Atlas-backed sprites are composited at compile time into a copy of the atlas, which is then emitted as a `Texture2D` replacement and rides the same in-place mutation path.

## Contract

- Modder drops a replacement image at `assets/replacements/sprites/<target-name>.<ext>` in a mod project. `<target-name>` is the sprite asset's runtime `name` (find it via `jiangyu assets search`).
- Jiangyu resolves the target sprite's backing `Texture2D` from the index and classifies the sprite as either unique-backed or atlas-backed.
  - **Unique-backed** (the backing `Texture2D` does not back any other indexed `Sprite`): the modder's image is emitted as a sprite asset in the mod's AssetBundle. At runtime the loader mutates the backing `Texture2D` in place.
  - **Atlas-backed** (the backing `Texture2D` is shared with other indexed `Sprite`s): the modder's image is composited into the sprite's `textureRect` within a copy of the original atlas at compile time. The composited atlas is emitted as a `Texture2D` replacement under the atlas's name, and the runtime's existing texture-mutation path applies it. Other sprite regions in the atlas remain untouched.
- The modder may also drop a full atlas replacement at `assets/replacements/textures/<atlas-name>.<ext>`. When both a texture replacement and one or more sprite replacements target the same atlas, the texture replacement is used as the base image and sprite regions are composited on top. The texture entry is absorbed into the composite (it is not emitted twice).
- At runtime, every UI consumer referencing the affected `Sprite` (`UnityEngine.UI.Image`, UI Toolkit `Image`, `VisualElement.style.backgroundImage`, `SpriteRenderer`, cached `ScriptableObject` references) picks up the new pixels because the `Sprite` object still references the same (now-mutated) `Texture2D`.

## Compile-time validation

- The sprite target must resolve to one or more `Sprite` entries in the asset index (`ResolveReplacementSpriteTarget`). When the bare name matches multiple entries, the compiler logs a warning listing every instance the replacement will paint and continues.
- The sprite must carry backing-texture identity and `textureRect` metadata in the index (`ClassifySpriteBackingTexture`). Both fields are populated during `jiangyu assets index`. Missing data is rejected with a re-index instruction.
- Atlas membership is decided by counting indexed `Sprite`s that share the backing `Texture2D`. Co-tenant count > 0 means atlas-backed.

Resolution and classification are exposed as `CompilationService.ResolveAndClassifySpriteTarget`.

## Atlas compositing

`AtlasCompositor.Composite` runs once per atlas group:

1. Load the base image: either the absorbed texture replacement (decoded as RGBA32) or the original atlas pulled from game data via `AssetPipelineService.LoadTexture2dRgba`.
2. If the texture-replacement base does not match the original atlas dimensions, resample to the original atlas dimensions so the indexed `textureRect` coordinates remain valid. A warning is emitted.
3. For each sprite replacement in the group, decode the modder's PNG/JPG, apply the indexed `SpritePackingRotation` transform if non-zero, resample to the indexed `textureRect` dimensions, and blit into the base.
4. PNG-encode the composited atlas and emit it as a `CompiledTexture` under the atlas's name.

Failures (atlas missing from game data, PNG decode failure) throw `InvalidOperationException` and fail the build, rather than silently dropping replacements.

## Validation

`SpriteTargetAtlasValidationTests` and `AtlasCompositingTests` in `tests/Jiangyu.Core.Tests/Compile/`:

- A unique-backed sprite (`menace_logo_main_menue`) classifies as non-atlas.
- An atlas-backed sprite (`icon_hitpoints` sharing `sactx-0-…-ui_sprite_atlas` with 6 other sprites) classifies as atlas-backed with co-tenant count 6 and exposes its `textureRect`.
- A sprite missing backing-texture identity or `textureRect` metadata is rejected with a re-index instruction.
- Two sprite replacements targeting the same atlas produce a single composited `CompiledTexture`; their target regions are painted and other regions remain unchanged.
- A sprite replacement plus a full-atlas texture replacement absorbs the texture entry; the sprite region wins over the texture base inside the rect.
- Modder images that mismatch the rect size are bilinear-resampled. Packing-rotated rects rotate the modder image to match before blitting.
- A missing atlas image or undecodable sprite PNG throws.

Runtime mutation re-uses the in-place texture mutation path validated on 2026-04-18 (see [`texture-replacement.md`](texture-replacement.md) and [`../investigations/2026-04-18-sprite-audio-runtime-routing.md`](../investigations/2026-04-18-sprite-audio-runtime-routing.md), "Follow-up — 2026-04-18 evening"). Because `Sprite.texture` is a reference to a `Texture2D` object, mutating that object updates every consumer without additional machinery.

## Out of scope for this contract

- **Runtime-created sprites** (`Sprite.Create` against a runtime-allocated `Texture2D`). Not targeted.
- **Sprite metadata changes** (pivot, pixels-per-unit, border). The replacement only mutates the backing texture's pixels; sprite rect, pivot, and pixels-per-unit on the game's `Sprite` object are unchanged.
- **Resampling fidelity for very different aspect ratios.** A 256x256 modder image composited into a 32x16 rect is bilinear-resampled to 32x16 before blitting; modders should provide replacements at native rect dimensions for best results.
