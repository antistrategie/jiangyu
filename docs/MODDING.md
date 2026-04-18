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

This replaces the matching `Texture2D` asset when Jiangyu can prove the runtime texture name is unique. At runtime the loader mutates the game's `Texture2D` in place via `Graphics.ConvertTexture` + `Graphics.CopyTexture`, so every consumer — materials, UGUI, UI Toolkit, caches, template references — inherits the new pixels. See [`docs/research/verified/texture-replacement.md`](research/verified/texture-replacement.md) for the full contract.

## Sprite Replacement Path

For UI icons and other direct sprite replacement, use:

```text
assets/replacements/sprites/<target-name>--<pathId>.<ext>
```

Example:

```text
assets/replacements/sprites/MenaceFontIcons_0--9316.png
```

Sprite replacement piggybacks on texture replacement: every `Sprite` references a
backing `Texture2D`, so mutating that texture updates the sprite for every
consumer (UGUI, UI Toolkit, `SpriteRenderer`, cached references) automatically.

**Only sprites backed by a unique `Texture2D` can be replaced.** The compiler
rejects any sprite target whose backing texture backs more than one indexed
sprite (i.e. a packed atlas); the error names the atlas texture and lists
co-tenant sprites. This is a principle-7 compile-time check — Jiangyu refuses
to mutate a shared atlas and silently corrupt unrelated UI elements. Use
`jiangyu assets search <name> --type Sprite` to find candidates; if the
resulting compile fails with an atlas error, that sprite is not replaceable in
the current contract. See [`docs/research/verified/sprite-replacement.md`](research/verified/sprite-replacement.md) for the full contract.

**Re-index after upgrading Jiangyu** (`jiangyu assets index`) so the atlas
check has backing-texture identity to work with. The compiler will tell you if
the current index is too old.

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

**Current runtime limit.** MENACE caches UI `AudioClip` references outside
scene-resident `AudioSource.clip` fields — typical audio-manager pattern with
`PlayOneShot(clip)`, which ignores `AudioSource.clip`. The current sweep over
`AudioSource.clip` therefore cannot land UI audio. In-place `AudioClip` sample
mutation would cover the audio-manager case, but is blocked by an
Il2CppInterop marshalling bug on this MelonLoader + Unity 6 combination that
needs a hand-written ICall wrapper to work around. Tracked in `TODO.md`
"Runtime Replacement Strategy — Validated, Ready To Implement".
