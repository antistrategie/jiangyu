# Game-API verb surface

Planning the imperative game-API for behaviour mods: curated verbs that *command* and *read* the live game (spawn a unit, query a path, list actors), distinct from the four existing surfaces (observe via `Hooks`, be-called-by-the-game via `[JiangyuType]`, change data via templates, raw method patch via `Patches`).

Every call sequence here is a hypothesis to spike on the live game and promote to `docs/research/verified/` before any verb depends on it. Offsets and runtime claims are reproduced against our own `validation/` baseline and live IL2CPP metadata, never assumed.

## Static verification (pass 1: signatures)

Confirmed every prerequisite type and method against the generated proxy `Assembly-CSharp.dll` (metadata-only `MetadataLoadContext` read, the same surface a `code/` mod compiles against). This pass settles existence and signatures; behaviour and timing remain for the live spike (pass 2).

Confirmed exact:
- `TacticalManager.Get()` static; `GetMap() -> Map`; `GetActiveActor()` / `SetActiveActor(Actor, bool=opt)`; `GetFaction(FactionType)` and `GetFaction(int)` (single-faction lookup, no need to iterate); `GetFactions()`; `GetActiveFactionID()`; `GetRound()`; `IsMissionRunning()` static; `GetActorCount(bool playerActors, bool enemyActors, bool alive, bool dead, Nullable<...>=opt actorType)`; `Finish(TacticalFinishReason)`. Its `OnFinished`/`OnMovementFinished`/`OnEntitySpawned` events are the broadcasters the hook publisher already binds.
- `TacticalManager.TrySpawnUnit(FactionType, EntityTemplate, Tile, ref Actor& unit)` — exact anchor signature (plus two RectInt/spawn-area overloads).
- `TacticalState.Get()` static; `TacticalState.EndTurn()`.
- `Map.GetTile(int x, int z) -> Tile` (plus `GetTile(int,int, ref Tile&)`, `GetTile(Vector2Int)`); `Map.GetBaseTile(int x, int z) -> BaseTile`; `IsValidTile(int,int)`.
- `Actor.MoveTo(Tile, ref MovementAction&, MovementFlags=opt)`; `Die(bool=opt quiet)` (and `ScatterAndDie(...)`); `HasLineOfSightTo(Entity, bool hasAlreadyBeenDetected, Tile=opt, Tile=opt)`; `CalculateTilesInMovementRange() -> Task<...>`.
- `EntityProperties.GetVision()/GetDetection()/GetConcealment()`.
- `BaseFaction.GetFactionType()/GetActors()` (namespace `Il2CppMenace.Tactical.AI`).
- `Skill.GetHitchance(Tile from, Tile target, EntityProperties=opt, EntityProperties=opt, bool=opt includeDropoff, Entity=opt overrideTarget, bool=opt forImmediateUse) -> HitChance`; `Skill.Use(Tile, UsageParameter=opt)`.
- `StrategyState.Get()` static, with public typed accessors `Roster`, `Operations`, `StoryFactions`, `BlackMarket`, `ShipUpgrades`, `Events`, `Squaddies`.
- `Roster.HireLeader(UnitLeaderTemplate)` and `TryDismissLeader(BaseUnitLeader)` — both public.

Corrections to the candidate plan:
1. `NextRound()` / `NextFaction()` are **public** on `TacticalManager` — no reflection path needed.
2. `FindPath` lives on `PathfindingProcess`, not the manager: `PathfindingManager.Get()` → `RequestProcess() -> PathfindingProcess` → `process.FindPath(Tile start, Tile dest, Actor, List<Vector3> result, Direction startingOrientation, int=opt apAvailable, bool=opt allowMoveThroughActors)` → `manager.ReturnProcess(process)`. The result list is **world-space `Vector3` waypoints**, not tiles (caught at compile time against the proxy).
3. `GetHitchance` needs only the two tile args; the rest are optional — minimal call is `skill.GetHitchance(fromTile, targetTile)`. Returns the value-type `Il2CppMenace.Tactical.Skills.HitChance`.
4. `TrySpawnUnit`'s unit parameter binds as **`out`** in C# (the metadata byref reads as out, not ref); the simple overload is `TrySpawnUnit(FactionType, EntityTemplate, Tile, out Actor)`.
5. The map type is `Il2CppMenace.Tactical.Map` (there is no `TacticalMap`); `GetMap()` returns it. Tiles carry `GetX()/GetZ()`; `Actor.GetTile()`, `GetHitpoints()`, `GetCurrentProperties()`, `GetSkills()`, `Entity.GetTemplate()/GetFaction()` all confirmed.
6. Strategy roster/operations/market/upgrades acquisition is via public typed properties on `StrategyState`, confirming the anti-pattern call: no offset reads needed for manager acquisition.
7. Hire/dismiss/leader ops are public typed methods, not non-public — no reflection needed.

