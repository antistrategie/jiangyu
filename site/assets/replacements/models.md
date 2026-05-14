# Model replacements

Replace any 3D model in the game by name. Jiangyu accepts authored glTF or GLB files from Blender (or any DCC tool that produces skinned glTF). At compile time it normalises the authored mesh onto the game's skeleton contract. At runtime, the loader swaps the live `SkinnedMeshRenderer`'s mesh, rebinds bones, and rewires materials so every existing reference picks up your replacement, including units spawned later in a session.

::: tip Replacement vs. addition
This page covers **replacing** a vanilla mesh in place. To **add** a new model that didn't exist before (e.g. a brand-new unit with its own prefab), see [Prefab additions](/assets/additions/prefabs). The two flows are orthogonal: replacement is mesh-level surgery on an existing prefab, and addition ships a complete new GameObject.
:::

## Studio workflow

1. Open the [Asset Browser](/studio#asset-browser) pane.
2. Type the asset name in the search box. Filter the type to **Model**.
3. Select the asset. The detail panel shows a `Replace` row with the folder under `assets/replacements/` to save your replacement at, for example:

    ```text
    models/el.local_forces_basic_soldier_white/model.gltf (.glb)
    ```

    The `(.glb)` part means the compiler accepts either format. Pick whichever your DCC tool exports.

4. Click **Export** to pull the vanilla model out as a starting point. Studio writes a self-contained model package to your chosen destination: a directory named after the target, containing a cleaned `model.gltf` plus any auxiliary textures.
5. Open the exported `model.gltf` in Blender, edit the meshes, save it back into your project's `assets/replacements/models/<target-name>/` directory.
6. [Compile](/studio#compile).

## File layout

```text
assets/replacements/models/<target-name>/model.gltf
assets/replacements/models/<target-name>/model.glb
```

Pick one of `.gltf` or `.glb` per replacement target. The directory must contain exactly one model file. Auxiliary textures referenced by the model's materials live alongside it under the same directory.

When the target name is shared by multiple models in the index, append `--<pathId>` to the directory name to pick a specific instance:

```text
assets/replacements/models/SM_Env_Rock_Seeding_01--1085/model.gltf
```

Studio's `Replace` row already includes the `--<pathId>` suffix when the name needs disambiguation. Use the path verbatim.

## Mesh hierarchy

A model in MENACE has a tree of named meshes (a soldier, for instance, might have separate meshes for the body, head, and weapon, each at a fixed position under the prefab root). The exported `model.gltf` mirrors that tree exactly: every mesh sits at the same position in the hierarchy as its target in the game prefab.

The compiler matches your authored meshes to game targets by their **position in that hierarchy**. Blender's `.001` numeric suffixes are stripped automatically, so duplicating an object in Blender and editing the duplicate doesn't break the match.

Concrete rules:

- **Don't rename objects.** Object names form the matching path. Rename one and that mesh won't match its target.
- **Don't reparent objects.** Moving an object to a different position in the hierarchy changes its path and breaks the match.
- **Edit geometry freely.** Vertex positions, weights, materials, normals, and textures can all change without affecting the match.

If the compiler can't match any of your meshes to expected targets, it rejects the build and lists the expected paths.

**Adding new meshes isn't supported.** The compiler only emits replacements for renderer paths that already exist in the game prefab, and meshes in your glTF that don't match an expected target are silently dropped. To add geometry beyond what the original model has, combine it into one of the existing meshes.

## Skinning weights

Jiangyu doesn't rewrite vertex weights. Whatever the authored glTF ships is what goes into the bundle. Match the kind of weights the original mesh used:

- **Rigid-skinned vanilla meshes** (every vertex 100% bound to a single bone, typical of mechanical rigs like vehicle chassis where each wheel vert follows its own wheel bone) export as rigid weights. Author rigid-skinned replacements.
- **Blended-skinned vanilla meshes** (vertices influenced by multiple bones, typical of character rigs where joints need smooth deformation) export with the per-vertex weight mix preserved. Author blended-skinned replacements.

Mixing them up is the most common cause of visual artefacts. If a mechanical part looks like it's growing and shrinking while moving, you've blended a rig that should be rigid. Rework it with hard parenting in Blender.

## LODs

If the target has multiple LODs (LOD0, LOD1, LOD2) and your replacement only provides some, the compiler warns and the loader uses the nearest available LOD at runtime. Complete LOD sets are still preferred. Replacing only LOD0 is acceptable for prototyping.

## Vertex space

Jiangyu auto-detects whether your authored mesh is in metres or centimetres by comparing its bounds to the vanilla target's local AABB:

- Authored extent ≈ target extent: pass through unchanged.
- Authored extent ≈ target extent × 0.01: apply a 100× scale-up (authored in metres, target stored in centimetres).

You don't need to set bone-name conventions, naming prefixes, or vehicle-vs-character hints. Author at whatever scale Blender shows by default.

## Textures bundled with the model

Materials in your glTF can reference texture files alongside `model.gltf`. The compiler picks them up automatically and emits matching `Texture2D` replacements. Name the texture files using MENACE's suffix conventions so the compiler infers the correct colour space:

| Suffix       | Treated as |
| ------------ | ---------- |
| `_MaskMap`   | linear     |
| `_NormalMap` | linear     |
| `_Normal`    | linear     |
| `_EffectMap` | linear     |
| anything else | sRGB      |

A normal map saved without one of the linear suffixes will be treated as sRGB and the resulting lighting will be wrong. The exported package's textures are already named correctly. Keep their names when you save changes.

## Compile-time errors

Compile refuses the build, with a clear message, when:

- The asset index isn't built or is unreadable.
- The target name doesn't resolve to any `PrefabHierarchyObject` or `GameObject` in the index.
- The target name is ambiguous and no `--<pathId>` was supplied. The error lists candidate paths.
- The replacement directory doesn't contain exactly one `.gltf` or `.glb` file.
- The authored glTF has no meshes whose names match the target's renderer paths. The error lists expected renderer paths.

## CLI alternative

```sh
jiangyu assets index
jiangyu assets search el.local_forces_basic_soldier_white --type PrefabHierarchyObject
jiangyu assets export model el.local_forces_basic_soldier_white
```

`assets export model` accepts `--path-id` (and `--collection`) to pick a specific instance when the name is ambiguous, and `--raw` to keep the native AssetRipper representation for inspection. Don't author against `--raw`. Use the default cleaned export.
