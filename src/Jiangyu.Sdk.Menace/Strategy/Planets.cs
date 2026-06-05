using Il2CppMenace.States;
using Il2CppMenace.Strategy;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Planet verbs. Manager lookups, per-planet menace reads and the menace mutations are
/// generated from the verb manifest into this partial class; the accessor and the
/// optional-status local-faction read are hand-written here.
/// </summary>
public static partial class Planets
{
    /// <summary>The planet manager, or null when no campaign is loaded.</summary>
    public static PlanetManager Manager => StrategyState.Get()?.Planets;

    /// <summary>The faction local to <paramref name="planet"/> (any status), or null.</summary>
    public static StoryFaction LocalFaction(Planet planet)
        => planet.GetLocalFaction(default);
}
