# Template Discovery Resolution

Date: 2026-04-14

## Goal

Finish Jiangyu-native offline template discovery so `templates index` and `templates inspect` can work from the game's own data rather than from legacy `MenaceAssetPacker` schema assumptions.

## Summary

The blocker turned out to be a dependency-resolution bug in vendored AssetRipper rather than a MENACE-specific lack of script metadata.

Raw serialised metadata already preserved the needed bridge:

- `ObjectInfo.ScriptTypeIndex` on MENACE `MonoBehaviour` objects
- `SerializedFile.ScriptTypes[scriptTypeIndex]`
- dependency file index `1`
- local object IDs that match the `m_Script` references seen in inspection output

The failure was later in import:

- `resources.assets` depended on `globalgamemanagers.assets`
- AssetRipper's `RequestDependency()` fallback stripped the extension too early
- that caused `globalgamemanagers.assets` to resolve to the sibling `globalgamemanagers` file when both existed
- the wrong file does not contain the script `MonoScript` objects
- as a result `MonoBehaviour.ScriptP` stayed null and `m_Structure` remained `StatelessAsset`

After fixing dependency resolution order, Jiangyu recovered real `MonoScript` assets and offline template discovery started working.

## Evidence

### 1. Raw file metadata preserved the script bridge

Low-level probe results from `resources.assets`:

- `bunker` (`pathId=112035`) had `ScriptTypeIndex=167`
- `turret.construct_gunslinger_twin_heavy_auto_repeater` (`pathId=112373`) had `ScriptTypeIndex=311`
- `ScriptTypes[167] = [fileIndex=1, localId=1398]`
- `ScriptTypes[311] = [fileIndex=1, localId=2394]`

Those local IDs exactly matched the `m_Script` references already visible in Jiangyu's object inspector.

### 2. The target objects were real `MonoScript` assets in `globalgamemanagers.assets`

Low-level probe results from `globalgamemanagers.assets`:

- `1398` is class `MonoScript`
- `2394` is class `MonoScript`

### 3. AssetRipper was importing the wrong sibling file

Before the fix, AssetRipper's imported `GameData` view showed:

- `globalgamemanagers` with `20` assets
- no `globalgamemanagers.assets` collection containing the script objects
- `resources.assets` template candidates with `ScriptP null? True`

`PlatformGameStructure.RequestDependency()` matched filename-without-extension before checking `DataPaths` for an exact file. That allowed a dependency on `globalgamemanagers.assets` to resolve to `globalgamemanagers`.

### 4. After the fix, imported script identity worked offline

After reordering the dependency lookup:

- `globalgamemanagers.assets` appeared as an imported collection with `5641` assets
- it contained `3187` `MonoScript` assets
- `1398` resolved to `EntityTemplate`
- `2394` resolved to `WeaponTemplate`
- `resources.assets` template candidates now had:
  - `ScriptP null? False`
  - resolved script `EntityTemplate` / `WeaponTemplate`

### 5. Jiangyu template indexing now works

Command:

```bash
dotnet run --project src/Jiangyu.Cli/Jiangyu.Cli.csproj -- templates index
```

Observed result:

- `Indexed 6215 template instances across 73 template types.`

## Code Change

The fix was applied in vendored AssetRipper:

- `vendor/AssetRipper/Source/AssetRipper.Import/Platforms/PlatformGameStructure.cs`

Change:

- move the `DataPaths` exact-file lookup ahead of the filename-without-extension fallback in `RequestDependency()`

That preserves the more specific dependency target when both:

- `globalgamemanagers`
- `globalgamemanagers.assets`

exist side by side.

## Conclusion

Jiangyu now has a working Jiangyu-native offline template identity bridge for MENACE:

- template candidates are discovered from game data
- `m_Script` resolves to real `MonoScript` assets
- `m_Structure` materializes as the real script type instead of `StatelessAsset`

This is enough to start real structural validation of legacy schema claims.

## Follow-up

1. Keep using Jiangyu-native template inventory as the selection source for validation targets.
2. Improve object inspection presentation later so resolved script identity is surfaced more directly at the top level for `MonoBehaviour` assets.
3. Continue with structural schema spot-checks before trusting legacy schema semantics.
