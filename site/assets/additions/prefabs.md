# Prefab additions

A **prefab addition** ships a GameObject in your mod, available for KDL `asset="..."` references on template fields whose declared Unity type is `UnityEngine.GameObject` or `List<UnityEngine.GameObject>`. Use prefab additions when a cloned EntityTemplate, ArmorTemplate, WeaponTemplate (etc.) needs a visual model that doesn't exist in vanilla.

## Authoring paths

Two ways to produce the bundle. Pick one per prefab.

### Unity Editor project (primary)

Jiangyu scaffolds a per-mod Unity project at `<mod>/unity/`. You author prefabs in Unity Editor; `jiangyu compile` invokes Unity in batchmode to build them into AssetBundles automatically.

```sh
jiangyu unity sync
```

This creates:

```text
<mod>/unity/
├── Assets/
│   ├── Jiangyu/              jiangyu-managed (re-sync refreshes)
│   │   ├── Editor/BuildBundles.cs
│   │   ├── Editor/ImportedPrefabPostProcessor.cs
│   │   ├── Editor/BakeHumanoid.cs
│   │   └── README.md
│   └── Prefabs/              your prefabs go here
├── Packages/manifest.json    seeded with no dependencies, extend as needed
└── .gitignore
```

Open the project in Unity Editor. On first open Unity creates `ProjectSettings/`, `Library/`, `ProjectVersion.txt`.

Author a prefab. Save it under `Assets/Prefabs/<asset-name>.prefab`, where `<asset-name>` is what your KDL will reference via `asset="..."`. The filename (and its relative path under `Assets/Prefabs/`) is the identity, and the Unity Object.name on the prefab can be whatever you set inside Unity.

Subfolders are supported. `Assets/Prefabs/dir/test_cube.prefab` becomes a bundle keyed under `dir/test_cube`, and a KDL reference of `asset="dir/test_cube"` resolves to it. Use folders to organise prefabs without affecting the asset names you reference.

`jiangyu compile` invokes Unity batchmode against the project, builds one AssetBundle per prefab, and stages them into `compiled/`.

Re-running `jiangyu unity sync` is idempotent. It refreshes `Assets/Jiangyu/` and `.gitignore` (Jiangyu owns those) and leaves your prefabs, custom packages, and `ProjectSettings/` untouched.

#### Importing a vanilla prefab to start from

`jiangyu unity import-prefab <name>` extracts a vanilla game prefab plus its dependencies (mesh, materials, textures, Avatar, AnimatorController) into `unity/Assets/Imported/<name>/`. Useful when you want to clone-and-modify rather than author from scratch.

#### Baking a humanoid character from a glTF

Authoring a soldier-shape addition prefab by hand is mostly correct steps plus a handful of gotchas any one of which silently breaks the result. `BakeHumanoid` automates them:

