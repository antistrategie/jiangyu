# Template Cloning

Status: **verified** (Jiangyu in-game cache readback and save loading,
including deferred ancestor registration on 2026-09-10).

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

Implemented in `src/Jiangyu.Loader/Templates/Clones/TemplateCloneApplier.cs`:

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
8. Walk `resolvedType.BaseType` through every ancestor, including
   `DataTemplate`. Insert into each existing ancestor map and extend its
   array with the same native element class. Remember the clone for every
   ancestor, including those already loaded. A postfix on
   `Resources.LoadAll(string, Type)` appends remembered clones to that
   ancestor's resource result before MENACE builds its map and typed array.
   The match requires both the ancestor type and its exact `GetBaseFolder`
   path. A query for a narrower subfolder does not receive unrelated clones.
   An ID already returned by the game or another mod retains precedence.

   Ancestor visibility must hold on the first `GetAll<Ancestor>()` call.
   `OwnedItems.Init` snapshots `GetAll<BaseItemTemplate>()` into
   `m_ItemInstances`, which `OwnedItems.ProcessSaveState` indexes strictly.
   Backfilling the ancestor cache on a later frame cannot repair that
   private snapshot. Inserting at the resource boundary preserves the
   first result without loading an ancestor folder merely to register a
   clone. The registry also serves subsequent cache rebuilds.

   `EffectListTemplate`, the parent of `ShipUpgradeTemplate`, resolves to
   `Data/`. Loading that family can pull in unrelated template assets and
   their dependencies. If the resource hook cannot install, the loader
   warns and materialises non-root ancestor caches before insertion.

Non-DataTemplate sources such as `ConversationTemplate` load from their
Resources folder before the by-name lookup. `NonDataTemplateIdentityRegistry`
defines that folder.

### Ancestor resource boundary

Native inspection of the installed MENACE `GameAssembly.dll` confirms:

- The shared `DataTemplateLoader.LoadTemplates<T>` body at RVA `0x9F1110`
  calls `GetBaseFolder(Type)` at `0x9F12DD`, then `Resources.LoadAll<T>`
  at `0x9F1354`, before constructing `m_TemplateMaps` and `m_TemplateArrays`.
- `Resources.LoadAll<T>` at `0xB230B0` calls the non-generic
  `Resources.LoadAll(string, Type)` at `0xB23103`. The non-generic method
  is at `0x2842320` and returns an `Object[]`. MENACE's typed cache arrays
  are constructed from that result, so the postfix returns an `Object[]`
  rather than changing the array element type expected by an existing cache.
- `DataTemplate.GetID` at `0x504930` initialises an empty `m_ID` from the
  Unity object name. It can identify loaded resources before their cache
  initialisation without invoking another template-family load.

### Deferred registration verification

WOMENACE live checks on 2026-09-10, with all 961 clones registered and all
9,661 patch operations matching:

- The title-screen cache dump contains no `DataTemplate` or
  `EffectListTemplate` slot. The concrete `ShipUpgradeTemplate` slot
  contains all nine Fairy upgrades.
- `BaseItemTemplate` contains all 290 configured item clones, each at the
  same native pointer as its concrete-type entry.
- The first `BaseItemTemplate` resource load occurs while resolving
  `GlobalDifficultyTemplate.InitialAdditionalUnlockedItems`. Its references
  to `vehicle.voymastina_mech` and `vehicle.voymastina_mech_erwin` apply
  successfully before another clone-registration prefix runs. All three
  difficulty templates contain the expected references on readback.
- An existing campaign loads through `OwnedItems.ProcessSaveState` without
  missing-template or missing-key errors. The inspected inventory includes
  cloned Doll weapons, calibration components and affinity gifts.
- Blackmarket stock resolves, all three Fairy slots are present, and the
  Fairy Lodge and Rescue Fairy installation-cost queries resolve.

In one before/after pair on the same PC with the same mods and assets,
early registration falls from 12.070 s to 5.351 s. Time from Jiangyu
initialisation to the title scene falls from 23.950 s to 18.027 s.
The 5.760 s eager `EffectListTemplate` cache load disappears. Resident
process memory after startup remains similar at 3.7 GB versus 3.6 GB.
These are local startup measurements, not a prediction for another PC.

## Session re-registration

Implemented in `src/Jiangyu.Loader/Templates/Clones/TemplateCloneEarlyInjectionPatch.cs`.

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

1. WOMENACE LRM5 directive
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

`OwnedItems` save-reload was confirmed end-to-end against the live game,
2026-05-04:

1. WOMENACE clones four `ModularVehicleWeaponTemplate` variants (lrm5,
   lrm10, lrm15, lrm20) from `mod_weapon.medium.rocket_launcher`. A campaign
   acquired all three smaller variants and saved.
2. Cold restart: the save loaded cleanly through `OwnedItems.ProcessSaveState`
   without `KeyNotFoundException`. `OwnedItems.Init` snapshots
   `GetAll<BaseItemTemplate>()` into `m_ItemInstances`. Clones must be
   present in that first snapshot before the strict-indexer access in
   `ProcessSaveState`. A clone-free first result causes
   `KeyNotFoundException: 'mod_weapon.heavy.rocket_launcher_lrm15
   (Menace.Strategy.ModularVehicleWeaponTemplate)' was not present in the
   dictionary` from `OwnedItems.ProcessSaveState`.
