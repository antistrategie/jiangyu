# Combat Roll Determinism Spike

Date: 2026-07-20, mechanism resolved 2026-07-21.

Status: complete. Follow-up to the determinism spike
([2026-07-19-determinism-spike.md](2026-07-19-determinism-spike.md)): per-shot
combat-roll equality on a fixed target, across fresh processes.

## Fixture

`DETERMINISMTEST` save (loaded by name via `nav` `load`, never `load-latest`, the
game autosaves over latest on operation start), operation 2 "Thwart Invasion",
mission 0, `Mission.GetSeed=840848`, game v0.7.10+19931. Shooters
`player_squad.voymastina` (AK-15) and `player_squad.soppo` (M4) at ~12 tiles from
`enemy.pirate_outcasts` at (23,39). All shots inside player turn one: no AI
activation, no spawns, template-addressed actors.

## Verdict

**Combat resolution rolls live on `TacticalManager.s_Random`, a seeded
`Il2CppMenace.Tools.PseudoRandom` created in `TacticalManager.OnInit`, and the
rolls themselves are deterministic.** A single traced shot consumes exactly the
same 117 draws with identical values in every fresh process (three independent
process pairs). With element aim delays pinned, a one-shot run is sim-identical
end to end: every actor line equal at every barrier, only the
`UnityEngine.Random` state fragment differs.

The desync is **consumption-order drift on that shared stream**. Wall-clock and
frame-scheduled consumers draw from `s_Random` alongside command resolution, so
two processes interleave the same stream differently and later consumers receive
shifted values. Direct evidence from a traced two-process `rolls-b` pair
(2026-07-21): the first four shots match, then at draw index 318 process B's
recurring `Range(2, 7)` timer fires one frame earlier than process A's
(`WaitedFrames` 325 vs 326) and slides ahead of the shot block. The shot's own
rolls stay value-identical in both processes, the timer draw does not
(4.1188807 vs 6.1406584), and from there the call mixes diverge (percent rolls
272 vs 269, timer firings 26 vs 22, impact-VFX triples 9 vs 4). The barrier
deltas at that step: truck armour 179 vs 175, target suppression 0.6168 vs
0.7289, shooter morale one 1/3 tick apart.

The shared stream drifts even in a quiet mission: an idle 20-second trace shows
`s_Random` consumed by a `Range(2, 7)` reschedule timer (~every 4.3 s),
`Range(0.15, 0.4)` polls and occasional `Next(1, 101)` + `Range(6, 30)` pairs.
Stream positions across two processes therefore diverge with wall-clock time
regardless of commands.

Consequences for command replication (phase 3):

- **Barrier-time re-seeding is insufficient**: it synchronises state but not
  interleaving, and the stream drifts between barriers even with no commands.
- **Move the cosmetic and wall-clock consumers off the sim stream.** The
  prior-art survey ([2026-07-21-multiplayer-prior-art.md](2026-07-21-multiplayer-prior-art.md))
  found every retrofit isolates non-simulation randomness (RimWorld's
  `PushState`/`PopState` scopes, AoE's save-restore, Factorio's separate render
  PRNG) so it cannot reorder the sim stream, and refutes carrying consumed rolls
  in the command as the standard pattern. Applied here: Harmony-patch the
  `s_Random` wall-clock and VFX draw sites (the recurring `Range(2, 7)` timer and
  the impact-frame triples) to draw from a private `PseudoRandom`, leaving the sim
  stream consumed only by command-driven combat and kept in order by host-ordered
  replay.
- **AI turns add a second source**: `Tactical.AI.Agent` constructs its
  per-agent `PseudoRandom` unseeded, so AI decisions are process-varying
  independent of stream ordering. Host-driven AI (already mandated by the
  2026-07-19 spike) covers this.

## The stream map

Every live `PseudoRandom` stream in a traced mission, named by matching trace
owner pointers against the `rngtrace` `owners` op (holder inventory from a
`MetadataLoadContext` scan of the interop assemblies):

| Stream | Seeding | Draws in one traced shot | Role |
| --- | --- | --- | --- |
| `TacticalManager.s_Random` | seeded, `.ctor(int)` in `OnInit` | 117 | combat resolution (to-hit `Next(1,101)`, scatter `Range(-3.5,3.5)`, per-pellet `Range(0,0.5)`, morale wobble `Range(-1.5,1.5)`) plus wall-clock timers and impact VFX triples (`0-359` angle, `0.9-1.1` scale, pick-of-2) |
| `TacticalBarksManager.m_Random` | unseeded, `BaseConversationManager..ctor` | ~13,100 | bark candidate list shuffling (descending `Next(max)` Fisher-Yates storms during action, silent when idle) |
| `GameConditionVars.RANDOM` | assigned statically | 38 per death | the data-driven condition system's `RANDOM` variable, `Next(100)` percent checks evaluated on events |
| `RandomAnimationPicker.RNG` | static | ~25 | animation variation picks (`DrawWeightedRandom` pairs) |
| `ExpandRetract.m_Random` (8 instances, `HALO_STAR_001` props) | unseeded in `Start`, identical time seeds within a process | 20 each | prop wobble `Range(0.15, 0.35)` every ~30 frames |
| `RandomBoredBehaviourEx.RNG` | static | small | idle behaviour picks |
| `Ragdoll.RANDOM` | unseeded, lazy in `GetRandom` | 0 until a death | death physics |
| `Tactical.AI.Agent.m_Random` | **unseeded**, per agent in `.ctor` | 0 on player turn | AI decisions |

