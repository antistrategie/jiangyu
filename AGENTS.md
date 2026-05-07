# Jiangyu

General-purpose modkit for MENACE (Unity 6, IL2CPP).

## Language

C# for the core stack: CLI compiler, MelonLoader loader, shared libraries. TypeScript and React for the Studio UI frontend. One language per boundary.

## English

- Use British English in repo-authored prose, code, comments, logs, and user-facing text.
- If you touch existing repo-authored wording that uses non-British spelling, normalise it while you are there.
- Do not rename or rewrite library/framework/API identifiers just to force British spelling.
- Do not use em dashes in repo prose. Reach for a period, semicolon, comma, or colon instead.

## Project documents

- `AGENTS.md`: this file. Architecture, constraints, conventions. Read by AI agents at session start.
- `src/Jiangyu.Studio.UI/AGENTS.md`: Studio UI internals (lib organisation, lint, state management). Read when working in that subtree.
- `docs/PRINCIPLES.md`: Jiangyu's long-term principles and decision rules.
- `docs/DESIGN_SYSTEM.md`: Jiangyu Design System; palette, typography, modal patterns, design rules.
- `site/`: public-facing modder documentation (VitePress, deployed to GitHub Pages via `.github/workflows/pages.yml`). Run `bun run docs:dev` from `site/` to author. This is the canonical modder-facing surface.
- `docs/research/VALIDATION.md`: how Jiangyu turns reverse-engineering work into verified knowledge.
- `docs/research/STRUCTURAL_VALIDATION_WORKFLOW.md`: repeatable structural-audit procedure.
- `docs/research/investigations/`: investigation notes and provenance.
- `docs/research/verified/`: promoted Jiangyu-owned findings that production code can rely on.
- `validation/`: committed machine-readable structural baseline inputs and outputs (diffable, re-runnable). Don't drop ad hoc dumps here.

## Tooling

- For Roslyn IDE/code-style diagnostics, prefer `dotnet format Jiangyu.slnx --verify-no-changes --severity info --no-restore --exclude vendor/*` over build output alone.

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
- The marker should say what the assumption is, what scope it is valid for, and ideally what kind of validation justified it.
- Do not use `JIANGYU-CONTRACT:` for ordinary constants or obvious implementation details. Reserve it for game-contract assumptions and scoped heuristics that matter architecturally.

## Comment conventions

- A comment is only valid if it describes the code directly next to it. Comments that point at other files, explain where responsibility has moved, or navigate the reader to a different part of the codebase do not belong in source.
- Never write "X has been moved to Y" / "X is handled by Y" / "replaced by Y" tombstones. Architecture and cross-cutting responsibilities belong in this file (see the Architecture section), not sprinkled as prose in source.
- Contract spec pointers (e.g. `docs/research/verified/…`) on a class's XML doc summary are fine when the class directly implements that contract. That's different from in-function prose explaining where unrelated code lives.

## Architecture

