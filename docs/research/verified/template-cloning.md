# Template Cloning

Status: **verified** (Jiangyu in-game readback plus cold-restart save/load
confirmed via EntityPatchSmoke + ClonePersistenceSmoke, 2026-04-20).

## Contract

Jiangyu's template cloning primitive deep-copies an existing live
`DataTemplate`-derived `ScriptableObject` and registers the copy under a new
`m_ID` so `DataTemplateLoader.Get<T>` / `TryGet<T>` resolve it. Modders drive
it via a top-level `templateClones` block in `jiangyu.json`:

```json
"templateClones": [
  { "templateType": "UnitLeaderTemplate",
    "sourceId": "squad_leader.darby",
    "cloneId": "squad_leader.darby_jiangyu_clone" }
]
```

Clones run before `templatePatches` apply so subsequent patches can target
the newly registered `cloneId`. Compile-time validation in
`TemplatePatchEmitter.EmitClones` rejects missing `templateType`, empty
`sourceId`/`cloneId`, `sourceId == cloneId`, and batch-internal duplicate
cloneIds.

Clone-backed saves are supported by re-registering configured clones on every
session before MENACE's save-slot discovery and save-load paths touch template
IDs. The save does not persist the clone object itself; it persists the
`cloneId`, and Jiangyu restores that ID-to-template registration from the
manifest on the next launch.

## Runtime steps

Implemented in `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs`:

1. `TemplateRuntimeAccess.GetAllTemplates(templateType)` — forces
   `DataTemplateLoader.GetAll<T>()` to materialise the per-type cache. An
   empty result means the cache isn't ready yet and the scheduled apply
   coroutine retries later.
2. Read the source template directly from
   `DataTemplateLoader.GetSingleton().m_TemplateMaps[type][sourceId]`. Jiangyu
   uses the already-materialised per-type lookup map instead of a second
   reflective `TryGet<T>` call.
3. `UnityEngine.Object.Instantiate(source.Cast<UnityEngine.Object>())` —
   deep-copies all serialised fields. `m_ID` is `[NonSerialized]` and is not
   propagated by `Instantiate`, so it is written separately.
4. Set `clone.name = cloneId` and `clone.hideFlags =
   HideFlags.DontUnloadUnusedAsset` so scene-change GC does not sweep the
   clone.
5. Walk the IL2CPP class hierarchy via `il2cpp_class_get_parent` to find
   `m_ID` (declared on the `DataTemplate` base), read its offset via
   `il2cpp_field_get_offset`, and `Marshal.WriteIntPtr` the new
   `ManagedStringToIl2Cpp` pointer at that offset.
6. Insert into `DataTemplateLoader.GetSingleton().m_TemplateMaps[type][cloneId]`
   via direct typed property access on the Il2CppInterop-generated wrapper.
   Only `m_TemplateMaps` (the name-lookup store) is written; see "Scope
   limits" for why `m_TemplateArrays` is not touched.

## Session re-registration

Implemented in `src/Jiangyu.Loader/Templates/TemplateCloneEarlyInjectionPatch.cs`.

Jiangyu installs Harmony prefixes on the earliest validated startup/load
surfaces in the current MENACE build:

- `SceneStateSettings.Awake`
- `GameStartConfig.InitializeGame`
- `SaveSystem.TryGetLatestSaveState`
- `SaveSystem.TryGetSaveState`
- `SaveSystem.GetSortedSaveStates`
- `SaveSystem.Load`
- `SaveSystem.ExecLoad`
- `SaveSystem.LoadSaveGameCoroutine`
- `StrategyState.CreateNewGame` when present

Each prefix clears the per-type "already applied" set and re-runs
`TemplateCloneApplier.TryApply(log)`. The important verified boundary is
`SceneStateSettings.Awake`: on the 2026-04-20 cold-restart smoke run it fired
before save-slot discovery and before the later save-load path, so the clone
IDs referenced by the save were already present in `m_TemplateMaps`.

## Patch applier switch to TryGet

Template patching was previously an enumerate-then-match design:
`GetAll<T>` → build `byId` dictionary → per-patch lookup. After this slice,
`TemplatePatchApplier.TryApplyType` instead uses
`TemplateRuntimeAccess.TryGetTemplateById`, which calls
`DataTemplateLoader.TryGet<T>(id, out template)` via a reflection-resolved
generic method. Benefits:

- O(1) per-patch lookup instead of O(N) enumeration.
- Reads from `m_TemplateMaps`, so it sees both game-native templates and
  Jiangyu-registered clones.
- Avoids the need to write to `m_TemplateArrays`.

## Verification

Confirmed by Jiangyu against the live game, 2026-04-20:

1. EntityPatchSmoke mod with
   `templateClones: [{ templateType: "UnitLeaderTemplate", sourceId: "squad_leader.darby", cloneId: "squad_leader.darby_jiangyu_clone" }]`
   and a companion patch targeting
   `squad_leader.darby_jiangyu_clone.InitialAttributes.Vitality = 77`.
