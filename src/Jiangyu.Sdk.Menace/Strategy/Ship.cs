using Il2CppMenace.States;
using Il2CppMenace.Strategy;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Ship-upgrade verbs. The slot reads, counts and equip/unequip mutations are generated
/// from the verb manifest into this partial class.
/// </summary>
public static partial class Ship
{
    /// <summary>The ship-upgrade subsystem, or null when no campaign is loaded.</summary>
    public static ShipUpgrades Upgrades => StrategyState.Get()?.ShipUpgrades;
}
