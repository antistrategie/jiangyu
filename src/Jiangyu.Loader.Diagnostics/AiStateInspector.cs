using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (ai): dump every actor's AI agent state in the running mission. Built to diagnose
// "the AI never uses a usable skill": the behaviour list says whether a skill's AIConfig
// registered a behaviour at all, and the sleep/state fields say whether the agent is even being
// evaluated on its faction's turn. Read-only, dev-loader only, main thread.
internal static class AiStateInspector
{
    public static object Capture()
    {
        try
        {
            if (!TacticalManager.IsMissionRunning())
                return new { error = "no mission running" };
            var manager = TacticalManager.Get();
            if (manager == null)
                return new { error = "no tactical manager" };

            var actors = new List<object>();
            var factions = manager.GetFactions();
            for (var f = 0; factions != null && f < factions.Length; f++)
            {
                var list = factions[f]?.GetActors();
                for (var a = 0; list != null && a < list.Count; a++)
                {
                    try
                    {
                        var actor = list[a];
                        if (actor == null || actor.GetHitpoints() <= 0)
                            continue;
                        actors.Add(DumpActor(f, actor));
                    }
                    catch (Exception ex)
                    {
                        actors.Add(new { faction = f, index = a, error = $"{ex.GetType().Name}: {ex.Message}" });
                    }
                }
            }
            return new { ok = true, actors };
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static object DumpActor(int faction, Actor actor)
    {
        var entity = Safe(() => actor.GetTemplate()?.GetID());
        var tile = Safe(() => $"{actor.GetTile().GetX()},{actor.GetTile().GetZ()}");

        Agent agent = null;
        try { agent = actor.GetAgent(); } catch { }
        if (agent == null)
            return new { faction, entity, tile, agent = (object)null };

        var behaviours = new List<object>();
        var all = agent.GetBehaviors();
        for (var i = 0; all != null && i < all.Count; i++)
        {
            var b = all[i];
            if (b == null)
                continue;
            var type = Safe(() => b.GetIl2CppType().FullName);
            string skill = null;
            var sb = b.TryCast<SkillBehavior>();
            if (sb != null)
                skill = Safe(() => sb.GetSkill()?.GetID());
            behaviours.Add(new { type, skill });
        }

        return new
        {
            faction,
            entity,
            tile,
            state = Safe(() => agent.GetState().ToString()),
            sleeping = Safe(() => agent.IsSleeping().ToString()),
            sleepUntil = Safe(() => agent.m_SleepUntil.ToString("F1")),
            turnDone = Safe(() => agent.IsTurnDone.ToString()),
            deactivation = Safe(() => agent.FlaggedForDeactivation.ToString()),
            priority = Safe(() => agent.GetPriority().ToString("F2")),
            score = Safe(() => agent.GetScore().ToString()),
            behaviours,
        };
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "<null>"; }
        catch (Exception ex) { return $"<threw: {ex.GetType().Name}>"; }
    }
}
