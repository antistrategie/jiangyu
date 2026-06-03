# JiangyuType Injection And SkillEventHandler Dispatch Spike

Date: 2026-06-02 (documenting spike runs from 2026-06-01)

## Goal

Establish whether a managed, code-defined type can be injected into the game's
IL2CPP type system as a polymorphic subtype of a game base class, and whether
the game's own native dispatch will call an overridden virtual on that injected
instance. This is the load-bearing assumption under the code-mod SDK design
(`[JiangyuType]`). If it does not hold at breadth and across updates, the
architecture changes.

## Context

The SDK design (Obsidian `Projects/Jiangyu/Code SDK Sketch.md`) makes
`[JiangyuType]` the primary per-clone mechanism: a modder writes a subtype of a
game base (`SkillEventHandler`, `TacticalCondition`, an action or effect type),
slots it via KDL `type=`, and the game's existing dispatch calls it. The worked
example is the Voymastina last-aegis perk, an `OnBeforeDamageReceived` override
that clamps lethal damage. None of that works unless injection plus native
dispatch hold.

## Environment

- Game: MENACE, Unity 6000.0.72f1, IL2CPP.
- Loader: MelonLoader 0.7.3, Il2CppInterop 1.5.1 (net6 runtime).
- Game assembly: `Assembly-CSharp.dll`, managed-proxy namespace root
  `Il2CppMenace`. Not `Il2CppMenace.dll`.
- Spike host: a throwaway `ClassInjectionSpike.cs` under the loader's
  `Diagnostics/`, driven from `JiangyuMod`, run against the live game then
  reverted via `git checkout`.

## Findings

### 1. ClassInjector registration and derived construction works

Claim: a managed type deriving from a game IL2CPP class can be registered and
instantiated such that it is managed-assignable to its base.
Source: live-game spike run (Tier 1).
Game version: as above.
Evidence category: reproducible in-game behaviour from an isolated test.
Validation method: `ClassInjector.RegisterTypeInIl2Cpp<JiangyuSpikeHandler>()`
with a `DerivedConstructorPointer<T>()` plus `DerivedConstructorBody(this)`
constructor, then construct and check assignability to `SkillEventHandler`.
Result: registration succeeds, the instance is assignable to the base.
Confidence: high for the single type spiked.
Follow-up: breadth across more than one root (see gated items).

### 2. Native vtable dispatch reaches the injected override (Tier 1)

Claim: an overridden game virtual on an injected instance is invoked through
the native vtable.
Source: live-game spike run (Tier 1).
Evidence category: reproducible in-game behaviour from an isolated test.
Validation method: override `OnTurnStart` on `JiangyuSpikeHandler :
SkillEventHandler`, invoke through the base path, observe the override firing.
Result: the override fires.
Confidence: high for this virtual.

### 3. The game's own dispatch reaches the injected handler in a live mission (Tier 2)

Claim: when an injected handler is attached to a live skill, the game's own
event fan-out calls the override, the same way it calls stock handlers.
Source: live-game spike run inside a Tactical mission (Tier 2).
Evidence category: reproducible in-game behaviour.
Validation method: reach a live skill via
`TacticalManager.Get().m_ActiveActor.GetSkills().QueryActives()`, attach the
injected handler with `Skill.AddEventHandler`, invoke the game's own
`Skill.OnTurnStart()` fan-out, observe the override firing. Log line emitted:
`GAME DISPATCH REACHES INJECTED HANDLER`.
Result: the injected override fires from the game's own dispatch loop.
Confidence: high for this path.
Follow-up: this is the basis for the whole skill-event hook surface. It means
no per-moment Harmony patches are needed for skill events.

### 4. Il2CppInterop emits IL2CPP interfaces as classes (gotcha)

Claim: Il2CppInterop renders IL2CPP interfaces as classes and drops the
implements-relations, so a game type is not managed-assignable to its game
interface.
Source: spike compile and run.
Evidence category: IL2CPP metadata plus runtime behaviour.
Validation method: attempt to treat `TacticalCondition` as
`ITacticalCondition` across the boundary.
Result: not managed-assignable. Cross-boundary writes need an explicit
`.Cast<ITacticalCondition>()`.
Confidence: high.
Follow-up: the SDK construction and slotting code must cast at the boundary
rather than rely on the managed type system.

