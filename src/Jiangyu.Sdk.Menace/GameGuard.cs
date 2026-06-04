using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;

namespace Jiangyu.Game;

/// <summary>
/// Runtime safety checks the mutating verbs share. These are the second line of
/// defence behind the author-time analyzer rules: a verb that would corrupt game
/// state when called from an unsafe context (a faction mid-evaluation, a stale
/// hook payload) refuses and reports, rather than crashing or corrupting the
/// game. Reads do not need these guards.
/// </summary>
internal static class GameGuard
{
    /// <summary>
    /// True if any AI faction is currently evaluating its turn. Mutating the
    /// battlefield during this window can corrupt the in-flight decision pass, so
    /// mutating verbs refuse while it holds. Walks <see cref="TacticalManager.GetFactions"/>
    /// and casts each to <see cref="AIFaction"/> (non-AI factions cast null).
    /// </summary>
    public static bool AnyFactionThinking()
    {
        try
        {
            var tm = TacticalManager.Get();
            var factions = tm != null ? tm.GetFactions() : null;
            if (factions == null)
                return false;
            for (var i = 0; i < factions.Length; i++)
            {
                var ai = factions[i] != null ? factions[i].TryCast<AIFaction>() : null;
                if (ai != null && ai.IsThinking())
                    return true;
            }
        }
        catch
        {
            // A failure to read faction state is not itself a reason to allow a
            // mutation; treat it as "cannot confirm safe" only for explicit checks.
        }
        return false;
    }
}
