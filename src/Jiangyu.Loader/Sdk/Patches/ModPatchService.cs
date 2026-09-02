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
        Action<PatchInfo> handler, IModHostLog log, int? parameterCount = null)
    {
        if (handler == null)
            return;
        if (_harmony == null)
        {
            log.Warn($"[{modId}] patches are unavailable; {typeName}.{methodName} not patched.");
            return;
        }

        var target = ResolveMethod(typeName, methodName, parameterCount, modId, log);
        if (target == null)
            return;

        var label = $"{typeName}.{methodName}";
        Registry.Add(kind, target, modId, label, handler, log);
        if (EnsurePatched(kind, target, log))
            log.Debug($"[{modId}] patch {kind.ToString().ToLowerInvariant()} registered on {label}");
    }

    public static void RemoveMod(string modId) => Registry.RemoveMod(modId);

    /// <summary>How many handlers <paramref name="modId"/> has registered, across both kinds.</summary>
    public static int CountForMod(string modId) => Registry.CountForMod(modId);

    private static bool EnsurePatched(ModPatchRegistry.Kind kind, MethodBase target, IModHostLog log)
    {
        var applied = kind == ModPatchRegistry.Kind.Prefix ? PatchedPrefix : PatchedPostfix;
        if (!applied.Add(target))
            return true;

        try
        {
            if (kind == ModPatchRegistry.Kind.Prefix)
            {
                // Mirror of the postfix machinery below: a typed ref-__result prefix
                // dispatcher lets a handler that skips the original also set the
                // skipped call's return value. Fall back to the result-less prefix
                // when the typed one does not bind, so skip-only prefixes still work.
                try
                {
                    _harmony.Patch(target, prefix: PrefixDispatcherFor(target));
                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn($"patch: {target.DeclaringType?.Name}.{target.Name} return not settable from a prefix ({ex.GetType().Name}); a skipping prefix leaves the default return.");
                    var prefixName = target.IsStatic ? nameof(DispatchPrefixStatic) : nameof(DispatchPrefix);
                    _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModPatchCoordinator), prefixName));
                    return true;
                }
            }

            // PostfixDispatcherFor maps the return type to its dispatcher (typed
            // int/bool/float, the reference-typed object dispatcher, the boxed one for other
            // value returns, or the result-less one for void). Try it; if binding the typed
            // ref-__result dispatcher throws (some Il2Cpp value returns do not marshal to a
            // ref parameter), fall back to the result-less dispatcher so an observe-only
            // postfix still registers. PrefixDispatcherFor mirrors the same mapping and the
            // two must move together.
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
    // ref object __result only binds to reference-typed returns, so int, bool
    // and float each get their own dispatcher and every reference-typed
    // return shares the object dispatcher. Other value returns (structs,
    // nullables) ride the BOXED dispatcher, whose exact-type gate
    // (BoxedMatchesReturn) accepts an override or drops it. Static targets
    // take the instance-less variants: an __instance parameter does not bind
    // to a static original.
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
        else if (returnType != null && returnType.IsValueType && returnType != typeof(void))
            name = target.IsStatic ? nameof(DispatchPostfixBoxedStatic) : nameof(DispatchPostfixBoxed);
        else
            name = target.IsStatic ? nameof(DispatchPostfixStatic) : nameof(DispatchPostfix);
        return new HarmonyMethod(typeof(ModPatchCoordinator), name);
    }

    // The prefix mapping is the same shape as PostfixDispatcherFor: typed ref-__result
    // dispatchers so a skipping prefix can set the return, the result-less pair for
    // void returns and the binding-failure fallback.
    private static HarmonyMethod PrefixDispatcherFor(MethodBase target)
    {
        var returnType = (target as MethodInfo)?.ReturnType;
        string name;
        if (returnType == typeof(int))
            name = target.IsStatic ? nameof(DispatchPrefixInt32Static) : nameof(DispatchPrefixInt32);
        else if (returnType == typeof(bool))
            name = target.IsStatic ? nameof(DispatchPrefixBooleanStatic) : nameof(DispatchPrefixBoolean);
        else if (returnType == typeof(float))
            name = target.IsStatic ? nameof(DispatchPrefixSingleStatic) : nameof(DispatchPrefixSingle);
        else if (returnType != null && !returnType.IsValueType && returnType != typeof(void))
            name = target.IsStatic ? nameof(DispatchPrefixObjectStatic) : nameof(DispatchPrefixObject);
        else if (returnType != null && returnType.IsValueType && returnType != typeof(void))
            name = target.IsStatic ? nameof(DispatchPrefixBoxedStatic) : nameof(DispatchPrefixBoxed);
        else
            name = target.IsStatic ? nameof(DispatchPrefixStatic) : nameof(DispatchPrefix);
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

    // Typed prefix dispatchers: run the handlers; when one skipped the original AND
    // assigned Result, write it into the return slot the skipped call leaves behind.
    // Type gates match the postfix Resolve helpers (exact value type, assignable
    // reference, exactly-typed boxed struct).
    private static bool DispatchPrefixInt32(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref int __result)
        => PrefixValue(__instance, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixInt32Static(object[] __args, MethodBase __originalMethod, ref int __result)
        => PrefixValue(null, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixBoolean(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref bool __result)
        => PrefixValue(__instance, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixBooleanStatic(object[] __args, MethodBase __originalMethod, ref bool __result)
        => PrefixValue(null, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixSingle(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref float __result)
        => PrefixValue(__instance, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixSingleStatic(object[] __args, MethodBase __originalMethod, ref float __result)
        => PrefixValue(null, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixObject(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref object __result)
        => PrefixObject(__instance, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixObjectStatic(object[] __args, MethodBase __originalMethod, ref object __result)
        => PrefixObject(null, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixBoxed(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref object __result)
        => PrefixBoxed(__instance, __args, __originalMethod, ref __result);

    private static bool DispatchPrefixBoxedStatic(object[] __args, MethodBase __originalMethod, ref object __result)
        => PrefixBoxed(null, __args, __originalMethod, ref __result);

    // The postfix twins below fall back to the ORIGINAL call's return value when they reject a
    // mismatched override, which makes rejection harmless there. A prefix that has already set Skip
    // has no such value to fall back on: Harmony's __result is still zero-initialised, so a rejected
    // override hands the caller default(T) rather than either the value the handler meant or
    // vanilla's. The rejection itself stands (coercing would round or throw inside the patched call,
    // see ResolveValue), but it is never silent: the symptom is a plain wrong number surfacing far
    // from the handler that caused it, and `info.Result = 12` on a float-returning method is the
    // natural way to write it.
    private static bool PrefixValue<T>(object instance, object[] args, MethodBase originalMethod, ref T result) where T : struct
    {
        var run = Registry.DispatchPrefix(originalMethod, instance, args ?? Array.Empty<object>(), out var overridden, out var value);
        if (!run && overridden)
        {
            if (value is T typed)
                result = typed;
            else
                WarnRejectedPrefixResult(originalMethod, value);
        }
        return run;
    }

    private static bool PrefixObject(object instance, object[] args, MethodBase originalMethod, ref object result)
    {
        var run = Registry.DispatchPrefix(originalMethod, instance, args ?? Array.Empty<object>(), out var overridden, out var value);
        if (!run && overridden)
        {
            if (ResultAssignable(originalMethod, value))
                result = value;
            else
                WarnRejectedPrefixResult(originalMethod, value);
        }
        return run;
    }

    private static bool PrefixBoxed(object instance, object[] args, MethodBase originalMethod, ref object result)
    {
        var run = Registry.DispatchPrefix(originalMethod, instance, args ?? Array.Empty<object>(), out var overridden, out var value);
        if (!run && overridden)
        {
            if (BoxedMatchesReturn(originalMethod, value))
                result = value;
            else
                WarnRejectedPrefixResult(originalMethod, value);
        }
        return run;
    }

    // Boxing a Nullable<T> yields a boxed T (or a null reference), never a boxed Nullable<T>, so a
    // nullable return has to be compared against its underlying type or the exact-type gate can
    // never be satisfied. Null passes for a nullable return and only there: a boxed empty
    // Nullable<T> IS a null reference, and rejecting it made 'return null' impossible to express.
    private static bool BoxedMatchesReturn(MethodBase originalMethod, object value)
    {
        var returnType = (originalMethod as MethodInfo)?.ReturnType;
        if (returnType == null)
            return false;
        var underlying = Nullable.GetUnderlyingType(returnType);
        if (value == null)
            return underlying != null;
        return value.GetType() == (underlying ?? returnType);
    }

    // Once per method: the rejection repeats on every invocation of the
    // patched call, and a hot method would otherwise flood the log with a
    // stack-walking warn per frame.
    private static readonly HashSet<MethodBase> WarnedRejected = new();

    private static void WarnRejectedPrefixResult(MethodBase originalMethod, object value)
    {
        lock (WarnedRejected)
        {
            if (originalMethod != null && !WarnedRejected.Add(originalMethod))
                return;
        }
        var returnType = (originalMethod as MethodInfo)?.ReturnType;
        Log.Warn(
            $"patch: a skipping prefix on {originalMethod?.DeclaringType?.Name}.{originalMethod?.Name} set Result to " +
            $"{value?.GetType().Name ?? "null"}, but the method returns {returnType?.Name ?? "?"}. The override was " +
            "ignored and the caller receives the default value; assign a Result of exactly the return type.");
    }

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

    // Other value-typed returns (structs beyond int/bool/float) ride Harmony's
    // boxed ref object __result: HarmonyX boxes the value on the way in and
    // unboxes an override on the way out, so the dispatcher only has to keep
    // the exact-type gate.
    private static void DispatchPostfixBoxed(Il2CppObjectBase __instance, object[] __args, MethodBase __originalMethod, ref object __result)
        => __result = ResolveBoxed(__instance, __args, __originalMethod, __result);

    private static void DispatchPostfixBoxedStatic(object[] __args, MethodBase __originalMethod, ref object __result)
        => __result = ResolveBoxed(null, __args, __originalMethod, __result);

    // Route the call to the mods' handlers, then accept an override only when it is the target's
    // exact value type. A boxed value of any other type is ignored, never coerced: Convert.ToXxx
    // would silently round (2.5 -> 2) or throw (overflow, non-numeric string) inside the postfix
    // and abort the game call.
    private static T ResolveValue<T>(object instance, object[] args, MethodBase originalMethod, T current) where T : struct
    {
        var result = Registry.DispatchPostfix(originalMethod, instance, args ?? Array.Empty<object>(), current, out var overridden);
        return overridden && result is T value ? value : current;
    }

    // As ResolveValue, for the boxed struct path: an override is accepted only when the assigned
    // boxed value is EXACTLY the target's return type. Anything else (null included: a struct
    // return has no null) is ignored, never coerced, so a stray assignment cannot corrupt the
    // patched game call's return slot.
    private static object ResolveBoxed(object instance, object[] args, MethodBase originalMethod, object current)
    {
        var result = Registry.DispatchPostfix(originalMethod, instance, args ?? Array.Empty<object>(), current, out var overridden);
        return overridden && BoxedMatchesReturn(originalMethod, result) ? result : current;
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

    private static MethodBase ResolveMethod(string typeName, string methodName, int? parameterCount, string modId, IModHostLog log)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            log.Warn($"[{modId}] patch target type '{typeName}' not found.");
            return null;
        }

        // Overloaded target: the caller disambiguates by parameter count.
        if (parameterCount is { } count)
        {
            var byArity = SelectMethodByArity(type.GetMethods(MethodFlags), methodName, count);
            if (byArity == null)
                log.Warn($"[{modId}] patch target '{typeName}.{methodName}' has no unique {count}-parameter overload.");
            return byArity;
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
            log.Warn($"[{modId}] patch target '{typeName}.{methodName}' is overloaded; pass a parameterCount to disambiguate.");
            return null;
        }
    }

    // The single method named <paramref name="methodName"/> taking exactly
    // <paramref name="parameterCount"/> parameters, or null when none or more than
    // one match (an overload set with two same-arity members cannot be resolved by
    // count alone). Internal + static so the loader tests can exercise the selection
    // against a plain test type, without game types.
    internal static MethodInfo SelectMethodByArity(MethodInfo[] methods, string methodName, int parameterCount)
    {
        MethodInfo match = null;
        foreach (var method in methods)
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                continue;
            if (method.GetParameters().Length != parameterCount)
                continue;
            if (match != null)
                return null;
            match = method;
        }
        return match;
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

    public void Prefix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler)
        => ModPatchCoordinator.Register(_modId, ModPatchRegistry.Kind.Prefix, typeName, methodName, handler, _log, parameterCount);

    public void Postfix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler)
        => ModPatchCoordinator.Register(_modId, ModPatchRegistry.Kind.Postfix, typeName, methodName, handler, _log, parameterCount);

    public void RemoveAll() => ModPatchCoordinator.RemoveMod(_modId);
}

/// <summary>The patches view for a context with no patch coordinator (tests).</summary>
internal sealed class NullModPatches : IModPatches
{
    public static readonly NullModPatches Instance = new();

    public void Prefix(string typeName, string methodName, Action<PatchInfo> handler) { }

    public void Postfix(string typeName, string methodName, Action<PatchInfo> handler) { }

    public void Prefix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler) { }

    public void Postfix(string typeName, string methodName, int parameterCount, Action<PatchInfo> handler) { }
}