Only `s_Random` (shared with timing-coupled consumers) and the unseeded AI agent
streams are sim-relevant. Everything else is cosmetic.

The global `UnityEngine.Random` stream carries no combat resolution at all: 63
census callers, all idle/gore/audio/VFX/terrain, plus the aim/movement delay
helper `Vector2Extensions.RandomPositiveBetweenXY` (callers `Element.SetAiming`,
`ElementAnimator.GetInitialAimDelay`, `ElementAnimator.GetRandomMovementDelay`,
`Gore.Launch`). Its state fragment in a barrier hash never matches across
processes, which is why the sim comparer strips it.

## Instruments

- `rngtrace` dev command (`RngTraceProbe.cs`), ops `start {cap}` / `stop` /
  `dump {tail}` / `status` / `census` / `pin {enabled}` / `owners`. Harmony
  postfixes on `UnityEngine.Random` and `PseudoRandom` record every draw
  (sequence, frame, api, arguments, result, owning instance pointer). Two
  processes' traces diff per owner to the first divergent draw. A managed hook
  cannot see its native caller, so attribution is by stream identity plus the
  `owners` pointer map.
- `owners` resolves the known holders (statics like `TacticalManager.s_Random`,
  `Ragdoll.RANDOM`, `GameConditionVars.RANDOM`, the barks manager via
  `TacticalState.Get().GetBarks()`, scene `ExpandRetract` instances) to live
  pointers, turning anonymous trace streams into named ones.
- `census` reads the xref table MelonLoader precomputes (`XrefScanner.UsedBy`,
  zero unresolved, no gameplay needed) for every RNG entry point **including
  both `PseudoRandom` constructors**. `.ctor(int)` callers are the seeded
  (mission/operation/mapgen) family. `.ctor()` callers create process-varying
  streams: `BaseConversationManager..ctor`, `Tactical.AI.Agent..ctor`,
  `ExpandRetract.Start`, `Ragdoll`, emotional-state and reward effects on the
  strategy layer.
- Holder inventory offline: `System.Reflection.MetadataLoadContext` over
  `<game>/MelonLoader/Il2CppAssemblies` (plus `MelonLoader/net6` for
  `Il2CppInterop.Runtime` refs, core assembly `System.Private.CoreLib`). Fields
  surface as interop properties, so scan properties and method signatures, not
  managed fields.

## Runs

- `rolls-b` (2026-07-20, two fresh-process records of 8 scripted shots):
  baselines sim-identical, first resolved shot diverges (suppression 0.1254 vs
  0.2368). Unpinned aim delays shift shot timing by multiple frames, so the
  interleave permutes early and widely.
- `rolls-c` (2026-07-20, `seedrng` before every shot): the seed step's own
  barrier matches on the full hash, the following shot still diverges. Seeding
  cannot fix ordering.
- `oneshot` traced pair (2026-07-21, pin on): **sim-identical end to end**,
  117/117 identical `s_Random` draws, divergence confined to the global-stream
  state fragment.
- `rolls-b` traced pair (2026-07-21, pin on): first divergence at `s_Random`
  index 318, the `Range(2, 7)` timer against the shot block, one frame of
  scheduling wobble. Steps 0-3 sim-match, step 4 onward carries the shifted
  values.

## Harness notes

- Faction actor lists are not positionally stable once actors act, so scripts
  address actors as `{faction, template}`. Index addressing is only safe for a
  first action.
- The journal `WaitedFrames` field separates resolved shots (~326 frames) from
  silently refused ones (full timeout), which is how the per-turn fire limit
  was spotted. Its 325-vs-326 wobble on resolved shots is the same one-frame
  scheduling noise that permutes the stream.
- `compare`'s `baselineMatch` uses sim semantics (rng fragment stripped),
  matching the per-step verdict.
- The interop member scan silently returns nothing when the
  `MetadataLoadContext` core assembly is wrong (`Il2Cppmscorlib` shadows the
  BCL but does not define `System.Void`) or when `Il2CppInterop.Runtime` is
  missing from the resolver. Both failures swallow into empty member lists
  rather than errors.
