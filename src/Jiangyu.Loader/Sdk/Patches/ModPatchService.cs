using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Templates;
using Jiangyu.Sdk;
using PatchInfo = Jiangyu.Sdk.PatchInfo;

namespace Jiangyu.Loader.Sdk.Patches;

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
            if (kind == ModPatchRegistry.Kind.Prefix)
            {
                var prefixName = target.IsStatic ? nameof(DispatchPrefixStatic) : nameof(DispatchPrefix);
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModPatchCoordinator), prefixName));
                return true;
            }

            // PostfixDispatcherFor is the single source of the return-type -> dispatcher mapping
            // (an overridable int/bool/float/reference dispatcher, or the result-less one for
            // other value returns and void). Try it; if binding the typed ref-__result dispatcher
            // throws (some Il2Cpp value returns do not marshal to a ref parameter), fall back to
            // the result-less dispatcher so an observe-only postfix still registers.
            try
            {
                _harmony.Patch(target, postfix: PostfixDispatcherFor(target));
                return true;
            }
            catch (Exception ex)
            {
                log.Warn($"patch: {target.DeclaringType?.Name}.{target.Name} return not overridable ({ex.GetType().Name}); postfix runs observe-only.");
                _harmony.Patch(target, postfix: new HarmonyMethod(typeof(ModPatchCoordinator),
                    target.IsStatic ? nameof(DispatchPostfixStatic) : nameof(DispatchPostfix)));
                return true;
            }
        }
        catch (Exception ex)
        {
            applied.Remove(target);
            log.Error($"patch: failed to attach dispatcher to {target.DeclaringType?.Name}.{target.Name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Harmony writes an overridden return through a typed ref parameter: a
    // ref object __result only binds to reference-typed returns, so value-typed
    // returns each get their own dispatcher and every reference-typed return
    // shares the object dispatcher. Targets with other value returns use the
    // no-result dispatcher: PatchInfo.Result stays null and assignments to it
    // are ignored, as documented on the SDK type. Static targets take the
    // instance-less variants: an __instance parameter does not bind to a
    // static original.
    private static HarmonyMethod PostfixDispatcherFor(MethodBase target)
    {
        var returnType = (target as MethodInfo)?.ReturnType;
        string name;
        if (returnType == typeof(int))
            name = target.IsStatic ? nameof(DispatchPostfixInt32Static) : nameof(DispatchPostfixInt32);
        else if (returnType == typeof(bool))
            name = target.IsStatic ? nameof(DispatchPostfixBooleanStatic) : nameof(DispatchPostfixBoolean);
        else if (returnType == typeof(float))
            name = target.IsStatic ? nameof(DispatchPostfixSingleStatic) : nameof(DispatchPostfixSingle);
        else if (returnType != null && !returnType.IsValueType)
            name = target.IsStatic ? nameof(DispatchPostfixObjectStatic) : nameof(DispatchPostfixObject);
        else
            name = target.IsStatic ? nameof(DispatchPostfixStatic) : nameof(DispatchPostfix);
        return new HarmonyMethod(typeof(ModPatchCoordinator), name);
    }

    // Shared dispatcher targets. __originalMethod identifies which game method ran, so
    // one method body serves every patched target. __args is populated for Il2Cpp
    // methods on this stack (see InventoryFilterPatch).
    private static bool DispatchPrefix(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPrefix(__originalMethod, __instance, __args ?? Array.Empty<object>());

    private static void DispatchPostfix(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPostfix(__originalMethod, __instance, __args ?? Array.Empty<object>());

    // Static-target variants take null for the instance: an __instance parameter does not bind to
    // a static original. Instance and static share each body below through ResolveValue/ResolveObject.
    private static bool DispatchPrefixStatic(object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPrefix(__originalMethod, null, __args ?? Array.Empty<object>());

    private static void DispatchPostfixStatic(object[] __args, MethodBase __originalMethod)
        => Registry.DispatchPostfix(__originalMethod, null, __args ?? Array.Empty<object>());

    private static void DispatchPostfixInt32(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref int __result)
        => __result = ResolveValue(__instance, __args, __originalMethod, __result);

    private static void DispatchPostfixInt32Static(object[] __args, MethodBase __originalMethod, ref int __result)
        => __result = ResolveValue(null, __args, __originalMethod, __result);

    private static void DispatchPostfixBoolean(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref bool __result)
        => __result = ResolveValue(__instance, __args, __originalMethod, __result);

    private static void DispatchPostfixBooleanStatic(object[] __args, MethodBase __originalMethod, ref bool __result)
        => __result = ResolveValue(null, __args, __originalMethod, __result);

    private static void DispatchPostfixSingle(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref float __result)
        => __result = ResolveValue(__instance, __args, __originalMethod, __result);

    private static void DispatchPostfixSingleStatic(object[] __args, MethodBase __originalMethod, ref float __result)
        => __result = ResolveValue(null, __args, __originalMethod, __result);

    private static void DispatchPostfixObject(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref object __result)
        => __result = ResolveObject(__instance, __args, __originalMethod, __result);

    private static void DispatchPostfixObjectStatic(object[] __args, MethodBase __originalMethod, ref object __result)
        => __result = ResolveObject(null, __args, __originalMethod, __result);

    // Route the call to the mods' handlers, then accept an override only when it is the target's
    // exact value type. A boxed value of any other type is ignored, never coerced: Convert.ToXxx
    // would silently round (2.5 -> 2) or throw (overflow, non-numeric string) inside the postfix
    // and abort the game call.
    private static T ResolveValue<T>(object instance, object[] args, MethodBase originalMethod, T current) where T : struct
    {
        var result = Registry.DispatchPostfix(originalMethod, instance, args ?? Array.Empty<object>(), current, out var overridden);
        return overridden && result is T value ? value : current;
    }

    // Route the call to the mods' handlers, then accept an override only when it is null or
    // assignable to the target return type. Harmony writes it back into the typed return slot, so
    // a mismatch would throw inside the patched game call.
    private static object ResolveObject(object instance, object[] args, MethodBase originalMethod, object current)
    {
        var result = Registry.DispatchPostfix(originalMethod, instance, args ?? Array.Empty<object>(), current, out var overridden);
        return overridden && ResultAssignable(originalMethod, result) ? result : current;
    }

    // Whether an overriding reference Result can be written into the target's return slot: null,
    // a managed instance of the return type, or an Il2Cpp object whose native type casts to it.
    // Managed IsInstanceOfType alone is too strict under Il2Cpp interop, where a valid native
    // object can be held through a base-typed wrapper (e.g. a GameObject as a UnityEngine.Object).
    private static bool ResultAssignable(MethodBase originalMethod, object result)
    {
        if (result == null)
            return true;
        var returnType = (originalMethod as MethodInfo)?.ReturnType;
        if (returnType == null || returnType.IsInstanceOfType(result))
            return true;
        return result is Il2CppObjectBase il2cpp
            && Il2CppReflectiveCast.CastOrNull(il2cpp, returnType) != null;
    }

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
