using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Tactical;

/// <summary>
/// Combat queries and actions. The actions route through the game's own combat
/// pipeline; calling any of these in a context the game does not expect is the
/// caller's responsibility.
/// </summary>
public static partial class Combat
{
    /// <summary>The 0..1 hit chance of <paramref name="skill"/> from <paramref name="from"/> at <paramref name="target"/>.</summary>
    public static float HitChance(Skill skill, Tile from, Tile target)
        => skill.GetHitchance(from, target).FinalValue;

    /// <summary>Whether <paramref name="from"/> has line of sight to <paramref name="to"/>.</summary>
    public static bool CanSee(Tile from, Tile to) => from.HasLineOfSightTo(to);

    /// <summary>
    /// Deal <paramref name="amount"/> damage to <paramref name="target"/> through the
    /// game's damage pipeline. Returns the damage the pipeline resolved (after handlers).
    /// </summary>
    [MutatingVerb]
    public static int Damage(Actor target, int amount, Actor attacker = null, Skill skill = null)
    {
        var resolved = target.OnDamageReceived(attacker, skill, new DamageInfo { Damage = amount });
        return resolved != null ? resolved.Damage : 0;
    }

    /// <summary>
    /// Restore <paramref name="amount"/> hitpoints to <paramref name="target"/> (negative
    /// damage; the game clamps at max). Returns the hitpoints the pipeline resolved.
    /// </summary>
    [MutatingVerb]
    public static int Heal(Actor target, int amount)
    {
        var resolved = target.OnDamageReceived(null, null, new DamageInfo { Damage = -amount });
        return resolved != null ? -resolved.Damage : 0;
    }

    /// <summary>Apply <paramref name="amount"/> suppression to <paramref name="target"/>.</summary>
    [MutatingVerb]
    public static void Suppress(Actor target, float amount, bool direct = false, Actor suppressor = null, Skill skill = null)
        => target.ApplySuppression(amount, direct, suppressor, skill);

    /// <summary><paramref name="actor"/>'s effective vision range, with all current modifiers applied.</summary>
    public static int Vision(Actor actor) => actor.GetCurrentProperties().GetVision();

    /// <summary><paramref name="actor"/>'s effective detection rating (clamped), with all current modifiers applied.</summary>
    public static int Detection(Actor actor) => actor.GetCurrentProperties().GetDetection();

    /// <summary><paramref name="actor"/>'s effective concealment, with all current modifiers applied.</summary>
    public static int Concealment(Actor actor) => actor.GetCurrentProperties().GetConcealment();

    /// <summary><paramref name="actor"/>'s effective accuracy, with all current modifiers applied.</summary>
    public static float Accuracy(Actor actor) => actor.GetCurrentProperties().GetAccuracy();

    /// <summary><paramref name="actor"/>'s effective per-shot damage, with all current modifiers applied.</summary>
    public static float Damage(Actor actor) => actor.GetCurrentProperties().GetDamage();

    /// <summary><paramref name="actor"/>'s effective armour value, with all current modifiers applied.</summary>
    public static int Armor(Actor actor) => actor.GetCurrentProperties().GetArmor();

    /// <summary><paramref name="actor"/>'s effective armour penetration, with all current modifiers applied.</summary>
    public static float ArmorPenetration(Actor actor) => actor.GetCurrentProperties().GetArmorPenetration();

    /// <summary>The suppression <paramref name="actor"/> deals, with all current modifiers applied.</summary>
    public static float SuppressionDealt(Actor actor) => actor.GetCurrentProperties().GetSuppression();

    /// <summary><paramref name="actor"/>'s effective max hitpoints, with all current modifiers applied.</summary>
    public static int MaxHitpoints(Actor actor) => actor.GetCurrentProperties().GetMaxHitpoints();

    /// <summary><paramref name="actor"/>'s effective discipline, with all current modifiers applied.</summary>
    public static int Discipline(Actor actor) => actor.GetCurrentProperties().GetDiscipline();

    /// <summary><paramref name="actor"/>'s effective action-point pool, with all current modifiers applied.</summary>
    public static int ActionPoints(Actor actor) => actor.GetCurrentProperties().GetActionPoints();

    /// <summary>Whether <paramref name="actor"/> may move under its current properties.</summary>
    public static bool CanMove(Actor actor) => actor.GetCurrentProperties().CanMove();

    /// <summary>Whether <paramref name="actor"/> is rooted (cannot leave its tile) under its current properties.</summary>
    public static bool IsRooted(Actor actor) => actor.GetCurrentProperties().IsRooted();
}
