# Template Cloning

Status: **verified** (Jiangyu in-game readback plus cold-restart save/load
confirmed via EntityPatchSmoke + ClonePersistenceSmoke, 2026-04-20).

## Contract

Jiangyu's template cloning primitive deep-copies an existing live
`DataTemplate`-derived `ScriptableObject` and registers the copy under a new
`m_ID`, so it resolves through the same surfaces vanilla templates do:
`Get<T>` / `TryGet<T>` for direct lookup, and `GetAll<T>` for any `T` from
the most-derived type up to `DataTemplate`. Modders drive it via a top-level
`templateClones` block in `jiangyu.json`:

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

A `clone` block in KDL may carry inline `set`/`append`/`insert`/`remove`/
`clear` ops as children. The parser splits each such block into a
`CompiledTemplateClone` directive plus a synthetic `CompiledTemplatePatch`
targeting the new `cloneId`, so the inline ops apply against the freshly
registered clone after the clone phase finishes. Authoring shape:

```kdl
clone "WeaponTemplate" from="weapon.foo" id="weapon.foo_buffed" {
    set "Damage" 50.0
    set "Range" 100
}
```

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
7. Allocate a length+1 native IL2CPP array via `il2cpp_array_new(elementClass,
   newLength)`, copy existing element pointers across, append the clone,
   wrap it in the original wrapper's runtime type via the generated
   `(IntPtr)` ctor, and replace the entry in `m_TemplateArrays[type]`. The
   element class comes from the original native array
   (`il2cpp_class_get_element_class(il2cpp_object_get_class(oldArrayPtr))`),
   not from the wrapper's generic `T`. This is the load-bearing detail: a
   prior attempt that allocated via `new Il2CppReferenceArray<DataTemplate>(managedArray)`
   used the base type's class and the game's own `GetAll<T>` consumer hung
   on the result. Using the original's element class keeps the replacement
   byte-identical to what the dict slot expects.
8. Walk `resolvedType.BaseType` upward while
   `typeof(DataTemplate).IsAssignableFrom(current)`. For each ancestor whose
   `Il2CppType` key already exists in `m_TemplateMaps`, insert the clone into
   the ancestor's inner map and run the same length+1 array extension on the
   corresponding `m_TemplateArrays` slot. This mirrors vanilla: a vanilla
   `WeaponTemplate` instance lives at the same native pointer in both
   `m_TemplateMaps[WeaponTemplate]` and `m_TemplateMaps[BaseItemTemplate]`,
   so consumers calling `GetAll<BaseItemTemplate>()` (e.g. the BlackMarket
   pool, filtered by `BlackMarketMaxQuantity > 0`) see clones the same way
   they see vanilla. The walk only writes to slots the game has already
   materialised; it never creates new ancestor-keyed slots, so its visible
   destinations are bounded by what the game itself populates. The walk is
   idempotent across re-registration ticks: an ancestor slot the game
   materialises later than the first apply pass is filled in on a
   subsequent tick.

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

Ancestor visibility was confirmed end-to-end against the live game,
2026-04-30:

1. WoMENACE LRM5 directive
   (`templateType: ModularVehicleWeaponTemplate`,
   `sourceId: mod_weapon.medium.rocket_launcher`,
   `cloneId: mod_weapon.medium.lrm5`) reaches the BlackMarket pool, which
   enumerates `GetAll<BaseItemTemplate>()` filtered by
   `BlackMarketMaxQuantity > 0`.
2. Inspect dump (`UserData/jiangyu-inspect/*-templates-*.json`) showed 259
   distinct vanilla native pointers registered under more than one
   `m_TemplateMaps` slot in a typical Strategy scene. The source for the
   smoke (`mod_weapon.medium.rocket_launcher`, native pointer
   `0x74D2BC40`) appears under both `BaseItemTemplate` and
   `WeaponTemplate`. The walk reproduces this multi-key registration for
   clones.
3. Loader log on the validated run reports the
   `Template clone registered: ModularVehicleWeaponTemplate:... -> mod_weapon.medium.lrm5`
   line and zero `failed to mirror into m_TemplateArrays[<ancestor>]`
   warnings. Idempotent re-registration ticks report
   `applied 0 clone registration(s)`, confirming the walk does not
   double-insert.

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

- **Non-serialised fields beyond `m_ID` stay at their pre-clone values.**
  `Instantiate` does not copy `[NonSerialized]` fields; only `m_ID` is
  rewritten. A specific non-serialised field that needs resetting on clone
  carries a `JIANGYU-CONTRACT:` marker at its IL2CPP offset-write site and
  documents the reason; there's no generic "reset all non-serialised"
  primitive because most such fields hold legitimate derived state that
  the source's value is correct for.
- **No cascade cloning.** Cloning a template whose referenced sub-templates
  should also be cloned is authored explicitly: one `clone` directive per
  template that needs an independent identity. There is no automatic
  deep-walk because most cross-template references are intentionally
  shared (icons, damage types, shared sub-templates) and a generic
  cascade would over-clone them.

## Jiangyu Implementation

- Schema: `CompiledTemplateClone` in
  `src/Jiangyu.Shared/Templates/CompiledTemplatePatchManifest.cs`.
- Compile-time validation:
  `src/Jiangyu.Core/Compile/TemplatePatchEmitter.cs:EmitClones`.
- Runtime catalogue:
  `src/Jiangyu.Loader/Templates/TemplateCloneCatalog.cs`.
- Runtime applier:
  `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs`.
- Ancestor mirror walk:
  `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs:MirrorCloneToAncestors`.
- Session re-registration hooks:
  `src/Jiangyu.Loader/Templates/TemplateCloneEarlyInjectionPatch.cs`.
- Direct-lookup helper:
  `src/Jiangyu.Loader/Templates/TemplateRuntimeAccess.cs:TryGetTemplateById`.
- Apply ordering: clones run before patches in
  `src/Jiangyu.Loader/Runtime/ReplacementCoordinator.cs`.
