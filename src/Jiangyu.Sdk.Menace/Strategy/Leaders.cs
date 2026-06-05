using Il2CppMenace.States;
using Il2CppMenace.Strategy;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Roster/leader verbs. <c>Hire</c> and <c>Dismiss</c> are generated from the verb
/// manifest into this partial class.
/// </summary>
public static partial class Leaders
{
    /// <summary>The campaign roster, or null when no campaign is loaded.</summary>
    public static Roster Roster => StrategyState.Get()?.Roster;
}
