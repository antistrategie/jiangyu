using System;
using System.Collections.Generic;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk.Hooks;

/// <summary>
/// A shared in-process publish-subscribe bus backing <see cref="IHookBus"/>.
/// Subscriptions are keyed by context type. Publishing a context invokes every
/// subscriber registered for that type. Hooks fire from game callbacks, so each
/// subscriber is invoked under its own try/catch: one throwing handler is logged
/// and never aborts the publish or the game moment that triggered it.
/// </summary>
internal sealed class InProcessHookBus : IHookBus
{
    private readonly object _gate = new();
    // Copy-on-write handler arrays: Subscribe/Remove swap in a new array under the
    // lock (rare), so Publish reads the immutable array reference without copying it
    // (no per-publish allocation) and iterates it outside the lock.
    private readonly Dictionary<Type, Delegate[]> _handlers = new();
    private readonly IModHostLog _log;

    public InProcessHookBus(IModHostLog log)
    {
        _log = log;
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        lock (_gate)
        {
            _handlers.TryGetValue(typeof(T), out var existing);
            var next = new Delegate[(existing?.Length ?? 0) + 1];
            existing?.CopyTo(next, 0);
            next[^1] = handler;
            _handlers[typeof(T)] = next;
        }

        return new Subscription(this, typeof(T), handler);
    }

    /// <summary>
    /// Whether any subscriber is registered for <typeparamref name="T"/>. A publisher
    /// guards on this before dispatching, so a game event with no listener costs one
    /// dictionary lookup instead of the subscriber invocation.
    /// </summary>
    public bool HasSubscribers<T>() where T : class
    {
        lock (_gate)
            return _handlers.TryGetValue(typeof(T), out var list) && list.Length > 0;
    }

    public void Publish<T>(T context) where T : class
    {
        Delegate[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out snapshot) || snapshot.Length == 0)
                return;
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<T>)handler)(context);
            }
            catch (Exception ex)
            {
                _log.Error($"hook subscriber for {typeof(T).Name} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Remove(Type type, Delegate handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(type, out var existing))
                return;

            var index = Array.IndexOf(existing, handler);
            if (index < 0)
                return;

            if (existing.Length == 1)
            {
                _handlers.Remove(type);
                return;
            }

            var next = new Delegate[existing.Length - 1];
            Array.Copy(existing, 0, next, 0, index);
            Array.Copy(existing, index + 1, next, index, existing.Length - index - 1);
            _handlers[type] = next;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InProcessHookBus _bus;
        private readonly Type _type;
        private readonly Delegate _handler;
        private bool _disposed;

        public Subscription(InProcessHookBus bus, Type type, Delegate handler)
        {
            _bus = bus;
            _type = type;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _bus.Remove(_type, _handler);
        }
    }
}
