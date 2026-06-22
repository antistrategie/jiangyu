using System;
using System.Collections.Generic;
using Jiangyu.Shared;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// Warns when a mod was built against a newer Jiangyu toolchain than the loader now
/// running. The compiler stamps its version into each mod's <c>jiangyu.json</c>
/// (<c>compiledForJiangyu</c>); at startup the loader compares it to its own version so a
/// mod relying on a newer loader surfaces an up-front warning rather than a confusing
/// partial failure once features fail to apply. A mod built against an older or equal
/// Jiangyu, or one with no stamp, is silent.
/// </summary>
internal static class JiangyuVersionGate
{
    /// <summary>Warn for each mod whose stamped build-time Jiangyu version is newer than
    /// <paramref name="runningJiangyuVersion"/>. Returns the number warned.</summary>
    public static int Check(
        string runningJiangyuVersion,
        IEnumerable<(string ModId, string CompiledForJiangyu)> mods,
        Action<string> warn)
    {
        if (!SemVer.TryParse(runningJiangyuVersion, out var running))
            return 0;

        var warned = 0;
        foreach (var (modId, compiledFor) in mods)
        {
            if (!SemVer.TryParse(compiledFor, out var built) || built <= running)
                continue;

            warn($"[{modId}] was built with Jiangyu {compiledFor}, but the installed loader is {runningJiangyuVersion}. "
                + "Update the Jiangyu loader; some of this mod's features may not apply until you do.");
            warned++;
        }

        return warned;
    }
}
