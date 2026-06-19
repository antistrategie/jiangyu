using System.Reflection;
using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Guards <c>SuppressionHandler.OnUpdate(EntityProperties)</c> against an off-mission null dereference.
/// The handler is a per-tick TACTICAL skill effect that reads the owning entity's
/// <c>EntityProperties</c>. When a leader's skill container is driven outside a tactical mission (e.g. a
/// mod re-shows or recomputes a leader on the strategy/Armory screen via <c>UnitWindow.SetLeader</c> or
/// <c>UpdatePropertiesBasedOnAttributes</c>), those properties are absent and the handler throws
/// <see cref="NullReferenceException"/>, which propagates out of the UI call and strands the screen.
///
/// A Harmony PREFIX guards the precondition instead of catching the fallout: when no tactical mission is
/// running (<c>TacticalManager.IsMissionRunning()</c>), OnUpdate is skipped entirely. This prevents the
/// deref rather than swallowing it, so a genuine fault inside a live mission is never masked. In a live
/// mission the gate is true and OnUpdate runs unchanged.
///
/// Loader-wide rather than mod-side because the mod SDK exposes only void prefix/postfix (no skip-prefix
/// or finalizer), and the trigger is a general engine assumption (OnUpdate presumes a mission context)
/// that any mod refreshing a leader off-mission can hit, not WOMENACE-specific logic.
/// </summary>
internal sealed class SuppressionHandlerGuardPatch : IHarmonyPatchModule
{
    private const string Label = "Suppression handler guard";

    // EXACT type name, not a suffix: a suffix match also hits ChangeSuppressionHandler, which does not
    // override OnUpdate, so it would resolve to (and patch) the shared base SkillEventHandler.OnUpdate
    // and wrap the prefix around every skill handler's per-tick update. SuppressionHandler declares its
    // own OnUpdate, so an exact match patches just that narrow override.
    private const string TypeName = "SuppressionHandler";
    private const string MethodName = "OnUpdate";

    private static MelonLogger.Instance _log;
    private static Func<bool> _isMissionRunning;
    private static bool _loggedSkip;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        var method = Il2CppMethodResolver.Find(TypeName, MethodName, new[] { "EntityProperties" }, exact: true, _log, Label);
        if (method == null)
        {
            _log.Warning($"{Label}: {TypeName}.{MethodName} not found; recomputing a leader off-mission may crash.");
            return;
        }

        // The precondition gate. TacticalManager.IsMissionRunning() is the canonical "a tactical mission
        // is live" check, bound once as a delegate so the per-tick prefix pays no reflection cost. If it
        // cannot be resolved the prefix runs the original unchanged (fail to normal behaviour), so a
        // game-side rename degrades to the pre-guard state, never to a wrong skip.
        var gate = Il2CppMethodResolver.Find("TacticalManager", "IsMissionRunning", Array.Empty<string>(), exact: true, _log, Label);
        if (gate == null)
            _log.Warning($"{Label}: TacticalManager.IsMissionRunning not found; the off-mission skip is disabled.");
        else
            try { _isMissionRunning = (Func<bool>)gate.CreateDelegate(typeof(Func<bool>)); }
            catch (Exception ex) { _log.Warning($"{Label}: could not bind IsMissionRunning ({ex.Message}); off-mission skip disabled."); }

        try
        {
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(SuppressionHandlerGuardPatch), nameof(Prefix)));
            _log.Msg($"{Label}: hooked {method.DeclaringType?.Name}.{MethodName} (skips off-mission).");
        }
        catch (Exception ex)
        {
            _log.Warning($"{Label}: patch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Prefix. Returning false skips OnUpdate entirely. True runs it. The method is a per-tick tactical
    /// effect, so it is skipped when no mission is running (the off-mission case that null-derefs). If the
    /// mission-state gate is unbound or throws, the original is run, so normal in-mission behaviour and
    /// any pre-existing behaviour on an unknown game build are preserved.
    /// </summary>
    public static bool Prefix()
    {
        if (_isMissionRunning == null)
            return true;
        try
        {
            bool inMission = _isMissionRunning();
            if (!inMission && !_loggedSkip)
            {
                _loggedSkip = true;
                _log?.Msg($"{Label}: {MethodName} called off-mission; skipping (per-tick tactical effect).");
            }
            return inMission;
        }
        catch
        {
            return true;
        }
    }
}
