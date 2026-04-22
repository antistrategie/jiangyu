# Jiangyu

General-purpose modkit for MENACE (Unity 6, IL2CPP). Named after Jiangyu, a Girls Frontline character. Replaces the existing MenaceAssetPacker modkit. Built and tested against a Girls Frontline overhaul mod (WoMENACE) as the driving use case.

## Language

C# for the core stack — CLI compiler, MelonLoader loader, shared libraries. TypeScript + React for the Studio UI frontend. One language per boundary.

## English

- Use British English in repo-authored prose, code, comments, logs, and user-facing text.
- If you touch existing repo-authored wording that uses non-British spelling, normalise it while you are there.
- Do not rename or rewrite library/framework/API identifiers just to force British spelling.

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
- `validation/` — committed machine-readable structural baseline inputs and outputs. Use this for diffable/re-runnable validation artefacts only; prefer Jiangyu inspect/runtime tools for fresh discovery work, and do not drop ad hoc dumps here unless they are meant to be regenerated and compared later.

If `PROGRESS.md` or `TODO.md` are absent, treat them as optional working docs rather than required repo inputs. Fall back to the committed docs, verified research, and git history.

## Tooling

- For Roslyn IDE/code-style diagnostics, prefer `dotnet format Jiangyu.slnx --verify-no-changes --severity info --no-restore --exclude vendor/AssetRipper` over build output alone.

## Commits

- Don't commit unless told to.
- One-line commit messages in conventional commit standards. No multi-paragraph bodies.
- No `Co-authored-by` trailers or AI attribution.

## Research policy

- Jiangyu derives its own knowledge from the current game's serialised data, Jiangyu-native tooling, and controlled experiments.
- Do not assume old schemas, field meanings, formulas, offsets, or runtime behaviour claims are correct unless Jiangyu has reproduced them.
- Prefer re-deriving knowledge inside Jiangyu over porting conclusions from older tooling.
- Production code should not depend on unverified research. Verified findings live under `docs/research/verified/`.

## Foundation correctness gate

- Be strict about correctness for foundation-critical knowledge, not for every open research question at once.
- Any assumption that becomes part of compiler output, runtime loader behaviour, public mod format, canonical exported asset contract, or Jiangyu-trusted schema must be verified before production code depends on it.
- Research notes, hypotheses, and exploratory tooling may remain unverified as long as they are clearly labeled and do not become production truth.
- Prefer validating one narrow vertical slice end to end over carrying many partially trusted assumptions forward.
- Validate only the assumptions on the current critical path, but require those critical-path assumptions to be Jiangyu-verified before promotion.

## Contract markers

- When code depends on a discovered MENACE-specific convention that is acceptable for the current proven path but is not yet a general Jiangyu-wide contract, mark it with a grep-friendly comment starting with `JIANGYU-CONTRACT:`.
- Use that marker for assumptions we may need to re-validate, generalise, or retire later.
- The marker comment should say what the assumption is, what scope it is valid for, and ideally what kind of validation justified it.
- Do not use `JIANGYU-CONTRACT:` for ordinary constants or obvious implementation details. Reserve it for game-contract assumptions and scoped heuristics that matter architecturally.

## Comment conventions

- A comment is only valid if it describes the code directly next to it. Comments that point at other files, explain where responsibility has moved, or navigate the reader to a different part of the codebase do not belong in source.
- Never write "X has been moved to Y" / "X is handled by Y" / "replaced by Y" tombstones. Architecture and cross-cutting responsibilities belong in this file (see the Architecture section), not sprinkled as prose in source.
- Contract spec pointers (e.g. `docs/research/verified/…`) on a class's XML doc summary are fine when the class directly implements that contract. That's different from in-function prose explaining where unrelated code lives.

## Architecture