- **Shared** (`Jiangyu.Shared`): small framework-agnostic library for pure logic that must compile in both the real loader and normal SDK/test contexts. Keep narrow. Use it for code that should not live behind IL2CPP/game reference constraints.
- **Core** (`Jiangyu.Core`): shared library. All reusable logic; asset pipeline (index, search, export), compilation orchestration, model cleanup, mesh compilation, inspection services, config, models. No console I/O. Both CLI and future GUI call this.
- **CLI** (`Jiangyu.Cli`): .NET CLI tool. Thin frontend over Core; parses commands via `System.CommandLine`, formats console output. Assembly name is `jiangyu`.
- **Loader** (`Jiangyu.Loader`): MelonLoader mod. Shared framework installed once. Scans `Mods/` for bundles and patch files, loads via `Il2CppAssetBundleManager`, swaps assets at runtime. The shipped loader is **always a single DLL**. `Jiangyu.Shared` and any managed dependencies that MelonLoader's net6 runtime does not already provide (currently `System.Text.Json` and `System.Text.Encodings.Web`) must be merged in before distribution. Automated via `src/Jiangyu.Loader/ILRepack.targets`, which runs ILRepack as an AfterBuild target on Release builds only (Debug keeps the multi-DLL layout for fast iteration). Never ship loose sidecar DLLs next to `Jiangyu.Loader.dll`.
- **Studio Host** (`Jiangyu.Studio.Host`): .NET backend bridging the React frontend to Core via JSON-RPC over InfiniFrame's message channel. Requests `{id, method, params}`, responses `{id, result?, error?}`, host-pushed notifications `{method, params}` (no id). Window-bound RPCs (file dialogs, pane management, agent lifecycle, project lifecycle, settings, streaming compile) live in `RpcDispatcher.*.cs` partials. Pure handlers (every `[McpTool]`-tagged one, plus `readFile`/`writeFile`) live in `Jiangyu.Studio.Rpc` and the dispatcher registers thin lambdas that delegate to them. `writeFile` is atomic via a sibling `.jiangyu.tmp` rename. `ProjectWatcher` pushes debounced `fileChanged` notifications; the dispatcher's `editFile` / `writeFile` lambdas wrap the moved handler with `ProjectWatcher.SuppressFor(path, window.Id)` so a same-window save doesn't fire its own conflict banner. Recent projects live in the frontend's `localStorage`.
- **Studio Rpc** (`Jiangyu.Studio.Rpc`): shared library at `src/Jiangyu.Studio.Rpc/` housing RPC handlers, their helpers, and the MCP server. Both Studio.Host and the standalone Mcp binary call into the same `RpcHandlers` static class so a tool exposed via MCP and the same RPC over the WebView always run identical code. Project-root sandboxing reads `RpcContext.ProjectRoot`; Host sets it on project-open, Mcp seeds it from `Environment.CurrentDirectory` at startup. KDL syntax docs are embedded here so both transports can serve them as MCP resources.
- **Studio UI** (`Jiangyu.Studio.UI`): React, TypeScript, and Vite frontend at `src/Jiangyu.Studio.UI/`. See its `AGENTS.md` for internals. Use **bun**, not npm.
- **ACP** (`Jiangyu.Acp`): standalone Agent Client Protocol library at `src/Jiangyu.Acp/`. Hand-rolled JSON-RPC 2.0 over stdio (Content-Length and NDJSON framing). Zero Jiangyu-specific dependencies; could be extracted as a NuGet package. Schema types cover initialise, session, prompt, session updates (10 polymorphic subtypes), permissions, filesystem, and terminal callbacks. `AcpClient` is the public API; `IAcpClientHandler` receives agent-to-client callbacks. Tests at `tests/Jiangyu.Acp.Tests/`.
- **Mcp** (`Jiangyu.Mcp`): standalone stdio MCP server. Single-file published as a sibling executable to `jiangyu-studio`; AgentProcessManager spawns it for ACP agents that only honour stdio MCP. Reads newline-framed JSON-RPC from stdin, dispatches through `Jiangyu.Studio.Rpc`'s `McpServer`, writes to stdout. Stderr is reserved for logging.

If logic must compile in both the real IL2CPP/game-reference loader context and a normal SDK/test context, put it in `Jiangyu.Shared` instead of linking the same source file into multiple projects.

### RPC type generation

Response types that reach the frontend are annotated with `[RpcType]` (attribute lives in `Jiangyu.Shared`). The `Jiangyu.Studio.Rpc.Generators` Roslyn incremental source generator (project-referenced from `Jiangyu.Studio.Host.csproj`) walks every `[RpcType]` class/struct in the Host and its referenced assemblies, maps C# property types to TypeScript, and emits `src/lib/rpc/types.ts` at build time. Most DTOs now live in `Jiangyu.Studio.Rpc` next to the handlers that produce them; the generator picks them up via `CollectFromReferences`. `src/lib/rpc/index.ts` re-exports them so the whole RPC surface (`rpcCall` and all response shapes) comes from one `@lib/rpc` import.

### MCP transport split

