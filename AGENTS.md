# Jiangyu

General-purpose modkit for MENACE (Unity 6, IL2CPP).

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
- `docs/research/VALIDATION.md` — how Jiangyu turns reverse-engineering work into verified project knowledge.
- `docs/research/STRUCTURAL_VALIDATION_WORKFLOW.md` — repeatable structural-audit procedure for edge cases and re-audits.
- `docs/research/investigations/` — investigation notes and provenance for completed research work.
- `docs/research/verified/` — promoted Jiangyu-owned findings that product work can rely on.
- `validation/` — committed machine-readable structural baseline inputs and outputs. Use this for diffable/re-runnable validation artefacts only; prefer Jiangyu inspect/runtime tools for fresh discovery work, and do not drop ad hoc dumps here unless they are meant to be regenerated and compared later.

## Tooling

- For Roslyn IDE/code-style diagnostics, prefer `dotnet format Jiangyu.slnx --verify-no-changes --severity info --no-restore --exclude vendor/AssetRipper` over build output alone.

## Commits

- Don't commit unless told to.
- One-line commit messages in conventional commit standards. No multi-paragraph bodies.
- No `Co-authored-by` trailers or AI attribution.

## Research and verification

- Jiangyu derives its own knowledge from the current game's serialised data, Jiangyu-native tooling, and controlled experiments. Do not port schemas, field meanings, formulas, offsets, or runtime claims from previous tooling unless Jiangyu has reproduced them.
- Verified findings live under `docs/research/verified/`. Production code depends only on those.
- Be strict about correctness for foundation-critical knowledge, not every open question at once. Any assumption that becomes part of compiler output, loader behaviour, public mod format, canonical exported asset contract, or Jiangyu-trusted schema must be verified before production code depends on it.
- Research notes, hypotheses, and exploratory tooling may remain unverified as long as they are clearly labelled and do not become production truth.
- Validate only the assumptions on the current critical path. Prefer one narrow vertical slice end-to-end over carrying many partially trusted assumptions forward.

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
- **CLI** (`Jiangyu.Cli`) — .NET CLI tool. Thin frontend over Core: parses commands via `System.CommandLine`, formats console output. Assembly name is `jiangyu`.
- **Loader** (`Jiangyu.Loader`) — MelonLoader mod. Shared framework installed once. Scans `Mods/` for bundles and patch files, loads via `Il2CppAssetBundleManager`, swaps assets at runtime. Runs in-game. The shipped loader is **always a single DLL** — `Jiangyu.Shared` and any managed dependencies that MelonLoader's net6 runtime does not already provide (currently `System.Text.Json` + `System.Text.Encodings.Web`) must be merged in before distribution. This is automated via `src/Jiangyu.Loader/ILRepack.targets`, which runs ILRepack as an AfterBuild target on Release builds only (Debug keeps the multi-DLL layout for fast iteration). Never ship loose sidecar DLLs next to `Jiangyu.Loader.dll`; modders drop one file into `Mods/`.

If logic must compile in both the real IL2CPP/game-reference loader context and a normal SDK/test context, put it in `Jiangyu.Shared` instead of linking the same source file into multiple projects.

- **Studio Host** (`Jiangyu.Studio.Host`) — .NET backend that bridges the React frontend to Core via JSON-RPC over InfiniFrame's message channel: requests `{id, method, params}`, responses `{id, result?, error?}`, host-pushed notifications `{method, params}` (no id). Handlers live in `RpcDispatcher.*.cs` partials; filesystem handlers gate on `EnsurePathInsideProject` (rejects paths outside the open project root) and `writeFile` is atomic via a sibling `.jiangyu.tmp` rename. `ProjectWatcher` pushes debounced `fileChanged` notifications; writes call `ProjectWatcher.SuppressFor(path)` so self-writes don't trigger the conflict banner. Recent projects live in the frontend's `localStorage`.
  - RPC response types that reach the frontend are annotated with `[RpcType]` (attribute lives in `Jiangyu.Shared`). The `Jiangyu.Rpc.Generators` Roslyn incremental source generator (referenced as an analyser in `Jiangyu.Studio.Host.csproj`) walks every `[RpcType]` class/struct in both the Host project and its referenced assemblies, maps C# property types to TypeScript, and writes `generated/rpc-types.ts` at build time. Types are re-exported from `src/lib/rpc.ts` (`export type * from "../../generated/rpc-types.js"`) so the entire RPC surface — `rpcCall` and all response shapes — comes from a single `@lib/rpc.ts` import.
