# Tactical Game-API Verbs

Status: **verified** (Jiangyu live-game run, game `v0.7.4+18448`, 2026-06-04).

Reproduced by the re-runnable `VerbSurfaceProbe` diagnostic
(`src/Jiangyu.Loader/Diagnostics/VerbProbe/`, gated by the `verbs` toggle in the
`<UserData>/jiangyu-flags` file, with the state-mutating spawns behind a further `verbs-spawn` toggle).
Draws from [`../investigations/2026-06-04-game-api-verb-surface.md`](../investigations/2026-06-04-game-api-verb-surface.md).
All nine probes passed in one live Tactical mission (active actor at tile 15,17; 10 faction slots, 23 actors).

## Contract

These typed call sequences against the live `Il2CppMenace.*` proxy are the proven
substrate for an imperative game-API verb surface. Each is reached from a manager
singleton; no raw offsets and no reflection are involved.

Acquisition (all public statics, non-null in a live Tactical scene):
- `TacticalManager.Get()` → `GetMap() : Map`.
- `TacticalState.Get()`; `PathfindingManager.Get()`.
- The active actor is `TacticalManager.Get().m_ActiveActor`.

Reads (verified returning live data):
- **Tile handle.** `Map.GetTile(int x, int z) : Tile` round-trips an actor's own
  tile by coordinate; `Map.GetBaseTile(int x, int z) : BaseTile` non-null at the
  same coordinate. `Tile.GetX()/GetZ()`, `HasActor()`, `IsBlocked()`.
- **Actors per faction.** `TacticalManager.GetFactions()` returns all 10 faction
  slots (including empty ones); per slot `BaseFaction.GetFactionType()` and
  `GetActors()`. Empty factions return an empty list, not null.
- **Computed properties.** `Actor.GetCurrentProperties() : EntityProperties` →
  `GetVision()/GetDetection()/GetConcealment()`. Same pipeline as
  [`jiangyutype-injection.md`](jiangyutype-injection.md) hooks.
- **Line of sight.** `Tile.HasLineOfSightTo(Tile other)` (optional
  `LineOfSightFlags`, namespace `Il2CppTactical`).
- **Hit chance.** `Skill.GetHitchance(Tile from, Tile target) : HitChance` (the
  other five params are optional). The result is a value-type struct; read
  `HitChance.FinalValue` (Single) for the hit value — `ToString()` only yields
  the type name. Other fields: `Accuracy`, `CoverMult`, `DefenseMult`,
  `AlwaysHits`, `IncludeDropoff`, `AccuracyDropoff`.
- **Path query.** `PathfindingManager.Get()` → `RequestProcess() :
  PathfindingProcess` → `process.FindPath(Tile from, Tile dest, Actor,
  List<Vector3> result, Direction startingOrientation, int=opt apAvailable,
  bool=opt allowMoveThroughActors) : bool` → `manager.ReturnProcess(process)`.
  The result list holds **world-space `Vector3` waypoints**, not tiles. The
  request/return bracket must wrap the call.
- **Movement range.** `Actor.CalculateTilesInMovementRange()` returns an
  already-completed Task when called from a main-thread frame in a live mission
  (`IsCompleted` true), so reading `.Result` is safe in that context. Treated as
  context-dependent until exercised off the active-actor frame.

Mutation (verified end to end, opt-in):
- **Spawn.** `TacticalManager.TrySpawnUnit(FactionType, EntityTemplate, Tile, out
  Actor) : bool` spawns onto a tile and returns the live actor (a unit spawned at
  50 HP from the active actor's own template). The unit param binds as `out` in C#.
  - **Destination tiles are NOT validated.** A spawn onto an already-occupied tile
    (the active actor's own tile) and onto a blocked tile (at 31,3) both *succeeded*
    and returned a live actor — `TrySpawnUnit` rejects neither occupied nor blocked
    destinations. The `Units.Spawn` verb refuses both anyway, as Jiangyu safety
    defaults (a stacked or wall-embedded unit is almost always a mod bug); a mod that
    wants either calls `TrySpawnUnit` directly. Whether the unit lands on the exact
    requested tile or the game relocates it is not separately verified.
  - **Faction is unconstrained.** A spawn for a faction other than the active
    actor's (an enemy `Pirates` unit) succeeded and the spawned unit carried the
    requested faction. Any `FactionType` is a valid spawn faction.
- **Despawn.** `Actor.Die(bool quiet = false)` is a clean removal: the unit leaves
  its faction roster (count 4 → 3), its tile is freed (`HasActor` true → false), and
  its HP goes to 0. `ScatterAndDie(...)` also exists. Deeper on-death triggers (loot,
  conversations, objective counting) are not separately verified.

## Required practices

- **Reach tiles through `Map.GetTile`, never an array-layout offset read.** The
  typed method is exact and update-stable.
- **Bracket every path query** with `RequestProcess` / `ReturnProcess`; the
  process is the unit of work, `FindPath` lives on it (not the manager).
- **Do not assume the movement-range Task is synchronous off the active-actor
  frame.** Verified completed-on-call only in the active-actor main-thread
  context; inspect `IsCompleted` before reading `.Result` elsewhere.
- **A verb that changes battlefield state belongs behind the game's own systems**
  (spawn/`Die` here), not a side ledger. Acquisition is via typed manager
  singletons and `StrategyState` typed properties — no offsets.
- **Game objects are `Il2CppSystem.Object`-rooted, not `UnityEngine.Object`.** The
  `Entity` base chain is `Entity -> Il2CppSystem.Object -> Il2CppObjectBase`, with no
  `UnityEngine.Object` in it, so actors are not MonoBehaviours and the Unity
  destroyed-as-null check (`m_CachedPtr` / `op_Implicit`) does not apply. The usable
  liveness signal for a cached wrapper (e.g. a stashed hook payload) is
  `Il2CppObjectBase.WasCollected` plus a non-zero `Pointer` — it reports an object
  Il2CppInterop has seen collected but cannot prove one freed outside that tracking is
  dead, so a payload held across frames should be re-resolved through a live lookup
  rather than trusted.

## Open follow-ups

- **Deeper `Die` triggers** (loot drops, death conversations, objective counting)
  are not separately verified; only roster removal, tile freeing, and HP → 0 are.
- **Exact spawn placement** (does the unit land on the requested occupied/blocked
  tile, or does the game relocate it) is not separately verified.
