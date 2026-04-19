# Verified — Convention-First Texture2D Replacement

Convention-first `Texture2D` replacement is validated end-to-end against live MENACE content.

## Contract

- Modder drops a replacement image at `assets/replacements/textures/<target-name>--<pathId>.<ext>` in a mod project.
- `<target-name>--<pathId>` identifies a `Texture2D` asset in the Jiangyu asset index.
- Jiangyu compiles that image into the mod's AssetBundle as a `Texture2D` asset named `<target-name>`.
- At runtime, the loader finds every loaded game `Texture2D` whose `name` equals `<target-name>` and mutates its pixel data in place. The mutation path:
  1. `Graphics.Blit` the replacement into an sRGB `ARGB32` `RenderTexture` sized to the destination, with mipmap auto-generation matching the destination's chain.
  2. `ReadPixels` into a readable staging `Texture2D` whose format matches the destination's alpha-presence (`RGB24` for `DXT1`/`BC1` no-alpha targets, `RGBA32` for `DXT5`/`BC3` with-alpha targets). Alpha-matched staging is what steers Unity's managed compressor to produce the right compressed family on the next step.
  3. `Texture2D.Compress(highQuality: true)` — managed compression rather than `Graphics.ConvertTexture`'s GPU encoder, which is unreliable across consumer GPUs and Proton.
  4. `Graphics.CopyTexture` from staging into the game texture when formats match. Fall back to `Graphics.ConvertTexture` only for compressed formats that managed `Compress` can't produce (BC7, ASTC, BC4/5/6H).
- Every consumer of the game texture — materials (`_BaseColorMap`, `_MaskMap`, `_NormalMap`, `_EffectMap`, etc.), UGUI, UI Toolkit, template references, manager-cached references — inherits the mutation automatically because Unity texture references are identity-based.
- Per-material texture bindings compiled alongside skinned mesh replacements follow the same in-place path: the loader mutates the Texture2D that the existing game material already references, keeping the game's shader-variant and keyword state intact. This avoids the "pink vehicle after squad screen" regression where `Object.Instantiate(material)` captured HDRP keyword state at clone time and mismatched in the destination scene.
- The mutation is idempotent per `Texture2D` instance per session: once mutated, an instance is skipped on later apply passes.

## Surfaces covered

Any consumer holding a reference to the mutated `Texture2D` instance, including but not limited to:

- `Material` property bindings (every texture slot, across every shader) on `SkinnedMeshRenderer`, `MeshRenderer`, `ParticleSystemRenderer`, and other renderer types.
- `UnityEngine.UI.RawImage.texture` and `UnityEngine.UIElements.Image.image`.
- `ScriptableObject` fields typed as `Texture2D`.

This breadth is inherent to in-place mutation and is a strict improvement over the earlier consumer-walk path, which silently dropped auxiliary material slots (`_MaskMap`, `_NormalMap`, `_EffectMap`) when it rebuilt materials.

## Compile-time validation

- The texture target must resolve to exactly one `Texture2D` entry in the asset index by name and pathId (`ResolveReplacementTextureTarget`).
- The texture's runtime `name` must be globally unique across the index; ambiguous names are rejected at compile time (`ValidateUniqueRuntimeTextureNames`). The loader matches by `texture.name` alone, so ambiguity would corrupt unrelated textures.

## Validation

2026-04-18, in-game smoke test with `RedSoldierTest` replacing the three `local_forces_basic_soldier*_BaseMap` `Texture2D` assets (pathIds 1473, 1511, 1610) with a solid red image:

- All soldier variants in the Tactical scene picked up the new `_BaseColorMap` pixels.
- `Graphics.ConvertTexture` handled the source/destination format mismatch (ARGB32 source → DXT1 destination) at runtime, without compile-time format matching.
- `Graphics.CopyTexture` propagated the mutation across 1024×1024 and 2048×2048 game textures with full mipmap chains.
- The full shader pipeline (`_MaskMap`, `_NormalMap`, `_EffectMap`) was preserved — the soldier rendered cream rather than raw red, confirming auxiliary maps were still active on the game's materials.

Full experiment details: [`../investigations/2026-04-18-sprite-audio-runtime-routing.md`](../investigations/2026-04-18-sprite-audio-runtime-routing.md) ("Follow-up — 2026-04-18 evening").

## Out of scope for this contract

- Sprite replacement for atlas-backed sprites. Covered separately: see [`sprite-replacement.md`](sprite-replacement.md).
- `AudioClip` replacement. Blocked on an Il2CppInterop `float[]` marshalling bug. See the investigation note above.
- `Texture` subtypes other than `Texture2D` (e.g. `Texture2DArray`, `Cubemap`, `RenderTexture`). Not currently targeted.
