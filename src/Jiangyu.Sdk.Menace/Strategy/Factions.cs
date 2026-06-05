using System.Collections.Generic;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Story-faction verbs. The per-faction trust/status reads and mutations are generated
/// from the verb manifest into this partial class; the overloaded lookup and the
/// list-returning unlocked-upgrades accessor are hand-written here.
/// </summary>
public static partial class Factions
{
    /// <summary>The story-faction subsystem, or null when no campaign is loaded.</summary>
    public static StoryFactions All => StrategyState.Get()?.StoryFactions;

    /// <summary>The faction of the given type, or null.</summary>
    public static StoryFaction Get(StoryFactionType faction)
        => StrategyState.Get().StoryFactions.GetFaction(faction);

    /// <summary>The ship upgrades <paramref name="faction"/> has unlocked.</summary>
    public static IReadOnlyList<ShipUpgradeTemplate> UnlockedUpgrades(StoryFaction faction)
    {
        var result = new List<ShipUpgradeTemplate>();
        var raw = faction.GetUnlockedUpgrades()
            .TryCast<Il2CppSystem.Collections.Generic.List<ShipUpgradeTemplate>>();
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }
}