- **Shared** (`Jiangyu.Shared`) — small framework-agnostic library for pure logic that must compile in both the real loader and normal SDK/test contexts. Keep this narrow. Use it for code that should not live behind IL2CPP/game reference constraints.
- **Core** (`Jiangyu.Core`) — shared library. All reusable logic: asset pipeline (index, search, export), compilation orchestration, model cleanup, mesh compilation, inspection services, config, models. No console I/O. Both CLI and future GUI call this.
- **CLI** (`Jiangyu.Cli`) — .NET CLI tool. Thin frontend over Core: parses commands via `System.CommandLine` 2.0.5, formats console output. Assembly name is `jiangyu`.
- **Loader** (`Jiangyu.Loader`) — MelonLoader mod. Shared framework installed once. Scans `Mods/` for bundles and patch files, loads via `Il2CppAssetBundleManager`, swaps assets at runtime. Runs in-game. The shipped loader is **always a single DLL** — `Jiangyu.Shared` and any managed dependencies that MelonLoader's net6 runtime does not already provide (currently `System.Text.Json` + `System.Text.Encodings.Web`) must be merged in before distribution. This is automated via `src/Jiangyu.Loader/ILRepack.targets`, which runs ILRepack as an AfterBuild target on Release builds only (Debug keeps the multi-DLL layout for fast iteration). Never ship loose sidecar DLLs next to `Jiangyu.Loader.dll`; modders drop one file into `Mods/`.

If logic must compile in both the real IL2CPP/game-reference loader context and a normal SDK/test context, put it in `Jiangyu.Shared` instead of linking the same source file into multiple projects.

- **Studio Host** (`Jiangyu.Studio.Host`) — .NET backend for Jiangyu Studio. Hosts the Tauri-style RPC bridge that the UI frontend calls into for asset indexing, search, export, file operations, and project configuration.
- **Studio UI** (`Jiangyu.Studio.UI`) — React + TypeScript + Vite frontend. Lives at `src/Jiangyu.Studio.UI/`. Component-per-folder structure under `src/components/` (AssetBrowser, Sidebar, EditorArea, Palette, Toast, Topbar, WelcomeScreen, etc.). CSS Modules for scoped styles, with generated `.d.ts` via `@css-modules-kit/codegen` (output in `generated/`). Dev server with hot reload. Run `npx tsc --noEmit` for type-checking; regenerate CSS module types with `npx @css-modules-kit/codegen`.

### Jiangyu Design System

The Studio UI follows the Jiangyu Design System — an ink-wash × near-future tactical visual language inspired by East Asian calligraphy and the source material's character-sheet art. Key rules:

- **Palette**: five families — Ink (sumi neutrals), Paper (warm parchment, never pure white), Cinnabar (朱 red, ≤10% of any surface), Gold (decorative eyebrows on dark panels only), Jade (informational/verified states). Tokens in `src/Jiangyu.Studio.UI/src/styles/tokens.css`.
- **Typography**: Noto Serif SC (display CJK), Barlow Condensed (display EN / labels), Cormorant Garamond (editorial serif), JetBrains Mono (data / CLI). Western labels are ALL CAPS with wide tracking. Chinese headings are never tracked.
- **Corner radii**: `0` everywhere. Jiangyu is hard-edged.
- **Borders**: hairline-first. 1px default, 2px for emphasis. Double keyline (nested 1px with 4px gap) for hero frames only.
- **Shadows**: essentially none. Depth comes from hairline borders and paper-vs-ink contrast.
- **Animation**: minimal. Fades only, 80–120ms, `ease-out`. No bounces, springs, or parallax. Hover = instant colour swap. Press = 1px inset shadow (no scale).
- **Iconography**: hairline SVG icons, 24px grid, `stroke-width: 1.25`. No icon fonts, no emoji, no PNG icons.
- **Imagery tone**: warm, painted, hand-rendered. Grain preserved. Never cold, never purple, never gradients.
- **Content voice**: terse, disciplined, bilingual (Chinese leads, English supports). Dossier voice (declarative, clipped) is primary; character voice (first-person to 长官) is accent only.
- **Form controls** (checkboxes, radios): custom-styled globally in `global.css`. Ink borders on paper background, cinnabar fill/dot when active. No browser chrome.

