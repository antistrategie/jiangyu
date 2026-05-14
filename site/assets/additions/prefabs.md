# Prefab additions

A **prefab addition** ships a GameObject in your mod, available for KDL `asset="..."` references on template fields whose declared Unity type is `UnityEngine.GameObject` or `List<UnityEngine.GameObject>`. Use prefab additions when a cloned EntityTemplate, ArmorTemplate, WeaponTemplate (etc.) needs a visual model that doesn't exist in vanilla.

## Authoring paths

Two ways to produce the bundle. Pick one per prefab.

### Unity Editor project (primary)

Jiangyu scaffolds a per-mod Unity project at `<mod>/unity/`. You author prefabs in Unity Editor; `jiangyu compile` invokes Unity in batchmode to build them into AssetBundles automatically.

```sh
jiangyu unity init
```

This creates:

```text
<mod>/unity/
├── Assets/
│   ├── Jiangyu/              jiangyu-managed (re-init refreshes)
│   │   ├── Editor/BuildBundles.cs
│   │   ├── Editor/ImportedPrefabPostProcessor.cs
│   │   └── README.md
│   └── Prefabs/              your prefabs go here
├── Packages/manifest.json    seeded with no dependencies; extend as needed
└── .gitignore
```

Open the project in Unity Editor. On first open Unity creates `ProjectSettings/`, `Library/`, `ProjectVersion.txt`.

Author a prefab. Save it under `Assets/Prefabs/<asset-name>.prefab`, where `<asset-name>` is what your KDL will reference via `asset="..."`. The filename (and its relative path under `Assets/Prefabs/`) is the identity; the Unity Object.name on the prefab can be whatever you set inside Unity.

Subfolders are supported. `Assets/Prefabs/dir/test_cube.prefab` becomes a bundle keyed under `dir/test_cube`, and a KDL reference of `asset="dir/test_cube"` resolves to it. Use folders to organise prefabs without affecting the asset names you reference.

`jiangyu compile` walks `Assets/Prefabs/`, invokes Unity batchmode against the project, and builds one AssetBundle per prefab into `.jiangyu/unity-build/`. The compile pipeline then stages those bundles into `compiled/` and records their names on the compiled manifest's `additionPrefabs` list.

Re-running `jiangyu unity init` is idempotent. It refreshes `Assets/Jiangyu/` and `.gitignore` (Jiangyu owns those) and leaves your prefabs, custom packages, and `ProjectSettings/` untouched.

#### Importing a vanilla prefab to start from

`jiangyu unity import-prefab <name>` extracts a vanilla game prefab plus its dependencies (mesh, materials, textures, Avatar, AnimatorController) into `unity/Assets/Imported/<name>/` via AssetRipper. Useful when you want to clone-and-modify rather than author from scratch. `ImportedPrefabPostProcessor` auto-strips missing-script components (Unity refuses to save prefabs that reference scripts not in the project) when the imported assets land.

### Pre-built bundle drop (escape hatch)

If you already have an AssetBundle (built in your own Unity project, or shared from another mod), drop it directly under `assets/additions/prefabs/<name>.bundle`. The compile pipeline picks it up alongside Unity-built bundles. The filename stem is the asset name your KDL references; the Object.name on the GameObject inside the bundle does not have to match.

When the same name appears in both sources, the Unity-built bundle wins so stale hand-shipped artefacts don't shadow fresh builds.

## KDL syntax

Reference a prefab addition the same way as any other asset:

```kdl
patch "EntityTemplate" "enemy.alien_drone" {
    set "Prefabs" {
        clear
        append asset="my_drone"
    }
}
```

For a single-GameObject field, use the scalar form:

```kdl
patch "ArmorTemplate" "armor.player_fatigues" {
    set "SquadLeaderModelFixed" asset="darby_voymastina"
}
```

The category is **inferred** from the destination field's declared Unity type. `Prefabs` on `EntityTemplate` is `List<UnityEngine.GameObject>`, so the compiler looks for `darby_voymastina` under the prefab addition path (either `unity/Assets/Prefabs/darby_voymastina.prefab` or `assets/additions/prefabs/darby_voymastina.bundle`).

### Soldier visual overrides on a specific cloned unit

Patching an ArmorTemplate directly affects every unit wearing that armor. To swap visuals for a **specific cloned unit leader** without touching other units, declare a `bind "leader_armor"` directive linking the cloned `UnitLeaderTemplate` to a cloned `ArmorTemplate`:

```kdl
clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_clone" {
    set "InitialAttributes" index=4 20
}

clone "ArmorTemplate" from="armor.player_fatigues" id="armor.darby_clone" {
    set "SquadLeaderModelFemaleBlack" asset="my_darby_voymastina"
    clear "MaleModels"
    append "MaleModels" asset="my_darby_voymastina"
    clear "FemaleModels"
    append "FemaleModels" asset="my_darby_voymastina"
}

bind "leader_armor" leader="squad_leader.darby_clone" armor="armor.darby_clone"
```

