using System;
using System.Reflection;
using HarmonyLib;
using Il2CppMenace.Items;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Publishes the strategy-layer moments that have no C# event (leader hired /
/// dismissed / perma-death / perk gained, operation finished, black-market item
/// added) by Harmony-patching the game methods that perform them. The hook bus is
/// created after these patches install, so it is handed in via the static
/// <see cref="Bus"/> and the postfixes publish through it once it is set.
/// </summary>
internal sealed class StrategyHarmonyPatch : IHarmonyPatchModule
{
    internal static InProcessHookBus Bus;
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        Patch(harmony, "Il2CppMenace.Strategy.Roster", "HireLeader", nameof(HireLeaderPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.Roster", "TryDismissLeader", nameof(TryDismissLeaderPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.Roster", "OnPermanentDeath", nameof(OnPermanentDeathPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.BaseUnitLeader", "AddPerk", nameof(AddPerkPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.OperationsManager", "OnOperationFinished", nameof(OnOperationFinishedPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.Operation", "StartOperation", nameof(StartOperationPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.BlackMarket", "AddItem", nameof(AddItemPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.BlackMarket", "FillUp", nameof(FillUpPostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, string typeName, string method, string postfix)
        => HarmonyPatching.TryPostfix(harmony, typeName, method, typeof(StrategyHarmonyPatch), postfix, _log, "strategy hooks");

    private static void Publish<T>(T context) where T : class
    {
        var bus = Bus;
        if (bus != null && bus.HasSubscribers<T>())
            bus.Publish(context);
    }

    private static void HireLeaderPostfix(BaseUnitLeader __result)
    {
        if (__result != null)
            Publish(new Jiangyu.Sdk.LeaderHiredContext { Leader = __result });
    }

    private static void TryDismissLeaderPostfix(BaseUnitLeader __0, bool __result)
    {
        if (__result && __0 != null)
            Publish(new Jiangyu.Sdk.LeaderDismissedContext { Leader = __0 });
    }

    private static void OnPermanentDeathPostfix(BaseUnitLeader __0)
    {
        if (__0 != null)
            Publish(new Jiangyu.Sdk.LeaderPermadeathContext { Leader = __0 });
    }

    private static void AddPerkPostfix(BaseUnitLeader __instance, PerkTemplate __0)
    {
        if (__instance != null)
            Publish(new Jiangyu.Sdk.LeaderPerkAddedContext { Leader = __instance, Perk = __0 });
    }

    private static void OnOperationFinishedPostfix(Operation __0)
    {
        if (__0 != null)
            Publish(new Jiangyu.Sdk.OperationFinishedContext { Operation = __0 });
    }

    private static void AddItemPostfix(BaseItem __0)
    {
        if (__0 != null)
            Publish(new Jiangyu.Sdk.BlackMarketItemAddedContext { Item = __0 });
    }

    private static void StartOperationPostfix(Operation __instance, Mission __0)
    {
        if (__instance != null)
            Publish(new Jiangyu.Sdk.OperationStartedContext { Operation = __instance, Mission = __0 });
    }

    private static void FillUpPostfix()
        => Publish(new Jiangyu.Sdk.BlackMarketRestockedContext());
}