ACP agents reach Jiangyu's tools via Model Context Protocol. We send **both** transports in `session/new` because no agent honours both:

- **stdio** (`jiangyu-mcp`, sibling exe): Anthropic's `claude-agent-sdk` silently DROPS HTTP MCP entries (despite advertising `mcpCapabilities.http=true`) and only honours stdio.
- **http** (`POST /mcp` mounted on Studio.Host): GitHub Copilot's ACP integration silently REJECTS stdio entries (logs `Rejecting non-http/sse MCP server`) and only honours http/sse.

Both entries share the name `jiangyu`; each agent drops the unsupported one and the model only ever sees one server. The HTTP endpoint is gated by a per-launch random bearer token handed to the agent in the `Authorization` header field of `McpServerConfig`; the token never goes through the WebView origin or localStorage. `AgentProcessManager.BuildMcpServerConfig` builds the array; both Studio.Host's `/mcp` endpoint and the standalone Mcp binary back the same `Jiangyu.Studio.Rpc.RpcHandlers` static class so behaviour is identical across transports.

### Mods

Individual mods compose from `.bundle` files, JSON/KDL patches and clones, and (where needed) companion MelonLoader DLLs alongside the data. The intended-modder tiers in `docs/PRINCIPLES.md` describe the progression: asset replacement, template patching/cloning, Unity-authored content, and runtime code via the planned SDK. Simple-things-stay-simple keeps content-only mods code-free; advanced cases reach for code paths.

Loader-side mod discovery works in two phases. Discover and validate manifests first, then load bundles only for unblocked mods. Bundle load order is deterministic lexical mod-folder order; later-loaded mods win conflicts explicitly.

### Core/CLI split

- Core owns all domain logic. Services accept `IProgressSink` and `ILogSink` for output.
- CLI implements `ConsoleProgressSink` (terminal progress bars) and `ConsoleLogSink` (stderr/stdout).
- CLI references only `Jiangyu.Core` and `System.CommandLine`. No direct AssetRipper/SharpGLTF/AssetsTools.NET references.
- `AssetRipperProgressAdapter` in Core bridges `IProgressSink` to AssetRipper's `ILogger`.

## CI

Workflow at `.github/workflows/ci.yml`. On push/PR to `main`, it runs `Jiangyu.Core.Tests`, `Jiangyu.Acp.Tests`, `Jiangyu.Studio.Host.Tests`, `Jiangyu.Loader.Tests`, and Studio UI tests. On tag push (e.g. `v1.0.0`) it builds release artefacts (single merged `Jiangyu.Loader.dll`, `jiangyu` CLI publish, Studio Host + sibling `jiangyu-mcp` binary, Studio UI Vite bundle) and creates a GitHub Release.

The docs site is deployed by `.github/workflows/pages.yml` on changes under `site/`.

