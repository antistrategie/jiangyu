using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Shared marshalling for the hook publishers that bridge IL2CPP game events onto the
/// hook bus. Converts a managed delegate to the game's event delegate, subscribes it,
/// and roots both halves so neither is collected while the subscription is live;
/// publishes a context only when a mod is listening; and drops the rooted delegates
/// when an attachment is torn down.
/// </summary>
internal abstract class HookPublisherBase
{
    protected readonly InProcessHookBus Bus;
    protected readonly IModHostLog Log;

    // Roots the managed delegates and their Il2Cpp proxies handed to the native
    // events so neither is collected while a subscription is live.
    private readonly List<object> _held = new();

    protected HookPublisherBase(InProcessHookBus bus, IModHostLog log)
    {
        Bus = bus;
        Log = log;
    }

    /// <summary>Game events currently hooked by this attachment (each hook roots a
    /// managed delegate and its proxy, so this is half the rooted-object count).</summary>
    protected int HookedEventCount => _held.Count / 2;

    // Convert a managed delegate to the Il2Cpp event delegate, subscribe, and root
    // both halves. Each subscription is isolated: one failed marshal is logged and
    // never blocks the rest.
    protected void Hook<TDelegate>(Action<TDelegate> add, Delegate managed, string name)
        where TDelegate : Il2CppSystem.Delegate
    {
        try
        {
            var proxy = DelegateSupport.ConvertDelegate<TDelegate>(managed);
            add(proxy);
            _held.Add(managed);
            _held.Add(proxy);
        }
        catch (Exception ex)
        {
            Log.Error($"hooks: failed to attach {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Publish only when a mod is listening, so an unsubscribed event skips the
    // subscriber dispatch. The caller builds the context, so an unsubscribed event
    // still allocates it; the guard saves the dispatch, not the allocation.
    protected void Publish<T>(T context) where T : class
    {
        if (Bus.HasSubscribers<T>())
            Bus.Publish(context);
    }

    // Drop the rooted delegates for a torn-down attachment so they can be collected
    // and the next attachment's hooked-event count starts from zero.
    protected void ClearHookedDelegates() => _held.Clear();
}
