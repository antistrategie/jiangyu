using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// Warns when a mod was compiled against a different game build than the one now
/// running. The compiler stamps the game's Unity version into each mod's
/// <c>jiangyu.json</c> (<c>compiledForUnity</c>); at startup the loader compares it
/// to the running game so a silent game update surfaces as a clear up-front warning
/// rather than scattered "field missing" lines once patches start applying.
/// </summary>
internal static class GameVersionGate
{
    /// <summary>Warn for each mod whose stamped compile-time game version differs
    /// from <paramref name="runningUnityVersion"/>. Mods with no stamp (hand-written
    /// or pre-stamp manifests) are skipped. Returns the number warned.</summary>
    public static int Check(
        string runningUnityVersion,
        IEnumerable<(string ModId, string CompiledForUnity)> mods,
        Action<string> warn)
    {
        if (string.IsNullOrEmpty(runningUnityVersion))
            return 0;

        var warned = 0;
        foreach (var (modId, compiledFor) in mods)
        {
            if (string.IsNullOrEmpty(compiledFor) || VersionsMatch(compiledFor, runningUnityVersion))
                continue;

            warn($"[{modId}] was compiled against game Unity {compiledFor}, running {runningUnityVersion}. "
                + "If the game updated, this mod's template patches and clones may no longer apply.");
            warned++;
        }

        return warned;
    }

    // The compile-time version detector and the runtime Application.unityVersion can
    // render the same build with different surrounding format, so compare the canonical
    // version token (major.minor.patch + release type + number) when present, falling
    // back to an exact compare. A real version difference still differs in the token.
    private static bool VersionsMatch(string compiledFor, string running)
    {
        if (string.Equals(compiledFor, running, StringComparison.Ordinal))
            return true;

        var compiledToken = VersionToken(compiledFor);
        return compiledToken != null && compiledToken == VersionToken(running);
    }

    private static string VersionToken(string version)
    {
        var match = Regex.Match(version ?? string.Empty, @"\d+\.\d+\.\d+[a-zA-Z]\d+");
        return match.Success ? match.Value : null;
    }
}
