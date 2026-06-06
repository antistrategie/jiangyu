using System.Collections.Generic;
using Jiangyu.Shared.Dev;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Cached read of the <c>jiangyu-flags</c> dev-flag file (see
/// <see cref="DevFlagFile"/> for the grammar) that gates the loader's diagnostics and
/// verbose logging. The file is read once and cached; <see cref="Refresh"/> re-reads
/// it (the loader calls it on scene load) so a dict lookup, not file I/O, backs the
/// gate checks. An absent file means everything is off.
///
/// <para>Toggles: <c>debug</c> (verbose logging), <c>bridge</c> (the Studio/agent
/// socket, normally written by Studio's toggle), <c>gate</c> / <c>gate-damage</c> (the
/// load-time injection-gate structural check and its opt-in self-hit), and
/// <c>verbs-spawn</c> (the opt-in spawns in the on-demand verb probe), and
/// <c>strategy-mutate</c> (the opt-in hire/dismiss characterisation in the strategy
/// probe). The on-demand scene, template, UI, gate, verb, and strategy inspectors run
/// over the bridge and need no toggle of their own.</para>
/// </summary>
internal static class DevFlags
{
    private static Dictionary<string, string> _cache;

    /// <summary>Whether <paramref name="toggle"/> is present in the dev file.</summary>
    public static bool IsEnabled(string toggle) => Cache().ContainsKey(toggle);

    /// <summary>The value of a <c>key=value</c> toggle, or null when bare or absent.</summary>
    public static string Value(string toggle) => Cache().TryGetValue(toggle, out var value) ? value : null;

    /// <summary>Re-read the dev file. Called on scene load so edits take effect.</summary>
    public static void Refresh() => _cache = Read();

    private static Dictionary<string, string> Cache() => _cache ??= Read();

    private static Dictionary<string, string> Read() => DevFlagFile.Read(MelonEnvironment.UserDataDirectory);
}