- **Avatar build**. Unity Editor's Avatar auto-config misidentifies bones on non-Mecanim rigs and fails silently. `BakeHumanoid` builds the Avatar via `AvatarBuilder.BuildHumanAvatar` with an explicit MENACE→Unity humanoid bone map (Hips→Hips, UpperArm_L→LeftUpperArm, etc.) so the avatar comes out usable first try.
- **T-pose muscle-zero**. The avatar's calibration is captured from the current scene-state bone transforms. The script assumes (and the HelpBox documents) the glTF is in T-pose at rest; if it isn't, Mecanim retargets badly. Whatever produces your glTF should bake T-pose into the rest pose before exporting.
- **Material clone without leakage**. The Menace/* shader is an AssetRipper stub and renders magenta in the Editor; you can't preview. The script clones shader + non-texture properties + keywords from a vanilla soldier reference material, then assigns the baked atlas to `_BaseMap` and 1×1 neutral defaults to `_NormalMap`/`_MaskMap` so the runtime doesn't fall back to its "white" mask map (Metallic=1 → chrome-blue render). Reference textures that wouldn't UV-map correctly onto the new mesh are explicitly nulled rather than left to leak through.
- **Root parent over Hips**. The reference avatar's `m_TOS` uses paths like `Root/Hips/Spine/...`. Without a `Root` GameObject above `Hips` the Mecanim bone resolution misses and the character stays stuck in T-pose at runtime.
- **LODGroup wiring**. Scans SMRs for meshes named `<basename>_LOD<N>`, sorts by index, and configures screen-space thresholds.
- **Animator config**. Attaches the reference's `runtimeAnimatorController` and copies `applyRootMotion` + `cullingMode`.

You can do all of this by hand in Unity Editor instead; the script doesn't gate access to anything Unity-public. It exists because every one of those steps has a sharp edge, and encoding them in the editor utility means the next humanoid character doesn't have to re-discover them.

Open via `Jiangyu → Bake humanoid prefab from glTF…`, or invoke batchmode via `-executeMethod Jiangyu.Mod.BakeHumanoid.BakeBatch` with `-gltfFolder`, `-referencePrefab`, `-outputDir`, `-outputName`. Output layout:

```text
Assets/Prefabs/MyCharacter/
├── main.prefab               the addition prefab
├── baked.mat                 atlas-textured material, Menace/* shader
└── avatar.asset              humanoid Avatar with T-pose muscle-zero
```

Then KDL reference is `asset="MyCharacter/main"`.

::: warning Requirements on the input glTF
- T-pose at rest. The avatar's muscle-zero is built from current bone transforms; a non-T-pose rest will retarget badly.
- MENACE humanoid bone names: Hips, Spine, Spine2, Neck, Head, Shoulder_L, UpperArm_L, LowerArm_L, Hand_L, UpperLeg_L, LowerLeg_L, Foot_L, and R-side equivalents.
- LOD meshes named `<basename>_LOD0..LODN`. Basename is auto-detected from the mesh names.
:::

### Pre-built bundle drop (escape hatch)

If you already have an AssetBundle (built in your own Unity project, or shared from another mod), drop it directly under `assets/additions/prefabs/<name>.bundle`. The compile pipeline picks it up alongside Unity-built bundles. The filename stem is the asset name your KDL references, and the Object.name on the GameObject inside the bundle does not have to match.

When the same name appears in both sources, the Unity-built bundle wins so stale hand-shipped artefacts don't shadow fresh builds.

## KDL syntax

Reference a prefab addition the same way as any other asset:

```kdl
patch "EntityTemplate" "enemy.alien_drone" {
    clear "Prefabs"
    append "Prefabs" asset="my_drone"
}
```

For a single-GameObject field, use the scalar form:

```kdl
patch "ArmorTemplate" "armor.player_fatigues" {
    set "SquadLeaderModelFixed" asset="my_squad_leader"
}
```

The category is **inferred** from the destination field's declared Unity type. `Prefabs` on `EntityTemplate` is `List<UnityEngine.GameObject>`, so the compiler looks for `my_squad_leader` under the prefab addition path (either `unity/Assets/Prefabs/my_squad_leader.prefab` or `assets/additions/prefabs/my_squad_leader.bundle`).

### Soldier visual overrides on a specific cloned unit

Patching an ArmorTemplate directly affects every unit wearing that armor. To swap visuals for a **specific cloned unit leader** without touching other units, declare a `bind "leader_armor"` directive linking the cloned `UnitLeaderTemplate` to a cloned `ArmorTemplate`:

```kdl
clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_clone" {
    set "InitialAttributes" index=4 20
}

clone "ArmorTemplate" from="armor.player_fatigues" id="armor.darby_clone" {
    set "SquadLeaderModelFemaleBlack" asset="my_squad_leader_clone"
    clear "MaleModels"
    append "MaleModels" asset="my_squad_leader_clone"
    clear "FemaleModels"
    append "FemaleModels" asset="my_squad_leader_clone"
}

bind "leader_armor" leader="squad_leader.darby_clone" armor="armor.darby_clone"
```

See [Bindings](/templates#bindings) for the full directive reference.

### Referencing vanilla game assets

You can use `asset="..."` to point at existing in-game assets directly, with no need to clone them into your project. The validator consults Jiangyu's game asset index, so any GameObject (or Sprite, Texture2D, etc.) in the game's `resources.assets` is fair game:

```kdl
clone "ArmorTemplate" from="armor.player_fatigues" id="armor.darby_clone" {
    set "SquadLeaderModelFemaleBlack" asset="rmc_default_female_soldier_2"
}
```

The lookup against the vanilla asset registry is by Unity Object name and is case-sensitive. The addition prefab lookup (Phase 1, against bundles your mod ships) is case-insensitive: Unity normalises asset bundle filenames to lowercase on write, so a prefab authored under `Assets/Prefabs/MyCharacter/main.prefab` lands on disk as `mycharacter__main.bundle`, and your KDL ref can still write `asset="MyCharacter/main"`. Asset categories are inferred from the destination field's Unity type just like for project additions.

## Constraints

- Open the project with the same Unity Editor version as the game. The build script refuses to build under a mismatched version.
- Forward slashes only in `asset="..."` references. Backslashes are rejected at parse time. Slashes mirror the prefab's relative path under `Assets/Prefabs/`, so `asset="MyCharacter/main"` resolves to the bundle built from `Assets/Prefabs/MyCharacter/main.prefab`.
- **Bundled materials must use `Menace/*` shaders** (`Menace/building`, etc.). MENACE doesn't ship URP's stock `Universal Render Pipeline/Lit` / `Unlit`, so materials referencing those render magenta at runtime. The `Menace/*` shaders also render magenta in the Unity Editor preview (they're stubs in your project), so iterate in-game rather than trusting the editor preview. `BakeHumanoid` clones the shader from the reference soldier material for you; only matters if you author a material by hand.
- **Bundled meshes and materials must be project assets**, not Unity built-in references. A prefab created from `GameObject.CreatePrimitive(Cube)` references Unity's built-in cube mesh and `Default-Material` by hardcoded ID, and production game runtimes commonly strip those built-ins, so the prefab spawns as an invisible ghost. Import a model (glTF/FBX) or import an existing vanilla prefab via `jiangyu unity import-prefab` for assets that bundle correctly.
- The scaffolded project ships no game-asset reference DLLs. Pure-visual prefabs (SkinnedMeshRenderer with materials, no scripts) build cleanly without them. Soldier-shape humanoid additions also build cleanly: MENACE's Footprints + Ragdoll components are attached at load time so they don't need to be in the bundle. If you need other MENACE MonoBehaviours on a non-humanoid prefab, copy the wrapper DLLs from `<game>/MelonLoader/Il2CppAssemblies/` into `Assets/Plugins/` manually.
