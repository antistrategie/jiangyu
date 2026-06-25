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
    /// created instance. <paramref name="showDialog"/> surfaces the game's pickup dialog,
    /// and <paramref name="showItemSlotInDialog"/> shows the item's slot within that dialog.
    /// </summary>
    [MutatingVerb]
    public static BaseItem AddItem(BaseItemTemplate template, bool showDialog = false, bool showItemSlotInDialog = false)
        => StrategyState.Get().OwnedItems.AddItem(template, showDialog, showItemSlotInDialog);

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

    /// <summary>
    /// Every owned item instance across all templates, each as a live handle. Read-only
    /// probe of the whole shared inventory: thread a handle into a later verb, or count
    /// the returned list to confirm a swap left the inventory size unchanged.
    /// </summary>
    public static IReadOnlyList<BaseItem> All()
    {
        var result = new List<BaseItem>();
        var owned = StrategyState.Get()?.OwnedItems;
        if (owned == null)
            return result;
        var buffer = new Il2CppSystem.Collections.Generic.List<BaseItem>();
        owned.GetInstances(buffer);
        for (var i = 0; i < buffer.Count; i++)
            result.Add(buffer[i]);
        return result;
    }

    /// <summary>
    /// An owned-but-unequipped instance of <paramref name="template"/>, or null when every
    /// owned copy is in use. Returned as a live handle to pass to <c>Leaders.Equip</c>, so
    /// equip-from-existing reuses a registered instance instead of minting a new one.
    /// </summary>
    public static Item UnusedInstance(ItemTemplate template)
        => StrategyState.Get()?.OwnedItems?.GetUnusedInstance(template, false);
}
