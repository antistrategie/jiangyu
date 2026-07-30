# Replacement bundle split: design

Status: implemented and verified end to end on WOMENACE. The name-conflict prerequisite is `ReplacementNameValidation` (wired into `CompilationService` before output reset), the plan authority is `ReplacementBundlePlan`, and the Unity-side consumer is `BuildMeshReplacementBundle`.

## Problem

A single replacement AssetBundle per mod is all or nothing. For WOMENACE that is 733 MB carrying 1132 audio clips, 334 sprites and 40 textures, and a one-clip audio edit measures 146 s end to end, of which ~101 s is the Unity pass that re-bakes and recompresses the whole bundle.

## Why the split is loader-transparent

`BundleReplacementCatalog.LoadBundles` iterates every `.bundle` in the mod directory, `_bundlesByMod` is a `List<Il2CppAssetBundle>`, `isAdditionBundle` keys off the filename stem against the manifest's `additionPrefabs`, and sprites, textures and audio register into flat name-keyed catalogues with no dependence on bundle name or count. `LoaderManifest` has no field naming the replacement bundle. The 51 addition prefab bundles already exercise the multi-bundle path. No loader change, no manifest schema change.

## Design

1. **Bundle plan, CLI authority.** `GlbMeshBundleCompiler` maps every replacement asset name to a bundle key and writes `.jiangyu/glb_staging/bundleplan.txt`, passed to the Unity build via a `-bundlePlanPath` arg. The Unity script consumes the plan verbatim, so the grouping rule lives in one place. Keys, all lowercase:
   - `<mod>__audio__<group>` where group is the segment before the first `__` of the clip name (the additions tree's per-character folder, flattened), with `<mod>__audio` as the fallback for unprefixed names.
   - `<mod>__sprites` for sprite objects, their `sprite_source__` backing textures and addition sprites together (internal PPtr locality, and the whole set is ~28 MB).
   - `<mod>__textures__<group>` with `<mod>__textures` fallback.
   - `<mod>__meshes` for replacement meshes.
2. **Extensionless output files.** Planned bundles build into `unity_build/` without the `.bundle` extension, exactly like the current single bundle, so `AdditionPrefabStaging`'s `*.bundle` glob does not stage them as addition prefabs. The CLI copies each to `compiled/bundles/<key>.bundle`.
3. **Content-stable `Generated/`.** Without this the split does nothing for texture bundles: `Generated/` is wiped and recreated per raw-GLB pass, its GUIDs churn, and every texture bundle rebuilds anyway. The CLI computes a per-asset input hash (texture name + bytes + linear flag, mesh data + contract, sprite source bytes) into the plan. The Unity build loads its previous baked state (`.jiangyu/generated_baked`), skips `CreateAsset` when the hash matches and the `.asset` file exists, deletes generated assets absent from the plan (with their metas), and writes the new baked state after a successful bake. A failed bake leaves the old state file, so the next run re-bakes whatever did not complete.
4. **Incremental map build.** The replacement `BuildAssetBundles` call drops `ForceRebuildAssetBundle` for an incremental build verified by `BundleBuildVerify.AllWritten` with one forced retry, the same shape `BuildBundles` uses for prefab bundles. Unity's per-bundle hashing then rebuilds only the group bundles whose inputs changed.
5. **Completion markers.** Because the bundle files persist as incremental state, their existence proves nothing about a given run. Each Unity invocation passes a fresh token; the build script writes it to `.jiangyu/unity_build_mesh.done` (prefab pass: `unity_build_prefabs.done`) as its last act after verifying its outputs, and the compile accepts nothing less. The marker also serves as the invoker's cold-start retry sentinel.
6. **Reuse and staleness.** Each half records its produced bundle file list with its phase; reuse requires the fingerprint to match AND every recorded file to exist. `AdditionPrefabStaging.ClearStaleBuildOutput` only ever deletes prefab-shaped files (`*.bundle` and their manifests) and only runs when a mod has no prefab work, so replacement bundles and their Unity manifests survive every path.
7. **Two-pass mesh contracts.** The mesh contract extractor reads the `<mod>__meshes` bundle as its pass-1 input rather than the whole replacement bundle.
8. **Cache keys carry every input.** The bake hashes fold in the Jiangyu version (a release re-bakes once rather than serving assets baked by removed logic), the restored-bundle cache folds in a metadata fingerprint of the game data the clip restoration reads, the code-build key folds in a metadata fingerprint of the game's MelonLoader assemblies, staging restages on recorded content hashes (never file times), and the assets fingerprint folds in the bundle name so a mod rename rebuilds instead of reusing old-prefix bundles.

## Name-conflict prerequisite

Everything above assumes bundle-internal and bundle-file names are collision-free. `ReplacementNameValidation` fails the compile on:

- a duplicate name within the merged audio set or the merged sprite set (replacements plus additions, which share one staged directory and one runtime catalogue per kind), compared case-insensitively because staged files, `Generated/` assets and Unity's asset database all are on Windows and macOS,
- a duplicate in the `Generated/<name>.asset` namespace (textures, replacement meshes, replacement sprite objects and their `sprite_source__` textures); the same check reruns inside `GlbMeshBundleCompiler` once GLB-extracted texture names have merged in,
- an addition prefab stem whose flattened bundle name lands on a replacement bundle shape (`<mod>` or `<mod>__<category>[__<group>]` over the fixed category set). A prefab merely named `<mod>__<something-else>` is fine, which keeps the documented `Character/Character` convention working for a mod named after its character.

A UXML or icon bundle name can still collide with a planned replacement bundle key (the additions catalog does not enumerate those), so the collect-output step checks the shipped replacement bundle names against the staged addition bundle names, where both sets are concrete, and fails the compile on a clash.

## Measured results (WOMENACE, 2026-07-30, split bundles = 30)

| case | monolithic baseline | with the split |
| --- | --- | --- |
| full compile, both halves stale | ~440 s | 76 s (prefab manifests survive, so only changed work rebuilds even here) |
| one audio clip changed | 362 s | **32 s** (two samples: 32, 31) |
| one prefab changed | 188 s | **31 s** (two samples, after dropping the pre-pass wipe of unity_build so Unity's per-bundle manifests survive and only the changed prefab's bundle rebuilds) |
| one code/ source changed | ~45 s | 22 s |
| unchanged | 45 s | 19.6 s |

The audio-edit Unity pass is 11.3 s: zero imports at refresh (content-stable staging), all 40 texture bakes skipped (baked state), and Unity's incremental bundle build rewrites exactly one audio group bundle, verified by the other bundles' file mtimes. The full build also drops because the monolith was previously built twice per full compile (once by the prefab pass from stale persistent assignments, once by the map build).
