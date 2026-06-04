using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// The single developer-flag file, <c>jiangyu-flags</c> in <c>&lt;UserData&gt;</c>, that
/// gates the loader's diagnostics and verbose logging. One toggle per line; blank
/// lines and <c>#</c> comments are ignored. A bare line (<c>verbs</c>) enables a
/// toggle; <c>key=value</c> (<c>inspect=20</c>) enables it with a value. Absent file
/// means everything is off.
///
/// <para>The toggles: <c>debug</c> (verbose logging), <c>inspect</c> (runtime
/// inspector dumps, optional <c>=N</c> retention cap), <c>gate</c> /
/// <c>gate-damage</c> (the injection gate and its opt-in self-hit), <c>verbs</c> /
/// <c>verbs-spawn</c> (the verb probe and its opt-in spawns).</para>
///
/// <para>The file is read once and cached; <see cref="Refresh"/> re-reads it (the
/// loader calls it on scene load) so a dict lookup, not file I/O, backs the per-frame
/// gate checks.</para>
/// </summary>
internal static class DevFlags
{
    private const string FileName = "jiangyu-flags";

    private static Dictionary<string, string> _cache;

    /// <summary>Whether <paramref name="toggle"/> is present in the dev file.</summary>
    public static bool IsEnabled(string toggle) => Cache().ContainsKey(toggle);

    /// <summary>The value of a <c>key=value</c> toggle, or null when bare or absent.</summary>
    public static string Value(string toggle) => Cache().TryGetValue(toggle, out var value) ? value : null;

    /// <summary>Re-read the dev file. Called on scene load so edits take effect.</summary>
    public static void Refresh() => _cache = Read();

    private static Dictionary<string, string> Cache() => _cache ??= Read();

    private static Dictionary<string, string> Read()
    {
        var toggles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(MelonEnvironment.UserDataDirectory, FileName);
            if (!File.Exists(path))
                return toggles;

            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var separator = line.IndexOf('=');
                if (separator < 0)
                    toggles[line] = null;
                else
                    toggles[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
        }
        catch
        {
            // An unreadable dev file gates nothing on, same as an absent one.
        }
        return toggles;
    }
}
