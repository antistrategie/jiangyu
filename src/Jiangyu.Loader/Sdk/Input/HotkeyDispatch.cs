using System;
using System.Linq;

namespace Jiangyu.Loader.Sdk.Input;

// The hotkey fan-out, with no knowledge of keys or input. Each entry pairs a per-frame signal
// (is this hotkey active right now?) with an optional gate and a handler; Tick runs the
// handlers whose gate passes and whose signal is active. Copy-on-write so a handler may
// register or dispose mid-frame without disturbing the in-progress dispatch.
//
// Each entry carries an optional owner (the mod that registered it). ClearOwner drops a whole
// mod's entries at once, so the loader can release a mod's hotkeys on unload the way it stops
// its coroutines and removes its patches. Kept free of UnityEngine so the dispatch and the
// ownership filtering are unit-testable without a live game; HotkeyRegistry binds the key/edge
// to input mapping into each signal and resolves the owner.
internal sealed class HotkeyDispatch
{
    private readonly object _writeLock = new();
    private volatile Entry[] _entries = Array.Empty<Entry>();

    /// <summary>Register a handler. <paramref name="owner"/> groups it for
    /// <see cref="ClearOwner"/>; null leaves it ungrouped (it lives until its handle is
    /// disposed).</summary>
    public IDisposable Add(string owner, Func<bool> active, Func<bool> when, Action handler)
    {
        var entry = new Entry(owner, active, when, handler);
        lock (_writeLock)
        {
            var existing = _entries;
            var next = new Entry[existing.Length + 1];
            existing.CopyTo(next, 0);
            next[^1] = entry;
            _entries = next;
        }
        return new Handle(this, entry);
    }

    /// <summary>Run the handlers active this frame whose gate passes. A throwing gate, signal,
    /// or handler is isolated through <paramref name="onError"/> so one bad mod can't break
    /// the others.</summary>
    public void Tick(Action<Exception> onError)
    {
        var snapshot = _entries; // copy-on-write: safe to iterate lock-free
        foreach (var entry in snapshot)
        {
            try
            {
                if ((entry.When == null || entry.When()) && entry.Active())
                    entry.Handler();
            }
            catch (Exception ex)
            {
                // Drop a handler the first time it throws, so a buggy hotkey reports once
                // instead of every frame. The others this frame still run. A faulting error
                // sink itself must not abort them nor escape to end the pump.
                Remove(entry);
                try { onError(ex); }
                catch { }
            }
        }
    }

    /// <summary>Drop every entry registered by <paramref name="owner"/>. A null or unknown
    /// owner is a no-op. Ungrouped entries (owner null at registration) are untouched.</summary>
    public void ClearOwner(string owner)
    {
        if (owner == null)
            return;
        lock (_writeLock)
            _entries = _entries.Where(e => e.Owner != owner).ToArray();
    }

    private void Remove(Entry entry)
    {
        lock (_writeLock)
        {
            var index = Array.IndexOf(_entries, entry);
            if (index < 0)
                return;

            if (_entries.Length == 1)
            {
                _entries = Array.Empty<Entry>();
                return;
            }

            var next = new Entry[_entries.Length - 1];
            Array.Copy(_entries, 0, next, 0, index);
            Array.Copy(_entries, index + 1, next, index, _entries.Length - index - 1);
            _entries = next;
        }
    }

    private sealed class Entry
    {
        public Entry(string owner, Func<bool> active, Func<bool> when, Action handler)
        {
            Owner = owner;
            Active = active;
            When = when;
            Handler = handler;
        }

        public string Owner { get; }
        public Func<bool> Active { get; }
        public Func<bool> When { get; }
        public Action Handler { get; }
    }

    private sealed class Handle : IDisposable
    {
        private readonly HotkeyDispatch _owner;
        private readonly Entry _entry;
        private bool _disposed;

        public Handle(HotkeyDispatch owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Remove(_entry);
        }
    }
}