- **Studio UI** (`Jiangyu.Studio.UI`) — React + TypeScript + Vite frontend at `src/Jiangyu.Studio.UI/`. Component-per-folder under `src/components/`. CSS Modules for scoped styles, with generated `.d.ts` via `@css-modules-kit/codegen` (output in `generated/`). Use **bun**, not npm, for installs and scripts (`bun install`, `bun run lint`, `bun test`). Run `bunx tsc --noEmit` for type-checking; regenerate CSS module types with `bunx @css-modules-kit/codegen`.

### Path aliases

Use the `@lib/*` and `@components/*` aliases rather than relative `../../lib/…` imports. Configured in both `tsconfig.json` (`paths`) and `vite.config.ts` (`resolve.alias`), and the tsconfig entry lists both the source dir and `generated/src/…` so aliased CSS-module imports resolve through cmk's generated `.d.ts` files. Same-folder sibling imports stay `./X` — the alias isn't meant to replace genuinely-local paths.

### Lint

ESLint flat config at `eslint.config.ts` (loaded via `jiti`). Extends `@eslint/js` recommended + `typescript-eslint` `strictTypeChecked` + `stylisticTypeChecked` + `eslint-plugin-jsx-a11y` recommended, with `react-hooks` and `react-refresh` plugins. Run `bun run lint`. Notable local choices:

- Type-aware rules use `projectService` so each file picks up the nearest tsconfig automatically; the config file itself is in `allowDefaultProject`.
- `no-unused-vars` is delegated to TS (`noUnusedLocals` / `noUnusedParameters`) to avoid double-reporting.
- `restrict-template-expressions` allows `number` and `boolean` (the rule's purpose is catching `${someObject}` "[object Object]" accidents); `no-empty-function` allows arrow no-ops (`() => {}` event-handler idiom).
- `no-floating-promises` is an error: every promise expression that isn't awaited must be marked with `void` or end with `.catch(...)`.
- Tests have unsafe-* relaxed since fixtures and stubs need free-form casts.
- Two `// eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions` markers exist on resize-handle separators and the image-viewer application surface; both have justifying comments. Don't add more without the same justification.

### Lib organisation

`src/lib/` is grouped by concern, not flat. Each subfolder owns one slice — read its files for the specifics; this index intentionally doesn't enumerate them.

- `lib/drag/` — HTML5 drag helpers (drag-image builder, drop-index math) and cross-window payload marshalling (tab, pane, instance, member). Cross-window payloads ride on `text/plain` because custom mimetypes don't bridge WebKitGTK's X11 DnD.
- `lib/editor/` — editor-buffer store + `useEditorContentSync()`, mounted once per window root to wire the host's `fileChanged` into the store.
- `lib/panes/` — pane workspace: layout tree + transform actions + autosave + fullscreen + reveal state, secondary-window spawn/persist/restore, browser-state shapes for URL params.
- `lib/project/` — current project + recent-projects list + lifecycle, plus the RPC wrappers and palette-command factories that go with it.
- `lib/palette/` — global action-registry store and the per-group action builders (`useRegisterActions` / `useRegisteredActions`).
- `lib/templateVisual/` — typed editor model for the KDL visual editor; mirrors the `KdlEditorDocument` RPC shape so parse/serialise stay server-side.
- `lib/toast/` — toast-queue store and mood→sticker mapping.
- `lib/compile/` — compile hook + state, and the config-gate RPC fetch.
- `lib/ui/` — generic UI utilities (shortcuts, zoom math, debounced scroll, time formatting).
- Root files are the truly cross-cutting primitives: `rpc.ts`, `layout.ts` (pure topology math), `path.ts`, `assets.ts`, `kdlSnippets.ts`, `settings.ts`.

### State management

Zustand stores own shared state; React hooks own per-component state. Use a store when state is read by 3+ components at different tree depths, needs to be reached from non-component code (RPC handlers, watchers), or has subscriptions / coordination that outlives a single mount. Use `useState` / a custom hook otherwise (modal flags, form inputs, component-scoped drag state).

Stores live in `lib/**/store.ts` and `lib/**/{name}Store.ts` — read each file for its slice. Cross-cutting expectations:

- Selectors (`useStore(s => s.slice)`) subscribe only to that slice so unrelated updates don't re-render the consumer. For imperative reads / actions from event handlers, use `useStore.getState()`.
- Project switching coordinates layout + pane-window stores atomically through `useProjectStore.switchProject(path)`. New stores that hold project-scoped state must hook into that flow.
- `useSyncPaneWindowProject(path)` must be mounted once in `App` so the secondary-window descriptor store sees project changes.
- Any non-component code (RPC handlers, background tasks) can push toasts via `useToastStore.getState().push({...})`. Likewise actions are registered via `useRegisterActions(actions)` and read via `useRegisteredActions()` — both replace earlier React-context providers.

### Jiangyu Design System

The Studio UI follows the Jiangyu Design System — an ink-wash × near-future tactical visual language inspired by East Asian calligraphy and the source material's character-sheet art. Key rules:

- **Palette**: five families — Ink (sumi neutrals), Paper (warm parchment, never pure white), Cinnabar (朱 red, ≤10% of any surface), Gold (decorative eyebrows on dark panels only), Jade (informational/verified states). Tokens in `src/Jiangyu.Studio.UI/src/styles/tokens.css`.
- **Typography**: six semantic roles in `tokens.css`, do not mix them up —
  - `--font-display-cjk` Noto Serif SC: CJK display / hero glyphs (绛雨), big stat readouts (weight 900)
  - `--font-display` Cormorant SC: chiseled western display serif — headings and **primary (filled) buttons** only
  - `--font-label` Barlow Condensed: tracked uppercase labels, section eyebrows, modal headers, ghost/default buttons. Never body copy.
  - `--font-ui` Noto Sans SC: CJK-capable body sans — body text, form inputs, data rows, banners
  - `--font-editorial` Cormorant Garamond: long-form serif passages (About blurbs, credits notes)
  - `--font-mono` JetBrains Mono: code, paths, hashes, CLI output, version stamps, small data values
  Western labels are ALL CAPS with `--tracking-wider` / `--tracking-section`. Chinese headings are never tracked. The serif on primary buttons is intentional — it signals the weight of a committing action versus the throwaway feel of a ghost button.
- **Modal dossier pattern**: long-running / state-rich actions (e.g. Compile) use a two-column modal at `min(1100px, 92vw) × min(760px, 88vh)` — **left** column is a terminal-style log on `--bg-inverse` with mono text, gold eyebrow, ink-0 scrollbar track; **right** column is a paper-toned info panel with 2×2 stat grid (Noto Serif SC 900 numbers, Barlow Condensed eyebrows), sub-stat rows, and action buttons at the bottom. `CompileModal` + `SettingsModal` are the canonical references; new modals should align to this shape. Long action completions also push a toast via `useToast()` with duration / warning count as detail and a Reveal action when a file artefact exists.
- **Corner radii**: `0` everywhere. Jiangyu is hard-edged.
- **Borders**: hairline-first. 1px default, 2px for emphasis. Double keyline (nested 1px with 4px gap) for hero frames only.
- **Shadows**: essentially none. Depth comes from hairline borders and paper-vs-ink contrast.
- **Animation**: minimal. Fades only, 80–120ms, `ease-out`. No bounces, springs, or parallax. Hover = instant colour swap. Press = 1px inset shadow (no scale).
- **Iconography**: hairline SVG icons, 24px grid, `stroke-width: 1.25`. No icon fonts, no emoji, no PNG icons.
- **Imagery tone**: warm, painted, hand-rendered. Grain preserved. Never cold, never purple, never gradients.
- **Content voice**: terse, disciplined, bilingual (Chinese leads, English supports). Dossier voice (declarative, clipped) is primary; character voice (first-person to 长官) is accent only.
- **Form controls** (checkboxes, radios): custom-styled globally in `global.css`. Ink borders on paper background, cinnabar fill/dot when active. No browser chrome.

### Stickers + toasts

Character stickers live at `public/stickers/Jiangyu_001.jpg`…`_009.jpg`. Mood pools in `lib/toast/stickers.ts` — Success: 004/007/009, Error: 001/003/006/008, Info: 002/005.

Toasts render fixed bottom-centre via `ToastContainer` with `aria-live="polite"` (errors `role="alert"`, others `role="status"`). 8s auto-dismiss, mood-matched sticker per variant, optional action buttons (e.g. "Reveal" for exported files).

### Confirm dialog

Destructive confirmations use `<ConfirmDialog>` (`components/ConfirmDialog/`), not `window.confirm`. Portal-based modal with Escape/Enter shortcuts and a `danger` variant for delete flows. Toasts are non-blocking and the wrong surface for "are you sure?" prompts.

Individual mods are just data — `.bundle` files, JSON patches, no code needed. Mods that need custom logic ship their own MelonLoader DLL alongside the data.

Loader-side mod discovery currently works in two phases: discover/validate manifests first, then load bundles only for unblocked mods. Bundle load order is deterministic lexical mod-folder order, and later-loaded mods win conflicts explicitly.

### Core/CLI split

- Core owns all domain logic. Services accept `IProgressSink` + `ILogSink` for output.
- CLI implements `ConsoleProgressSink` (terminal progress bars) and `ConsoleLogSink` (stderr/stdout).
- CLI references only `Jiangyu.Core` + `System.CommandLine`. No direct AssetRipper/SharpGLTF/AssetsTools.NET references.
- `AssetRipperProgressAdapter` in Core bridges `IProgressSink` to AssetRipper's `ILogger`.

## CI

Workflow at `.github/workflows/ci.yml`.

On push/PR to `main`: runs `Jiangyu.Core.Tests`, `Jiangyu.Loader.Tests`, and Studio UI tests.

On tag push (e.g. `v1.0.0`): builds all release artefacts and creates a GitHub Release with
auto-generated release notes:

- **Jiangyu.Loader.dll** — single merged DLL built against stripped reference assemblies from
  [`beanpuppy/menace-ci-dependencies`](https://github.com/beanpuppy/menace-ci-dependencies)
  and MelonLoader 0.7.2 downloaded from GitHub releases. The Loader cannot be functionally
  tested in CI (needs a live Unity/IL2CPP process), but the build verifies compilation and
  ILRepack merge correctness.
- **jiangyu CLI** — `dotnet publish` of `Jiangyu.Cli`.
- **Studio UI bundle** — Vite production build.

### menace-ci-dependencies

[`beanpuppy/menace-ci-dependencies`](https://github.com/beanpuppy/menace-ci-dependencies)
is a separate repo containing stripped reference assemblies for the 11 game DLLs that
`Jiangyu.Loader.csproj` references under `$(GameAssembliesDir)`. These are the real MENACE
Il2CppAssemblies with all method bodies, non-public types/members, and runtime attributes
removed via [DeepStrip](https://git.sr.ht/~malicean/DeepStrip), preserving only the public
API signatures the compiler and ILRepack need.

MelonLoader DLLs (`MelonLoader.dll`, `0Harmony.dll`, `Il2CppInterop.Runtime.dll`) are
**not** included; they are downloaded from [MelonLoader releases](https://github.com/LavaGang/MelonLoader/releases)
during CI.

To regenerate the stubs after a game update, run `./update.sh` (or `./update.ps1`) from a
machine with MENACE installed. The script clones and builds DeepStrip on demand.

## Vendored dependencies

- AssetRipper 1.3.14 lives at `vendor/AssetRipper/` as a **git subtree** (not a submodule).
- Patches are applied directly to files under `vendor/AssetRipper/` as normal commits.
- Current version: 1.3.14. Pull from versioned tags, not main.
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
  - **Audio**: playback-time substitution via `AudioReplacementPatch`. Harmony prefixes installed on `AudioSource.Play`, `PlayOneShot(AudioClip)`, `PlayOneShot(AudioClip, float)`, `PlayDelayed`, `PlayScheduled`, and static `PlayClipAtPoint` substitute the clip about to play when its `.name` matches a registered target. Used instead of sample-level mutation because `AudioClip.GetData`/`SetData` hit a `float[]` marshalling bug on the current Il2CppInterop stack. See `docs/research/verified/audio-replacement.md`.
- Replacement apply is driven by `JiangyuMod` via a scheduled scene-load coroutine (frames 5, 10, 15…100 dense, then exponential tail at 150, 250, 400, 600) for texture/mesh/sprite load-time work, plus a continuous mesh/prefab spawn monitor at 10-frame cadence after t=600 for player-deployed units. Audio is event-driven via Harmony, never polled.
- Investigation tooling: `Jiangyu.Loader.Diagnostics.RuntimeInspector` writes a JSON dump of live sprite and audio identity (both scene-scoped and `Resources.FindObjectsOfTypeAll`-scoped) to `<UserData>/jiangyu-inspect/` on each scene load, gated by a flag file in `<UserData>`: plain `jiangyu-inspect.flag` keeps all dumps forever, `jiangyu-inspect.<N>.flag` keeps at most N files per kind (runtime / templates) and rotates oldest out after each write. On `Strategy`/`MissionPreparation`/`Tactical` scenes the inspector additionally emits a full DataTemplate-subtype state dump (`*-templates-*.json`) that walks every entry in `DataTemplateLoader.m_TemplateMaps`, flags likely Jiangyu clones via `hideFlags`, and classifies each serialised member as scalar/bytes/reference/collection/odinBlob/unreadable/null. Use these to answer "what does MENACE actually have in memory right now" questions before adding code, and to diff template state across save/reload or mission boundaries. Extend the inspector — don't write one-off diagnostic scripts — as new identity surfaces become relevant.

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
  - models: `assets/replacements/models/<target-name>/model.gltf` (or `.glb`)
  - textures: `assets/replacements/textures/<target-name>.<ext>`
  - sprites: `assets/replacements/sprites/<target-name>.<ext>`
  - audio: `assets/replacements/audio/<target-name>.<ext>`
  where `<target-name>` is the asset name surfaced by `assets search`. When the name is ambiguous (multiple assets share it), append `--<pathId>` to disambiguate, e.g. `soldier--20510`. The compiler resolves bare names when unique and rejects ambiguous ones with a candidate list. Model mesh names inside the file must match the target mesh contract. Replacements are discovered by convention; there is no explicit mapping list in `jiangyu.json`.
- For model-replacement UX, prefer `PrefabHierarchyObject` as the modder-facing target when both `PrefabHierarchyObject` and `GameObject` exist for the same effective model. Treat `GameObject` as the lower-level/internal identity used for inspection and runtime resolution unless there is no prefab-hierarchy view.
- PHO→GameObject collapse is shared infrastructure: `AssetPipelineService.ResolveGameObjectBacking(index, target)` takes any index entry and, if it is a `PrefabHierarchyObject`, returns its single same-named `GameObject`. Both compile-time target resolution (`CompilationService.ResolveReplacementModelTarget`) and CLI model export (`assets export model`) route through this helper, so a PHO pathId is accepted anywhere a model target is expected. Missing/ambiguous backing `GameObject`s are hard errors, not heuristics.
- `assets/additions/` is for additional bundled assets preserved under their own names. It is created only when a mod needs it.
- `depends` — the loader enforces required-mod presence. Matching is against manifest `name`. Version text such as `>= 1.0.0` is stored but not enforced; when mods gain a stable `id` the match will move off `name`.
- Template patching is a modder-facing contract. Modders author KDL under `templates/` (parsed by `KdlTemplateParser`) — either as text in the Source editor, or visually in Studio's `TemplateVisualEditor` (host-side parse/serialise via `KdlTemplateParser.ParseText` / `KdlTemplateSerialiser.Serialise` over the same `KdlEditorDocument`, so both modes round-trip). The compiler emits `templatePatches` + `templateClones` into the compiled manifest. Each patch targets a `DataTemplate` subtype by name + `m_ID` and carries a list of operations (`set`, `append`, `insert index=N`, `remove index=N`) against dotted/indexed field paths. Value kinds: `Boolean`, `Byte`, `Int32`, `Single`, `String`, `Enum`, `TemplateReference` (resolves an existing live template by `templateId` — `templateType` is implicit on concrete-typed fields and required only when the destination is polymorphic, e.g. an abstract base; the catalog validator and loader derive the lookup type from the declared field), and `Composite` (sub-object literal for collection elements). Clones synthesise new templates from an existing base. `templatePatches` can also be authored directly in `jiangyu.json` for small changes, but KDL is the primary surface. See `docs/MODDING.md` for the modder-facing shape and `docs/research/verified/unitleader-initial-attributes.md` for the verified `UnitLeaderTemplate.InitialAttributes` offset contract (use the indexed form directly — the `.AttributeName` sugar has been removed). `localisation/` patching is not shipped.
- `compiled/jiangyu.json` is compiler-owned output. Modders should not author fields inside it by hand.

## Asset pipeline

- Jiangyu owns AssetRipper as an in-process library (vendored subtree with patches).
- Index-first workflow: index game data once, search/export on demand.
- Two-stage export: raw AssetRipper extraction → Jiangyu cleanup rebuild (`ModelCleanupService`).
- Responsibility split:
  - **AssetRipper patches** = extraction fidelity and completeness.
  - **Jiangyu** = authoring cleanup, scale normalisation, hierarchy cleanup.
- Validated across: infantry characters, alien creatures, constructs, static weapons, vehicles.

### Commands

- `jiangyu assets index` builds the searchable catalogue Jiangyu uses for discovery, plus an IL2CPP metadata supplement (`<cache>/il2cpp-metadata.json`) extracted via the vendored Cpp2IL pipeline. The supplement carries attribute data that Il2CppInterop wrappers strip on generation — currently `[NamedArray(typeof(T))]` pairings and per-field `[Range]`/`[Min]`/`[Tooltip]`/`[HideInInspector]`/`Stem.SoundIDAttribute` hints. `TemplateTypeCatalog` overlays it onto each `MemberShape` at construction (see `Jiangyu.Core.Il2Cpp.Il2CppMetadataExtractor`/`Il2CppMetadataCache`); both compile (`CompilationService`) and editor (`templatesQuery` / `templatesParse` RPCs) consume the result.
- `jiangyu assets search` is the normal way to find replacement targets and their suggested paths.
- `jiangyu assets export model` and `jiangyu assets inspect ...` cover export and advanced inspection. Use CLI help for the current command surface.
- `assets export model` takes `--path-id` (and optional `--collection`) to disambiguate duplicate names. Accepts `GameObject`, `Mesh`, or `PrefabHierarchyObject` pathIds; PHOs are collapsed to their backing `GameObject` before extraction. Ambiguity is surfaced to the user with a candidate list, never silently resolved.

### Model package layout

- `assets export model` produces a self-contained model package.
- Default export is the cleaned authoring representation Jiangyu expects modders to work from.
- `--raw` keeps the native representation for inspection, not authoring.
- Compiler texture discovery comes from the glTF material graph plus Jiangyu material extras when needed.

### Cache

Located at `GlobalConfig.DefaultCacheDir` (XDG/LocalAppData-adaptive, overridable via global config `cache` field). Holds the asset index and exported package cache.

## Tests

Run locally:

- `tests/Jiangyu.Core.Tests/` — xUnit, .NET 10. Run with `dotnet test tests/Jiangyu.Core.Tests/`.
- `tests/Jiangyu.Loader.Tests/` — xUnit, .NET 10. Pure tests for shared loader-side logic factored out of the live IL2CPP project. Run with `dotnet test tests/Jiangyu.Loader.Tests/`.
- `src/Jiangyu.Studio.UI/` — vitest, Node environment. Run with `bun test` from that directory. Covers layout topology, path utilities, palette filtering, keyboard-shortcut matching, drop-zone geometry, zoom math, recent-projects storage, asset-kind guards, etc. No browser or host needed — the few places that touch `localStorage` stub it via `vi.stubGlobal`.

Coverage across the .NET suites:

- Pure unit tests for config, manifest serialisation, asset ref parsing, texture mapping rules.
- Service-level tests with fixture data: search/resolve, sidecar texture discovery, package validation.
- Scene rebuild tests with programmatic SharpGLTF fixtures: LOD pruning, skeleton preservation, scale baking.
- Export shape tests: cleaned flag, material identity stripping, non-standard texture extras.

No tests require game data or Unity. All fast and deterministic.
