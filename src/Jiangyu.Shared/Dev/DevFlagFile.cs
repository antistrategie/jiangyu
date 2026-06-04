using System;
using System.Collections.Generic;
using System.IO;

namespace Jiangyu.Shared.Dev;

/// <summary>
/// The <c>jiangyu-flags</c> dev-flag file grammar, shared so the loader (which reads
/// flags to gate diagnostics) and Studio (which reads and writes them, e.g. the
/// bridge toggle) agree on one format instead of hand-rolling two parsers that drift.
///
/// <para>Grammar: one toggle per line; blank lines and <c>#</c> comments are ignored;
/// a bare line (<c>verbs</c>) enables a toggle, <c>key=value</c> (<c>inspect=20</c>)
/// enables it with a value. Keys are case-insensitive. The file lives in the game's
/// <c>UserData</c> directory.</para>
/// </summary>
public static class DevFlagFile
{
    public const string FileName = "jiangyu-flags";

    /// <summary>Parse flag lines into a case-insensitive key-to-value map (value null for a bare key).</summary>
    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        var toggles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
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
        return toggles;
    }

    /// <summary>Read and parse the flag file under <paramref name="userDataDir"/>. Empty when absent or unreadable.</summary>
    public static Dictionary<string, string?> Read(string userDataDir)
    {
        try
        {
            var path = Path.Combine(userDataDir, FileName);
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty();
        }
        catch
        {
            return Empty();
        }
    }

    /// <summary>Whether <paramref name="toggle"/> is present in the flag file.</summary>
    public static bool IsEnabled(string userDataDir, string toggle) => Read(userDataDir).ContainsKey(toggle);

    /// <summary>
    /// Add or remove <paramref name="toggle"/> in the flag file, preserving other lines
    /// and comments. Matches the toggle in either bare or <c>key=value</c> form,
    /// case-insensitively. Written atomically (temp file plus rename).
    /// </summary>
    public static void Set(string userDataDir, string toggle, bool enabled)
    {
        var path = Path.Combine(userDataDir, FileName);
        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();

        var present = lines.Exists(line => MatchesKey(line, toggle));
        if (enabled && !present)
            lines.Add(toggle);
        else if (!enabled && present)
            lines.RemoveAll(line => MatchesKey(line, toggle));
        else
            return;

        Directory.CreateDirectory(userDataDir);
        var tmp = path + ".jiangyu.tmp";
        try
        {
            File.WriteAllLines(tmp, lines);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private static bool MatchesKey(string line, string toggle)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return false;
        var separator = trimmed.IndexOf('=');
        var key = separator < 0 ? trimmed : trimmed.Substring(0, separator).Trim();
        return string.Equals(key, toggle, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string?> Empty() => new(StringComparer.OrdinalIgnoreCase);
}
