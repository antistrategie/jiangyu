using System;
using System.Reflection;
using HarmonyLib;
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
            log.Msg($"{label}: patched {typeName}.{method}");
        }
        catch (Exception ex)
        {
            log.Error($"{label}: failed to patch {typeName}.{method}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
