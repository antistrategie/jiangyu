using System;
using System.Collections.Generic;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Bridges the strategy-layer (campaign meta-game) moments onto the hook bus. Unlike
/// the tactical events (one TacticalManager singleton), the strategy events are
/// scattered across per-instance types, and the factions populate progressively
/// after the StrategyState appears. So <see cref="EnsureAttached"/> runs each frame
/// on the strategy scene and subscribes each owner exactly once, keyed by its native
/// pointer — the singleton, the squaddies collection, and every faction as it loads.
/// A new StrategyState (a loaded save or a different campaign) appears at a new
/// pointer, which drops the prior session's bookkeeping so its owners re-hook.
/// </summary>
internal sealed class StrategyHookPublisher : HookPublisherBase
{
    private readonly HashSet<IntPtr> _subscribed = new();
    private int _factionsHooked;
    private int _lastFactionCount = -1;
    private IntPtr _attachedState;

    public StrategyHookPublisher(InProcessHookBus bus, IModHostLog log)
        : base(bus, log)
    {
    }

    /// <summary>
    /// Subscribe any strategy owner not yet hooked. Each owner (StrategyState, the
    /// squaddies collection, each faction) is keyed by its native pointer, so this is
    /// idempotent across frames and re-entries: no owner is double-subscribed, and
    /// factions that load late get picked up on a later frame.
    /// </summary>
    public void EnsureAttached()
    {
        var ss = StrategyState.Get();
        if (ss == null || ss.Pointer == IntPtr.Zero)
            return;

        // A fresh StrategyState (loaded save or new campaign) appears at a new pointer.
        // Drop the previous session's subscription bookkeeping so its owners re-hook
        // instead of being skipped as already-seen against stale (and possibly
        // pointer-reused) entries, and reset the faction-count gate and held delegates.
        if (ss.Pointer != _attachedState)
        {
            _attachedState = ss.Pointer;
            _subscribed.Clear();
            _lastFactionCount = -1;
            _factionsHooked = 0;
            ClearHookedDelegates();
        }

        if (_subscribed.Add(ss.Pointer))
        {
            Hook<StrategyState.ConversationVarChangedDelegate>(ss.add_OnConversationVarChanged, (Action<string, int, int>)OnConversationVarChanged, "OnConversationVarChanged");
            Log.Info("hooks: attached to StrategyState");
        }

        var squaddies = ss.Squaddies;
        if (squaddies != null && squaddies.Pointer != IntPtr.Zero && _subscribed.Add(squaddies.Pointer))
            Hook<Il2CppSystem.Action<int>>(squaddies.add_OnAliveSquaddiesChanged, (Action<int>)OnAliveSquaddiesChanged, "OnAliveSquaddiesChanged");

        var factions = ss.StoryFactions;
        if (factions != null && factions.Pointer != IntPtr.Zero)
        {
            try
            {
                // Re-enumerate the Il2Cpp faction dictionary only when its count
                // changes (a faction loaded). Once settled, this is one count read
                // per frame instead of marshalling every entry across the boundary.
                var count = factions.m_Factions.Count;
                if (count != _lastFactionCount)
                {
                    _lastFactionCount = count;
                    foreach (var pair in factions.m_Factions)
                        SubscribeFaction(pair.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"hooks: failed to enumerate factions: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void SubscribeFaction(StoryFaction faction)
    {
        if (faction == null || faction.Pointer == IntPtr.Zero || !_subscribed.Add(faction.Pointer))
            return;
        Hook<StoryFaction.TrustChangedDelegate>(faction.add_OnTrustChanged, (Action<StoryFaction, int, int>)OnFactionTrustChanged, "OnTrustChanged");
        Hook<StoryFaction.StatusChangedDelegate>(faction.add_OnStatusChanged, (Action<StoryFaction, StoryFactionStatus, StoryFactionStatus>)OnFactionStatusChanged, "OnStatusChanged");
        Hook<StoryFaction.UpgradeUnlockedDelegate>(faction.add_OnUpgradeUnlocked, (Action<StoryFaction, ShipUpgradeTemplate>)OnFactionUpgradeUnlocked, "OnUpgradeUnlocked");
        Log.Info($"hooks: story factions subscribed ({++_factionsHooked})");
    }

    private void OnConversationVarChanged(string name, int oldValue, int newValue)
        => Publish(new ConversationVarChangedContext { Name = name, OldValue = oldValue, NewValue = newValue });

    private void OnAliveSquaddiesChanged(int aliveCount)
        => Publish(new AliveSquaddiesChangedContext { AliveCount = aliveCount });

    private void OnFactionTrustChanged(StoryFaction faction, int oldTrust, int newTrust)
        => Publish(new FactionTrustChangedContext { Faction = faction, OldTrust = oldTrust, NewTrust = newTrust });

    private void OnFactionStatusChanged(StoryFaction faction, StoryFactionStatus oldStatus, StoryFactionStatus newStatus)
        => Publish(new FactionStatusChangedContext { Faction = faction, OldStatus = oldStatus, NewStatus = newStatus });

    private void OnFactionUpgradeUnlocked(StoryFaction faction, ShipUpgradeTemplate upgrade)
        => Publish(new FactionUpgradeUnlockedContext { Faction = faction, Upgrade = upgrade });
}
