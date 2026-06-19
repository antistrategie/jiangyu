using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Guards tactical spawn of a modular vehicle that carries no mountable
/// weapons. MENACE's <c>ModularVehicleSystem.VisuallyMountWeapons</c> calls
/// <c>ItemsModularVehicle.CheckForTwinFire</c>, which indexes the vehicle's
/// weapon/slot arrays without a bounds check. A modkit vehicle whose
/// <c>ModularVehicle</c> has no slots and whose <c>Items</c> carry no weapons
/// (e.g. a cosmetic chassis that fights through its pilot's skills) makes that
/// index throw <see cref="IndexOutOfRangeException"/>. The throw propagates out
/// of <c>Element.Create</c>, leaving the element half-built so its per-frame
/// <c>Element.Update</c> then null-refs forever and the mission appears frozen.
///
/// A Harmony finalizer swallows any exception escaping VisuallyMountWeapons so
/// <c>Element.Create</c> runs to completion. The weaponless vehicle simply
/// mounts nothing, which is the intended outcome. The guard is inert for
/// vanilla vehicles: they always carry weapons, so this method never throws for
/// them and the finalizer's exception branch is never taken.
/// </summary>
internal sealed class ModularVehicleSpawnGuardPatch : IHarmonyPatchModule
{
    private const string TypeNameSuffix = "ModularVehicleSystem";
    private const string MethodName = "VisuallyMountWeapons";

    private static MelonLogger.Instance _log;
    private static readonly HashSet<string> _warnedSignatures = new();

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        var method = Il2CppMethodResolver.Find(TypeNameSuffix, MethodName, new[] { "ItemContainer", "Transform" }, exact: false, _log, "Modular vehicle spawn guard");
        if (method == null)
        {
            _log.Warning($"Modular vehicle spawn guard: {TypeNameSuffix}.{MethodName} not found; weaponless modular vehicles may crash on spawn.");
            return;
        }

        try
        {
            harmony.Patch(method,
                finalizer: new HarmonyMethod(typeof(ModularVehicleSpawnGuardPatch), nameof(Finalizer)));
            _log.Msg($"Modular vehicle spawn guard: hooked {method.DeclaringType?.Name}.{MethodName}.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Modular vehicle spawn guard: patch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finalizer. Returning null clears any pending exception from the original
    /// method, so a weapon-mount failure on a weaponless vehicle no longer
    /// aborts the surrounding <c>Element.Create</c>. A weaponless modular vehicle
    /// is the supported case this guard exists for, so the swallow is logged once
    /// per distinct signature at info level rather than as a warning. Logging
    /// once still leaves a breadcrumb if a genuinely different failure is ever
    /// hidden here, without flagging the expected case as a problem.
    /// </summary>
    public static Exception Finalizer(Exception __exception)
    {
        if (__exception != null)
        {
            // Il2CppException.Message embeds a "--- BEGIN IL2CPP STACK TRACE ---" block, which reads
            // like an unhandled error in the log even though this exception was caught and swallowed.
            // Keep only the first line: enough to identify the case, without the alarming trace text.
            var message = __exception.Message ?? string.Empty;
            var firstLineBreak = message.IndexOfAny(new[] { '\r', '\n' });
            if (firstLineBreak >= 0)
                message = message.Substring(0, firstLineBreak);
            var signature = __exception.GetType().Name + ": " + message;
            if (signature.Length > 200)
                signature = signature.Substring(0, 200);
            if (_warnedSignatures.Add(signature))
                _log?.Msg($"Modular vehicle spawn guard: swallowed in {MethodName}, spawn continues without mounted weapons (expected for weaponless modular vehicles). {signature}");
        }
        return null;
    }
}
