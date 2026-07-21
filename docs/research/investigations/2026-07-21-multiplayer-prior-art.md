# Multiplayer Retrofit Prior Art

Date: 2026-07-21.

Status: research complete. Feeds phase 3 (command replication core) of the
multiplayer framework
([2026-07-19-multiplayer-framework-design.md](2026-07-19-multiplayer-framework-design.md)).
Prior-art survey of how existing mods and games retrofit deterministic
multiplayer command replication, read against jiangyu's resolved combat-roll
mechanism ([2026-07-20-combat-roll-spike.md](2026-07-20-combat-roll-spike.md)).

## Sources

Primary: RimWorld Multiplayer source (Zetrith and rwmt forks: `ScheduledCommand.cs`,
`CommandHandler.cs`, `Seeds.cs`, `SyncCoordinator.cs`, `ClientSyncOpinion.cs`,
`DesyncCheck.cs`), the Age of Empires "1500 Archers on a 28.8" postmortem
(Terrano and Bettner), Factorio's FFF-188 and desync wiki, the Teardown
multiplayer engine blog, BeaverBuddies (Timberborn co-op mod). RimWorld
Multiplayer is the closest analogue: a single-player-to-co-op retrofit of a
Unity game via managed-C# Harmony patching.

## Findings

The architecture is settled across every case: **replicate the commands that
drive the simulation, never the resulting object state**, and rely on every peer
reproducing bit-identical results.

### Command envelope and ordering

RimWorld's `ScheduledCommand` is `{CommandType type, int ticks, int factionId,
int mapId, int playerId, byte[] data}`. The server stamps `ticks` with its
authoritative game timer and broadcasts; clients run behind the server timer and
replay each command when their counter reaches the stamped tick. This is
**host-authoritative tick-scheduling**: one authority assigns a single total
order, and the payload is an opaque binary blob. AoE and Factorio instead use
**symmetric peer lockstep** (commands delayed a fixed number of turns, every peer
simulates every tick identically). Both families share the invariant: inputs not
state, and any divergence however small cascades into a desync. The
host-authoritative model is simpler to reason about and is what jiangyu's design
already chose. Commands travel on a reliable in-order channel (Teardown confirms:
scene-modifying commands on a guaranteed-ordered stream, cosmetic transforms on a
separate unreliable one).

### RNG strategy (the decisive finding)

The proven pattern is **not** to carry consumed rolls in the command (refuted
outright, 0-3 adversarial votes) and **not** a single synchronised global seed.
It is: wrap RNG-consuming simulation operations in per-scope isolated streams
re-seeded from stable in-game object identifiers, and critically keep cosmetic,
render, and wall-clock consumers **off** the simulation stream.

- RimWorld uses a `Rand.PushState(seed)` / `Rand.PopState()` save-restore stack
  (`Seeds.cs`), seeding each scope from a stable id: map `uniqueID` for map load
  and caravan entry, `pawn.thingIDNumber` for pawn graphics, `thing.thingIDNumber`
  before a random rotation. None of the eight enumerated push sites derives its
  seed from a tick counter, and none carries a consumed roll value. The seed is
  the raw stable id, not a hash-combine with a world constant (a claim asserting
  the hash-combine was refuted against the source).
- AoE saves and restores the RNG's last value around cosmetic uses (terrain
  sounds) so they do not advance the simulation stream, and requires every
  machine to make the same number of `random` calls within the simulation.
- Factorio keeps cosmetic and render randomness on a separate PRNG: "you should
  not change PRNG state during rendering."

This is the mirror image of jiangyu's mechanism. Menace combat rolls are already
deterministic on the seeded `TacticalManager.s_Random`, but wall-clock timers and
impact VFX share that stream and perturb its consumption order. The prior-art
answer applied inversely: **move the wall-clock and cosmetic consumers of
`s_Random` onto a private stream** (Harmony-patch those draw sites to draw from a
separate `PseudoRandom` instance), leaving the sim stream consumed only by
command-driven combat. Under host-ordered command replay both peers then consume
the sim stream in identical order. The one caveat the sources raise, that
RimWorld's Harmony interception is managed-C# and unverified under IL2CPP, does
not apply to jiangyu: the `rngtrace` and `pin` probes already Harmony-patch the
game's `PseudoRandom` under IL2CPP, so the interception is verified.

### Checksum and barrier design

RimWorld's `SyncCoordinator` accumulates a `ClientSyncOpinion` per client:
RNG state partitioned by consumer domain (command, world, per-map) stored as the
**upper 32 bits** of each 64-bit state, plus stack-trace hashes. Opinions sharing
a `startTick` are compared entry-by-entry (`SequenceEqual`) to localise the exact
tick and subsystem, emitting messages that name the diverged stream ("Random
state from commands doesn't match", "Wrong random state on map {mapId}"). AoE
checksums whole subsystems; Factorio diffs whole game state. RimWorld's
domain-partitioning is the useful pattern: the mismatch message itself names the
diverged stream. jiangyu already has the pieces: the determinism probe's state
projection plus the `rngtrace owners` stream-to-pointer map, so the checksum
service partitions by named stream owner.

### Desync recovery

Two schools. RimWorld and AoE **hard-stop with diagnostics** (RimWorld writes a
rotated `Desync-##.zip` with local and remote sync opinions and a game snapshot,
then offers only manual resync via a host re-pull; AoE stopped the game). Factorio
**auto-resyncs the diverged client alone** by dropping it and re-downloading the
full map while the shared simulation continues. Hard-stop-plus-diagnostics is the
lower-effort start; manual resync-from-host is the upgrade. RimWorld also runs an
Arbiter, a neutral background instance that helps decide which client is at fault.

### What breaks in practice

Floating-point drift and any divergence in RNG call count. Teardown rewrote its
destruction core in fixed-point integer math, though its author calls FP
determinism "mostly navigable" and chose fixed-point largely for voxel
discreteness. For jiangyu: combat rolls are already integer-deterministic on a
seeded stream, so no fixed-point rewrite is implied, but the checksum must not
hash any FP state that can drift (RimWorld even compares FP round mode), and the
wall-clock timers that perturb RNG call order are the concrete instance of the
call-count hazard, addressed by the stream-isolation fix above.

## Open questions the survey did not close

- Late join and savegame transfer: the mechanics of a mid-session join replaying
  to the current tick (serialise, transfer, deterministic catch-up) were not
  detailed beyond Factorio's full-map re-download and RimWorld's snapshot zip.
- Time-control synchronisation (pause and speed changes across peers) in a
  host-ordered tick model was not covered.
- BeaverBuddies (Timberborn) surfaced as a second Unity co-op retrofit but was
  not read at implementation depth. Timberborn/Oxygen Not Included/Stardew co-op
  produced no other verified detail.
