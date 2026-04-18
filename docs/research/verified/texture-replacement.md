# Verified — Convention-First Texture2D Replacement

Convention-first `Texture2D` replacement is validated end-to-end against live MENACE content.

## Contract

- Modder drops a replacement image at `assets/replacements/textures/<target-name>--<pathId>.<ext>` in a mod project.
- `<target-name>--<pathId>` identifies a `Texture2D` asset in the Jiangyu asset index.
- Jiangyu compiles that image into the mod's AssetBundle as a `Texture2D` asset named `<target-name>`.
- At runtime, the loader swaps every occurrence of the original `Texture2D` bound as a material input on a `SkinnedMeshRenderer.sharedMaterials` slot with the replacement texture.
- Replacement only lands on `SkinnedMeshRenderer`s currently alive in the scene; the scanner walks them on scene load and whenever `HasReplacementTargets()` detects a new match.

## Surfaces covered

- Live `SkinnedMeshRenderer.sharedMaterials[*]` → `Material` properties → `Texture2D` bindings.

Other consumers of `Texture2D` (e.g. `MeshRenderer` materials, UI raw images, `ScriptableObject` texture fields) are not covered by this contract and remain unverified.

## Validation

2026-04-18, in-game smoke test with `RedSoldierTest` replacing the three `local_forces_basic_soldier*_BaseMap` `Texture2D` assets (pathIds 1473, 1511, 1610) with a solid red image.

Loader log line:

```
[Jiangyu] Applied 48 visual replacement(s), 0 sprite replacement(s), and 0 audio replacement(s).
```

All soldier variants in the Tactical scene rendered red; material bindings swapped without per-instance authoring. Compile pipeline did not require a Unity editor round-trip for the texture-only bundle.

See investigation note: [`../investigations/2026-04-18-sprite-audio-runtime-routing.md`](../investigations/2026-04-18-sprite-audio-runtime-routing.md) for the test setup, the parallel sprite and audio results, and the research questions those raised (which are **not** part of this verified contract).

## Out of scope for this contract

- `Sprite` replacement. Convention-first sprite replacement compiles and registers correctly, but the loader's runtime-apply scanner does not yet reliably reach UI sprites that are routed through sprite atlases or instantiated after initial scene load. See the investigation note above.
- `AudioClip` replacement. Convention-first audio replacement compiles and registers correctly, but UI click audio in MENACE is not routed through scene-scanned `AudioSource.clip` fields. See the investigation note above.
- `MeshRenderer` (non-skinned) material texture bindings. Not walked by the current scanner.
- `Texture2D` references held on `ScriptableObject` fields or in non-material runtime caches.
