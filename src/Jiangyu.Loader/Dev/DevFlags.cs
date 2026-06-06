using System.Collections.Generic;
using Jiangyu.Shared.Dev;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Cached read of the <c>jiangyu-flags</c> dev-flag file (see
/// <see cref="DevFlagFile"/> for the grammar) that gates the loader's verbose logging.
/// The file is read once and cached; <see cref="Refresh"/> re-reads it (the loader
/// calls it on scene load) so a dict lookup, not file I/O, backs the gate checks. An
/// absent file means everything is off.
///
/// <para>Toggles: <c>debug</c> (verbose logging) and <c>bridge</c> (the Studio/agent
/// socket, normally written by Studio's toggle). The bridge commands (the verb runner
/// and the on-demand scene/template/UI inspectors) run on request and need no toggle of
/// their own; a verb that mutates game state is gated per call by <c>mutate:true</c>.</para>
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
