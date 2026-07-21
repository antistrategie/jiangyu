# Multiplayer Framework Design

Date: 2026-07-19

Status: draft for review. The determinism spike (section 8) is the tracked go/no-go
predecessor; nothing here should become production truth until it runs.

## Goal

Give mod authors a Jiangyu-owned multiplayer framework: session management, transport,
command replication, and arbitration, such that a mod can ship multiplayer game modes
without owning any networking code. The framework must serve the full spread of
multiplayer mod types, not only the first one we build.

A mod author should be able to write a PvP skirmish mod, a co-op tactical mod, or a
co-op campaign mod against the same primitives, differing only in arbitration policy
and game-mode logic.

## Non-goals

- Public matchmaking, ranked play, dedicated servers. Sessions are friends-only,
  host-client over Steam P2P.
- Anti-cheat. Peers are trusted to run the same mods (enforced by handshake), but no
  effort is spent defending against a deliberately hostile client.
- Cross-platform or non-Steam transports in v1. The transport sits behind an
  interface so a raw-UDP/direct-IP transport can be added later.
- More than two peers in v1. The design should not preclude N peers, but every
  milestone targets host + one client.

## Mod-type taxonomy

The framework exists to serve these shapes. The taxonomy drives the design: the
shared core is session + transport + command replication + state verification, and
each mod type is an arbitration policy layered on top.

### A. PvP tactical (remote faction)

A standard tactical mission where a remote peer controls what the AI would normally
control. The game already models the enemy side as a distinct type,
`Menace.Tactical.AI.AIFaction` (with `GetActiveAIFaction` on the tactical manager),
so the interception is clean: when an AI-owned faction has a decision point, the
framework waits for the peer's command instead of running local AI behaviours
(`Menace.Tactical.AI.Behaviors` + `Criterions`). Both peers run the same mission;
commands are the only traffic. This is the narrowest slice and the first milestone.

### B. Co-op tactical (shared side)

Two players on the same side against AI. Two sub-modes a mod may pick:

- **Faction-lock**: each player owns a distinct faction (or squad) and acts only on
  their own faction's turn. Trivially arbitrated; near-identical to type A.
- **Unit-grant**: both players share one faction's turn, each controlling an
  assigned subset of units, potentially acting simultaneously. This needs the single
  executor rule (section 6): every command is serialised through the host into one
  total order before execution, so simultaneous input can never fork the sim.

### C. Co-op campaign (shared strategy layer)

Both players share one campaign: strategy layer (roster, market, ship, operations
map via `StrategyState`) plus co-op tactical missions inside it. The strategy layer
has no natural turn barrier and a much wider interaction surface than tactical, so
it is host-authoritative with intent submission (section 6.3). Time advancement
(`StrategyState.AdvanceTime` exists as the single throttle) is gated on all peers
ready. Campaign events and conversations present choices as replicated commands;
who sees which dialogue is mod policy.

### D. Asymmetric / versus campaign

Two players running opposing campaigns that intersect (invasion-style). Out of
scope for the framework's first release; noted so the session and ownership models
do not assume both peers are always in the same mission.

### Cross-cutting

Spectator (a peer with no owned faction), disconnect/timeout with AI fallback, and
session persistence (an in-progress multiplayer mission or campaign saved and
resumed later) are framework responsibilities shared by all types.

## What the game already provides

Findings from recon against the installed game (`MelonLoader/Il2CppAssemblies/`,
game build current as of 2026-07-19). These are investigation-grade observations;
the ones the design depends on are marked for verification in the spike.

- **Steam networking is already in the process.** The game ships Steamworks.NET
  2025.162.1 (Steamworks SDK 1.62) and its Il2CppInterop wrapper
  (`Il2Cppcom.rlabrecque.steamworks.net.dll`) exposes the full surface:
  `ISteamNetworkingMessages` (`SendMessageToUser`, sessions, channels),
  `ISteamNetworkingSockets` (P2P connect/listen, FakeIP, SDR relay), and the lobby
  matchmaking API (`ISteamMatchmaking_CreateLobby`, lobby data, join requests).
  The game initialises the Steam API itself and pumps callbacks, but uses it only
  for achievements (`Il2CppMenace.Achievements`, which already has a swappable
  `IAchievementManager.SetImplementation` provider pattern). A mod riding the
  game's own Steam API instance gets initialisation, callback dispatch, and SDR
  NAT traversal with no new native dependency and no second `SteamAPI_Init`.
