# Determinism spike: record/replay probe

Date: 2026-07-19

Status: **complete, verdict below**. Harness: `src/Jiangyu.Loader.Diagnostics/Determinism/`
(probe) plus `src/Jiangyu.Loader.Diagnostics/NavDriver.cs` (unattended session entry).

The go/no-go spike for action-level command replication, phase 1 of
[2026-07-19-multiplayer-framework-design.md](2026-07-19-multiplayer-framework-design.md)
section 8. Question: does the same scripted command sequence, driven into the same
loaded save in two fresh processes, produce the same tactical state at every action
barrier?

## Verdict (2026-07-19, game v0.7.10+19931)

Two fresh headless processes, same strategy save, same mission (seed 5031703 both
runs), same seven-step script (spawn a spiderling, DP-12 attack on it, a move, three
end turns covering a full AI round, a settle wait). Journals:
`skirmish-a-record-20260719-084859` vs `skirmish-a-replay-20260719-085257`.

- **Mission generation and initial state: DETERMINISTIC.** Baseline actor projection
  (13 actors: faction, template, tile, HP, AP, morale, suppression, armour, stance,
  flags) identical across processes. Same save + same operation reproduces the
  mission exactly, including auto-deployment.
- **Player command execution: DETERMINISTIC.** Attack (skill use, kill resolution,
  AP/acted bookkeeping), movement, and end-turn mechanics produced identical state
  at every barrier up to the first AI activation.
- **`TrySpawnUnit` stat rolls: NOT DETERMINISTIC.** The spawned spiderling rolled
  HP 120 / armour 200 in one process, HP 90 / armour 150 in the other. Spawn-time
  stat rolls escape the seeded stream.
- **AI decisions: NOT DETERMINISTIC.** From the first AI activation, destination
  picks differed by a tile (same unit chose (21,27) vs (20,28)); after the full AI
  round, unit positions diverged widely and reinforcement composition differed
  (a `big_warrior_young` appeared in one run only).
- **`UnityEngine.Random.state` at barriers always differs** across processes
  (visuals consume it per frame). It is noise, not a sim signal; the probe now
  classifies hash mismatches as sim vs rng-only and reports only the former.

### Consequences for the design doc

Outcome 2 ("deterministic given RNG") with specifics:

1. Action-level command replication is **viable for player-driven actions**.
2. **AI must be host-driven**: AI decisions for shared sessions are made host-side
   and shipped as commands (the Remote-AI mechanism, exactly as the design doc's
   risk mitigation proposed). This is a hard requirement, not a fallback.
3. **Spawn stat rolls need host-shipped values or re-seeding** at the spawn sites
   (reinforcements, `Units.Spawn`).
4. The RNG stream position to watch in hashes is the actor projection, not
   `UnityEngine.Random.state`.

### Follow-up spikes (not yet run)

- **Combat-roll determinism against a fixed target**: the attack above killed a
  different-HP target in each run (both died), so hit/damage roll equality given
  *identical* inputs is inferred, not proven. Fire (with
  `UsageParameter.IgnoreUsabilityCheck`) at a pre-existing enemy with fixed HP and
  compare per-shot outcomes.
- **Where spawn rolls come from**: hook candidate RNG sites around `TrySpawnUnit`
  to find the unseeded source.

### Operational learnings (used by the harness now)

- `Skill.Use(tile, UsageParameter.Default)` **silently no-ops** when the usability
  check fails (e.g. target not yet detected); `IgnoreUsabilityCheck` (flag 2)
  executes the real combat pipeline (AP spent, kill resolved, `OnAfterSkillUse`
  fired). `UsageParameter` is a flags enum: Default=0, Free=1,
  IgnoreUsabilityCheck=2, Fake=4, InstantResolve=32.
- `TacticalState.EndTurn()` ends the **active unit's** turn, not the faction turn.
- No mid-mission saves exist; the fixture is a strategy save plus scripted mission
  entry (`nav load {save}` -> `nav enter-mission`).
- The game autosaves over `latest.save` when an operation starts and rotates
  autosaves, so a rig that reloads latest eats its own fixture. The standing fixture
  is the dedicated `DETERMINISMTEST` save, loaded by name; never `load-latest` for
  fixture work.
- `MissionPrepUIScreen.UpdateSupplies` null-refs in `MissionPrepScene.SetLoadoutsStatus`
  if the prep background scene has not finished loading; the first failed call
  leaves the screen broken, so recovery is re-running the whole entry chain
  (NavDriver does this, up to 3 attempts).

## Harness

New dev-loader bridge command `determinism`
(`src/Jiangyu.Loader.Diagnostics/Determinism/`), driven over the Studio bridge or the
`jiangyu_determinism_probe` MCP tool. A run is a coroutine that:

1. Attaches to the live `TacticalManager`'s completion events (same IL2CPP
   delegate-marshalling pattern as `TacticalHookPublisher`).
2. Executes the script step by step: `move` (`Actor.MoveTo`), `attack`
   (`Skill.Use(tile, default)`), `skip` (`Actor.SkipTurn`), `endturn`
   (`TacticalState.EndTurn`, compile-confirmed against the interop assembly), `wait`.
3. After each step waits for the step's barrier event (`OnMovementFinished` /
   `OnAfterSkillUse` / `OnTurnEnd` / `OnPlayerTurn`), then a quiet window (default 90
   frames with no tactical event) so animation-coupled fallout settles.
4. Snapshots the state projection at that barrier and hashes it (FNV-1a 64).

