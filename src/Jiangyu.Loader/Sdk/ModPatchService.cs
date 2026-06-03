using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Logging;
using Jiangyu.Sdk;
using PatchInfo = Jiangyu.Sdk.PatchInfo;

namespace Jiangyu.Loader.Sdk;

/// <summary>
/// Owns the one shared Harmony dispatcher behind every mod patch. Each distinct
/// target method is patched once (a dispatcher prefix and/or postfix); the
/// <see cref="ModPatchRegistry"/> then routes the call to the mods' handlers. Patching
/// once and routing avoids stacking a separate Harmony patch per mod (which would run
/// the handlers more than once when two mods patch the same method).
/// </summary>
internal static class ModPatchCoordinator
{
    private const BindingFlags MethodFlags =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ModPatchRegistry Registry = new();
    private static readonly HashSet<MethodBase> PatchedPrefix = new();
    private static readonly HashSet<MethodBase> PatchedPostfix = new();

    private static HarmonyLib.Harmony _harmony;

    public static void Initialise(HarmonyLib.Harmony harmony) => _harmony = harmony;

    public static void Register(
        string modId, ModPatchRegistry.Kind kind, string typeName, string methodName,
        Action<PatchInfo> handler, IModHostLog log)
    {
        if (handler == null)
            return;
        if (_harmony == null)
        {
            log.Warn($"[{modId}] patches are unavailable; {typeName}.{methodName} not patched.");
            return;
        }

        var target = ResolveMethod(typeName, methodName, modId, log);
        if (target == null)
            return;

        var label = $"{typeName}.{methodName}";
        Registry.Add(kind, target, modId, label, handler, log);
        if (EnsurePatched(kind, target, log))
            log.Info($"[{modId}] patch {kind.ToString().ToLowerInvariant()} registered on {label}");
    }

    public static void RemoveMod(string modId) => Registry.RemoveMod(modId);

    private static bool EnsurePatched(ModPatchRegistry.Kind kind, MethodBase target, IModHostLog log)
    {
        var applied = kind == ModPatchRegistry.Kind.Prefix ? PatchedPrefix : PatchedPostfix;
        if (!applied.Add(target))
            return true;

        try
        {
            var dispatcher = new HarmonyMethod(typeof(ModPatchCoordinator),
                kind == ModPatchRegistry.Kind.Prefix ? nameof(DispatchPrefix) : nameof(DispatchPostfix));
            if (kind == ModPatchRegistry.Kind.Prefix)
                _harmony.Patch(target, prefix: dispatcher);
            else
                _harmony.Patch(target, postfix: dispatcher);
            return true;
        }
        catch (Exception ex)
        {
            applied.Remove(target);
            log.Error($"patch: failed to attach dispatcher to {target.DeclaringType?.Name}.{target.Name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Shared dispatcher targets. __originalMethod identifies which game method ran, so
    // one method body serves every patched target. __args is populated for Il2Cpp
    // methods on this stack (see InventoryFilterPatch).
    private static bool DispatchPrefix(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPrefix(__originalMethod, __instance, __args ?? Array.Empty<object>());

    private static void DispatchPostfix(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPostfix(__originalMethod, __instance, __args ?? Array.Empty<object>());

    private static MethodBase ResolveMethod(string typeName, string methodName, string modId, IModHostLog log)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            log.Warn($"[{modId}] patch target type '{typeName}' not found.");
            return null;
        }

        try
        {
            var method = type.GetMethod(methodName, MethodFlags);
            if (method == null)
                log.Warn($"[{modId}] patch target method '{typeName}.{methodName}' not found.");
            return method;
        }
        catch (AmbiguousMatchException)
        {
            log.Warn($"[{modId}] patch target '{typeName}.{methodName}' is overloaded; cannot resolve a single method.");
            return null;
        }
    }
}

/// <summary>A mod's <see cref="IModPatches"/>: registers the mod's handlers with the
/// shared <see cref="ModPatchCoordinator"/> and drops them all on unload.</summary>
internal sealed class ModPatchService : IModPatches
{
    private readonly string _modId;
    private readonly IModHostLog _log;

    public ModPatchService(string modId, IModHostLog log)
    {
        _modId = modId;
        _log = log;
    }

    public void Prefix(string typeName, string methodName, Action<PatchInfo> handler)
        => ModPatchCoordinator.Register(_modId, ModPatchRegistry.Kind.Prefix, typeName, methodName, handler, _log);

    public void Postfix(string typeName, string methodName, Action<PatchInfo> handler)
        => ModPatchCoordinator.Register(_modId, ModPatchRegistry.Kind.Postfix, typeName, methodName, handler, _log);

    public void RemoveAll() => ModPatchCoordinator.RemoveMod(_modId);
}

/// <summary>The patches view for a context with no patch coordinator (tests).</summary>
internal sealed class NullModPatches : IModPatches
{
    public static readonly NullModPatches Instance = new();

    public void Prefix(string typeName, string methodName, Action<PatchInfo> handler) { }

    public void Postfix(string typeName, string methodName, Action<PatchInfo> handler) { }
}
