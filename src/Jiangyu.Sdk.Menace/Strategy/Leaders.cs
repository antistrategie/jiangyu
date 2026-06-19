using System.Collections.Generic;
using Il2CppMenace.Items;
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

    /// <summary>
    /// The leaders currently in the campaign roster, each handed back as a live handle
    /// so it can be threaded into a later verb as <c>{ref:"..."}</c>.
    /// </summary>
    public static IReadOnlyList<BaseUnitLeader> Hired()
    {
        var result = new List<BaseUnitLeader>();
        var hired = StrategyState.Get()?.Roster?.m_HiredLeaders;
        if (hired != null)
            for (var i = 0; i < hired.Count; i++)
                result.Add(hired[i]);
        return result;
    }

    /// <summary>
    /// The items in a leader's equipment container (armour, weapons, accessories), each as
    /// a live handle. Read-only probe of what a form actually has equipped.
    /// </summary>
    public static IReadOnlyList<Item> EquippedItems(BaseUnitLeader self)
    {
        var result = new List<Item>();
        var items = self?.GetItems()?.GetAllItems();
        if (items != null)
            for (var i = 0; i < items.Count; i++)
                result.Add(items[i]);
        return result;
    }

    /// <summary>
    /// Equip an already-owned item instance onto a leader's container (creating a slot if needed). Use
    /// with an <see cref="Inventory.UnusedInstance"/> handle to equip-from-existing without minting a
    /// fresh (unregistered) item. Returns false when the add is rejected.
    /// </summary>
    [global::Jiangyu.Sdk.MutatingVerb]
    public static bool Equip(BaseUnitLeader self, Item item)
        => self.GetItems().Add(item, true);

    /// <summary>Unequip one item from a leader's container. Returns false when it is not equipped.</summary>
    [global::Jiangyu.Sdk.MutatingVerb]
    public static bool Unequip(BaseUnitLeader self, Item item)
        => self.GetItems().TryUnequip(item, true);
}
