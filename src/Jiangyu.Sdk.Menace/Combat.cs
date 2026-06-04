using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;

namespace Jiangyu.Game;

/// <summary>
/// Read-only combat queries. Safe to call from any context, including AI
/// evaluation predicates, because they do not mutate state.
/// </summary>
public static class Combat
{
    /// <summary>
    /// The hit chance of <paramref name="skill"/> fired from <paramref name="from"/>
    /// at <paramref name="target"/>, as a 0..1 value. Reads
    /// <see cref="HitChance.FinalValue"/> (the struct's <c>ToString</c> yields only
    /// the type name). Returns 0 for any null argument.
    /// </summary>
    public static float HitChance(Skill skill, Tile from, Tile target)
        => skill != null && from != null && target != null
            ? skill.GetHitchance(from, target).FinalValue
            : 0f;

    /// <summary>Whether <paramref name="from"/> has line of sight to <paramref name="to"/>.</summary>
    public static bool CanSee(Tile from, Tile to)
        => from != null && to != null && from.HasLineOfSightTo(to);
}