- **The sim appears built around seeded randomness.** There is a `PseudoRandom`
  type (used by the weighted-draw helpers) and a seed hierarchy:
  `GetGameSeed` / `GetOperationSeed` / `GetMissionSeed`, `NextSeed`, `SetSeed`,
  plus `_seedOverwrite` and `SeedOverwriteType`. Spike question: whether tactical
  combat resolution consumes this seeded stream or escapes to
  `UnityEngine.Random` / wall-clock anywhere on the command path.
- **Faction-turn structure.** `TacticalManager` exposes `GetActiveFactionID`,
  `GetRound`, public `NextRound()` / `NextFaction()`, and `OnFinished` /
  `OnMovementFinished` broadcast events (already bound by our hook publishers).
  Turns and completed actions are natural replication barriers.
- **Save/load is a full-state serialiser.** `Menace.Strategy.SaveSystem` with
  slot management and a `LoadSaveGameCoroutine`. This is the resync escape hatch:
  a desync or a resumed session is a host-side save, a chunked transfer, and a
  client-side load.
- **The verb surface already exists.** `Jiangyu.Sdk.Menace` curates the imperative
  tactical/strategy API (`MoveTo`, `Skill.Use`, `EndTurn`, hire/dismiss, market,
  operations; see `docs/research/verified/tactical-game-api-verbs.md` and
  `2026-06-04-game-api-verb-surface.md`). Commands replicate at exactly this
  granularity: one verb call is one message.

## Replication model

### Options considered

1. **Tick lockstep** (fixed-step input delay, per-tick input exchange). Wrong tool:
   the sim is not a fixed-step RTS loop, input is sparse, and animation-driven
   action resolution does not map onto ticks. Rejected.
2. **Host-authoritative outcome streaming** (only the host simulates; clients
   receive outcomes and play them back visually). Always correct, but every verb
   needs a bespoke outcome-serialisation path, spectating a mission you do not
   simulate is a large UI problem (the client has no live tactical state to
   inspect), and reconnect is unsolved. Held in reserve.
3. **Action-level command replication** (recommended). The unit of replication is
   a completed game action: a move, a skill use, an end turn. The acting peer
   submits a command; both peers execute the same verb against their own sim;
   both run the action's animation to completion; the next command is only
   accepted at the action barrier. Traffic is a handful of small messages per
   turn, and it composes directly with the existing verb surface.

### Determinism requirement and RNG strategy

Model 3 requires that the same command applied to the same state produces the same
result on both peers. The spike settled this
([2026-07-20-combat-roll-spike.md](2026-07-20-combat-roll-spike.md)): combat rolls
are deterministic on the seeded `TacticalManager.s_Random` stream, but that stream
is also consumed by wall-clock timers and impact VFX, which perturb its
consumption order across processes. The desync is stream scheduling, not the rolls.

