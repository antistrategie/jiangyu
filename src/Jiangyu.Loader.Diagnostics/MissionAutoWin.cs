using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using MelonLoader;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (winmission): kill every enemy and complete the primary objectives so the mission
// resolves as a win. Built for fast iteration, e.g. exercising enemy loot / reward drops. Killing
// goes through the normal damage pipeline so on-kill effects (and loot) fire as usual. Mutating,
// dev-loader only. Runs on the main thread (the bridge pump guarantees that).
internal static class MissionAutoWin
{
    public static object Run(MelonLogger.Instance log)
    {
        try
        {
            if (!TacticalManager.IsMissionRunning())
                return new { error = "no mission running" };
            var manager = TacticalManager.Get();
            if (manager == null)
                return new { error = "no tactical manager" };

            var killed = 0;
            var factions = manager.GetFactions();
            // Attribute the kills to a player unit so the game counts them as player kills: loot and
            // on-kill drop hooks fire for player-attributed deaths, not for an unattributed one.
            var killer = FindPlayerActor(factions);
            if (killer == null)
                log?.Warning("[winmission] no live player actor to attribute kills to; on-kill drop hooks will not fire");
            if (factions != null)
            {
                for (var i = 0; i < factions.Length; i++)
                {
                    var actors = factions[i]?.GetActors();
                    if (actors == null)
                        continue;

                    // Snapshot first: applying lethal damage mutates the live faction actor lists.
                    var snapshot = new List<Actor>(actors.Count);
                    for (var j = 0; j < actors.Count; j++)
                        snapshot.Add(actors[j]);

                    foreach (var actor in snapshot)
                    {
                        // Spare player units and their allied AI, only finish off pure enemies that
                        // are still standing.
                        if (actor == null || actor.IsPlayerControlled(true) || actor.GetHitpoints() <= 0)
                            continue;
                        try
                        {
                            actor.OnDamageReceived(killer, null, new DamageInfo { Damage = actor.GetHitpointsMax() + 9999 });
                            killed++;
                        }
                        catch (Exception ex) { log?.Warning($"[winmission] kill failed: {ex.Message}"); }
                    }
                }
            }

            // Completing the primary objectives is what resolves the mission as a victory.
            manager.GetMission()?.Objectives?.CompletePrimaryObjectives();

            log?.Msg($"[winmission] killed {killed} enemy actor(s) and completed primary objectives");
            return new { ok = true, killed };
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    // The first live player-side actor, used to attribute the forced kills to the player. Uses the
    // same IsPlayerControlled(true) predicate as the spare-check above (and as the on-kill drop
    // hooks' player-kill gate), so any unit we are not killing is a valid attributed killer. The
    // stricter IsPlayerControlled(false) could find none and leave kills unattributed, so no drop
    // hook would fire.
    private static Actor FindPlayerActor(BaseFaction[] factions)
    {
        if (factions == null)
            return null;
        for (var i = 0; i < factions.Length; i++)
        {
            var actors = factions[i]?.GetActors();
            if (actors == null)
                continue;
            for (var j = 0; j < actors.Count; j++)
            {
                var actor = actors[j];
                if (actor != null && actor.IsPlayerControlled(true) && actor.GetHitpoints() > 0)
                    return actor;
            }
        }
        return null;
    }
}