### 5. TacticalCondition override target is `IsTrue`, not `Evaluate`

Claim: the overridable decision method on `TacticalCondition` is
`IsTrue(Entity, Skill)`, not `Evaluate(TacticalContext)`.
Source: spike against the live type.
Evidence category: IL2CPP metadata plus runtime behaviour.
Result: `IsTrue` is the dispatch target.
Confidence: high.

## Caveats and gated items (not yet verified)

These are the open tests folded into the Phase 0 injection go/no-go. None are
proven by the runs above.

- **Odin serialisation survival (the remaining gate).** Whether an injected
  type written into a real Odin-routed slot survives a save/load round-trip and
  a fresh template-load with its fields intact. Not spiked. This is the single
  highest-risk item.
- **`AddEventHandler` append, not replace.** `AddEventHandler(h, 0)` left
  `m_EventHandlers` count at 1 to 1, meaning index 0 replaced the existing
  handler rather than appending. The real forwarder or attach path must append
  (correct index or insert) so it does not clobber a skill's existing handlers.
- **`DamageInfo` mutation honoured.** That a modifier handler's mutation to a
  passed `DamageInfo` is respected by the game is strongly implied by the stock
  `IgnoreDamage` handler (which absorbs damage exactly this way) but was not
  directly spiked. The Voymastina lethal veto depends on it.
- **Breadth of roots.** Only one handler type was injected. The gate requires
  at least three distinct roots (a `SkillEventHandler`, a `TacticalCondition`,
  and an action or effect type) injecting and dispatching cleanly.
- **Update-stability.** How an injected type fails when the game surface shifts
  (renamed or reordered) is untested. The gate requires it to fail loud and
  guarded, never silently and never as a hard crash, and to be caught by the
  structural self-check against `validation/template-structure-baseline.json`.

## The re-runnable diagnostic (replaces the reverted spike)

The throwaway spike is replaced by a committed, re-runnable diagnostic under
`src/Jiangyu.Loader/Diagnostics/InjectionGate/`:

- `GateTypes.cs` defines the three injected roots (`GateEventHandler :
  SkillEventHandler`, `GateCondition : TacticalCondition`, `GateHandlerTemplate
  : SkillEventHandlerTemplate`) and a latched registrar.
- `InjectionGateInspector.cs` runs the checks and writes a timestamped JSON
  report next to the other inspector dumps. Gated by a `jiangyu-gate.flag` file
  in `<UserData>`, so it is inert in a normal session.

It is wired into `JiangyuMod`: the structural phase at loader init, the live
phase retried on late Tactical frames until an active actor is present.

Checks, all four gate tests implemented and compiling:

- `inject.register`, `inject.assignable`, `bind.missingProbe`,
  `create.blankSkillTemplate` (structural, no mission).
- `dispatch.appendAndFire` (live): re-establishes append-not-replace and
  game-dispatch-reaches-injected-handler in one shot.
