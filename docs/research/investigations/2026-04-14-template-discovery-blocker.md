# Template Discovery Blocker

Status: resolved later on 2026-04-14. See `docs/research/investigations/2026-04-14-template-discovery-resolution.md`.

Date: 2026-04-14

## Goal

Use Jiangyu's own template inventory and object inspector to begin structural validation of legacy `EntityTemplate` and `WeaponTemplate` claims without relying on `MenaceAssetPacker` for template discovery.

## Summary

Jiangyu-native template indexing is still blocked, but the failure mode is narrower now.

What was established:

- Known template candidates such as `bunker` and `turret.construct_gunslinger_twin_heavy_auto_repeater` exist in Jiangyu's own asset index.
- They surface as Unity `MonoBehaviour` assets rather than script-specific types like `EntityTemplate` or `WeaponTemplate`.
- MENACE's game binaries do contain legacy type names such as `EntityTemplate`, `WeaponTemplate`, and `LocaState`.

What was tried:

1. `v1` classifier: top-level Unity `ClassName.EndsWith("Template")`
2. `v2` classifier: for `MonoBehaviour`, resolve `m_Script` and match the script class name ending with `Template`
3. enabled AssetRipper script import (`ScriptContentLevel.Level2`) for template indexing and object inspection

Result:

- `templates index` still finds **0 template instances**
- inspected `MonoBehaviour` objects still expose:
  - unresolved `m_Script` references (`fileId=1`, `pathId=...`)
  - `m_Structure` as `StatelessAsset`

So the current blocker is not just classifier logic. Jiangyu still lacks a bridge from imported `MonoBehaviour` assets to their real IL2CPP script types in this game.

## Evidence

### 1. Template index runs

Command:

```bash
dotnet run --project src/Jiangyu.Cli/Jiangyu.Cli.csproj -- templates index
```

Observed result for both `v1` and `v2` classification attempts:

- `Indexed 0 template instances across 0 template types.`

### 2. Known legacy candidates exist in Jiangyu's asset index

From `asset-index.json` and `jiangyu assets search ... --type MonoBehaviour`:

- `bunker` → `MonoBehaviour` in `resources.assets`, `pathId=112035`
- `turret.construct_gunslinger_twin_heavy_auto_repeater` → `MonoBehaviour` in `resources.assets`, `pathId=112373`

### 3. Object inspection of those candidates

Command:

```bash
dotnet run --project src/Jiangyu.Cli/Jiangyu.Cli.csproj -- assets inspect object --collection resources.assets --path-id 112035 --max-depth 2 --max-array-sample 4 --output pretty
```

Command:

```bash
dotnet run --project src/Jiangyu.Cli/Jiangyu.Cli.csproj -- assets inspect object --collection resources.assets --path-id 112373 --max-depth 4 --max-array-sample 4 --output pretty
```

Common observed shape:

- top-level `className` is `MonoBehaviour`
- each object has a non-null `m_Script` reference:
  - `bunker` → `fileId=1`, `pathId=1398`
  - `turret.construct_gunslinger_twin_heavy_auto_repeater` → `fileId=1`, `pathId=2394`
- each object has an `m_Structure` field with `fieldTypeName = "StatelessAsset"`

Even after enabling script import, this is enough to show that template identity likely lives behind:

- `MonoBehaviour.m_Script`
- and/or the nested `m_Structure` payload

but those layers are not yet materialized into a usable Jiangyu-side type identity.

### 4. Game binary evidence

Command:

```bash
strings ~/.steam/steam/steamapps/common/Menace/GameAssembly.dll | rg "EntityTemplate|WeaponTemplate|LocaState"
```

Observed strings include:

- `EntityTemplate`
- `WeaponTemplate`
- `LocaState`
- `Menace.Tactical|EntityTemplate`
- `Menace.Items|WeaponTemplate`
- source path strings for `EntityTemplate.cs` and `WeaponTemplate.cs`

So the legacy type names do exist in the shipped game binaries.

## Conclusion

Jiangyu still cannot do a meaningful Jiangyu-owned `EntityTemplate` / `WeaponTemplate` structural validation pass, but the blocker is now more precise:

- the type names are real
- the candidate assets are real
- AssetRipper import settings are no longer the limiting factor

The missing piece is a reliable bridge from imported `MonoBehaviour` assets to their real script type identity for MENACE.

## Next Step

Add or patch a lower-level script identity bridge.

Likely options:

1. Patch vendored AssetRipper or Jiangyu's import path to preserve enough MonoBehaviour script metadata for offline classification.
2. Expose script identity explicitly in `assets inspect object` for `MonoBehaviour` assets so discovery debugging is not blind.
3. Investigate serialised-file metadata (`ScriptTypeIndex`, type tree/script identifiers, dependency resolution for `m_Script`) as the likely offline bridge.
4. Only use name-based MonoBehaviour heuristics as a temporary fallback if the low-level bridge proves too expensive.

## Status

- `templates index` infrastructure: working
- first and second Jiangyu-native template validation attempts: blocked by missing script identity bridge
- legacy schema comparison for `EntityTemplate` / `WeaponTemplate`: not started yet
