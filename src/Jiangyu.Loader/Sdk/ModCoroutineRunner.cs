using System;
using System.Collections;
using System.Collections.Generic;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// A mod's <see cref="IModCoroutines"/>: forwards to the host coroutine driver
/// (MelonLoader's) while tracking each running routine so the mod's coroutines can
/// be stopped together when it unloads, and guarding each step so an exception in
/// the routine is logged against the mod and stops only that routine.
/// </summary>
internal sealed class ModCoroutineRunner : IModCoroutines
{
    private readonly string _modId;
    private readonly Func<IEnumerator, object> _start;
    private readonly Action<object> _stop;
    private readonly IModHostLog _log;
    private readonly HashSet<object> _running = new();

    public ModCoroutineRunner(string modId, Func<IEnumerator, object> start, Action<object> stop, IModHostLog log)
    {
        _modId = modId;
        _start = start;
        _stop = stop;
        _log = log;
    }

    public object Start(IEnumerator routine)
    {
        if (routine == null)
            return null;

        var slot = new HandleSlot();
        var handle = _start(Guard(routine, slot));
        slot.Handle = handle;
        // A routine that runs to completion synchronously inside _start (an empty or
        // immediately-finishing one) already ran Guard's finally with a null handle, so
        // only track a handle for a routine still running after _start returns.
        if (handle != null && !slot.Completed)
            _running.Add(handle);
        return handle;
    }

    public void Stop(object handle)
    {
        if (handle == null)
            return;
        _running.Remove(handle);
        _stop(handle);
    }

    /// <summary>Stops every routine this mod still has running.</summary>
    public void StopAll()
    {
        // Snapshot: stopping a routine disposes its iterator, whose finally prunes
        // the handle from _running, which would mutate the set mid-iteration.
        var handles = new object[_running.Count];
        _running.CopyTo(handles);
        _running.Clear();
        foreach (var handle in handles)
            _stop(handle);
    }

    // Wrap the mod's routine so a throw is logged against the mod (not swallowed by
    // the host pump untagged) and ends only this routine, and so the handle is pruned
    // when the routine finishes or is stopped. The MoveNext try has no yield (a yield
    // is illegal in a try with a catch); the outer try carries the finally.
    private IEnumerator Guard(IEnumerator inner, HandleSlot slot)
    {
        try
        {
            while (true)
            {
                object current = null;
                var done = false;
                try
                {
                    if (inner.MoveNext())
                        current = inner.Current;
                    else
                        done = true;
                }
                catch (Exception ex)
                {
                    _log.Error($"[{_modId}] coroutine threw {ex.GetType().Name}: {ex.Message}");
                    done = true;
                }

                if (done)
                    yield break;
                yield return current;
            }
        }
        finally
        {
            slot.Completed = true;
            if (slot.Handle != null)
                _running.Remove(slot.Handle);
        }
    }

    // Carries the host handle into the guard so the guard's finally can prune it.
    // The handle is only known after _start returns, which is after the guard is
    // built, so it cannot be a closure capture. Completed flags a routine that
    // finished inside _start, before Start could track its handle.
    private sealed class HandleSlot
    {
        public object Handle;
        public bool Completed;
    }
}

/// <summary>The coroutines view for a context with no host coroutine driver (tests).</summary>
internal sealed class NullModCoroutines : IModCoroutines
{
    public static readonly NullModCoroutines Instance = new();

    public object Start(IEnumerator routine) => null;

    public void Stop(object handle) { }
}