The strategy, confirmed against prior art
([2026-07-21-multiplayer-prior-art.md](2026-07-21-multiplayer-prior-art.md)): keep
the cosmetic and wall-clock consumers **off** the simulation stream. Every retrofit
surveyed (RimWorld's `Rand.PushState`/`PopState` scopes, AoE's save-restore around
cosmetic draws, Factorio's separate render PRNG) isolates non-simulation randomness
so it cannot advance or reorder the sim stream. Carrying consumed rolls in the
command envelope, considered here originally, is refuted as the standard pattern.
Applied to Menace: Harmony-patch the wall-clock and VFX consumers of `s_Random`
(the recurring `Range(2, 7)` reschedule timer and the impact-frame VFX triples,
named in the spike trace) to draw from a private `PseudoRandom` instance instead.
Under host-ordered command replay, both peers then consume `s_Random` in identical
order. Per-agent AI streams are constructed unseeded, so AI stays host-driven (an
independent requirement from the 2026-07-19 spike). The interception is verified
viable under IL2CPP: `rngtrace`/`pin` already Harmony-patch `PseudoRandom`.

The action barrier makes this tractable: intra-action timing (animation events,
coroutines) does not matter as long as the state at action completion is
deterministic, because no peer acts mid-action. `OnFinished` /
`OnMovementFinished` are the existing barrier signals.

### Verification and resync

Trust but verify, continuously:

- **Journal**: every command is journaled with a sequence number on both peers.
- **Checksums**: at each action barrier (and each faction-turn boundary), both
  peers hash an agreed state projection (per-actor tile, HP, AP, statuses,
  morale; round and faction order; RNG stream position if reachable) and compare
  out of band. Mismatch = desync, detected within one action of divergence.
- **Resync**: on desync (or reconnect), the host serialises a save, transfers it
  chunked over the bulk channel, the client loads it, and the session resumes at
  the barrier. Slow and correct beats fast and forked.
- **Desync forensics**: journals plus per-barrier hashes from both peers are
  dumped for bisection. This is the framework's debugging surface and a
  first-class feature, not an afterthought.

## Session architecture

### Roles

One **host** (the lobby owner) is the arbiter: it owns the total order of
commands, validates submissions against ownership and sequence, and is the
snapshot source. **Peers** submit commands for what they own and apply the
ordered stream. All peers run the full sim; there is no dumb client in v1.

### Transport

Implemented loader-side over the game's own Steam interop wrapper. No
Facepunch.Steamworks: a second managed wrapper would double-initialise the Steam
API in a process that already owns it, and every native export we need is already
wrapped.

- **v1: `ISteamNetworkingMessages`.** Connectionless, per-channel reliable or
  unreliable, trivially driven from the game's existing callback pump. Adequate
  for two peers and our message rates.
- **Later option: `ISteamNetworkingSockets` with SDR** for persistent connections,
  connection-quality telemetry, and N-peer topologies.
- **Channel plan**: `control` (reliable ordered: session, handshake, ready
  states), `commands` (reliable ordered: the command stream), `bulk` (reliable:
  snapshot chunks, throttled).
- **Interface seam**: a `LoopbackTransport` (two in-process endpoints, and a
  dev-loopback for two local instances) exists from day one so the determinism
  spike, tests, and development do not need Steam or a second account.

### Lobby and handshake

Steam lobby for discovery and invites (`GameLobbyJoinRequested_t` handling gives
friend-list join for free). On join, before play:

1. Game build version must match exactly.
2. Mod set handshake: each peer sends its ordered mod list (id, version, and a
   hash of each compiled manifest). Any mismatch rejects the session with a
   readable diff. Non-identical sims desync by construction; this check is
   mandatory, not advisory.
3. Mod-mode negotiation: the multiplayer mod declares the mode (type A/B/C) and
   assigns factions/roles; both peers acknowledge the same assignment.

## Arbitration models

### Ownership map

The framework's core arbitration primitive is a session-owned map from
**controllable entity to peer**. At tactical granularity the entity is a faction
(type A, type B faction-lock) or a unit set (type B unit-grant). At strategy
granularity it is a subsystem (market, roster, operations map) or, for full
co-pilot co-op, the whole layer for all peers. The multiplayer mod declares the
map; the framework enforces it.

### The single executor rule

All commands, from every peer including the host, enter one ordered stream owned
by the host. A command is executed by every peer (host included) only at its
position in that stream, and only at an action barrier. Consequences:

- Simultaneous input in unit-grant co-op cannot fork the sim: both players click
  freely, the host serialises, both peers observe the same order.
- Host advantage is limited to latency, never to authority divergence.
- Validation happens before ordering: a peer may only submit commands for what
  it owns, and the host rejects ownership or sequence violations. Clients never
  trust peer state; they trust the ordered command stream.

### Strategy-layer arbitration (co-op campaign)

No turn barrier exists on the strategy layer, so:

- Strategy verbs (hire, buy, upgrade, start operation) are commands in the same
  ordered stream, validated against the ownership map.
- **Time advancement is consensus-gated**: `StrategyState.AdvanceTime` runs only
  when all peers are ready. Pausing is any-peer; advancing past an event is
  all-ready.
- Event/conversation choices are commands; which peer may choose is mod policy
  (owner, vote, or host).
- Entering a tactical mission transitions the session to tactical arbitration;
  mission setup (seed, map, forces) is decided host-side and shipped as the
  first command so both peers generate identical terrain.

## Framework architecture in Jiangyu terms

- **`Jiangyu.Sdk.Net`** (new assembly, game-agnostic, ships and merges exactly
  like `Jiangyu.Sdk` / `Jiangyu.Sdk.Menace`): the modder surface. Sketch:

  ```csharp
  namespace Jiangyu.Net;

  public static class Lobby
  {
      static LobbyHandle Create(LobbyOptions options);
      static LobbyHandle Join(SteamId friend);
      static event Action<Peer> PeerJoined;
      static event Action<Peer> PeerLeft;
  }

  public static class Session
  {
      static bool IsActive { get; }
      static bool IsHost { get; }
      static Peer Local { get; }
      static IReadOnlyList<Peer> Peers { get; }
      static OwnershipMap Ownership { get; }   // mod declares, framework enforces
  }

  public static class Net
  {
      static void Submit<T>(T command) where T : struct;  // [NetCommand] types
      static event Action<CommandEnvelope> CommandApplied; // ordered stream, all peers
      static SyncStatus Sync { get; }   // checksum state, desync events
  }
  ```

  `[NetCommand]` types are plain serialisable structs (System.Text.Json source-gen
  or a small hand-rolled writer; no game object references, only ids and values).
  The envelope carries sequence, owning peer, and any RNG payload the sync layer
  attached.

- **Loader**: `SteamTransport` over the interop wrapper, a pump hooked to the
  game's callback dispatch, and a `MultiplayerSessionSystem` (a loader-internal
  `JiangyuSystem` so ordering and lifecycle compose with mods' systems via
  `[DependsOn]`). Owns the journal, the checksum service, the snapshot service,
  and the barrier logic (gating command application on `OnFinished` /
  `OnMovementFinished`).