The bind directive is pure Jiangyu metadata. See [Bindings](/templates#bindings) in the Templates reference for the full mechanism and the rationale (direct ArmorTemplate patching or `InfantryUnitTemplate` redirects don't isolate cleanly).

## Compile-time checks

The compiler verifies:

- Every `asset="..."` reference resolves either to a project addition (mod-shipped) **or** to a vanilla game asset of the matching category. References that don't exist in either are a compile error.
- The destination field's declared Unity type is one of `UnityEngine.GameObject`, `List<UnityEngine.GameObject>`, or a wrapper array type the runtime knows how to write into.

### Referencing vanilla game assets

You can use `asset="..."` to point at existing in-game assets directly — no need to clone them into your project. The validator consults Jiangyu's game asset index, so any GameObject (or Sprite, Texture2D, etc.) in the game's `resources.assets` is fair game:

```kdl
clone "ArmorTemplate" from="armor.player_fatigues" id="armor.darby_clone" {
    set "SquadLeaderModelFemaleBlack" asset="rmc_default_female_soldier_2"
}
```

The lookup is by Unity Object name and is case-sensitive. Asset categories are inferred from the destination field's Unity type just like for project additions.

## Runtime resolution

At apply time the loader walks the mod's bundle catalogue first (the `additionPrefabs` dictionary populated from your compiled bundles). If the name isn't there, it falls back to the live game-asset registry via `Resources.FindObjectsOfTypeAll<GameObject>`. So a mod can substitute either its own prefab or a vanilla game GameObject through the same KDL reference.

The runtime registry contains assets Unity has loaded into memory at apply time. Most game content is loaded eagerly during startup (anything referenced by a loaded ScriptableObject), so vanilla references resolve in practice. An asset that's never been touched this session (a prefab unreachable from any loaded template) won't be found; if this happens you'll see a "not found" warning at apply time.

### Shader rebind

When the loader registers a bundled prefab, it walks every Renderer's materials and reassigns each material's shader by name via `Shader.Find`. This is essential because:

- Bundled shaders are stubs (AssetRipper-extracted, or any shader that doesn't have variants precompiled for MENACE's runtime).
- Without rebinding, bundled materials fall back to Unity's `Hidden/Shader Graph/FallbackError` (magenta) at render time.
- The runtime has the real `Menace/*` shaders loaded for the game's own assets, so `Shader.Find("Menace/building")` (or any other in-runtime shader name) resolves to a working shader with correct variants.

The rebind preserves all material properties (colors, textures, keyword toggles). Use the `Menace/*` shader family (`Menace/building`, etc.) on your bundled materials — those are the shaders MENACE actually ships. URP's stock shaders (`Universal Render Pipeline/Lit`, `Universal Render Pipeline/Unlit`) are **not** in the runtime; bundling materials that reference them leaves the rebind unable to resolve them and the material renders magenta in-game. A warning is logged at prefab-registration time listing every material whose shader name can't be resolved.

## Constraints

- Open the project with the same Unity Editor version as the game. `BuildBundles.cs` checks `Application.unityVersion` and refuses to build under a mismatched version.
- Forward slashes only in `asset="..."` references; backslashes are rejected at parse time. Slashes mirror the prefab's relative path under `Assets/Prefabs/`; `asset="darby/body"` resolves to the bundle built from `Assets/Prefabs/darby/body.prefab`.
- **Bundled materials must use `Menace/*` shaders.** MENACE runs its own URP-derived shader family (`Menace/building`, etc.) and does not include URP's stock `Universal Render Pipeline/Lit` / `Unlit`. AssetRipper extracts the `Menace/*` shaders as stubs that render magenta in the Unity Editor's scene + material previews; that's expected. Iterate by building the bundle and inspecting in-game; don't trust the editor preview.
- **Bundled meshes and materials must be project assets**, not Unity built-in references. A prefab created from `GameObject.CreatePrimitive(Cube)` references Unity's built-in cube mesh and `Default-Material` by hardcoded ID; production game runtimes commonly strip those built-ins, so the prefab spawns as an invisible ghost. Import a model (glTF/FBX) or import an existing vanilla prefab via `jiangyu unity import-prefab` for assets that bundle correctly.
- The scaffolded project ships no game-asset reference DLLs. Pure-visual prefabs (SkinnedMeshRenderer with materials, no scripts) build cleanly without them. If you write custom MonoBehaviours that reference game types, copy the wrapper DLLs from `<game>/MelonLoader/Il2CppAssemblies/` into `Assets/Plugins/` manually.

## Animation retargeting

For soldier-shaped prefabs, the game's AnimatorControllers drive clips through Mecanim's humanoid muscle space. Configure your prefab's Avatar as `Animation Type: Humanoid` in the Rig tab, map the 20 humanoid bones in the Avatar configurator, and the game's existing animations retarget to your skeleton at runtime. You don't have to match the game's bone naming or hierarchy.

Vehicles, turrets, and other non-humanoid models use generic Avatars; for those you'd need to ship your own AnimatorController plus animation clips that match your rig, or skin to the host prefab's skeleton.
