using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (aiscore): force-evaluate every skill behaviour on every AI actor and report the
// score each returns right now, without waiting for that faction's turn. Exists to answer "why
// does the AI never pick this skill": a 0 pinpoints the scoring stage, and an exception names
// the method that dies. Calls OnEvaluate (which writes agent-internal targeting state) and then
// OnReset to drop that state; the game re-runs both on the agent's real turn, so the probe does
// not change what the AI will do. Dev-loader only, main thread.
internal static class AiScoreProbe
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
                    var actor = list[a];
                    if (actor == null)
                        continue;
                    try
                    {
                        if (actor.GetHitpoints() <= 0 || actor.GetAgent() == null)
                            continue;
                        actors.Add(DumpActor(actor));
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

    private static object DumpActor(Actor actor)
    {
        var agent = actor.GetAgent();
        var scores = new List<object>();
        var all = agent.GetBehaviors();
        for (var i = 0; all != null && i < all.Count; i++)
        {
            var b = all[i];
            var sb = b?.TryCast<SkillBehavior>();
            if (sb == null)
                continue;
            var type = Safe(() => b.GetIl2CppType().FullName);
            var skill = Safe(() => sb.GetSkill()?.GetID());
            string score, error = null;
            try
            {
                score = sb.OnEvaluate(actor).ToString();
            }
            catch (Exception ex)
            {
                score = null;
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            string resetError = null;
            try
            {
                sb.OnReset();
            }
            catch (Exception ex)
            {
                resetError = $"{ex.GetType().Name}: {ex.Message}";
            }
            scores.Add(new { type, skill, score, error, resetError });
        }
        var entity = Safe(() => actor.GetTemplate()?.GetID());
        var tile = Safe(() => $"{actor.GetTile().GetX()},{actor.GetTile().GetZ()}");
        return new { entity, tile, scores };
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "<null>"; }
        catch (Exception ex) { return $"<threw: {ex.GetType().Name}>"; }
    }
}
