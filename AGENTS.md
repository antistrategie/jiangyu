# Jiangyu

General-purpose modkit for MENACE (Unity 6, IL2CPP). Named after Jiangyu, a Girls Frontline character. Replaces the existing MenaceAssetPacker modkit. Built and tested against a Girls Frontline overhaul mod (WoMENACE) as the driving use case.

## Language

C# for everything — CLI compiler, MelonLoader loader, shared libraries. One language for the whole stack.

## Project documents

- `AGENTS.md` — this file. Architecture, constraints, conventions. Read by AI agents at session start.
- `docs/PRINCIPLES.md` — Jiangyu's long-term principles and decision rules. Use when making structural or workflow-boundary decisions.
- `docs/MODDING.md` — current public-facing modder contract. Keep this thin and stable.
- `PROGRESS.md` — internal chronological log of what was done, what was discovered, what decisions were made. Append-only. May be gitignored or absent in public clones.
- `TODO.md` — internal future-work backlog only. Not a record of what was completed. May be gitignored or absent in public clones.
- `docs/research/VALIDATION.md` — how Jiangyu turns reverse-engineering work into verified project knowledge.
- `docs/research/STRUCTURAL_VALIDATION_WORKFLOW.md` — repeatable structural-audit procedure for edge cases and re-audits.
- `docs/research/investigations/` — investigation notes and provenance for completed research work.
- `docs/research/verified/` — promoted Jiangyu-owned findings that product work can rely on.
- `validation/` — committed machine-readable structural baseline inputs and outputs.

If `PROGRESS.md` or `TODO.md` are absent, treat them as optional working docs rather than required repo inputs. Fall back to the committed docs, verified research, and git history.

## Tooling

- For Roslyn IDE/code-style diagnostics, prefer `dotnet format Jiangyu.slnx --verify-no-changes --severity info --no-restore --exclude vendor/AssetRipper` over build output alone.

## Research policy

- Jiangyu derives its own knowledge from the current game's serialized data, Jiangyu-native tooling, and controlled experiments.
- Do not assume old schemas, field meanings, formulas, offsets, or runtime behavior claims are correct unless Jiangyu has reproduced them.
- Prefer re-deriving knowledge inside Jiangyu over porting conclusions from older tooling.
- Production code should not depend on unverified research. Verified findings live under `docs/research/verified/`.

## Foundation correctness gate

- Be strict about correctness for foundation-critical knowledge, not for every open research question at once.
- Any assumption that becomes part of compiler output, runtime loader behavior, public mod format, canonical exported asset contract, or Jiangyu-trusted schema must be verified before production code depends on it.
- Research notes, hypotheses, and exploratory tooling may remain unverified as long as they are clearly labeled and do not become production truth.
- Prefer validating one narrow vertical slice end to end over carrying many partially trusted assumptions forward.
- Validate only the assumptions on the current critical path, but require those critical-path assumptions to be Jiangyu-verified before promotion.

## Contract markers

- When code depends on a discovered MENACE-specific convention that is acceptable for the current proven path but is not yet a general Jiangyu-wide contract, mark it with a grep-friendly comment starting with `JIANGYU-CONTRACT:`.
- Use that marker for assumptions we may need to re-validate, generalize, or retire later.
- The marker comment should say what the assumption is, what scope it is valid for, and ideally what kind of validation justified it.
- Do not use `JIANGYU-CONTRACT:` for ordinary constants or obvious implementation details. Reserve it for game-contract assumptions and scoped heuristics that matter architecturally.

## Architecture

- **Shared** (`Jiangyu.Shared`) — small framework-agnostic library for pure logic that must compile in both the real loader and normal SDK/test contexts. Keep this narrow. Use it for code that should not live behind IL2CPP/game reference constraints.
- **Core** (`Jiangyu.Core`) — shared library. All reusable logic: asset pipeline (index, search, export), compilation orchestration, model cleanup, mesh compilation, inspection services, config, models. No console I/O. Both CLI and future GUI call this.
- **CLI** (`Jiangyu.Cli`) — .NET CLI tool. Thin frontend over Core: parses commands via `System.CommandLine` 2.0.5, formats console output. Assembly name is `jiangyu`.
- **Loader** (`Jiangyu.Loader`) — MelonLoader mod. Shared framework installed once. Scans `Mods/` for bundles and patch files, loads via `Il2CppAssetBundleManager`, swaps assets at runtime. Runs in-game.

If logic must compile in both the real IL2CPP/game-reference loader context and a normal SDK/test context, put it in `Jiangyu.Shared` instead of linking the same source file into multiple projects.

Individual mods are just data — `.bundle` files, JSON patches, no code needed. Mods that need custom logic ship their own MelonLoader DLL alongside the data.

Loader-side mod discovery currently works in two phases: discover/validate manifests first, then load bundles only for unblocked mods. Bundle load order is deterministic lexical mod-folder order, and later-loaded mods win conflicts explicitly.

### Core/CLI split

- Core owns all domain logic. Services accept `IProgressSink` + `ILogSink` for output.
- CLI implements `ConsoleProgressSink` (terminal progress bars) and `ConsoleLogSink` (stderr/stdout).
- CLI references only `Jiangyu.Core` + `System.CommandLine`. No direct AssetRipper/SharpGLTF/AssetsTools.NET references.
- `AssetRipperProgressAdapter` in Core bridges `IProgressSink` to AssetRipper's `ILogger`.

## Vendored dependencies

