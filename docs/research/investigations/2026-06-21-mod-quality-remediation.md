# Mod-quality remediation

Source: community feedback on comparable Unity IL2CPP mod frameworks, cross-checked
against the Jiangyu loader on 2026-06-21. Each item records the concrete finding, the
file it lives in, the decision, and a status box. Status legend: `[ ]` open, `[~]`
blocked on research, `[x]` done.

## Decisions taken

- **Strictness.** Avoid `OnUpdate` entirely. Bounded per-frame coroutines are also
  disfavoured, so a "fire a short settle loop off the event" fallback is not accepted
  on its own. Research how mature IL2CPP mods re-apply after content streams in late
  (see section C) before committing to any settle mechanism.
- **Modder per-frame API.** Remove `JiangyuSystem.OnUpdate` if the hook and coroutine
  surface covers real modder needs. Confirm the surface is sufficient first.
- **Driven bone sync.** Use a scoped `LateUpdate` component on the driven object, not a
  global per-frame sweep.

## A. Eliminate OnUpdate

`JiangyuMod.OnUpdate` (`src/Jiangyu.Loader/Runtime/JiangyuMod.cs:241`) drives six
unrelated jobs. Rehome each, then delete the method, the `_frameInScene` counter, and
any now-dead plumbing.

- [x] **A1. Hook attachment, tactical.** Done 2026-06-21. `EnsureAttached` became
  `TacticalHookPublisher.Attach(TacticalManager)`, driven by `TacticalManagerStartPatch`
  postfixing `TacticalManager.Start()`. Subscribe logic unchanged, per-frame call removed
  from `OnUpdate`. Compiles, 263 loader tests pass, dev loader deployed. Validated in-game
  2026-06-21: `TacticalManager.Start` patched at install, mission start logged
  `attached to TacticalManager (34 events)` exactly once, no errors.
- [x] **A2. Spawn monitor.** Done 2026-06-21 (code). New `ElementSpawnReplacementPatch`
  postfixes `Element.OnSpawned` and pulls the unit's SkinnedMeshRenderers from the
  element's own `GetRenderers()` list (`List<SharedMaterialsRenderers>`, each exposing
  `GetRenderers()` → `List<Renderer>`), handing them to `ReplacementCoordinator.ApplyToSpawnedRenderers`,
  which runs the extracted `TryApplyToRenderer` (verbatim per-SMR apply, so the proven
  mesh/driven logic is unchanged, only the trigger moved from poll to event). The probe
  (A2b) ruled out a transform scan: the SMRs are never parented under `Element.m_Mesh`.
  The steady-state >600 poll, `SpawnMonitorIntervalFrames`, and `_frameOfLastSpawnMonitor`
  are gone, and `JiangyuMod._frameInScene` with it. Compiles, 263 loader tests pass,
  deployed. In-game smoke pending (covers A2b): see validation note below.
- [x] **A2b. In-game validation (covers the timing question).** Validated 2026-06-21:
  `Element.OnSpawned` patched at install; in a tactical mission all 79 spawned elements
  logged `[spawn] element skinnedRenderers=N` with N > 0 (15/4/3...), i.e. renderers
  available synchronously at the anchor via `GetRenderers()`. No postfix throws, no errors,
  `TacticalManager` still attached. The debug-gated `[spawn]` line stays as a permanent
  diagnostic. Original plan notes follow.

  A model-replacement mod
  needs a slow Unity batchmode build and only fires if the targeted body is in the live
  squad (uncertain with WOMENACE active), so validation is via a debug-gated probe instead.
  `ApplyToSpawnedRoot` logs `[spawn] Element.OnSpawned root='X' skinnedRenderers=N` for
  every spawned element when the `debug` flag is set, before the replacements guard, so it
  is body- and mission-agnostic. Confirm at launch `spawn replacement: patched
  Il2CppMenace.Tactical.Element.OnSpawned`, then that each spawned unit (squaddies, any
  enemy) logs a `[spawn] element skinnedRenderers=N` line with N > 0 (proves the postfix
  fires per element with renderers already built). The full apply (`TryApplyToRenderer`) is
  the verbatim proven poll-path code, so the trigger plus renderer-readiness is the only new
  risk. Also confirm no regression to WOMENACE custom humanoids (the humanoid-mirror drain
  is now scene-load-only).

  Probe findings (2026-06-21): both `Element.Create` (197x) and `Element.OnSpawned` (79x)
  fire, but the SMRs are never children of `m_Mesh` (childSMRs=0 throughout). The element's
  `GetRenderers()` list is partial at `Create` (1-3 entries, initialSmrCount=0) and fully
  built at `OnSpawned` (e.g. 10 groups / initialSmrCount=15). Hence the final anchor is
  `OnSpawned` reading `GetRenderers()`, not a transform scan.