- `damage.clampHonoured` (live): drives a lethal `DamageInfo` through the
  target's own `Entity.OnDamageReceived(Entity, Skill, DamageInfo,
  EntityProperties)` with the clamp handler attached, and asserts the override
  saw it, clamped, and the target survived. The Voymastina mechanism end to end,
  the same path `IgnoreDamage` rides.
- `odin.serialisationSurvival`: two probes, no mission. A polymorphic
  type-binder round-trip (`DefaultSerializationBinder.BindToName` then
  `BindToType` on the injected type, a null resolve means Odin would drop our
  element on deserialise) plus in-memory slot retention in an Odin-typed
  `List<SkillEventHandlerTemplate>`.

### Compile-time finding (new, 2026-06-02)

Claim: the `[JiangyuType]` typed-override surface is expressible as C# against
the live Il2CppInterop proxies.
Source: `dotnet build` of `Jiangyu.Loader` against the live `Assembly-CSharp`
proxy assembly.
Evidence category: IL2CPP metadata (the proxy assembly's typed surface).
Validation method: compile injected subclasses overriding
`SkillEventHandler.OnTurnStart()`,
`SkillEventHandler.OnBeforeDamageReceived(Skill, Entity, DamageInfo,
EntityProperties)`, `TacticalCondition.IsTrue(Entity, Skill)`, and
`SkillEventHandlerTemplate.Create()`.
Result: compiles clean. The override signatures resolve and match. So do the
driven-damage path (`Entity.OnDamageReceived(Entity, Skill, DamageInfo,
EntityProperties)`, `new DamageInfo`, `GetCurrentProperties`, `GetHitpoints`)
and the Odin binder API (`DefaultSerializationBinder.BindToName`/`BindToType`).
This does not prove runtime registration, dispatch, or Odin survival (all
exercised by the harness at launch), only that the surface is typed-correct.
Confidence: high for the compile claim, scoped narrowly.

### Odin scope, clarified (2026-06-02)

The runtime-reinject approach narrows what "Odin survival" must mean. The binary
`SaveState` stores templates by `m_ID` and re-resolves them from the live
registry each session, so a code-defined template's field values are rebuilt by
the loader on every launch and never transit Odin or the save. Odin asset
serialisation of an injected type therefore only matters for a
bake-into-`resources.assets` distribution, not for runtime registration. The
gate's Odin check is consequently scoped to the genuine residual risk: whether
Odin's polymorphic type binder can resolve the injected type by name on
deserialise (the `BindToType` probe). Full save/load survival for the reinject
path is registry re-resolution by `m_ID`, a separate launch sub-step against the
strategy layer, covered by the save-format work rather than by Odin.

## Run 1 observations (2026-06-02, game v0.7.4+18448)

First live run of the harness. Findings, separating what holds from what needs work:

Holds:
- All three roots register in the IL2CPP domain, including the
  ScriptableObject-rooted `GateHandlerTemplate` (Il2CppInterop logs "Registered
  mono type ... in il2cpp domain" for each).
- `GateEventHandler` and `GateCondition` construct via the injected ctor and are
  runtime-assignable to their game bases (`inject.assignable` true for both).
- A blank `SkillTemplate` allocates with an empty `m_ID` (`create` foundation).

Findings (two harness fixes, one architectural):
- `Skill.AddEventHandler(SkillEventHandler, int _index)` is an indexed write into
  a fixed-size `Il2CppReferenceArray`, not an append: `_index == length` throws
  `IndexOutOfRangeException`, `_index 0` clobbers the existing handler. Runtime
  attach must grow `m_EventHandlers` (build a new array of length+1). The natural
  SDK path avoids this entirely: handlers enter via the template's
  `EventHandlers` and `Create()` at skill construction, correctly sized. Fixed in
  the harness via an `AppendHandler` resize helper.
- An injected ScriptableObject-rooted type (`GateHandlerTemplate`) cannot be
  `new`-constructed: registration succeeds but `new` yields an invalid object
  (assignable false). Must use `ScriptableObject.CreateInstance`. Fixed.
- Odin's `DefaultSerializationBinder.BindToType("...GateHandlerTemplate,
  InjectedMonoTypes")` returns null: Odin cannot resolve an injected type by name
  on deserialise, so it would drop an injected polymorphic element. This confirms
  a bake-into-`resources.assets` distribution needs a custom binder, while the
  runtime-reinject path (which never deserialises through Odin) is unaffected.

Still unproven, pending a re-run with the fixes: `dispatch.appendAndFire` (game
dispatch reaches the injected handler) and `damage.clampHonoured`. Both errored
on the old fixed-array `AddEventHandler` before reaching the dispatch they were
meant to test.

## Run 2 observations (2026-06-02, game v0.7.4+18448)

With the two harness fixes from run 1 applied:

- **Structural phase now passes 5/5.** `inject.assignable` is true for all three
  roots (the `ScriptableObject.CreateInstance` fix landed the template root), and
  `odin.serialisationSurvival` passes on in-memory slot retention.
- **The game crashed on loading the Tactical mission, before the live phase
  ran** (the log ends at the `t60f` scene dump; the live phase fires at `t120f`).
  So dispatch and damage are still unproven.
- **Prime suspect, and a real harness bug:** `create.blankSkillTemplate`
  allocated a `SkillTemplate` via `Il2CppInstanceAllocator` (raw
  `il2cpp_object_new`). `SkillTemplate` is a ScriptableObject, and Unity logged
  "SkillTemplate must be instantiated using ScriptableObject.CreateInstance
  instead of new SkillTemplate" (Player.log). A raw-allocated ScriptableObject is
  malformed and a credible cause of the later native crash during scene
  processing. The repo's clone path never raw-allocates ScriptableObjects (it
  uses `Instantiate`/`CreateInstance`); the harness used the wrong allocator.
  Fixed to `ScriptableObject.CreateInstance`. This is also the correct
  construction path for the eventual `create` op.