The live spike runs as a re-runnable loader diagnostic (`src/Jiangyu.Loader/Diagnostics/VerbProbe/VerbSurfaceProbe.cs`), gated by the `verbs` toggle in the `<UserData>/jiangyu-flags` file, mirroring the injection gate. Read-only probes (manager+map acquisition, tile round-trip, faction/actor enumeration, EntityProperties, LOS, hit-chance, path query, movement-range Task inspection) run on that toggle; the state-mutating probes (spawn a unit from the active actor's template, then despawn via `Die`) are opt-in behind a further `verbs-spawn` toggle. Each writes a timestamped JSON report beside the other inspector dumps. The movement-range probe deliberately does not block on `Task.Result` — it records `IsCompleted` instead — to answer the synchronous-vs-async question without risking a main-thread deadlock.

## Live verification (pass 2: complete)

Ran on game `v0.7.4+18448`, 2026-06-04, one Tactical mission (active actor at tile 15,17; 10 faction slots, 23 actors: Player 3, Pirates 20). **All nine probes passed**, promoted to [`../verified/tactical-game-api-verbs.md`](../verified/tactical-game-api-verbs.md). Highlights and the answers to the open runtime questions:

- **Spawn anchor works end to end.** `TrySpawnUnit(faction, activeActorTemplate, freeTile, out unit)` returned a live 50-HP Player unit on a free adjacent tile; `Die(true)` removed it (HP → 0).
- **Map/tile, faction enumeration, EntityProperties, LOS, path query** all returned live data. `GetFactions()` returns all 10 faction slots including empty ones (empty → empty list, not null). The path query returned 3 world-space `Vector3` waypoints.
- **Movement-range Task was already completed on call** (`IsCompleted` true) from the active-actor main-thread frame, so `.Result` is safe there — treated as context-dependent until exercised off that frame.
- **Two refinements** (call proven, value extraction pending): `HitChance.ToString()` only yields the type name — read `HitChance.FinalValue` (Single) for the hit value. `Die` side effects beyond unit removal are uncharacterised.

Remaining before verbs ship: characterise spawn occupancy/faction-validity rules and `Die`'s behavioural surface; then build the loader-provided game-bound companion exposing these as typed verbs.

Open for the live spike (pass 2, not answerable from metadata):
- Safe-timing window for each singleton (when non-null across scene loads).
- Whether blocking on `CalculateTilesInMovementRange().Result` is safe on this stack or must be awaited on a specific thread.
- `Die` / `ScatterAndDie` behaviour: do they fire on-death hooks, drop loot, count toward objectives.
- How to obtain a `MovementAction` for the `ref` param — its constructor is `protected`.
- Tile-occupancy precondition and faction validity for `TrySpawnUnit`.
- `Actor` skill-container access path for `Use`/add/remove.

## Triage categories

- **Directly callable** — one typed `Il2CppMenace.*` proxy call, or a short chain. A `code/` mod already makes these against the generated proxies. The deliverable is a docs page, not API surface.
- **Candidate sequence to verify** — non-obvious call ordering: which singleton, out/ref params, request/return bracketing, occupancy preconditions, mid-mission safety, default arg values. The reverse-engineered knowledge worth owning. Each is a recon target: spike, verify, then ship a thin typed verb.
- **Anti-pattern** — a shape to avoid: a parallel ledger that drifts from the game, or a raw-offset field poke that breaks silently on game update. The replacement is our injection model or a supplement-resolved typed access, never hand-counted offsets.

## Shared prerequisites (verify first)

Everything tactical-spatial and strategy-imperative bottlenecks through a tiny set of acquisition primitives. These are the first recon targets because every candidate verb depends on them.

- **Manager singletons**: `TacticalManager.Get()`, `States.StrategyState.Get()`, `PathfindingManager.Get()`, `TacticalState.Get()` — all confirmed public statics (each also exposes `s_Singleton`). `OperationsManager`/`EventManager` reached via `StrategyState` properties. Accessors settled; only the safe-timing window (when is each non-null across scene loads) remains for the live spike.
- **Map + tile handle**: `TacticalManager.Get().GetMap()` → `map.GetTile(x,z)` / `map.GetBaseTile(x,z)`. The map handle is the prerequisite for every tile query and spatial verb. Reach tiles through the typed `Map.GetTile` method, not array-layout offset reads.
- **Template lookup**: covered by our existing `DataTemplateLoader.GetAll<T>()` plus the asset catalogue. No new query layer.
- **Supplement-restored fields**: some live reads land on fields the Il2CppInterop proxy strips or makes awkward. Those are exactly what the `il2cpp-metadata.json` supplement exists to restore; route such reads through the catalogue rather than chasing offsets.

## Candidate sequences to verify

Spawn / lifecycle:
- **SpawnUnit** (anchor verb): `Templates.TryGet(templateId)` → `GetMap().GetBaseTile(x,z)` → tile-occupancy check → `TacticalManager.Get().TrySpawnUnit(faction, template, tile, out actor)`. The `out actor` shape and the occupancy precondition are the non-obvious parts. Exercises template lookup + tile handle + spawn in one slice.
- **DestroyEntity**: `Actor.Die(immediate)` with an alive precondition. Verify `Die` semantics (on-death hooks, loot, objective counting) before exposing.

Movement / pathing:
- **MoveTo**: construct a `MovementAction`, call `actor.MoveTo(tile, ref action, MovementFlags)`. The `ref` param + action construction is the catch. Teleport is the same call with `MovementFlags.ForceTeleport`.
- **GetMovementRange**: `actor.CalculateTilesInMovementRange()` returns a `Task<IEnumerable<Tile>>`. Verify whether blocking on a game `Task` is safe on our stack or whether it must be awaited on the right thread — likely footgun.
- **FindPath**: `PathfindingManager.Get()` → `RequestProcess()` (returns a `PathfindingProcess`) → `process.FindPath(start, dest, actor, resultList, startingOrientation, apAvailable, allowMoveThroughActors)` → `manager.ReturnProcess(process)`. The request/return bracketing is the catch; the spike must confirm the process is single-use and what an empty result list signals.

Combat / skills:
- **Hit-chance simulation**: `skill.GetHitchance(fromTile, targetTile)` (the remaining five params are optional). Returns a `HitChance`. The spike confirms the struct's fields and whether the optional `overrideTargetEntity` is needed for a unit-vs-unit query.
- **Use a skill**: resolve skill on actor (skill container → match by `GetID()`) → `skill.Use(targetTile, null)`. Verify the skill-container access is reachable typed.
- **Add / remove skill**: `SkillContainer.Add(SkillTemplate)` / `RemoveSkillByIndex(i)`. Verify container is reachable typed.

Vision / LOS:
- **LOS**: `tile.HasLineOfSightTo(targetTile, flags)` and `actor.HasLineOfSightTo(target, false, null, null)`. Arg defaults non-obvious.
- **Computed vision/detection/concealment**: `entity.GetCurrentProperties()` → `EntityProperties.GetVision()/GetDetection()/GetConcealment()`. Partially verified already — this is the same `EntityProperties` pipeline the injection work hooked; cross-reference `docs/research/verified/entityproperties-contract.md`.

Turn / mission control:
- **End turn / finish**: `TacticalState.EndTurn()`; `TacticalManager.Finish(TacticalFinishReason)`. Which type owns each is the catch (state vs manager).
- **NextRound / NextFaction**: public on `TacticalManager`. Spike only needs to confirm they are safe to drive externally mid-turn and what side effects fire.
- **Enumerate actors**: `TacticalManager.Get().GetFactions()` → per-faction `GetFactionType()` + `GetActors()`, or `GetFaction(FactionType)` for a single faction. The faction→actors traversal is the reusable primitive behind list/count.

Strategy:
- **Hire / dismiss leader**: `StrategyState.Get().Roster` → `HireLeader(template)` / `TryDismissLeader(leader)`. Both public; spike confirms cost/availability side effects.
- **Start / end operation, fire event**: `StrategyState.Get().Operations` → `StartOperation(...)` / `EndCurrentOperation(...)`; `StrategyState.Get().Events` → fire event. Signatures on `OperationsManager`/`EventManager` not yet metadata-checked — confirm in the next static pass before spiking.
- **Stock black-market item**: `Templates.TryGet(id)` → `template.CreateItem()` → `blackMarket.AddItem(item)`. The `CreateItem`→`AddItem` pairing is the sequence.
- **Trigger / apply emotion**: construct a `PseudoRandom`, then `emotionalStates.TriggerEmotion(...)` / `TryApplyEmotionalState(template, ...)`. The required `PseudoRandom` construction is the non-obvious bit; emotion removal candidate is a non-public `RemoveState()`.
- **Faction trust / status / upgrade**: `StrategyState.Get().StoryFactions.GetFaction(type)` → `ChangeTrust(delta)` / `SetStatus(status)` / `UnlockUpgrade(template)`. The faction acquisition is the shared prereq; the mutators are typed (borderline directly-callable).
- **Mission objectives**: `mission.Objectives` → `objectiveManager.GetObjectives()` → `obj.ForceComplete()`. Traversal + `ForceComplete`.

## Directly callable — docs, not API

A mod calls these on the typed proxy directly. Families, not every method:

- **Tile predicates**: `IsBlocked`, `HasActor`, `IsEmpty`, `IsValidMovementDestination`, `CanBeEntered`, `GetCover`, `GetNeighbor`/`GetAllNeighbors`, `GetDistanceTo`/`GetManhattanDistanceTo`, `GetDirectionTo`, `IsVisibleToPlayer`/`IsVisibleToFaction`, `GetActor`. Each is one `Tile.Method()` after the map handle.
- **Tactical state getters**: `GetRound`, `GetActiveFactionID`, `IsPaused`/`SetPaused`, `IsMissionRunning`, `IsAnyPlayerUnitAlive`/`IsAnyAIUnitAlive`, `GetActorCount`-based counts, `GetActiveActor`/`SetActiveActor`.
- **Vehicle**: `IsVehicle`, `HealAndClearDamageEffects`, `SetHitpointsPct`, `SetArmorDurabilityPct`.
- **Strategy read getters**: leader/faction/operation/perk/market enumeration — field reads or short managed-method chains. Caveat: any field that does not read cleanly off the typed proxy graduates to needing the metadata supplement (not to a verb).

These justify a "reading and driving the live game from a behaviour mod" docs page that shows the manager-acquisition prereqs once, then lets the modder call the proxy.

## Anti-patterns to avoid

- **Parallel ledgers**: a side dictionary of stat modifiers / tile overrides / visibility overrides ticked by a mod-installed hook, invisible to the game. Drifts from real behaviour. Replace temporary stat modifiers with the game's own `EntityProperties` pipeline via an injected `SkillEventHandler` or the vanilla `ChangeProperty` effect — proven end-to-end (`docs/research/verified/jiangyutype-injection.md`).
- **Raw-offset field pokes**: writing HP / morale / suppression / flags by hand-counted byte offset bypasses death handling, armour, on-death hooks, and breaks silently on game update. Damage must enter through the game's skill/damage path; flag and stat reads/writes go through typed members or supplement-restored fields.
- **Runtime tile mutation by offset**: defer entirely unless a real consumer appears; if it does, it needs supplement-resolved field handles and a real revert path.
- **AI-internal steering**: poking the live AI graph (behaviour scores, morale-as-threat) is a deep research target, not an early verb.

## Synthesis

The genuine asset is the ~15-18 candidate sequences above, and they cluster on a handful of shared prerequisites (manager acquisition + map/tile handle + template lookup, the last already covered). The verb surface stays small — a curated dozen-ish, grown the way the hook surface was — and most of the work is verifying a few call sequences, not writing breadth.

Recommended order: (1) verify the shared prerequisites (manager singletons, map/tile handle, safe-timing) against game metadata then live; (2) land SpawnUnit as the anchor verb end-to-end; (3) prove the "route through the game, don't ledger" principle by implementing a temporary stat modifier on the injected-handler path; (4) grow the rest on demand. Game-bound verbs live in a loader-provided companion that returns real `Il2CppMenace` types, keeping `Jiangyu.Sdk` game-agnostic.
