using System;
using System.Collections.Generic;
using System.Linq;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;

namespace Jiangyu.Loader.Sdk;

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

    private readonly Dictionary<object, List<Entry>> _prefixes = new();
    private readonly Dictionary<object, List<Entry>> _postfixes = new();

    public void Add(Kind kind, object key, string modId, string target, Action<PatchInfo> handler, IModHostLog log)
    {
        var map = Map(kind);
        if (!map.TryGetValue(key, out var entries))
            map[key] = entries = new List<Entry>();
        entries.Add(new Entry { ModId = modId, Target = target, Handler = handler, Log = log });
    }

    /// <summary>Runs the prefix handlers and returns whether the original should run.</summary>
    public bool DispatchPrefix(object key, object instance, object[] args)
    {
        if (!_prefixes.TryGetValue(key, out var entries))
            return true;

        var info = new PatchInfo(instance, args);
        foreach (var entry in entries.ToArray())
            Invoke(entry, info, Kind.Prefix);
        return !info.Skip;
    }

    public void DispatchPostfix(object key, object instance, object[] args)
    {
        if (!_postfixes.TryGetValue(key, out var entries))
            return;

        var info = new PatchInfo(instance, args);
        foreach (var entry in entries.ToArray())
            Invoke(entry, info, Kind.Postfix);
    }

    /// <summary>Drop every handler a mod registered, on its unload or quarantine. The
    /// shared dispatcher stays on the target but finds no handlers and does nothing.</summary>
    public void RemoveMod(string modId)
    {
        foreach (var map in new[] { _prefixes, _postfixes })
        {
            foreach (var entries in map.Values)
                entries.RemoveAll(e => string.Equals(e.ModId, modId, StringComparison.Ordinal));
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

    private Dictionary<object, List<Entry>> Map(Kind kind)
        => kind == Kind.Prefix ? _prefixes : _postfixes;
}
