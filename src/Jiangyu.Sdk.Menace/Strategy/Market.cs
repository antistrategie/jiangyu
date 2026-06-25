using Il2CppMenace.Items;
using Il2CppMenace.States;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Strategy;

/// <summary>Black-market verbs.</summary>
public static partial class Market
{
    /// <summary>
    /// Restock the black market, decrementing remaining timeouts and resetting the restock counter,
    /// then return the number of regular-stock items afterwards. This restocks only; it does not
    /// replicate the rest of an operation-finished pass. It runs the game's <c>Restock</c>, so it
    /// publishes the <c>BlackMarketRestocked</c> hook. Do not call it from a handler of that hook.
    /// </summary>
    [MutatingVerb]
    public static int Refresh()
    {
        var market = StrategyState.Get().BlackMarket;
        market.Restock(true, true);
        var items = new Il2CppSystem.Collections.Generic.List<BaseItem>();
        market.GetInstances(items, false);
        return items.Count;
    }
}
