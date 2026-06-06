using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Loader.Diagnostics;
using MelonLoader;
using UnityEngine;
using ProbeReport = Jiangyu.Loader.Diagnostics.InspectionReport;
using ProbeCheck = Jiangyu.Loader.Diagnostics.InspectionCheck;

namespace Jiangyu.Loader.Diagnostics.VerbProbe;

/// <summary>
/// Live spike (pass 2) for the game-API verb surface. Pass 1 confirmed the
/// candidate call signatures statically against the generated proxy; this drives
/// each one against a running Tactical mission and records the runtime answers
/// metadata cannot give: singleton timing, whether the spawn path works end to
/// end, what a path query returns, and whether the movement-range Task completes
/// synchronously.
///
/// <para>Driven on demand by the <c>verbs.run</c> bridge request and returned to the
/// caller. The read-only probes run on the request alone. The state-mutating probes
/// (spawn a unit, then despawn it) are opt-in behind the <c>verbs-spawn</c> toggle,
/// matching the injection gate's self-hit opt-in. Logs each result the instant it
/// completes so a later native crash still leaves a trail.</para>
/// </summary>
internal static class VerbSurfaceProbe
{
    private const string Pass = DiagnosticStatus.Pass;
    private const string Fail = DiagnosticStatus.Fail;
    private const string Skipped = DiagnosticStatus.Skipped;

    // The state-mutating spawn probes are opt-in behind the `verbs-spawn` toggle.
    private static bool IsSpawnEnabled() => DevFlags.IsEnabled("verbs-spawn");

    // On-demand verb-surface run for the `verbs.run` bridge request: every read-only
    // probe, plus (behind the verbs-spawn opt-in) the spawn/despawn probes, driven
    // against the live mission and returned. The return type is object so the loader
    // hands it (or a no-actor error) straight to the JSON serialiser. Must run on the
    // Unity main thread.
    internal static object Capture(string sceneTag, MelonLogger.Instance log)
    {
        if (!TryGetActiveActor(out var actor))
            return new { sceneTag, error = "no active actor (enter a Tactical mission first)" };

        var report = new ProbeReport
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneTag = sceneTag,
            SdkLoaderVersion = BuildInfo.Version,
            GameVersion = InspectionReporter.SafeGameVersion(),
        };

        Emit(report, CheckManagerAndMap(), log);
        Emit(report, CheckActiveActorTile(actor), log);
        Emit(report, CheckFactionsAndActors(), log);
        Emit(report, CheckEntityProperties(actor), log);
        Emit(report, CheckLineOfSight(actor), log);
        Emit(report, CheckHitChance(actor), log);
        Emit(report, CheckPathQuery(actor), log);
        Emit(report, CheckMovementRangeTask(actor), log);
        Emit(report, CheckSpawnUnit(actor), log);
        Emit(report, CheckSpawnOnOccupiedTile(actor), log);
        Emit(report, CheckSpawnOnBlockedTile(actor), log);
        Emit(report, CheckSpawnForeignFaction(actor), log);
        Emit(report, CheckDieBehaviour(actor), log);
        Emit(report, CheckDamageAndHeal(actor), log);
        Emit(report, CheckMoveCommit(actor), log);
        Emit(report, CheckSuppression(actor), log);

