using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk.Patches;

/// <summary>
/// The per-target handler lists behind <see cref="IModPatches"/>: each game method
/// is patched once with a shared dispatcher, and this registry maps the method to the
/// mod handlers to invoke. Dispatch isolates each handler (a throw is logged against
/// its mod and never stops the others or the game), and a prefix handler can set
/// <see cref="PatchInfo.Skip"/> to stop the original. Holds no Harmony or Il2Cpp types
/// so the dispatch logic is unit-testable on its own.
/// </summary>
internal sealed class ModPatchRegistry
{
    internal enum Kind { Prefix, Postfix }

    private sealed class Entry
    {
        public string ModId;
        public string Target;
        public Action<PatchInfo> Handler;
        public IModHostLog Log;
    }

    // Copy-on-write: a write builds a new map holding new arrays and publishes it in one
    // reference swap, so dispatch reads without a lock or a per-call copy. Patched methods run
    // on the AI's worker threads as well as the main thread, and a handler may register or a
    // mod unload while one is mid-dispatch. Writes are rare (init, unload), dispatch is hot.
    private Dictionary<object, Entry[]> _prefixes = new();
    private Dictionary<object, Entry[]> _postfixes = new();
    private readonly object _writeLock = new();

    public void Add(Kind kind, object key, string modId, string target, Action<PatchInfo> handler, IModHostLog log)
    {
        var entry = new Entry { ModId = modId, Target = target, Handler = handler, Log = log };
        lock (_writeLock)
        {
            var map = new Dictionary<object, Entry[]>(Map(kind));
            map[key] = map.TryGetValue(key, out var entries) ? entries.Append(entry).ToArray() : new[] { entry };
            Publish(kind, map);
        }
    }

    /// <summary>Runs the prefix handlers and returns whether the original should run.</summary>
    public bool DispatchPrefix(object key, object instance, object[] args)
        => DispatchPrefix(key, instance, args, out _, out _);

    /// <summary>Runs the prefix handlers. Returns whether the original should run; when a
    /// handler skipped the original, <paramref name="overridden"/> and
    /// <paramref name="result"/> carry any <see cref="PatchInfo.Result"/> it assigned, for
    /// the dispatcher to write into the skipped call's return slot.</summary>
    public bool DispatchPrefix(object key, object instance, object[] args, out bool overridden, out object result)
    {
        overridden = false;
        result = null;
        if (!Volatile.Read(ref _prefixes).TryGetValue(key, out var entries))
            return true;

        // One PatchInfo PER HANDLER, not shared: Skip and Result pair up
        // within a single handler's invocation, so mod A assigning Result
        // without Skip (a documented no-op) can never become the return
        // value of a skip that mod B requested. Skips still OR together,
        // and when several handlers skip with a Result, the last one wins.
        var skip = false;
        foreach (var entry in entries)
        {
            var info = new PatchInfo(instance, args);
            Invoke(entry, info, Kind.Prefix);
            if (!info.Skip)
                continue;
            skip = true;
            if (info.IsResultOverridden)
            {
                overridden = true;
                result = info.Result;
            }
        }
        return !skip;
    }

    public void DispatchPostfix(object key, object instance, object[] args)
        => DispatchPostfix(key, instance, args, null, out _);

    /// <summary>Runs the postfix handlers with the original's return value visible on
    /// <see cref="PatchInfo.Result"/>. Returns the (possibly overridden) value and
    /// whether any handler overrode it.</summary>
    public object DispatchPostfix(object key, object instance, object[] args, object result, out bool overridden)
    {
        overridden = false;
        if (!Volatile.Read(ref _postfixes).TryGetValue(key, out var entries))
            return result;

        var info = new PatchInfo(instance, args, result);
        foreach (var entry in entries)
            Invoke(entry, info, Kind.Postfix);
        overridden = info.IsResultOverridden;
        return info.Result;
    }

    /// <summary>How many handlers <paramref name="modId"/> has registered, across both kinds.</summary>
    public int CountForMod(string modId)
        => Volatile.Read(ref _prefixes).Values.Concat(Volatile.Read(ref _postfixes).Values)
            .Sum(entries => entries.Count(entry => string.Equals(entry.ModId, modId, StringComparison.Ordinal)));

    /// <summary>Drop every handler a mod registered, on its unload or quarantine. The
    /// shared dispatcher stays on the target, and a target left with no handlers loses its
    /// map entry, so its dispatch returns at the first lookup.</summary>
    public void RemoveMod(string modId)
    {
        lock (_writeLock)
        {
            foreach (var kind in new[] { Kind.Prefix, Kind.Postfix })
            {
                var map = new Dictionary<object, Entry[]>();
                foreach (var (key, entries) in Map(kind))
                {
                    var kept = Array.FindAll(entries, e => !string.Equals(e.ModId, modId, StringComparison.Ordinal));
                    if (kept.Length > 0)
                        map[key] = kept;
                }
                Publish(kind, map);
            }
        }
    }

    private static void Invoke(Entry entry, PatchInfo info, Kind kind)
    {
        try
        {
            entry.Handler(info);
        }
        catch (Exception ex)
        {
            entry.Log.Error($"[{entry.ModId}] patch {kind.ToString().ToLowerInvariant()} on {entry.Target} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private Dictionary<object, Entry[]> Map(Kind kind)
        => kind == Kind.Prefix ? Volatile.Read(ref _prefixes) : Volatile.Read(ref _postfixes);

    private void Publish(Kind kind, Dictionary<object, Entry[]> map)
    {
        if (kind == Kind.Prefix)
            Volatile.Write(ref _prefixes, map);
        else
            Volatile.Write(ref _postfixes, map);
    }
}