Loader CI builds against stripped reference assemblies from [`beanpuppy/menace-ci-dependencies`](https://github.com/beanpuppy/menace-ci-dependencies); MelonLoader DLLs are downloaded from upstream releases. The IL2CPP-bound code paths (Cast<T>, ScriptableObject.CreateInstance, native pointer reads) cannot run in CI without a live Unity/IL2CPP process; those stay live-game smokes. Pure-reflection helpers (IsOwnedElementType, HasStrictDescendant, GetIl2CppListElementType, ResolveIl2CppSubtype, etc.) **are** testable in CI: `Jiangyu.Loader.Tests` references `Jiangyu.Loader` directly and walks Type metadata against the real stripped Assembly-CSharp.dll, catching structural-rule regressions (e.g. a refactor that breaks the abstract-polymorphic detection).

## Vendored dependencies

- AssetRipper 1.3.14 lives at `vendor/AssetRipper/` as a **git subtree** (not a submodule). Patches are normal commits on files under `vendor/AssetRipper/`.
- Pull from versioned tags: `git subtree pull --prefix=vendor/AssetRipper --squash https://github.com/AssetRipper/AssetRipper.git <version-tag>`.
- `Jiangyu.Core` references AssetRipper as in-process project references (`Import`, `Processing`, `Export.Modules.Models`, `Export.Modules.Textures`).
- TinySerializer (Apache-2.0) lives at `vendor/TinySerializer/` as a git subtree, retargeted to net10.0. It is the offline binary reader for Sirenix Odin `serializationData` blobs that Unity's native serialiser cannot decode. `Jiangyu.Core.Templates.Odin.OdinPayloadDecoder` drives `BinaryDataReader` through a custom binder that returns sentinel `Type` instances per name so the decoder recovers raw type names without resolving real CLR types. Treated as a dormant fork; pull updates with `git subtree pull --prefix=vendor/TinySerializer --squash https://github.com/gestiran/TinySerializer.git main` if upstream resumes.

## Key constraints

- AssetBundles are built via `Unity -batchmode -executeMethod Build` using the Unity Editor. No hand-written Unity serialisation. No AssetsTools.NET for meshes.
- Template/data patching operates on the live IL2CPP wrapper objects at runtime, NOT binary patching of `resources.assets`. The applier reflects on `DataTemplateLoader.GetAll<T>()`, matches instances by their serialised `m_ID`, and writes scalar values / `TemplateReference` resolutions into the matched template via the wrapper's own writable members and indexers. No Harmony hooks are needed on this path; Harmony is reserved for playback-time substitution (e.g. audio) where the game's own method is the natural interception point.
- Odin-routed fields (interface- or abstract-typed scalars whose values live in `serializationData` rather than the Unity field tree, e.g. `Attack.DamageFilterCondition: ITacticalCondition`) are read by `OdinPayloadDecoder` (offline) and written by the runtime applier the same way Unity-native fields are. Two authoring shapes:
  - **Construct-fresh** (`set "Field" handler="Subtype" { ... }`): `ValidateHandlerConstruction` accepts polymorphic-scalar destinations alongside collection elements; `TryConstructComposite` instantiates the named subtype via reflection (ScriptableObject / Il2CppObjectBase / pure managed) and the new wrapper-cast in `ApplyAndVerify` lets `PropertyInfo.SetValue` accept assignments across IL2CPP-stripped interface boundaries.
  - **Drill-into-existing** (`set "Field" type="Subtype" { ... }` with no `index=`): `TemplateDescentStep.Index` is nullable; null marks scalar polymorphic descent. The navigator, validator, and applier all branch on the null index. Wire-format change vs. earlier shape — old patches with `Index=0` continue to deserialise as collection descent.
  - The runtime fallback for IL2CPP's stripped interface impls is `IsAssignableFromIl2Cpp`: managed `IsAssignableFrom` first, then `IL2CPP.il2cpp_class_is_assignable_from(targetPtr, candidatePtr)` via `Il2CppClassPointerStore.GetNativeClassPointer(Type)`. Mirrors the catalogue's offline supplement-driven fallback.
- Bundle loading uses `Il2CppAssetBundleManager.LoadFromMemory()` for proton safety (managed I/O reads bytes, avoids Wine path translation issues with `LoadFromFile`).
- The game uses Unity 6 (6000.0.72f1). Bundles must be built with the matching editor version.
- Each replacement category uses a strategy matched to the asset type's runtime representation. Verified contracts in `docs/research/verified/`:
  - **Texture** (`texture-replacement.md`): in-place `Texture2D` mutation. Multiple `Texture2D` entries sharing a target name are warned about and all painted; the loader matches by `texture.name`, so this is intentional.
  - **Sprite** (`sprite-replacement.md`): unique-backed sprites ride the texture-mutation path. Atlas-backed sprites are composited into a copy of the atlas at compile time (`AtlasCompositor`), and the resulting `Texture2D` rides the same mutation path. Modders may also drop a full atlas replacement under `textures/`; sprite regions are composited on top of it.
  - **Mesh / prefab**: `DirectMeshReplacementApplier` and `DrivenPrefabReplacementManager`. SMR `sharedMesh` swap with bone/material rewire via `MaterialReplacementService`. Continuous spawn monitor at 10-frame cadence catches player-deployed units.
  - **Audio** (`audio-replacement.md`): playback-time substitution via Harmony prefixes on the `AudioSource.Play` family. Used instead of sample-level mutation because `AudioClip.GetData`/`SetData` hit a `float[]` marshalling bug on the current Il2CppInterop stack.
- Replacement apply is driven by `JiangyuMod` via a scheduled scene-load coroutine (frames 5, 10, 15…100 dense, then exponential tail at 150, 250, 400, 600) for texture/mesh/sprite load-time work, plus a continuous mesh/prefab spawn monitor at 10-frame cadence after t=600. Audio is event-driven via Harmony, never polled.
- Investigation tooling: `Jiangyu.Loader.Diagnostics.RuntimeInspector` writes JSON dumps of live sprite/audio identity and DataTemplate state to `<UserData>/jiangyu-inspect/` on each scene load, gated by a flag file (`jiangyu-inspect.flag` keeps all; `jiangyu-inspect.<N>.flag` rotates oldest after N). Extend the inspector instead of writing one-off diagnostic scripts as new identity surfaces become relevant.

## Config

- `jiangyu.json`: mod manifest. Committed, ships with compiled mod. See the [docs site](https://antistrategie.github.io/jiangyu/) for the modder-facing shape.
- Global config at `GlobalConfig.ConfigPath` (XDG/AppData-adaptive). Environment settings: `game` (install path), `unityEditor` (editor binary path), `cache` (asset pipeline cache root).
- `.jiangyu/` directory in project root: build cache only (Unity staging project). Not committed.
- `compiled/jiangyu.json` is compiler-owned output. Modders should not author fields inside it by hand.
- Replacements come from `assets/replacements/<category>/<target-name>.<ext>` by convention (categories: `models/`, `textures/`, `sprites/`, `audio/`). `<target-name>` is the asset name surfaced by `assets search`; the runtime resolves Texture2D/Sprite/AudioClip replacements by `name`, so the bare name is the filename. Only ambiguous models (when both a `PrefabHierarchyObject` and `GameObject` share a name with another model) carry a `--<pathId>` suffix in the directory name. Discovered by convention; no explicit mapping list in `jiangyu.json`.
- For model-replacement UX, prefer `PrefabHierarchyObject` as the modder-facing target when both PHO and `GameObject` exist for the same effective model. PHO to GameObject collapse is shared via `AssetPipelineService.ResolveGameObjectBacking`. Missing or ambiguous backing `GameObject`s are hard errors.
- Template patching is a modder-facing contract. Modders author KDL under `templates/` (parsed by `KdlTemplateParser`); either as text in the Source editor, or visually in Studio's `TemplateVisualEditor` (host-side parse/serialise via `KdlTemplateParser.ParseText` and `KdlTemplateSerialiser.Serialise` over the same `KdlEditorDocument`, so both modes round-trip). The compiler emits `templatePatches` and `templateClones` into the compiled manifest. See the [docs site](https://antistrategie.github.io/jiangyu/) for the modder-facing shape and `docs/research/verified/` for the verified field contracts.
- `depends` matches against manifest `name`. Version text such as `>= 1.0.0` is stored but not enforced; when mods gain a stable `id` the match will move off `name`.

## Asset pipeline

- Jiangyu owns AssetRipper as an in-process library (vendored subtree with patches).
- Index-first workflow: index game data once, search/export on demand.
- Two-stage export: raw AssetRipper extraction, then Jiangyu cleanup rebuild (`ModelCleanupService`).
- Responsibility split: AssetRipper patches handle extraction fidelity; Jiangyu handles authoring cleanup, scale normalisation, and hierarchy cleanup.
- `jiangyu assets index` builds the searchable catalogue plus an IL2CPP metadata supplement (`<cache>/il2cpp-metadata.json`) extracted via the vendored Cpp2IL pipeline. The supplement carries data Il2CppInterop wrappers strip on generation: `[NamedArray(typeof(T))]` pairings, per-field `[Range]`/`[Min]`/`[Tooltip]`/`[HideInInspector]`/`Stem.SoundIDAttribute` hints, and `(concrete, interface)` implementation pairs (interfaces are wrapped as classes and the implements relations are dropped from CIL, so `Type.IsAssignableFrom` returns false for every interface impl; supplement entries restore that offline). `TemplateTypeCatalog` overlays it onto each `MemberShape`; `EnumerateConcreteSubtypes` consults the interface-impl map; both compile (`CompilationService`) and editor (`templatesQuery` and `templatesParse` RPCs) consume the result. Schema version is bumped whenever any of these tables change shape.
- Sprite atlas metadata is part of the index. Each `Sprite` entry carries its `textureRect` (X, Y, width, height) and `SpritePackingRotation` so compile-time atlas compositing can blit into the correct region.
- `asset-index.json` is versioned by `AssetPipelineService.CurrentFormatVersion`. Bump it whenever the index entry shape changes (sprite metadata fields, classification metadata, etc.) — older caches are flagged stale and modders re-run `jiangyu assets index`.
- `template-values.json` is the inspect cache (full per-template field tree, served by `jiangyu_templates_inspect` and `jiangyu_templates_value`). Versioned by `TemplateIndexService.CurrentFormatVersion`. Bump it whenever `InspectedFieldNode` shape or enricher output changes — e.g. v6 added paired enum-member names on each `[NamedArray(typeof(T))]` element. Stale caches surface in `templatesIndexStatus` and prompt a rebuild via `templatesIndex`.
- `jiangyu assets search` is the normal way to find replacement targets and their suggested paths. Use CLI help for the current command surface.
- `assets export model` produces a self-contained model package. Default is the cleaned authoring representation; `--raw` keeps the native form for inspection. Accepts `GameObject`, `Mesh`, or `PrefabHierarchyObject` pathIds; PHOs are collapsed to their backing `GameObject` before extraction. Ambiguity is surfaced with a candidate list, never silently resolved.
- `assets export atlas` exports an atlas Texture2D as PNG with coloured sprite-region outlines plus a companion legend `.txt`. Useful for finding which atlas region a target sprite occupies.
- Cache at `GlobalConfig.DefaultCacheDir` (XDG/LocalAppData-adaptive, overridable via global config `cache` field). Holds the asset index and exported package cache.

## Tests

- `tests/Jiangyu.Core.Tests/`: xUnit, .NET 10. `dotnet test tests/Jiangyu.Core.Tests/`.
- `tests/Jiangyu.Acp.Tests/`: xUnit, .NET 10. ACP schema round-trip, JSON-RPC framing, polymorphic SessionUpdate decoding, and a mock-agent integration test. `dotnet test tests/Jiangyu.Acp.Tests/`.
- `tests/Jiangyu.Studio.Host.Tests/`: xUnit, .NET 10. Covers the dispatcher (RPC envelope, malformed-request handling, path security, edit/write file flows), the in-process MCP server (tool reflection, schema shape, fs-tools-not-exposed invariant), the HTTP `/mcp` endpoint (bearer-token gate, body cap, tools/list), `AgentProcessManager` config (dual-transport, token redaction), agent session persistence, and the `editFile`/`writeFile` path-suppression wrappers. `dotnet test tests/Jiangyu.Studio.Host.Tests/`.
- `tests/Jiangyu.Loader.Tests/`: xUnit, .NET 10. Tests for loader code that doesn't invoke the IL2CPP runtime — path validators, mod load planning, and pure-reflection helpers in the loader project (called via `InternalsVisibleTo`). `dotnet test tests/Jiangyu.Loader.Tests/` (locally needs `MelonLoaderDir`/`GameAssembliesDir` env or default install at `~/.local/share/Steam/steamapps/common/Menace`).
- `src/Jiangyu.Studio.UI/`: vitest, Node environment. `bun run test` from that directory. (Plain `bun test` runs Bun's native runner and produces false failures.)

No tests require game data or Unity. All fast and deterministic.