### Stickers

Character stickers live at `src/Jiangyu.Studio.UI/public/stickers/Jiangyu_001.jpg` through `_009.jpg`. Used by the toast system and available for other UI surfaces. Mood mapping in `src/Jiangyu.Studio.UI/src/lib/stickers.ts`:

- **Success**: 004 (triumphant flex), 007 (happy/hearts), 009 (double pointing/agreement)
- **Error**: 001 (punching), 003 (winding up attack), 006 (sad/rain), 008 (asking for a fight)
- **Info**: 002 (waving goodbye), 005 ("come at me" gesture)

### Toast system

App-wide toast notifications via `useToast()` context (`src/Jiangyu.Studio.UI/src/lib/toast.tsx`). `ToastProvider` wraps the app in `main.tsx`; `ToastContainer` renders fixed bottom-centre. Each toast auto-picks a random sticker from the mood pool matching its variant (`success`/`error`/`info`). Auto-dismisses after 8 seconds. Supports action buttons (e.g. "Reveal" to open exported file in explorer).

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
- Template/data patching operates on the live IL2CPP wrapper objects at runtime, NOT binary patching of `resources.assets`. The applier reflects on `DataTemplateLoader.GetAll<T>()`, matches instances by their serialised `m_ID`, and writes scalar values / `TemplateReference` resolutions into the matched template via the wrapper's own writable members and indexers. No Harmony hooks are needed on this path; Harmony is reserved for playback-time substitution (e.g. audio) where the game's own method is the natural interception point.
- Bundle loading uses `Il2CppAssetBundleManager.LoadFromMemory()` — proton-safe (managed I/O reads bytes, avoids Wine path translation issues with `LoadFromFile`).
- The game uses Unity 6 (6000.0.63f1). Bundles must be built with the matching editor version.
- Each replacement category uses a strategy matched to the asset type's runtime representation. Verified contracts live in `docs/research/verified/`:
  - **Texture**: in-place `Texture2D` mutation. `TextureMutationService` sweeps `Resources.FindObjectsOfTypeAll(Texture2D)`, matches by name, renders the modder's source into a staging buffer at the destination's dimensions, compresses to the destination's format via `Texture2D.Compress()`, and `Graphics.CopyTexture`s into the live game texture. Every consumer that holds a reference to that texture (materials, UGUI, UI Toolkit, template refs, caches) inherits the change because Unity texture references are identity-based. Compile-time rejects ambiguous texture names. See `docs/research/verified/texture-replacement.md`.
  - **Sprite**: rides on texture mutation. `Sprite` references a backing `Texture2D`; mutating the backing texture updates every consumer of the sprite. `TextureMutationService` has a second sweep over `Resources.FindObjectsOfTypeAll(Sprite)` that resolves sprites to their backing texture when the texture isn't directly reachable via the Texture2D sweep. Compile-time atlas detection (`ValidateSpriteBackingTextureIsUnique`) rejects sprite targets whose backing texture is shared with other indexed sprites. See `docs/research/verified/sprite-replacement.md`.
  - **Mesh / prefab**: `DirectMeshReplacementApplier` / `DrivenPrefabReplacementManager`. `SkinnedMeshRenderer.sharedMesh` is replaced on matching live SMRs; bones, rootBone, localBounds, and material bindings are rewired via `MaterialReplacementService.GetOrCreateReplacementMaterials`. Idempotent per-SMR via `_processedSmrInstanceIds`. Continuous spawn monitor runs every 10 frames after the initial scene-load window to catch player-deployed units.
  - **Audio**: playback-time substitution via `AudioReplacementPatch`. Harmony prefixes installed on `AudioSource.Play`, `PlayOneShot(AudioClip)`, `PlayOneShot(AudioClip, float)`, `PlayDelayed`, `PlayScheduled`, and static `PlayClipAtPoint` substitute the clip about to play when its `.name` matches a registered target. Used instead of sample-level mutation because `AudioClip.GetData`/`SetData` hit an Il2CppInterop `float[]` marshalling bug on this MelonLoader 0.7.2 + Il2CppInterop 1.5.1 stack (probed and confirmed against the alpha-development 0.7.3-ci build — bug persists there too). See `docs/research/verified/audio-replacement.md`.