2. Loader log:
   `Template clone registered: UnitLeaderTemplate:squad_leader.darby -> squad_leader.darby_jiangyu_clone (mod 'EntityPatchSmoke').`
3. Loader log:
   `Template patch 'UnitLeaderTemplate:squad_leader.darby_jiangyu_clone.InitialAttributes[4]' (mod 'EntityPatchSmoke'): set to 77, readback matches.`
4. Apply summary reported `Applied 2 UnitLeaderTemplate patch op(s). [skipped: missingTemplate=0 …]` — the clone was resolvable by `TryGet<UnitLeaderTemplate>("squad_leader.darby_jiangyu_clone")`.
5. Scene transitioned cleanly from Splash → Title → gameplay without crash
   or hang.

Save/reload persistence was then confirmed with a separate smoke case:

1. ClonePersistenceSmoke cloned `EntityTemplate:player_squad.darby` to
   `player_squad.darby_jiangyu_save_clone` and patched
   `UnitLeaderTemplate:squad_leader.darby.InfantryUnitTemplate` to reference
   that clone.
2. A new campaign was started, saved as `clean_smoke`, the game was fully
   closed, then the save was loaded from a fresh launch.
3. `clean_smoke.save` contained the clone-backed IDs, so the reload path was
   forced to resolve them after restart.
4. `MelonLoader/Latest.log` showed:
   `Template clone registered: EntityTemplate:player_squad.darby -> player_squad.darby_jiangyu_save_clone`
   and
   `Template clone early injection via SceneStateSettings.Awake: applied 2 clone registration(s).`
5. `Player.log` contained no
   `Failed to get DataTemplate ... player_squad.darby_jiangyu_save_clone`
   or
   `Failed to get DataTemplate ... squad_leader.darby_jiangyu_clone`
   lines on the final validated run.

That proves Jiangyu's shipped clone contract is not "new game only": clone IDs
referenced by a save survive a cold restart because the loader restores the
`m_TemplateMaps` entries before MENACE consumes them.

## Reference cross-check

Same-game prior art: `p0ss/MenaceAssetPacker`'s
`src/Menace.ModpackLoader/TemplateCloning.cs` uses the same
Instantiate + IL2CPP `m_ID` offset write + `m_TemplateMaps` insertion path.
Jiangyu diverges by:

- accessing `m_TemplateMaps` through the typed Il2CppInterop wrapper property
  (`singleton.m_TemplateMaps`) rather than via hardcoded 0x18 struct-offset
  pointer arithmetic — a game-side rename becomes a compile error instead of
  a runtime silent-fail;
- requiring `templateType` explicitly on each directive (no silent
  `EntityTemplate` default);
- routing the patch applier through `DataTemplateLoader.TryGet<T>` so clones
  and vanilla templates resolve through the same API.

## Scope limits

- **`m_TemplateArrays` is not updated.** `DataTemplateLoader.GetAll<T>()`
  reads from `m_TemplateArrays` and will not include clones in its
  enumeration. This was deliberate after an earlier attempt: rebuilding via
  `new Il2CppReferenceArray<DataTemplate>(managedArray)` produced a native
  IL2CPP array the game's own `GetAll<T>` enumeration hung on (froze
  new-game start, 2026-04-20). The `TryGet<T>`-based patch applier does not
  need enumeration, so leaving `m_TemplateArrays` untouched is correct for
  patch targeting. Gameplay code that enumerates `GetAll<T>` for a given
  template type will not see clones; if that becomes limiting, the
  enumeration backing store can be handled with a safer array-construction
  approach (e.g. `il2cpp_array_new` + element copy via raw pointer writes).
- **Non-serialised fields beyond `m_ID` stay at their pre-clone values.**
  `Instantiate` does not copy `[NonSerialized]` fields; only `m_ID` is
  rewritten. Additional non-serialised fields on a specific template type
  will need the same IL2CPP offset-write pattern if they need to be reset.
- **No cascade cloning.** Cloning a template whose referenced sub-templates
  also need to be cloned is not supported; modder authors separate clone
  directives for each.
- **No value overrides in the clone directive.** Authoring shape is "clone,
  then optionally patch" — combining clone + inline set into a single block
  is a future ergonomics pass.

## Jiangyu Implementation

- Schema: `CompiledTemplateClone` in
  `src/Jiangyu.Shared/Templates/CompiledTemplatePatchManifest.cs`.
- Compile-time validation:
  `src/Jiangyu.Core/Compile/TemplatePatchEmitter.cs:EmitClones`.
- Runtime catalogue:
  `src/Jiangyu.Loader/Templates/TemplateCloneCatalog.cs`.
- Runtime applier:
  `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs`.
- Session re-registration hooks:
  `src/Jiangyu.Loader/Templates/TemplateCloneEarlyInjectionPatch.cs`.
- Direct-lookup helper:
  `src/Jiangyu.Loader/Templates/TemplateRuntimeAccess.cs:TryGetTemplateById`.
- Apply ordering: clones run before patches in
  `src/Jiangyu.Loader/Runtime/ReplacementCoordinator.cs`.
