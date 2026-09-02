using System;
using System.Reflection;
using HarmonyLib;
using Jiangyu.Loader.Logging;
using MelonLoader;

namespace Jiangyu.Loader.Runtime.Patching;

/// <summary>
/// Shared helper for the loader's Harmony patch modules: resolve a game method by
/// name and attach a postfix, logging each failure mode under a module label.
/// </summary>
internal static class HarmonyPatching
{
    private const BindingFlags MethodFlags =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Loader patches installed so far this session, reported as one line at startup.</summary>
    public static int InstalledCount { get; private set; }

    /// <summary>Record one installed loader patch. The line itself is debug: the startup log
    /// carries the aggregate, and a failed install is a warning on its own.</summary>
    public static void Installed(MelonLogger.Instance log, string line)
    {
        InstalledCount++;
        LoaderDebug.Write(log, line);
    }

    public static void TryPostfix(
        HarmonyLib.Harmony harmony, string typeName, string method,
        Type postfixType, string postfixName, MelonLogger.Instance log, string label)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            log.Warning($"{label}: type {typeName} not found, skipping {method}");
            return;
        }

        var target = type.GetMethod(method, MethodFlags);
        if (target == null)
        {
            log.Warning($"{label}: {typeName}.{method} not found");
            return;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(postfixType, postfixName));
            Installed(log, $"{label}: patched {typeName}.{method}");
        }
        catch (Exception ex)
        {
            log.Error($"{label}: failed to patch {typeName}.{method}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