3. Dangling-reference behaviour was characterised in the same session by
   deleting `lrm5` and renaming `lrm10` between save and reload. MENACE's
   own resolver (`SaveState.ProcessDataTemplate<T>`) sets the ref to null
   when `DataTemplateLoader.TryGet<T>` returns false; `OwnedItems` then
   skips null-template entries silently, the in-memory inventory loads
   without the dropped items, and the original save bytes remain
   untouched. Restoring the clones and reloading the same save brought
   the items back. Saving in the broken state, however, re-serialises
   the in-memory state and the dropped m_IDs do not roundtrip; this is
   acceptable behaviour and lives outside Jiangyu's responsibility.

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

## Owned-reference deep-copy

`Object.Instantiate` deep-copies the asset's serialised data but *shallow-
copies* PPtr lists: the clone's reference arrays start out pointing at the
source's referenced assets. For most cross-template references this is
correct (icons, damage types, shared sub-templates, registry entries
identified by `m_ID`). For one specific shape it is not: collections whose
element type is an abstract-polymorphic non-`DataTemplate` ScriptableObject
(currently `SkillEventHandlerTemplate` on `SkillTemplate.EventHandlers` and
`PerkTemplate.EventHandlers`) are conceptually *owned* by their parent —
each parent has its own handler instances configuring its own behaviour.
Sharing those PPtrs across a clone means clone-side patches mutate the
source's handlers.

Immediately after the `Instantiate` step and before
`m_TemplateMaps`/`m_TemplateArrays` registration, the applier walks the
concrete-typed clone's collection-typed properties and fields. For each
collection whose element type matches the owned shape:

1. `Object.Instantiate(element)` → fresh asset.
2. `hideFlags = HideFlags.DontUnloadUnusedAsset` so scene-change GC doesn't
   sweep the freshly-created handler.
3. `TryCast<element-type>` to get the indexer-compatible wrapper, then
   write back into the same list slot via the `Item` indexer.

The owned shape is detected structurally: element type descends from
`UnityEngine.ScriptableObject`, does *not* descend from `DataTemplate`
(those are intentional registry sharing), and has at least one strict
subtype in the same assembly (concrete wrappers like `SkillGroup` and
`DefectGroup` carry no subtypes and stay shared). The decision is cached
per element type. The applier reflects against the concrete wrapper type
(via `TryCast<concreteType>`) because the `clone` variable arrives as a
`DataTemplate` wrapper and base-typed reflection misses subtype-declared
collections like `PerkTemplate.EventHandlers`.

Verified in-game 2026-05-01: cloning Darby's
`perk.unique_darby_high_value_targets` produces a clone whose
`EventHandlers[0]` PPtr is a fresh `AddSkill(Clone)` asset at a different
native pointer from the source's handler. Loader log on the validated
run reads:

```
Template clone 'perk.unique_darby_high_value_targets_clone_leak_smoke':
    deep-copied 1 owned-PPtr element(s) across 1 list field(s) so
    clone-side patches don't leak into the source.
```

## Non-`DataTemplate` ScriptableObject clones

A subset of MENACE's authored data lives on `ScriptableObject`s that don't
descend from `DataTemplate` (`PerkTreeTemplate` is the worked case;
`SpeakerTemplate` ancestors land in the same bucket on builds where they
don't inherit `DataTemplate`). These are still cloneable, with the same
authoring shape and modder-facing contract — `clone "PerkTreeTemplate"
from="..." id="..."`, addressable by `ref="..."` from subsequent patches —
but a smaller runtime registration step:

1. `Object.Instantiate(source)` against the loaded source ScriptableObject.
2. `cloneObj.name = cloneId`.
3. `hideFlags = DontUnloadUnusedAsset` + `DontDestroyOnLoad` so scene
   changes don't sweep the clone.

No `m_TemplateMaps` insertion, no `m_ID` rewrite, no ancestor mirroring:
non-`DataTemplate` SOs aren't registered with `DataTemplateLoader`. Runtime
resolution for them goes through
`TemplateRuntimeAccess.TryGetTemplateById`'s by-name branch
(`Resources.FindObjectsOfTypeAll<T>` + `Object.name` match), which finds
the clone the moment `Instantiate` returns.

The applier routes between the two tracks structurally:
`typeof(DataTemplate).IsAssignableFrom(resolvedType)` selects the
`DataTemplateLoader` path; everything else flows through
`TryApplyScriptableObjectType`. Studio's RPC layer mirrors the same
distinction: any concrete non-`DataTemplate` ScriptableObject with no
polymorphic ref-subtypes surfaces as a `TemplateReference` destination
(modder picks an existing instance or a clone id from the ref combobox),
so both tracks share the authoring UX.

## Scope limits

- **Non-serialised fields beyond `m_ID` stay at their pre-clone values.**
  `Instantiate` does not copy `[NonSerialized]` fields; only `m_ID` is
  rewritten. A specific non-serialised field that needs resetting on clone
  carries a `JIANGYU-CONTRACT:` marker at its IL2CPP offset-write site and
  documents the reason; there's no generic "reset all non-serialised"
  primitive because most such fields hold legitimate derived state that
  the source's value is correct for.
- **No cascade cloning of named DataTemplate references.** Cloning a
  template whose referenced sub-templates should also be cloned is
  authored explicitly: one `clone` directive per template that needs an
  independent identity. The owned-reference deep-copy above is structural
  (it triggers on the polymorphic-abstract non-DataTemplate shape, not on
  every reference) so cross-template references — icons, damage types,
  shared sub-templates, registry-identified DataTemplates — keep their
  intentional sharing. Genuine cascade cloning of DataTemplate refs is
  modder-driven, one directive per identity.

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
- Owned-reference deep-copy:
  `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs:DeepCopyOwnedReferences`
  and `IsOwnedElementType`.
- Non-`DataTemplate` ScriptableObject clone path:
  `src/Jiangyu.Loader/Templates/TemplateCloneApplier.cs:TryApplyScriptableObjectType`.