- [x] **A3. Driven bone sync — resolved by DELETING the dead driven path.** Done 2026-06-21.
  Investigation (git archaeology) showed the driven-prefab path was not just dormant but
  unreachable dead code since commit `908af4d` (2026-04-19, "fix rigid-skin export and in-place
  material mutation"): that commit flipped the apply loop to try the direct mesh-rebind first
  (returning/continuing before the prefab branch), and since the catalog co-registers a
  `ReplacementMesh` for every `ReplacementPrefab` under the same key (since `a8d33c2`,
  2026-04-15), the prefab/driven branch can never be reached. It was reachable (even primary)
  only for ~5 days at project start (initial commit 2026-04-14 → 2026-04-18), before the rebind
  rework superseded it, and almost certainly never ran in production. So rather than build a
  `BoneDriver` LateUpdate component (an IL2CPP-injection risk to serve unreachable code), the
  whole path was deleted: `DrivenPrefabReplacementManager`, the (interim) `BoneDriver`,
  `ReplacementPrefab`, the `_catalog.Prefabs` dict + its registration + `_prefabOwners` +
  `RegisterPrefabOverride` + `CollectPrefabBoneNames`, and the driven branch in
  `TryApplyToRenderer`. The bundle instance is now disposed after mesh extraction (it was kept
  only to Instantiate driven copies; the meshes are pinned independently and the direct path
  + `PrefabMeshRebindApplier` never reference it). `HasMeshOrPrefabReplacements` → `HasMeshReplacements`.
  This also removes the per-frame `new List<int>()` (B1). No IL2CPP-injection risk, no per-frame
  anything. Solution builds clean (0 warnings), 263 loader tests pass, deployed.
- [x] **A4. UI re-injection.** Done and validated in-game 2026-06-21. Deleted
  `UiInjectionDriver` (the per-frame `GetActiveScreen()` poll + 60-frame settle window). New
  `UiInjectionActivatePatch` postfixes both `UIManager.OpenScreen` and `ActivateScreen` and
  calls `UI.NotifyScreenActivated(screen.GetRootElement())`, which re-applies immediately and
  registers a `GeometryChangedEvent` on the screen root (once per root) as a post-open safety
  net. `UI.ReapplyAll` is idempotent (skips occupied sites). Removed the `_ui` field and its
  `OnUpdate`/`OnSceneLoaded` calls. Key finding: the strategy screens come up via `OpenScreen`,
  NOT `ActivateScreen` (zero ActivateScreen fires observed), so an ActivateScreen-only hook
  landed nothing on the first attempt; both are now postfixed. Validated: WOMENACE's 7
  injections registered, opening `ArmoryUIScreen` landed its 4 Armory-targeted ones
  immediately, no errors, MissionPrep-targeted ones correctly stayed unlanded until that
  screen opens; content was ready at the OpenScreen postfix (no settle needed). 263 loader
  tests pass, deployed. (The `ui-inject` dev flag is vestigial, no consumers; not used.)
- [x] **A5. Hook attachment, strategy.** Done 2026-06-21. `EnsureAttached` split into
  `AttachState(StrategyState)` (state plus squaddies plus current factions) and
  `SubscribeFactionsFrom(StoryFactions)`, driven by `StrategyAttachPatch` postfixing
  `StrategyState.OnAdded()`, `StoryFactions.Init()`, and `StoryFactions.ProcessSaveState`.
  Per-frame `m_Factions.Count` poll removed (folds in B2); a shared `EnsureFreshState`
  pointer guard keeps it idempotent across event orderings. Compiles, tests pass, deployed.
  Validated in-game 2026-06-21: all three anchors patched at install, campaign load logged
  `attached to StrategyState` once plus 14 faction subscriptions, no double-attach and no
  enumeration errors.
- [x] **A6. Modder per-frame API.** Done 2026-06-21. Removed `JiangyuSystem.OnUpdate`,
  `ModHost.Update`, and the `_modHost.Update()` forwarding call. Rationale settled with the
  user: to delete the loader's own `OnUpdate`, the modder one must go too, otherwise the
  loader would run a per-frame coroutine purely to forward it (the same pump, renamed).
  Modders needing per-frame work attach a scoped MonoBehaviour (the Unity-idiomatic path,
  same as A3) or use `Context.Coroutines`. Swept: `ModHostTests` (pivoted the forwarding and
  quarantine tests to `OnSceneLoaded`), the JIA008 analyzer test (the `OnUpdate` case was
  redundant with an existing `OnSceneLoaded` test, removed; the analyzer's `OnUpdate`
  branches stay valid for game-type `[JiangyuType]` overrides), `site/sdk/index.md` and the
  scaffold `README.md` lifecycle lists. 263 loader + 51 analyzer tests pass; solution builds
  clean. Loader redeploy deferred (no in-game behaviour to validate; batches with A4/A3).
- [x] **A7. Dev bridge pump.** Done 2026-06-21. `IDevServices.OnUpdate` removed;
  `DevServices` now starts a self-contained `MelonCoroutines` pump loop on its first
  `OnSceneLoaded` (the coroutine runner is not ready at OnInitializeMelon time). The loop
  drains `_bridge.Pump()` and does the periodic flag check. Off `JiangyuMod`, dev-loader
  only. Compiles, deployed. In-game check: bridge still responds with the `bridge` flag set.
- [x] **A8. Delete the method.** Done 2026-06-21. `JiangyuMod.OnUpdate` removed (and
  `_frameInScene` earlier with A2). The loader carries no `MelonMod.OnUpdate` override:
  replacement application, UI re-injection, and hook attachment are event-driven (Harmony
  postfixes on the game's own methods), the driven-prefab bone-sync that used to live here was
  deleted as unreachable dead code (A3), and the dev bridge pump is a self-contained dev-loader
  coroutine. The only remaining per-frame loops are the bounded scene-load follow-up poll
  (`FollowUpPoll`, self-terminating) and that dev pump. Class doc updated. Solution builds
  clean, 263 loader tests pass.

## B. Other findings

- [x] **B1. Per-frame list alloc.** Removed wholesale by A3: `DrivenPrefabReplacementManager.Update`
  (and its per-frame `new List<int>()`) is gone; bone mirroring is now a per-object component.
- [x] **B2. Per-frame faction-count poll.** Done 2026-06-21 with A5: the `m_Factions.Count`
  read and `_lastFactionCount` gate are gone, replaced by event-driven `SubscribeFactionsFrom`.
- [x] **B3. Spawn-sweep string alloc.** Defused 2026-06-21 by A2: `NormaliseRendererPath`
  no longer runs in a per-frame sweep. It now fires only on the bounded scene-load polls
  and once per element spawn (event-driven), so the per-frame allocation pressure is gone.
  A dedicated cache is unnecessary at that frequency.
- [x] **B4. Unscoped AppDomain scan.** Done 2026-06-21. `TemplateCloneApplier` now resolves
  `Il2CppStem.SoundManager` through a cached `ResolveSoundManagerType()` (static field), so
  the AppDomain walk runs only until the first hit; a miss is not cached so an early call
  before Stem loads retries. Solution clean, 263 tests pass.
- [x] **B5. Reconcile mod-DLL load.** Done 2026-06-21 (confirm + document; no conflict). The
  two load paths are deliberately different by context: at runtime the loader loads each
  `code/` DLL once via `Assembly.LoadFrom` (`JiangyuMod`), because stable assembly identity
  is required for `ModHost` dedup and `SdkAssemblyResolver` redirection (a byte-load mints a
  fresh identity and would break both); the file-lock during gameplay is harmless (no mid-session
  recompile). The byte-load is purely compile-time, in `Jiangyu.Core`'s `TemplateTypeCatalog`
  (commit `a715720`), reading the freshly-built DLL into a `MetadataLoadContext` so Windows
  does not lock it past dispose during a recompile. The runtime template catalogs load only
  manifests, never code DLLs, so there is no double-load. Documented in AGENTS.md (Mods section).

## C. Research (resolved)

- [x] **C1. Late-streaming re-apply without a per-frame loop.** Resolved 2026-06-21 via
  community-pattern research plus MENACE assembly recon. Both A2 and A4 have a clean
  no-poll anchor, so neither needs a poll or a settle coroutine.

  **Community pattern (fallback hierarchy, most to least preferred).**
  1. Subscribe to a C# event the game already exposes.
  2. Harmony-postfix the game method that finalises the thing (the visual-build method,
     the UI populate/rebuild method), used as an event. This is the loaders' primary tool
     and the right answer when no event exists.
  3. Inject a MonoBehaviour (`ClassInjector.RegisterTypeInIl2Cpp<T>()`, `IntPtr` base
     ctor, attach via `AddComponent`, not `new`) and act in its `Awake`/`Start`. Note
     `Start` runs the frame after `Instantiate`, so it sees visuals an `Awake` would miss.
     Or subscribe to an Addressables `AsyncOperationHandle.Completed` callback when the
     game loads async.
  4. UI Toolkit element callbacks: `AttachToPanelEvent`/`DetachFromPanelEvent` on a
     long-lived host, or `GeometryChangedEvent` as a weaker proxy (it re-fires, so
     unregister aggressively). There is no UI Toolkit "children changed" event.
  5. A single `yield return null` (one-shot, next frame, not a poll) only where the visual
     lands one frame later with nothing to hook. Forcing layout synchronously
     (`Canvas.ForceUpdateCanvases`) often removes even that. Polling is the last resort.

  **MENACE anchors (recon).**
  - Spawn visuals: `Il2CppMenace.Tactical.Element.Create(...)` (virtual, builds
    `m_Renderers`/`m_Mesh`/`m_ElementAnimator`) and `Element.OnSpawned()` right after. No
    visual-ready event exists. Drives A2.
  - UI rebuild: shared base `BaseUnitSelector<T>` (`Init`, `AddUnitSlot`,
    `CreateSlotForLeader`, subclass `Refresh()`), selection event
    `BaseUnitSelector<T>.add_OnSelectedUnitChanged`, detail panel `UnitWindow.Refresh`/
    `SetLeader`, root element via `UIScreen.GetRootElement()` / `m_RootElement`. Drives A4.

  **Carry-forward caveats.** Injected-MonoBehaviour `Start`/`OnEnable` support is inferred
  from MonoBehaviour treatment (docs show `Awake` and `Update` verbatim). `GeometryChangedEvent`
  firing on rebuild rests on a Unity-staff forum statement, not the static manual. ilspycmd
  reads only IL2CPP native stubs, so `Element.Create`/`OnSpawned` call ordering and renderer
  readiness need the A2b live probe. Do not inject `IEnumerator` coroutines onto an injected
  type (use `MelonCoroutines`); pin long-lived injected references against GC.

## D. Confirmed sound (no action, do not re-litigate)

- Hook bus dispatch is allocation-free copy-on-write
  (`src/Jiangyu.Loader/Sdk/Hooks/InProcessHookBus.cs`).
- No reflection on gameplay hot paths. The verb surface is direct IL2CPP calls. Template
  reflection is one-shot at apply time.
- No publicizer needed: IL2CPP wrappers already expose native private members.
- Mod discovery is manifest-first (`jiangyu.json` glob), not a load-every-DLL plugin
  scan. Code DLLs load only from a mod's declared `code/` directory, with assembly
  identity dedupe.
- Control is inverted: mods declare `JiangyuSystem` types the loader discovers, not a
  cross-plugin domain scan.
- No `OnDestroy`/`OnDisable` cleanup that could fire at the wrong time. Only MelonLoader
  lifecycle events are used.
- Bundles load via `Il2CppAssetBundleManager.LoadFromFile`, no `LoadFromMemory` byte
  duplication.
- No embedded-asset unpacking in the loader. Assets ship as loose `.bundle` files.
- Ships as a single merged DLL (first-party plus `System.Text.Json` and
  `System.Text.Encodings.Web`), not a 70-file third-party sprawl.