- Replacement apply is driven by `JiangyuMod` via a scheduled scene-load coroutine (frames 5, 10, 15…100 dense, then exponential tail at 150, 250, 400, 600) for texture/mesh/sprite load-time work, plus a continuous mesh/prefab spawn monitor at 10-frame cadence after t=600 for player-deployed units. Audio is event-driven via Harmony, never polled.
- Investigation tooling: `Jiangyu.Loader.Diagnostics.RuntimeInspector` writes a JSON dump of live sprite and audio identity (both scene-scoped and `Resources.FindObjectsOfTypeAll`-scoped) to `<UserData>/jiangyu-inspect/` on each scene load, gated by a flag file in `<UserData>`: plain `jiangyu-inspect.flag` keeps all dumps forever, `jiangyu-inspect.<N>.flag` keeps at most N files per kind (runtime / templates) and rotates oldest out after each write. On `Strategy`/`MissionPreparation`/`Tactical` scenes the inspector additionally emits a full DataTemplate-subtype state dump (`*-templates-*.json`) that walks every entry in `DataTemplateLoader.m_TemplateMaps`, flags likely Jiangyu clones via `hideFlags`, and classifies each serialised member as scalar/bytes/reference/collection/odinBlob/unreadable/null. Use these to answer "what does MENACE actually have in memory right now" questions before adding code, and to diff template state across save/reload or mission boundaries. Extend the inspector — don't write one-off diagnostic scripts — as new identity surfaces become relevant.
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
- PHO→GameObject collapse is shared infrastructure: `AssetPipelineService.ResolveGameObjectBacking(index, target)` takes any index entry and, if it is a `PrefabHierarchyObject`, returns its single same-named `GameObject`. Both compile-time target resolution (`CompilationService.ResolveReplacementModelTarget`) and CLI model export (`assets export model`) route through this helper, so a PHO pathId is accepted anywhere a model target is expected. Missing/ambiguous backing `GameObject`s are hard errors, not heuristics.
- `assets/additions/` is for additional bundled assets preserved under their own names. It is created only when a mod needs it.
- `depends` — loader currently enforces required mod presence only. Matching is against manifest `name` for now (provisional until Jiangyu has a stable mod `id`). Version text such as `>= 1.0.0` is preserved in the manifest but not enforced yet.
- Template patching is a current modder-facing contract via a top-level `templatePatches` field in `jiangyu.json`. Each entry targets a `DataTemplate` subtype by name + `m_ID` and carries a list of `set` operations against dotted/indexed field paths. Value kinds: `Boolean`, `Byte`, `Int32`, `Single`, `String`, `Enum`, and `TemplateReference` (resolves an existing live template by `(templateType, templateId)`). `UnitLeaderTemplate.InitialAttributes.<AttributeName>` is sugar for the byte-offset form. See `docs/MODDING.md` for the modder-facing shape and `docs/research/verified/unitleader-initial-attributes.md` for the verified offset contract. `templates/` (KDL authoring surface) and `localisation/` patching remain planned, not shipped.
- `compiled/jiangyu.json` is compiler-owned output. Modders should not author fields inside it by hand.

### Asset commands

- `jiangyu assets index` builds the searchable catalogue Jiangyu uses for discovery.
- `jiangyu assets search` is the normal way to find replacement targets and their suggested paths.
- `jiangyu assets export model` and `jiangyu assets inspect ...` cover export and advanced inspection. Use CLI help for the current command surface.
- `assets export model` takes `--path-id` (and optional `--collection`) to disambiguate duplicate names. Accepts `GameObject`, `Mesh`, or `PrefabHierarchyObject` pathIds; PHOs are collapsed to their backing `GameObject` before extraction. Ambiguity is surfaced to the user with a candidate list, never silently resolved.

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