The projection: round, active faction, active actor, one canonical line per actor
(faction, template, tile, HP, AP, morale, suppression, armour, stance, alive, acted,
turn-done, sorted ordinal), and the `UnityEngine.Random.state` seed block (readable
through the interop wrapper, so an actor-match-but-RNG-mismatch result is directly
visible). Full snapshot lines are journaled, so a mismatch diffs to the field level.

Ops: `record {script}` writes
`<UserData>/determinism/<script>-record-<stamp>.journal.json`; `replay {script, journal}`
re-runs the script and reports per-step hash agreement plus the first divergence;
`compare {a, b}` diffs two journals file-only (record vs record, e.g. cross-machine);
`status` / `abort` manage the active run.

## Procedure

No mid-mission saves exist, so the fixture is a **strategy save plus scripted mission
entry**, driven unattended over the bridge:

1. Launch the game headless (`steam -applaunch 2432860`; see the headless section).
   Wait for the title screen (`scene` command).
2. `nav load {save: "determinismtest"}`, wait for `strategy: true` in `nav status`.
3. `nav enter-mission` with the fixture's operation/mission indices, wait for
   `mission: true`. The launched mission reproduces exactly across processes (same
   save + same operation = same seed, same map, same auto-deployment). An operation
   with no generated missions throws at selection; list operations first
   (`Operations.Available`, `Operations.Missions`) when the fixture changes.
4. `determinism record {script}`; wait for completion; note the journal filename.
5. Kill the game, relaunch, repeat 1-3, then
   `determinism replay {script, journal}`. The replay status and journal carry the
   verdict; `determinism compare {a, b}` diffs any two journals file-only.

Craft `<UserData>/determinism/<name>.script.json` for the mission (inspect first:
`Mission.Actors`, the `skills` command, tile verbs). Actor references are
`{faction, template}` (the actor's EntityTemplate name, stable across the whole run)
or `{faction, index}`; the faction actor list is not positionally stable once actors
act, so index addressing is only safe for an actor's first action. The script used for the verdict:

```json
{
  "settleFrames": 90,
  "timeoutFrames": 3600,
  "steps": [
    { "op": "spawn",  "faction": "Wildlife", "template": "enemy.alien_01_small_spiderling", "tile": [38, 1] },
    { "op": "attack", "actor": { "faction": "Player", "index": 2 }, "skill": "active.fire_helen_dp12", "usage": 2, "tile": [38, 1] },
    { "op": "move",   "actor": { "faction": "Player", "index": 0 }, "tile": [37, 3] },
    { "op": "endturn" },
    { "op": "endturn" },
    { "op": "endturn" },
    { "op": "wait",   "frames": 600 }
  ]
}
```

`usage: 2` is `UsageParameter.IgnoreUsabilityCheck` and is required for scripted
shots at targets the usability check rejects (e.g. not-yet-detected spawns) -
without it `Skill.Use` silently no-ops. Keep scripts short: a spawn+attack for the
combat path, a move, end turns to cover a full AI round.

## Running the game headless (dev machine)

Steam launch options for MENACE (appid 2432860) point at a wrapper script
(`~/.local/bin/jiangyu-menace-headless.sh`):

```bash
#!/usr/bin/env bash
pactl load-module module-null-sink sink_name=jiangyu_headless \
  sink_properties=device.description=jiangyu_headless >/dev/null 2>&1
exec env WINEDLLOVERRIDES="version=n,b" PULSE_SINK=jiangyu_headless \
  gamescope --backend headless -W 1600 -H 900 -- "$@"
```

Failure modes hit while getting here, in case they recur:

- **No `version=n,b` override**: wine loads its builtin `version.dll` instead of the
  MelonLoader proxy next to the exe; the game runs fine but MelonLoader never
  starts (no log, no bridge).
- **`proton run` directly**: the running Steam client re-wraps the launch with its
  own environment and the game escapes any xvfb/gamescope container onto the real
  display. Launch through Steam instead.
- **`-batchmode -nographics`**: breaks the game's boot (PersistentPlayerSettings NRE
  on splash; UIManager.OpenScreen fails).
- **xvfb without disabling wine-wayland**: Proton-GE's Wayland driver puts the
  window on the real desktop anyway.
- One display-session crash occurred around a gamescope-backed launch; cause
  unconfirmed, watch for recurrence.

## Verdict criteria

The `UnityEngine.Random.state` fragment of the hash is per-frame visual noise and
always differs across processes; the probe classifies it out. The verdict rests on
the sim projection (actor lines + mission string minus rng):

- **Sim MATCH** (actor projection agrees at every barrier): action-level command
  replication is viable for the exercised paths. Promote to `docs/research/verified/`.
- **Localised sim divergence** (an identifiable actor/field): outcome 2 of the
  design doc. The command envelope carries those values or the implicated RNG is
  re-seeded per action; AI decisions go host-driven. Bisect: the `onlyInJournal` /
  `onlyInReplay` diff names the actor and field; hook candidate RNG sites on the
  implicated path (`UnityEngine.Random.state` is get/settable through the wrapper,
  so per-action re-seeding is implementable).
- **Broad sim divergence**: the affected verbs move to host-authoritative outcome
  streaming (design doc model 2).

## Risks to the harness itself

- Actor `{faction, index}` references assume identical actor list ordering across
  processes. If the baseline hash matches, ordering held for that run; if a run is
  rejected with "out of range", the list order or count differed and the save/fixture
  is not as fixed as assumed.
- The quiet window is heuristic. A step whose journal entry says `barrier event ...
  not seen` means the wait model, not the sim, failed; adjust `waitEvent`/`settle`.
- `endturn` waits for `OnPlayerTurn`: if the mission has no returning player turn
  (mission end), use `waitEvent: "MissionFinished"` on that step.