- AssetRipper 1.3.12 lives at `vendor/AssetRipper/` as a **git subtree** (not a submodule).
- Patches are applied directly to files under `vendor/AssetRipper/` as normal commits.
- Current version: 1.3.12. Pull from versioned tags, not main.
- To pull upstream updates: `git subtree pull --prefix=vendor/AssetRipper --squash https://github.com/AssetRipper/AssetRipper.git <version-tag>`
- `Jiangyu.Core` references AssetRipper as in-process project references (`Import`, `Processing`, `Export.Modules.Models`, `Export.Modules.Textures`), not via HTTP server or NuGet.

## Key constraints

- AssetBundles are built via `Unity -batchmode -executeMethod Build` using the Unity Editor. No hand-written Unity serialisation. No AssetsTools.NET for meshes.
- Template/data patching uses runtime Harmony interception, NOT binary patching of `resources.assets`. Universal standard across IL2CPP modding communities.
- Bundle loading uses `Il2CppAssetBundleManager.LoadFromMemory()` — proton-safe (managed I/O reads bytes, avoids Wine path translation issues with `LoadFromFile`).
- The game uses Unity 6 (6000.0.63f1). Bundles must be built with the matching editor version.
- The currently proven working 3D path is character mesh/material replacement through Unity-built AssetBundles plus Jiangyu-side normalization against MENACE's native asset contract.
- Raw runtime GLB skin construction and direct native asset patching were investigated and are not the active architecture.

## Config

- `jiangyu.json` — mod manifest. Committed, ships with compiled mod.
- Global config at `GlobalConfig.ConfigPath` (XDG/AppData-adaptive) — environment settings: `game` (install path), `unityEditor` (editor binary path), `cache` (asset pipeline cache root).
- `.jiangyu/` directory in project root — build cache only (Unity staging project). Not committed.

### jiangyu.json format

Source project manifests should stay ergonomic for modders. Compiled manifests are allowed to expand entries for the loader.

```json
{
  "name": "WoMENACE",
  "version": "1.0.0",
  "author": "Antistrategie",
  "description": "Girls Frontline overhaul for MENACE",
  "depends": ["Jiangyu >= 1.0.0"]
}
```

- Replacements come from `assets/replacements/` by convention. Current supported categories are:
  - models: `assets/replacements/models/<target-name>--<pathId>/model.gltf` (or `.glb`)
  - textures: `assets/replacements/textures/<target-name>--<pathId>.<ext>`
  - sprites: `assets/replacements/sprites/<target-name>--<pathId>.<ext>`
  - audio: `assets/replacements/audio/<target-name>--<pathId>.<ext>`
  where `<target-name>--<pathId>` is the short Jiangyu replacement alias surfaced by `assets search`. Model mesh names inside the file must match the target mesh contract. Explicit manifest replacement mappings are not part of the primary project shape anymore.
- For model-replacement UX, prefer `PrefabHierarchyObject` as the modder-facing target when both `PrefabHierarchyObject` and `GameObject` exist for the same effective model. Treat `GameObject` as the lower-level/internal identity used for inspection and runtime resolution unless there is no prefab-hierarchy view.
- `assets/additions/` is for additional bundled assets preserved under their own names. It is created only when a mod needs it.
- `depends` — loader currently enforces required mod presence only. Matching is against manifest `name` for now (provisional until Jiangyu has a stable mod `id`). Version text such as `>= 1.0.0` is preserved in the manifest but not enforced yet.
- `templates/` and `localisation/` remain reserved/planned areas. Template patching and localisation patching are not current modder-facing contracts yet.
- `compiled/jiangyu.json` is compiler-owned output. Modders should not author fields inside it by hand.

### Asset commands

- `jiangyu assets index` builds the searchable catalogue Jiangyu uses for discovery.
- `jiangyu assets search` is the normal way to find replacement targets and their suggested paths.
- `jiangyu assets export model` and `jiangyu assets inspect ...` cover export and advanced inspection. Use CLI help for the current command surface.

### Model package layout

- `assets export model` produces a self-contained model package.
- Default export is the cleaned authoring representation Jiangyu expects modders to work from.
- `--raw` keeps the native representation for inspection, not authoring.
- Compiler texture discovery comes from the glTF material graph plus Jiangyu material extras when needed.

### Asset pipeline cache

Located at `GlobalConfig.DefaultCacheDir` (XDG/LocalAppData-adaptive, overridable via global config `cache` field). Holds the asset index and exported package cache.

### Asset Pipeline

- Jiangyu owns AssetRipper as an in-process library (vendored subtree with patches)
- Index-first workflow: index game data once, search/export on demand
- Two-stage export: raw AssetRipper extraction → Jiangyu cleanup rebuild (`ModelCleanupService`)
- Responsibility split:
  - **AssetRipper patches** = extraction fidelity and completeness
  - **Jiangyu** = authoring cleanup, scale normalisation, hierarchy cleanup
- Validated across: infantry characters, alien creatures, constructs, static weapons, vehicles

## Tests

`tests/Jiangyu.Core.Tests/` — xUnit, .NET 10. Run with `dotnet test tests/Jiangyu.Core.Tests/`.

`tests/Jiangyu.Loader.Tests/` — xUnit, .NET 10. Pure tests for shared loader-side logic factored out of the live IL2CPP project. Run with `dotnet test tests/Jiangyu.Loader.Tests/`.

- Pure unit tests for config, manifest serialisation, asset ref parsing, texture mapping rules
- Service-level tests with fixture data: search/resolve, sidecar texture discovery, package validation
- Scene rebuild tests with programmatic SharpGLTF fixtures: LOD pruning, skeleton preservation, scale baking
- Export shape tests: cleaned flag, material identity stripping, non-standard texture extras

No tests require game data or Unity. All fast and deterministic.
