# Jiangyu-managed scripts

This directory is owned by Jiangyu. Re-running `jiangyu unity init` overwrites
its contents. Do not edit files here directly; changes will be lost on the
next init.

Modder-authored assets (prefabs, materials, textures, custom editor scripts)
belong outside this directory, e.g. `Assets/Prefabs/`, `Assets/Materials/`.

`Editor/BuildBundles.cs` is the batchmode entry the Jiangyu compile pipeline
invokes to build every prefab under `Assets/Prefabs/` into its own
AssetBundle.

`Editor/ImportedPrefabPostProcessor.cs` strips missing-script components from
prefabs under `Assets/Imported/` so they save without errors.

## Shaders

Use the `Menace/*` shaders (`Menace/building`, etc.) on bundled materials.
AssetRipper extracts these as stubs, so the Unity Editor renders them as
magenta in the scene and material previews. That is expected. Iterate by
building the bundle and checking the result in-game; do not trust the
editor preview.