- **Compiler / Studio**: no v1 changes. The handshake hash is computed from the
  compiled manifests the loader already reads.
- **Docs**: a `site/sdk/multiplayer.md` narrative page plus reference generation
  for `Jiangyu.Sdk.Net`, following the existing verbs/hooks codegen pattern.

## Determinism spike (go/no-go) — COMPLETE

Ran 2026-07-19 on game v0.7.10+19931; full write-up and evidence in
[2026-07-19-determinism-spike.md](2026-07-19-determinism-spike.md). Verdict:
**outcome 2**. Mission generation, initial state, and player command execution
are deterministic across fresh processes; `TrySpawnUnit` stat rolls and all AI
decision-making are not. Consequences adopted by this doc: action-level command
replication is the model (model 3 confirmed for player actions); AI in shared
sessions is host-driven by requirement (the Remote-AI mechanism doubles as the
shared-session AI); spawn rolls are carried or re-seeded. The remaining
sub-questions (per-shot roll equality on identical inputs, the unseeded spawn
source) are follow-up spikes listed in the spike doc.

Original spike plan (kept for reference):

1. **Record**: load a fixed save, drive a scripted command sequence through a
   tactical mission (moves, attacks with known hit chances, overwatch, an end
   turn), journaling commands and hashing the agreed state projection at every
   action barrier.
2. **Replay**: fresh process, same save, same script, same hashes. Any divergence
   localises to the first differing action.
3. **Bisect on failure**: if combat diverges, hook candidate RNG call sites and
   re-run until the escaping site is identified (expected outcome: a small set
   of sites feeding `UnityEngine.Random` or an unseeded `PseudoRandom`).
4. **Two-process run**: repeat across two live instances over the loopback
   transport to catch cross-machine float or ordering effects.

Exit criteria: a written verdict on which of the three determinism outcomes holds,
and if outcome 2, the exact RNG sites the command envelope must carry. Promote the
result to `docs/research/verified/` before any production code depends on it.

## Risks and mitigations

- **Animation-coupled state mutation**: if state changes at animation events
  rather than upfront, determinism still holds (both peers animate the same
  action to completion) but barrier detection must use the game's completion
  events, never timers. Covered by the barrier design.
- **AI nondeterminism (CONFIRMED by the spike)**: AI decisions diverge across
  processes from the first activation. In every shared session, AI decisions are
  made host-side and shipped as commands (the Remote-AI mechanism applied to the
  AI itself). This is a load-bearing design constraint, not a mitigation.
- **Cross-machine floating point**: all peers run the identical precompiled
  `GameAssembly.dll` on x86-64 Windows. Low risk; two-process spike stage
  confirms.