- Not yet proven that this warning is the crash cause versus the loaded WOMENACE
  mod's heavy prefab/texture injection (Player.log showed ~5.9 GB texture
  memory). Isolation test pending: load the same mission with the gate flag off.

Harness hardening after the crash: each check is logged the instant it completes
(a native crash now leaves a per-check trail in Latest.log), and the damage
self-hit is opt-in behind a separate `jiangyu-gate-damage.flag` so the default
armed run proves dispatch without the riskiest operation.

## Run 3 observations (2026-06-02, game v0.7.4+18448)

With the blank-template fix and per-check incremental logging:

- **Structural phase passes 5/5 again, in both the init and the live run** (the
  blank-template `CreateInstance` fix held: no repeat of the prior crash there).
- **The crash moved to `dispatch.gameFansToInjected`**: the live run logged the
  five structural passes then died inside the dispatch check, which never logged
  a result. Player.log has no managed stack, so it is a native crash.
- **Prime suspect: a base-virtual call from an injected override.**
  `GateEventHandler.OnTurnStart` called `base.OnTurnStart()`. For an injected
  IL2CPP type a base-virtual call can re-dispatch to the override rather than the
  native base, recursing to a stack-overflow native crash with no managed trace.
  This fits: the structural checks never invoke the override, so they pass; the
  moment the game's `OnTurnStart` fan-out reaches the override it recurses. The
  prior session's spike fired cleanly, most likely because its override did not
  call base.

Fixes: the overrides no longer call base (the base bodies are no-ops). The
dispatch check now swaps in an array containing only the injected handler, calls
`Skill.OnTurnStart`, and restores the originals in a finally (no live-handler
collateral, skill left intact). The live phase also moved to a later, settled
frame. Unproven until the next run: `dispatch.gameFansToInjected`.

## Run 4 observations (2026-06-02, game v0.7.4+18448) — dispatch proven

No crash. The recursion fix held. Live run 6/7 green:

- `inject.register`, `inject.assignable` (3/3), `bind.missingProbe`,
  `create.blankSkillTemplate`, `odin.serialisationSurvival` (slot retention; the
  binder still returns null for the injected type, as expected): all PASS.
- **`dispatch.gameFansToInjected`: PASS, `overrideFired = true`.** The game's own
  `Skill.OnTurnStart` fan-out invoked the injected `GateEventHandler` override.
  This re-establishes the prior session's Tier-2 finding in the re-runnable
  diagnostic, on a recorded game version, controlled and isolated (only our
  handler in the array, originals restored). Per `VALIDATION.md` this is now
  reproducible evidence, promotable to `docs/research/verified/`.
- `damage.clampHonoured`: SKIPPED (opt-in behind `jiangyu-gate-damage.flag`).

Standing: injection, registration, runtime-assignability across three roots,
blank-template construction, Odin slot retention, the Odin-binder limitation, and
game-dispatch-into-injected-handlers are all proven on the live game. The only
remaining gate item is the explicit damage-clamp behaviour, which is strongly
implied by the proven dispatch plus the stock `IgnoreDamage` handler (which
absorbs damage through this exact `OnBeforeDamageReceived` path), and will be
proven in context when the real perk is built.

## Reproduction status

The diagnostic compiles and is deployed (Release, flag-gated and inert). Under
`VALIDATION.md` the runtime claims remain investigation-grade until the
diagnostic is run in the batched Phase 0 launch session and the report shows
the structural and live checks passing on a recorded game version. Promotion to
`docs/research/verified/` is blocked until then. The live phase needs an active
actor in a Tactical mission.

## Bonus observation

`TacticalManager` exposes plain C# events (`OnEntitySpawned`, `OnPlayerTurn`,
`OnActorActed`, and kill and damage delegates). These give a global-hook route
that needs neither injection nor Harmony, only a subscription. This is a
candidate provider for the `Subscribe<T>` global-hook surface, recorded here as
a hypothesis to confirm during the same launch.

## Provenance

Prior-session spike runs (2026-06-01). Related design and findings:
`Code SDK Sketch.md`, `Build Plan.md`. Supersedes the chat-only and
memory-only records of these runs.