        return report;
    }

    // Characterise the suppression path a future Combat.Suppress verb would call:
    // ApplySuppression on a throwaway unit in both direct modes with a null suppressor
    // and skill, recording whether either throws. Opt-in; despawns the unit it spawns.
    private static ProbeCheck CheckSuppression(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "verb.suppression", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "verb.suppression", Status = Skipped, Detail = "no manager, map, or tile" };
            var tile = FindAdjacentFreeTile(map, from);
            if (tile == null)
                return new ProbeCheck { Name = "verb.suppression", Status = Skipped, Detail = "no free adjacent tile" };
            if (!tm.TrySpawnUnit(actor.GetFaction(), actor.GetTemplate(), tile, out var dummy) || dummy == null)
                return new ProbeCheck { Name = "verb.suppression", Status = Skipped, Detail = "spawn for the probe failed" };

            var check = new ProbeCheck
            {
                Name = "verb.suppression",
                Status = Pass,
                Detail = "ApplySuppression(amount, direct, suppressor=null, skill=null) on a throwaway unit in both direct modes: whether a Combat.Suppress verb is safe with a null suppressor/skill",
            };
            try { dummy.ApplySuppression(5f, false, null, null); check.Evidence["directFalseOk"] = true; }
            catch (Exception ex) { check.Evidence["directFalseError"] = $"{ex.GetType().Name}: {ex.Message}"; check.Status = Fail; }
            try { dummy.ApplySuppression(5f, true, null, null); check.Evidence["directTrueOk"] = true; }
            catch (Exception ex) { check.Evidence["directTrueError"] = $"{ex.GetType().Name}: {ex.Message}"; check.Status = Fail; }

            dummy.Die(true);
            return check;
        }
        catch (Exception ex) { return Errored("verb.suppression", ex); }
    }

    // Characterise the imperative damage path the way Combat.Damage calls it: drive a
    // small DamageInfo through a throwaway unit's OnDamageReceived with a null attacker
    // and null skill, then a negative DamageInfo, to learn whether negative damage heals
    // or is clamped (the open question gating a future Combat.Heal). Opt-in; despawns the
    // unit it spawns.
    private static ProbeCheck CheckDamageAndHeal(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "verb.damageAndHeal", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "verb.damageAndHeal", Status = Skipped, Detail = "no manager, map, or tile" };
            var tile = FindAdjacentFreeTile(map, from);
            if (tile == null)
                return new ProbeCheck { Name = "verb.damageAndHeal", Status = Skipped, Detail = "no free adjacent tile" };
            if (!tm.TrySpawnUnit(actor.GetFaction(), actor.GetTemplate(), tile, out var dummy) || dummy == null)
                return new ProbeCheck { Name = "verb.damageAndHeal", Status = Skipped, Detail = "spawn for the probe failed" };

            var check = new ProbeCheck
            {
                Name = "verb.damageAndHeal",
                Status = Pass,
                Detail = "OnDamageReceived(attacker=null, skill=null, DamageInfo) on a throwaway unit: validates Combat.Damage's null-skill path, then a negative Damage to learn whether it heals or clamps",
            };
            check.Evidence["hpFull"] = dummy.GetHitpoints();

            try
            {
                var dealt = dummy.OnDamageReceived(null, null, new DamageInfo { Damage = 5 });
                check.Evidence["damageResolved"] = dealt != null ? dealt.Damage : -1;
                check.Evidence["hpAfterDamage"] = SafeHp(dummy);
            }
            catch (Exception ex) { check.Evidence["damageError"] = $"{ex.GetType().Name}: {ex.Message}"; check.Status = Fail; }

            try
            {
                var hpBeforeHeal = SafeHp(dummy);
                var healed = dummy.OnDamageReceived(null, null, new DamageInfo { Damage = -5 });
                var hpAfterHeal = SafeHp(dummy);
                check.Evidence["negativeDamageResolved"] = healed != null ? healed.Damage : -1;
                check.Evidence["hpBeforeHeal"] = hpBeforeHeal;
                check.Evidence["hpAfterHeal"] = hpAfterHeal;
                check.Evidence["negativeDamageHeals"] = hpAfterHeal > hpBeforeHeal;
            }
            catch (Exception ex) { check.Evidence["healError"] = $"{ex.GetType().Name}: {ex.Message}"; }

            dummy.Die(true);
            return check;
        }
        catch (Exception ex) { return Errored("verb.damageAndHeal", ex); }
    }

    // Characterise the imperative move path the way Units.Move calls it: spawn a throwaway
    // unit and MoveTo an adjacent free tile, recording the return and whether the unit's
    // tile actually changed (does the move commit synchronously?). Opt-in; despawns the
    // unit it spawns.
    private static ProbeCheck CheckMoveCommit(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "verb.moveCommit", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "verb.moveCommit", Status = Skipped, Detail = "no manager, map, or tile" };
            var spawnTile = FindAdjacentFreeTile(map, from);
            if (spawnTile == null)
                return new ProbeCheck { Name = "verb.moveCommit", Status = Skipped, Detail = "no free adjacent tile to spawn onto" };
            if (!tm.TrySpawnUnit(actor.GetFaction(), actor.GetTemplate(), spawnTile, out var dummy) || dummy == null)
                return new ProbeCheck { Name = "verb.moveCommit", Status = Skipped, Detail = "spawn for the probe failed" };

            var dest = FindAdjacentFreeTile(map, spawnTile);
            if (dest == null)
            {
                dummy.Die(true);
                return new ProbeCheck { Name = "verb.moveCommit", Status = Skipped, Detail = "no free tile adjacent to the spawned unit" };
            }

            var check = new ProbeCheck
            {
                Name = "verb.moveCommit",
                Detail = "MoveTo(adjacentFreeTile, ref default MovementAction, MovementFlags.None) on a throwaway unit: validates Units.Move and whether the move commits synchronously",
            };
            try
            {
                var action = default(MovementAction);
                var returned = dummy.MoveTo(dest, ref action, MovementFlags.None);
                var landed = dummy.GetTile();
                check.Status = returned ? Pass : Fail;
                check.Evidence["moveToReturned"] = returned;
                check.Evidence["movedSynchronously"] = landed != null && landed.Pointer == dest.Pointer;
                check.Evidence["destX"] = dest.GetX();
                check.Evidence["destZ"] = dest.GetZ();
            }
            catch (Exception ex)
            {
                check.Status = Fail;
                check.Evidence["moveError"] = $"{ex.GetType().Name}: {ex.Message}";
            }

            dummy.Die(true);
            return check;
        }
        catch (Exception ex) { return Errored("verb.moveCommit", ex); }
    }

    // The shared prerequisite: manager singleton non-null and a live map handle.
    private static ProbeCheck CheckManagerAndMap()
    {
        try
        {
            var tm = TacticalManager.Get();
            var map = tm != null ? tm.GetMap() : null;
            var ok = tm != null && map != null;
            return new ProbeCheck
            {
                Name = "acquire.managerAndMap",
                Status = ok ? Pass : Fail,
                Detail = "TacticalManager.Get() and GetMap() both resolved in the live Tactical scene",
                Evidence =
                {
                    ["managerNonNull"] = tm != null,
                    ["mapNonNull"] = map != null,
                },
            };
        }
        catch (Exception ex) { return Errored("acquire.managerAndMap", ex); }
    }

    // Map+tile handle round-trip: read the active actor's tile, then re-fetch it
    // by coordinate through Map.GetTile and confirm identity. Also exercises
    // GetBaseTile at the same coordinate.
    private static ProbeCheck CheckActiveActorTile(Actor actor)
    {
        try
        {
            var map = TacticalManager.Get()?.GetMap();
            var tile = actor.GetTile();
            if (map == null || tile == null)
                return new ProbeCheck { Name = "read.activeActorTile", Status = Skipped, Detail = "no map or actor tile" };

            int x = tile.GetX(), z = tile.GetZ();
            var byCoord = map.GetTile(x, z);
            var baseTile = map.GetBaseTile(x, z);
            var roundTrips = byCoord != null && byCoord.GetX() == x && byCoord.GetZ() == z;

            return new ProbeCheck
            {
                Name = "read.activeActorTile",
                Status = roundTrips ? Pass : Fail,
                Detail = "read the active actor's tile, re-fetched it by coordinate via Map.GetTile, confirmed identity",
                Evidence =
                {
                    ["x"] = x,
                    ["z"] = z,
                    ["getTileRoundTrips"] = roundTrips,
                    ["getBaseTileNonNull"] = baseTile != null,
                    ["tileHasActor"] = tile.HasActor(),
                },
            };
        }
        catch (Exception ex) { return Errored("read.activeActorTile", ex); }
    }

    // Enumerate-actors primitive: walk every faction and count its actors.
    private static ProbeCheck CheckFactionsAndActors()
    {
        try
        {
            var tm = TacticalManager.Get();
            var factions = tm?.GetFactions();
            if (factions == null)
                return new ProbeCheck { Name = "read.factionsAndActors", Status = Fail, Detail = "GetFactions() returned null" };

            var perFaction = new System.Collections.Generic.List<string>();
            var total = 0;
            for (var i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                var type = f.GetFactionType();
                var actors = f.GetActors();
                var count = actors != null ? actors.Count : 0;
                total += count;
                perFaction.Add($"{type}:{count}");
            }

            return new ProbeCheck
            {
                Name = "read.factionsAndActors",
                Status = Pass,
                Detail = "enumerated factions via TacticalManager.GetFactions() and counted actors per faction",
                Evidence =
                {
                    ["factionCount"] = (int)factions.Length,
                    ["totalActors"] = total,
                    ["perFaction"] = string.Join(", ", perFaction),
                },
            };
        }
        catch (Exception ex) { return Errored("read.factionsAndActors", ex); }
    }

    // Computed properties: the same EntityProperties pipeline the injection work
    // hooked, read here through the verb-facing getters.
    private static ProbeCheck CheckEntityProperties(Actor actor)
    {
        try
        {
            var props = actor.GetCurrentProperties();
            if (props == null)
                return new ProbeCheck { Name = "read.entityProperties", Status = Fail, Detail = "GetCurrentProperties() returned null" };

            return new ProbeCheck
            {
                Name = "read.entityProperties",
                Status = Pass,
                Detail = "read vision/detection/concealment off the active actor's computed EntityProperties",
                Evidence =
                {
                    ["vision"] = props.GetVision(),
                    ["detection"] = props.GetDetection(),
                    ["concealment"] = props.GetConcealment(),
                },
            };
        }
        catch (Exception ex) { return Errored("read.entityProperties", ex); }
    }

    // Tile-to-tile line of sight between the active actor and the nearest other actor.
    private static ProbeCheck CheckLineOfSight(Actor actor)
    {
        try
        {
            var from = actor.GetTile();
            var other = FindOtherActor(actor);
            var to = other?.GetTile();
            if (from == null || to == null)
                return new ProbeCheck { Name = "read.lineOfSight", Status = Skipped, Detail = "need a second actor with a tile" };

            var los = from.HasLineOfSightTo(to);
            return new ProbeCheck
            {
                Name = "read.lineOfSight",
                Status = Pass,
                Detail = "queried Tile.HasLineOfSightTo between the active actor's tile and another actor's tile",
                Evidence =
                {
                    ["hasLineOfSight"] = los,
                    ["fromX"] = from.GetX(),
                    ["fromZ"] = from.GetZ(),
                    ["toX"] = to.GetX(),
                    ["toZ"] = to.GetZ(),
                },
            };
        }
        catch (Exception ex) { return Errored("read.lineOfSight", ex); }
    }

    // Hit-chance simulation against the nearest other actor with the active
    // actor's first active skill. Read-only; records the HitChance struct's text.
    private static ProbeCheck CheckHitChance(Actor actor)
    {
        try
        {
            var from = actor.GetTile();
            var target = FindOtherActor(actor);
            var targetTile = target?.GetTile();
            var skill = FirstActiveSkill(actor);
            if (from == null || targetTile == null || skill == null)
                return new ProbeCheck { Name = "query.hitChance", Status = Skipped, Detail = "need a target actor and an active skill" };

            HitChance hc = skill.GetHitchance(from, targetTile);
            return new ProbeCheck
            {
                Name = "query.hitChance",
                Status = Pass,
                Detail = "called Skill.GetHitchance(fromTile, targetTile) with all-optional args defaulted",
                Evidence =
                {
                    ["hitChance"] = hc.ToString(),
                },
            };
        }
        catch (Exception ex) { return Errored("query.hitChance", ex); }
    }

    // Path query: the RequestProcess -> FindPath -> ReturnProcess bracket. dest is
    // a free tile adjacent to the actor; records the bool result and path length.
    private static ProbeCheck CheckPathQuery(Actor actor)
    {
        try
        {
            var mgr = PathfindingManager.Get();
            var map = TacticalManager.Get()?.GetMap();
            var from = actor.GetTile();
            if (mgr == null || map == null || from == null)
                return new ProbeCheck { Name = "query.path", Status = Skipped, Detail = "no pathfinding manager, map, or actor tile" };

            var dest = FindAdjacentFreeTile(map, from);
            if (dest == null)
                return new ProbeCheck { Name = "query.path", Status = Skipped, Detail = "no free adjacent tile" };

            var process = mgr.RequestProcess();
            if (process == null)
                return new ProbeCheck { Name = "query.path", Status = Fail, Detail = "RequestProcess() returned null" };

            var result = new Il2CppSystem.Collections.Generic.List<Vector3>();
            bool found;
            try
            {
                found = process.FindPath(from, dest, actor, result, (Direction)0);
            }
            finally
            {
                mgr.ReturnProcess(process);
            }

            return new ProbeCheck
            {
                Name = "query.path",
                Status = Pass,
                Detail = "RequestProcess -> FindPath(from, adjacentDest, actor, result, dir) -> ReturnProcess; result is a list of world-space Vector3 waypoints",
                Evidence =
                {
                    ["found"] = found,
                    ["resultWaypointCount"] = result.Count,
                    ["destX"] = dest.GetX(),
                    ["destZ"] = dest.GetZ(),
                },
            };
        }
        catch (Exception ex) { return Errored("query.path", ex); }
    }

    // The movement-range Task. Deliberately does NOT block on .Result: records
    // whether the Task is already complete, answering the "is this synchronous"
    // half of the blocking question without risking a main-thread deadlock.
    private static ProbeCheck CheckMovementRangeTask(Actor actor)
    {
        try
        {
            var task = actor.CalculateTilesInMovementRange();
            if (task == null)
                return new ProbeCheck { Name = "query.movementRangeTask", Status = Fail, Detail = "CalculateTilesInMovementRange() returned null" };

            return new ProbeCheck
            {
                Name = "query.movementRangeTask",
                Status = Pass,
                Detail = "called CalculateTilesInMovementRange() and inspected the Task without blocking on .Result",
                Evidence =
                {
                    ["taskNonNull"] = true,
                    ["isCompleted"] = task.IsCompleted,
                    ["isFaulted"] = task.IsFaulted,
                },
            };
        }
        catch (Exception ex) { return Errored("query.movementRangeTask", ex); }
    }

    // The anchor verb, end to end. Opt-in because it mutates the battlefield:
    // spawns a unit from the active actor's own template+faction onto a free
    // adjacent tile, records the spawned unit, then despawns it via Die(quiet) to
    // leave the mission clean.
    private static ProbeCheck CheckSpawnUnit(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "mutate.spawnUnit", Status = Skipped, Detail = "opt-in: spawns then despawns a unit. Enable the verbs-spawn toggle in the dev file." };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "mutate.spawnUnit", Status = Skipped, Detail = "no manager, map, or tile" };
            var template = actor.GetTemplate();
            if (template == null)
                return new ProbeCheck { Name = "mutate.spawnUnit", Status = Skipped, Detail = "no entity template" };
            var faction = actor.GetFaction();

            var tile = FindAdjacentFreeTile(map, from);
            if (tile == null)
                return new ProbeCheck { Name = "mutate.spawnUnit", Status = Skipped, Detail = "no free adjacent tile to spawn onto" };

            Actor spawned = null;
            var ok = tm.TrySpawnUnit(faction, template, tile, out spawned);

            var check = new ProbeCheck
            {
                Name = "mutate.spawnUnit",
                Status = ok && spawned != null ? Pass : Fail,
                Detail = "TrySpawnUnit(faction, activeActorTemplate, freeTile, out unit) on the active actor's faction, then despawned via Die(quiet)",
            };
            check.Evidence["returned"] = ok;
            check.Evidence["spawnedNonNull"] = spawned != null;
            check.Evidence["spawnTileX"] = tile.GetX();
            check.Evidence["spawnTileZ"] = tile.GetZ();

            if (spawned != null)
            {
                check.Evidence["spawnedHp"] = spawned.GetHitpoints();
                check.Evidence["spawnedFaction"] = spawned.GetFaction().ToString();
                try
                {
                    spawned.Die(true);
                    check.Evidence["despawned"] = true;
                    check.Evidence["hpAfterDie"] = SafeHp(spawned);
                }
                catch (Exception ex)
                {
                    check.Evidence["despawnError"] = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            return check;
        }
        catch (Exception ex) { return Errored("mutate.spawnUnit", ex); }
    }

    // Characterisation: does the game refuse a spawn onto an occupied tile? Spawns
    // the active actor's template+faction onto the active actor's own (occupied)
    // tile and records whether TrySpawnUnit refused. Records behaviour, does not
    // judge it; cleans up on the unexpected case that it succeeds.
    private static ProbeCheck CheckSpawnOnOccupiedTile(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "spawnRule.occupiedTile", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            var tm = TacticalManager.Get();
            var occupied = actor.GetTile();
            if (tm == null || occupied == null || !occupied.HasActor())
                return new ProbeCheck { Name = "spawnRule.occupiedTile", Status = Skipped, Detail = "active actor's tile is not occupied" };

            var ok = tm.TrySpawnUnit(actor.GetFaction(), actor.GetTemplate(), occupied, out var spawned);
            if (ok && spawned != null)
                spawned.Die(true);

            return new ProbeCheck
            {
                Name = "spawnRule.occupiedTile",
                Status = Pass,
                Detail = "TrySpawnUnit onto the active actor's own (occupied) tile, to learn whether the game enforces occupancy",
                Evidence =
                {
                    ["occupancyEnforced"] = !ok,
                    ["returned"] = ok,
                },
            };
        }
        catch (Exception ex) { return Errored("spawnRule.occupiedTile", ex); }
    }

    // Characterisation: does the game refuse a spawn onto a blocked tile?
    private static ProbeCheck CheckSpawnOnBlockedTile(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "spawnRule.blockedTile", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "spawnRule.blockedTile", Status = Skipped, Detail = "no manager, map, or tile" };

            var blocked = FindBlockedTileNear(map, from);
            if (blocked == null)
                return new ProbeCheck { Name = "spawnRule.blockedTile", Status = Skipped, Detail = "no blocked tile within range" };

            var ok = tm.TrySpawnUnit(actor.GetFaction(), actor.GetTemplate(), blocked, out var spawned);
            if (ok && spawned != null)
                spawned.Die(true);

            return new ProbeCheck
            {
                Name = "spawnRule.blockedTile",
                Status = Pass,
                Detail = "TrySpawnUnit onto a blocked tile, to learn whether the game refuses blocked destinations",
                Evidence =
                {
                    ["blockedRejected"] = !ok,
                    ["returned"] = ok,
                    ["tileX"] = blocked.GetX(),
                    ["tileZ"] = blocked.GetZ(),
                },
            };
        }
        catch (Exception ex) { return Errored("spawnRule.blockedTile", ex); }
    }

    // Characterisation: can a unit be spawned for a faction other than the active
    // actor's? Spawns a foreign-faction unit on a free tile and despawns it.
    private static ProbeCheck CheckSpawnForeignFaction(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "spawnRule.foreignFaction", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "spawnRule.foreignFaction", Status = Skipped, Detail = "no manager, map, or tile" };
            if (!TryFindForeignFaction(actor, out var foreign))
                return new ProbeCheck { Name = "spawnRule.foreignFaction", Status = Skipped, Detail = "no other in-play faction" };

            var tile = FindAdjacentFreeTile(map, from);
            if (tile == null)
                return new ProbeCheck { Name = "spawnRule.foreignFaction", Status = Skipped, Detail = "no free adjacent tile" };

            var ok = tm.TrySpawnUnit(foreign, actor.GetTemplate(), tile, out var spawned);
            var check = new ProbeCheck
            {
                Name = "spawnRule.foreignFaction",
                Status = Pass,
                Detail = "TrySpawnUnit for a faction other than the active actor's, to learn whether spawn faction is unconstrained",
            };
            check.Evidence["foreignFactionRequested"] = foreign.ToString();
            check.Evidence["spawned"] = ok && spawned != null;
            if (spawned != null)
            {
                check.Evidence["spawnedFaction"] = spawned.GetFaction().ToString();
                spawned.Die(true);
            }
            return check;
        }
        catch (Exception ex) { return Errored("spawnRule.foreignFaction", ex); }
    }

    // Characterisation: what does Die do observably? Spawns a throwaway unit, then
    // records the faction roster count and the tile's occupancy before and after
    // Die(quiet), plus the unit's HP after.
    private static ProbeCheck CheckDieBehaviour(Actor actor)
    {
        if (!IsSpawnEnabled())
            return new ProbeCheck { Name = "behaviour.die", Status = Skipped, Detail = "opt-in: needs the verbs-spawn toggle" };

        try
        {
            if (!TryTacticalContext(actor, out var tm, out var map, out var from))
                return new ProbeCheck { Name = "behaviour.die", Status = Skipped, Detail = "no manager, map, or tile" };

            var tile = FindAdjacentFreeTile(map, from);
            if (tile == null)
                return new ProbeCheck { Name = "behaviour.die", Status = Skipped, Detail = "no free adjacent tile" };

            var faction = actor.GetFaction();
            if (!tm.TrySpawnUnit(faction, actor.GetTemplate(), tile, out var spawned) || spawned == null)
                return new ProbeCheck { Name = "behaviour.die", Status = Skipped, Detail = "spawn for the probe failed" };

            var rosterBefore = FactionActorCount(faction);
            var tileOccupiedBefore = tile.HasActor();

            spawned.Die(true);

            return new ProbeCheck
            {
                Name = "behaviour.die",
                Status = Pass,
                Detail = "spawned a throwaway unit, then recorded roster count and tile occupancy before/after Die(quiet) to characterise its observable effects",
                Evidence =
                {
                    ["rosterWithSpawned"] = rosterBefore,
                    ["rosterAfterDie"] = FactionActorCount(faction),
                    ["tileOccupiedBeforeDie"] = tileOccupiedBefore,
                    ["tileOccupiedAfterDie"] = tile.HasActor(),
                    ["hpAfterDie"] = SafeHp(spawned),
                },
            };
        }
        catch (Exception ex) { return Errored("behaviour.die", ex); }
    }

    // The nearest blocked tile to origin, or null. Walks rings outward (so the
    // closest blocker wins) across a wide radius, since blocked tiles can be anywhere
    // on the map, not just beside the active actor. Out-of-bounds coordinates return
    // a null tile and are skipped.
    private static Tile FindBlockedTileNear(Map map, Tile origin)
    {
        int ox = origin.GetX(), oz = origin.GetZ();
        for (var r = 1; r <= 60; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dz = -r; dz <= r; dz++)
                {
                    if (System.Math.Abs(dx) != r && System.Math.Abs(dz) != r) continue;
                    Tile t;
                    try { t = map.GetTile(ox + dx, oz + dz); }
                    catch { continue; }
                    if (t != null && t.IsBlocked())
                        return t;
                }
        return null;
    }

    // The first faction other than self's that has actors in play, or false.
    private static bool TryFindForeignFaction(Actor self, out FactionType faction)
    {
        faction = default;
        try
        {
            var factions = TacticalManager.Get()?.GetFactions();
            if (factions == null) return false;
            var mine = self.GetFaction();
            for (var i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f == null) continue;
                var type = f.GetFactionType();
                var actors = f.GetActors();
                if (type != mine && actors != null && actors.Count > 0)
                {
                    faction = type;
                    return true;
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }

    // The number of actors a faction currently has, or -1 if unreadable.
    private static int FactionActorCount(FactionType faction)
    {
        try
        {
            var factions = TacticalManager.Get()?.GetFactions();
            if (factions == null) return -1;
            for (var i = 0; i < factions.Length; i++)
            {
                var f = factions[i];
                if (f != null && f.GetFactionType() == faction)
                {
                    var actors = f.GetActors();
                    return actors != null ? actors.Count : 0;
                }
            }
        }
        catch { /* best effort */ }
        return -1;
    }

    private static int SafeHp(Actor a)
    {
        try { return a.GetHitpoints(); }
        catch { return -1; }
    }

    // The manager, map, and the actor's tile in one acquisition; false (with the
    // outs left null) if any is missing. The spawn-family probes all open with this.
    private static bool TryTacticalContext(Actor actor, out TacticalManager tm, out Map map, out Tile from)
    {
        tm = TacticalManager.Get();
        map = tm != null ? tm.GetMap() : null;
        from = actor.GetTile();
        return tm != null && map != null && from != null;
    }

    // The first other actor on the field, for LOS and hit-chance targets.
    private static Actor FindOtherActor(Actor self)
    {
        try
        {
            var factions = TacticalManager.Get()?.GetFactions();
            if (factions == null) return null;
            for (var i = 0; i < factions.Length; i++)
            {
                var actors = factions[i]?.GetActors();
                if (actors == null) continue;
                for (var j = 0; j < actors.Count; j++)
                {
                    var a = actors[j];
                    if (a != null && a.Pointer != self.Pointer && a.GetTile() != null)
                        return a;
                }
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static Skill FirstActiveSkill(Actor actor)
    {
        try
        {
            var skills = actor.GetSkills()?.QueryActives();
            return skills != null && skills.Count > 0 ? skills[0] : null;
        }
        catch { return null; }
    }

    // A tile adjacent to origin that is on the map, not blocked, and unoccupied.
    private static Tile FindAdjacentFreeTile(Map map, Tile origin)
    {
        int ox = origin.GetX(), oz = origin.GetZ();
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                Tile t;
                try { t = map.GetTile(ox + dx, oz + dz); }
                catch { continue; }
                if (t != null && !t.HasActor() && !t.IsBlocked())
                    return t;
            }
        return null;
    }

    private static bool TryGetActiveActor(out Actor actor) => InspectionReporter.TryGetActiveActor(out actor);

    private static ProbeCheck Errored(string name, Exception ex) => InspectionReporter.Errored(name, ex);

    private static void Emit(ProbeReport report, ProbeCheck check, MelonLogger.Instance log)
        => InspectionReporter.Emit(report, check, log, "verbs");
}
