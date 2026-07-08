using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (skills): dump the usability state of every player-controlled actor's skills in the
// running mission. Built to diagnose "skills greyed / unusable even with AP": IsUsable() is the
// gate the skill bar greys on, and the neighbouring fields (uses left, AP cost, AP consumer bound,
// disabled-by-defect) say WHY it is false. Read-only, dev-loader only, main thread.
internal static class SkillStateInspector
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
                    // One throwing actor (unbound properties is the very state this dump exists to
                    // surface) must not abandon the actors already collected: isolate each.
                    try
                    {
                        var actor = list[a];
                        if (actor == null || !actor.IsPlayerControlled(true) || actor.GetHitpoints() <= 0)
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
        var ap = SafeInt(() => actor.GetActionPoints());
        var apMax = SafeInt(() => actor.GetActionPointsAtTurnStart());
        var propsNull = SafeBool(() => actor.GetCurrentProperties() == null, onThrow: true);

        var skills = new List<object>();
        SkillContainer container = null;
        try { container = actor.GetSkills(); } catch { }
        var all = container?.GetAllSkills();
        // GetUsesLeftThisTurn does AP arithmetic, so only feed it a real value: if the AP read threw,
        // 0 keeps the game maths sane rather than a garbage sentinel.
        var apForUses = ap is int a ? a : 0;
        for (var i = 0; all != null && i < all.Count; i++)
        {
            var skill = all[i]?.TryCast<Skill>();
            if (skill != null)
                skills.Add(DumpSkill(skill, apForUses));
        }
        return new { ap, apMax, propsNull, skills };
    }

    private static object DumpSkill(Skill s, int ap) => new
    {
        id = SafeStr(() => s.GetID()),
        usable = SafeStr(() => s.IsUsable().ToString()),
        uses = SafeInt(() => s.GetUses()),
        maxUses = SafeInt(() => s.GetMaxUses()),
        usesLeftThisTurn = SafeInt(() => s.GetUsesLeftThisTurn(ap)),
        // Note: a global finalizer guards GetActionPointCost, so a throw here surfaces as the
        // substituted base cost, not a throw marker. IsUsable/apConsumerNull carry the real signal.
        apCost = SafeStr(() => s.GetActionPointCost().ToString()),
        apConsumerNull = SafeStr(() => (s.GetAPConsumer() == null).ToString()),
        disabledByDefect = SafeStr(() => s.IsDisabledByDefect.ToString()),
    };

    // The int, or a "<threw: ...>" marker so a failed getter is never mistaken for a real value
    // (matching SafeStr/SafeBool, which also make a throw visible rather than returning a sentinel).
    private static object SafeInt(Func<int> f)
    {
        try { return f(); }
        catch (Exception ex) { return $"<threw: {ex.GetType().Name}>"; }
    }

    private static string SafeStr(Func<string> f)
    {
        try { return f() ?? "<null>"; }
        catch (Exception ex) { return $"<threw: {ex.GetType().Name}>"; }
    }

    private static bool SafeBool(Func<bool> f, bool onThrow)
    {
        try { return f(); }
        catch { return onThrow; }
    }
}
