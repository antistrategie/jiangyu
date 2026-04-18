# Modding

Jiangyu uses convention-first replacements under `assets/replacements/`.

Current supported replacement categories:

- models
- textures
- sprites
- audio

Use `assets search` to find the target and the suggested replacement path for each supported asset type.

## 3D Model Targets

For **3D model replacements**, the preferred modder-facing target is a **`PrefabHierarchyObject`**, not a raw `GameObject`.

Example:

```bash
dotnet /path/to/jiangyu.dll assets search local_forces_basic_soldier --type PrefabHierarchyObject
```

If a prefab-hierarchy view exists, that is the target modders should use. `GameObject` remains useful for low-level inspection and internal resolution, but it is not the preferred authoring target.

## Model Replacement Path

Current convention-first model replacement path shape:

```text
assets/replacements/models/<target-name>--<pathId>/model.gltf
assets/replacements/models/<target-name>--<pathId>/model.glb
```

Example:

```text
assets/replacements/models/el.local_forces_basic_soldier--519/model.gltf
```

or:

```text
assets/replacements/models/el.local_forces_basic_soldier--519/model.glb
```

Inside the model file, mesh names should match the target mesh/LOD contract.

Jiangyu accepts authored skinned model replacements from Blender in both `.gltf` and `.glb` form. The compiler normalises authored metre-space skin data onto Jiangyu's proven replacement path; modders do not need to preserve Jiangyu-specific glTF metadata for that round-trip to work.

For changed rest poses or moderate proportion drift, Jiangyu automatically exports the indexed target into its own compiler-owned reference model and retargets the authored mesh back onto the game's expected skeleton contract. v1 bind-pose retargeting supports authored skinned models with the same bone names and hierarchy as the game skeleton.

If a replacement only provides part of an LOD family, Jiangyu warns at compile time and the loader uses the nearest available replacement within that family at runtime. Complete LOD sets are still preferred.

## Current Limitation

At runtime, Jiangyu still resolves live mesh replacements by `sharedMesh.name`.

That means convention-first model replacement is only safe when the target model's expected mesh names are unique. Jiangyu should reject ambiguous targets at compile time rather than silently replacing the wrong live mesh.

## Texture Replacement Path

For direct texture replacement, use:

```text
assets/replacements/textures/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/textures/local_forces_basic_soldier_BaseMap--1234.png
```

This replaces the matching `Texture2D` asset when Jiangyu can prove the runtime texture name is unique. Validated end-to-end for textures bound on `SkinnedMeshRenderer.sharedMaterials`; see [`docs/research/verified/texture-replacement.md`](research/verified/texture-replacement.md) for the full scope.

## Sprite Replacement Path

For UI icons and other direct sprite replacement, use:

```text
assets/replacements/sprites/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/sprites/MenaceFontIcons_0--9316.png
```

Jiangyu compiles these image files into real `Sprite` assets and the loader scans the following live surfaces for a matching `sprite.name`:

- `SpriteRenderer.sprite`
- `UnityEngine.UI.Image.sprite`

This is separate from `Texture2D` replacement. UI icons should be treated as sprite
targets, not raw textures.

**Current runtime limit.** This is a scoped direct-reference sweep, not a general
`Sprite` replacement contract. UI sprites routed through sprite atlases
(`SpriteAtlas.GetSprite`), pre-resolved packed `Sprite` references baked into prefabs
at build time, or `ScriptableObject` `Sprite` fields are not yet covered. A first
in-game smoke test on 2026-04-18 showed a unique `icon_hitpoints` sprite replacement
that compiled and registered cleanly but produced zero runtime applications — see
`docs/research/investigations/2026-04-18-sprite-audio-runtime-routing.md`. Expect
sprite replacement to land only when the target's `sprite.name` is the visible
`SpriteRenderer`/`Image` reference at apply time.

## Audio Replacement Path

For direct audio replacement, use:

```text
assets/replacements/audio/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/audio/sfx_rifle_fire--4321.wav
```

Jiangyu applies these replacements to matching `AudioSource.clip` references when the runtime clip name is unique.

**Current runtime limit.** This is a scoped direct-reference sweep, not a general
`AudioClip` replacement contract. UI sounds and other SFX routed through audio
managers that cache `AudioClip` references and fire them via `PlayOneShot(clip)` do
not land — `PlayOneShot` uses its argument clip and ignores `AudioSource.clip`. A
first in-game smoke test on 2026-04-18 swapped `button_click_01` on one
`AudioSource.clip` without audibly changing UI button clicks. See
`docs/research/investigations/2026-04-18-sprite-audio-runtime-routing.md`.