- **Dictionary iteration order**: any sim path iterating an unordered collection
  whose order affects outcomes is a latent desync. Not directly detectable by
  string recon; the spike's scripted mission is the probe. If found, that path is
  a host-executes-and-ships-outcome candidate.
- **Save snapshot size and load time**: chunked bulk transfer with progress;
  acceptable because resync is rare. Measure during the first vertical slice.
- **Peer clock/pause abuse**: any-peer pause, host-enforced resume policy,
  per-command rate limiting. Trusted-friend threat model keeps this small.

## Testing strategy

- **Loopback transport + scripted sessions**: the replication, ordering, and
  checksum logic runs in `Jiangyu.Loader.Tests`-style harnesses without Steam,
  driving two in-process session endpoints.
- **Record/replay determinism harness** from the spike becomes a permanent
  diagnostic, re-runnable after game updates (the seed hierarchy surviving a
  game update is exactly the kind of contract the surface-baseline tooling
  watches).
- **Live two-instance smokes** over Steam for the lobby/invite/SDR paths; these
  cannot run in CI and stay manual, like the IL2CPP-bound smokes today.

## Phasing

1. **Spike: determinism probe** (section 8). Go/no-go for model 3, identifies RNG
   sites if needed.
2. **Transport + lobby** (built and smoke-verified 2026-07-20, see
   [2026-07-20-net-transport-lobby.md](2026-07-20-net-transport-lobby.md)): Steam
   messages, loopback transport, handshake with mod hash, chat-grade echo proven
   via the `net` dev probe over the real Steam stack.
3. **Command replication core**: journal, ordered stream, barrier logic,
   checksums, on a scripted two-instance session.
4. **Vertical slice: PvP tactical (type A)**: Remote-AI interception, one
   skirmish mission, desync detection live, snapshot resync working.
5. **Co-op tactical (type B)**: faction-lock first, then unit-grant with the
   single executor rule.
6. **Co-op campaign (type C)**: strategy command surface, consensus time
   advancement, session persistence into saves, mission transitions.
7. **Hardening**: reconnect mid-mission, spectator, N-peer readiness review,
   docs and reference generation.

Each phase ships a usable subset; no phase depends on a later one.

## Open questions

Answered by the spike (2026-07-19): combat moves and player-driven combat are
deterministic across processes; AI decision-making is not (host-driven AI is now
mandatory); spawn stat rolls are not (carry or re-seed); the state-projection
hash belongs on the actor projection, not on `UnityEngine.Random.state`
(visuals consume it per frame, so it never matches across processes).

Resolved by the combat roll spike (2026-07-20/21, see
[2026-07-20-combat-roll-spike.md](2026-07-20-combat-roll-spike.md)) and the
prior-art survey (2026-07-21, see
[2026-07-21-multiplayer-prior-art.md](2026-07-21-multiplayer-prior-art.md)):
combat rolls live on the seeded `TacticalManager.s_Random` stream and are
themselves deterministic (a traced shot draws the same 117 values in every
process). The desync is consumption-order drift: wall-clock timers and impact VFX
share that stream with command resolution, so one frame of scheduling wobble
permutes who gets which value, and the stream drifts even in an idle mission.
Barrier-time re-seeding cannot fix ordering. The fix, matching every retrofit
surveyed, is to move the cosmetic and wall-clock consumers onto a private stream
so the sim stream is consumed only by command-driven combat, kept in order by
host-ordered replay. Carrying rolls in the envelope is refuted as the standard
pattern. The spike's stream map also confirms per-agent AI streams are
constructed unseeded, which host-driven AI already covers.

Still open:

For review:

- Serialisation format for `[NetCommand]` structs: System.Text.Json source-gen
  (already a merged loader dependency) versus a compact hand-rolled binary
  writer. Json first is proposed; bandwidth is a non-issue at our rates.
- Should `OwnershipMap` live in `Jiangyu.Sdk.Net` as data or be expressed as mod
  code hooks? Data is proposed for handshake comparability.
- Pause policy: any-peer pause with host override, or consensus?
- Do we expose the raw transport to mods (arbitrary messages, e.g. for chat or
  custom UI sync) or only the command stream? Exposing a raw lane is proposed,
  clearly marked as sim-unsafe.
