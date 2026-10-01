using System;
using System.Collections.Generic;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// Bridges the strategy-layer (campaign meta-game) moments onto the hook bus. Unlike
/// the tactical events (one TacticalManager singleton), the strategy events are
/// scattered across per-instance types, and the factions populate progressively after
/// the StrategyState appears. The <c>StrategyState.OnAdded</c> postfix calls
/// <see cref="AttachState"/>, and the <c>StoryFactions.Init</c> and
/// <c>ProcessSaveState</c> postfixes call <see cref="SubscribeFactionsFrom"/>, so each
/// owner (the state, the squaddies collection, every faction) is subscribed once at load
/// time rather than by polling. Each owner is keyed by its native pointer. A new
/// StrategyState (a loaded save or a different campaign) appears at a new pointer, which
/// drops the prior session's bookkeeping so its owners re-hook.
/// </summary>
internal sealed class StrategyHookPublisher : HookPublisherBase
{
    private readonly HashSet<IntPtr> _subscribed = new();
    private int _factionsHooked;
    private IntPtr _attachedState;
    private int _lastAliveCount = -1;

    public StrategyHookPublisher(InProcessHookBus bus, IModHostLog log)
        : base(bus, log)
    {
    }

    // Drop the prior session's bookkeeping when a different StrategyState becomes active
    // (a loaded save or a new campaign appears at a new native pointer), so its owners
    // re-hook against the fresh objects instead of being skipped as already-seen against
    // stale (and possibly pointer-reused) entries.
    private void EnsureFreshState(IntPtr statePointer)
    {
        if (statePointer == IntPtr.Zero || statePointer == _attachedState)
            return;

        _attachedState = statePointer;
        _subscribed.Clear();
        _factionsHooked = 0;
        _lastAliveCount = -1;
        ClearHookedDelegates();
    }

    /// <summary>
    /// Subscribe the StrategyState and its squaddies collection, plus any factions
    /// already loaded. Driven by the <c>StrategyState.OnAdded</c> Harmony postfix.
    /// Idempotent: each owner is keyed by its native pointer and subscribed once.
    /// </summary>
    public void AttachState(StrategyState ss)
    {
        if (ss == null || ss.Pointer == IntPtr.Zero)
            return;

        EnsureFreshState(ss.Pointer);

        if (_subscribed.Add(ss.Pointer))
        {
            Hook<StrategyState.ConversationVarChangedDelegate>(ss.add_OnConversationVarChanged, (Action<string, int, int>)OnConversationVarChanged, "OnConversationVarChanged");
            Log.Debug("hooks: attached to StrategyState");
        }

        var squaddies = ss.Squaddies;
        if (squaddies != null && squaddies.Pointer != IntPtr.Zero && _subscribed.Add(squaddies.Pointer))
        {
            var owner = squaddies;
            Hook<Il2CppSystem.Action>(squaddies.add_OnSquaddiesChanged, (Action)(() => OnSquaddiesChanged(owner)), "OnSquaddiesChanged");
        }

        SubscribeFactionsFrom(ss.StoryFactions);
    }

    /// <summary>
    /// Subscribe every faction in <paramref name="factions"/> not yet hooked. Driven by
    /// the <c>StoryFactions.Init</c> and <c>StoryFactions.ProcessSaveState</c> postfixes,
    /// so factions that load after the StrategyState are picked up at load time rather
    /// than by polling. Idempotent via the per-pointer subscription set.
    /// </summary>
    public void SubscribeFactionsFrom(StoryFactions factions)
    {
        if (factions == null || factions.Pointer == IntPtr.Zero)
            return;

        // A faction collection can populate before its StrategyState is marked active, so
        // sync the fresh-state bookkeeping off the live state before subscribing.
        EnsureFreshState(StrategyState.Get()?.Pointer ?? IntPtr.Zero);

        try
        {
            foreach (var pair in factions.m_Factions)
                SubscribeFaction(pair.Value);
        }
        catch (Exception ex)
        {
            Log.Error($"hooks: failed to enumerate factions: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SubscribeFaction(StoryFaction faction)
    {
        if (faction == null || faction.Pointer == IntPtr.Zero || !_subscribed.Add(faction.Pointer))
            return;
        Hook<StoryFaction.TrustChangedDelegate>(faction.add_OnTrustChanged, (Action<StoryFaction, int, int>)OnFactionTrustChanged, "OnTrustChanged");
        Hook<StoryFaction.StatusChangedDelegate>(faction.add_OnStatusChanged, (Action<StoryFaction, StoryFactionStatus, StoryFactionStatus>)OnFactionStatusChanged, "OnStatusChanged");
        Hook<StoryFaction.UpgradeUnlockedDelegate>(faction.add_OnUpgradeUnlocked, (Action<StoryFaction, ShipUpgradeTemplate>)OnFactionUpgradeUnlocked, "OnUpgradeUnlocked");
        Log.Debug($"hooks: story factions subscribed ({++_factionsHooked})");
    }

    private void OnConversationVarChanged(string name, int oldValue, int newValue)
        => Publish(new ConversationVarChangedContext { Name = name, OldValue = oldValue, NewValue = newValue });

    // JIANGYU-CONTRACT: Squaddies.OnSquaddiesChanged carries no argument and fires on any
    // roster change, wounding included (checked against v0.7.15). The hook reports only a
    // change in the alive count, read from m_AliveSquaddies, because its contract is the
    // alive count. The first roster change after a state attaches always reports.
    private void OnSquaddiesChanged(Squaddies squaddies)
    {
        var alive = squaddies.m_AliveSquaddies;
        var aliveCount = alive?.Count ?? 0;
        if (aliveCount == _lastAliveCount)
            return;
        _lastAliveCount = aliveCount;
        Publish(new AliveSquaddiesChangedContext { AliveCount = aliveCount });
    }

    private void OnFactionTrustChanged(StoryFaction faction, int oldTrust, int newTrust)
        => Publish(new FactionTrustChangedContext { Faction = faction, OldTrust = oldTrust, NewTrust = newTrust });

    private void OnFactionStatusChanged(StoryFaction faction, StoryFactionStatus oldStatus, StoryFactionStatus newStatus)
        => Publish(new FactionStatusChangedContext { Faction = faction, OldStatus = oldStatus, NewStatus = newStatus });

    private void OnFactionUpgradeUnlocked(StoryFaction faction, ShipUpgradeTemplate upgrade)
        => Publish(new FactionUpgradeUnlockedContext { Faction = faction, Upgrade = upgrade });
}
