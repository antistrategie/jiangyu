using System.Collections.Generic;
using Il2CppMenace.Items;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Jiangyu.Sdk;

namespace Jiangyu.Game.Strategy;

/// <summary>
/// Owned-inventory verbs. Counts and item reads are generated from the verb manifest into
/// this partial class; the overloaded add/remove mutations and the list-returning instance
/// accessor are hand-written here.
/// </summary>
public static partial class Inventory
{
    /// <summary>The owned-items subsystem, or null when no campaign is loaded.</summary>
    public static OwnedItems Owned => StrategyState.Get()?.OwnedItems;

    /// <summary>
    /// Add an instance of <paramref name="template"/> to the campaign inventory. Returns the
    /// created instance. <paramref name="showDialog"/> surfaces the game's pickup dialog.
    /// </summary>
    [MutatingVerb]
    public static BaseItem AddItem(BaseItemTemplate template, bool showDialog = false)
        => StrategyState.Get().OwnedItems.AddItem(template, showDialog);

    /// <summary>Remove a specific owned item instance. Returns false when it is not owned.</summary>
    [MutatingVerb]
    public static bool RemoveItem(BaseItem item)
        => StrategyState.Get().OwnedItems.RemoveItem(item);

    /// <summary>Remove one owned instance of <paramref name="template"/>. Returns false when none is owned.</summary>
    [MutatingVerb]
    public static bool RemoveItem(BaseItemTemplate template)
        => StrategyState.Get().OwnedItems.RemoveItem(template);

    /// <summary>The owned instances of <paramref name="template"/>.</summary>
    public static IReadOnlyList<BaseItem> Instances(BaseItemTemplate template)
    {
        var result = new List<BaseItem>();
        var raw = StrategyState.Get().OwnedItems.GetInstances(template)
            .TryCast<Il2CppSystem.Collections.Generic.List<BaseItem>>();
        if (raw != null)
            for (var i = 0; i < raw.Count; i++)
                result.Add(raw[i]);
        return result;
    }
}
