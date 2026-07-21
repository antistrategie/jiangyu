using Il2CppInterop.Runtime;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// A set of IL2CPP event subscriptions with per-subscription teardown, shared by the
/// determinism harness and the net mission session: both subscribe to a run of
/// <c>TacticalManager</c> completion events and must unsubscribe when they stop. Each
/// <see cref="Hook{TDelegate}"/> converts a managed delegate to the game's event delegate,
/// subscribes it through the <c>add_</c> accessor, roots both halves so neither is
/// collected while live, and remembers the <c>remove_</c> accessor so
/// <see cref="DetachAll"/> can unsubscribe. A failed marshal is isolated and reported, not
/// fatal. Distinct from <see cref="HookPublisherBase"/>, which drops its roots wholesale
/// rather than tracking a per-subscription <c>remove_</c>.
/// </summary>
internal sealed class Il2CppEventSubscriptions
{
    private readonly List<Action> _detach = new();
    private readonly List<object> _roots = new();
    private readonly Action<string> _onError;

    /// <param name="onError">Called with a description when a subscription fails to
    /// marshal; null swallows the failure (the caller degrades gracefully).</param>
    public Il2CppEventSubscriptions(Action<string> onError = null) => _onError = onError;

    public void Hook<TDelegate>(Action<TDelegate> add, Action<TDelegate> remove, Delegate managed, string name)
        where TDelegate : Il2CppSystem.Delegate
    {
        try
        {
            var proxy = DelegateSupport.ConvertDelegate<TDelegate>(managed);
            add(proxy);
            _roots.Add(managed);
            _roots.Add(proxy);
            _detach.Add(() =>
            {
                try { remove(proxy); }
                catch { /* manager already gone */ }
            });
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"failed to attach {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void DetachAll()
    {
        foreach (var detach in _detach)
            detach();
        _detach.Clear();
        _roots.Clear();
    }
}
