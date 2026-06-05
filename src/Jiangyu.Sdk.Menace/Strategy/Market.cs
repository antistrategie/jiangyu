using Il2CppMenace.Items;
using Il2CppMenace.States;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Strategy;

/// <summary>Black-market verbs.</summary>
public static partial class Market
{
    /// <summary>
    /// Refresh (restock) the black market through the game's <c>OnOperationFinished</c>.
    /// Returns the number of regular-stock items afterwards.
    /// </summary>
    [MutatingVerb]
    public static int Refresh()
    {
        var market = StrategyState.Get().BlackMarket;
        market.OnOperationFinished(null);
        var items = new Il2CppSystem.Collections.Generic.List<BaseItem>();
        market.GetInstances(items, false);
        return items.Count;
    }
}
