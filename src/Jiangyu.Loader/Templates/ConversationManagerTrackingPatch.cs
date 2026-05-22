using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Harmony prefix on
/// <c>BaseConversationManager.GetAvailableConversationTemplates</c>
/// (inherited unchanged by every concrete subclass). MENACE's matcher
/// calls this method per trigger to get its candidate list; the prefix
/// runs BEFORE that body, captures <c>__instance</c>, and hands it to
/// <see cref="ConversationManagerRegistry"/>, which injects any pending
/// cloned ConversationTemplates into the manager's per-trigger bucket
/// dictionary. The method body then reads the (now-extended) bucket
/// and returns the candidate list with our clones included — even on
/// the very first dispatch after a manager constructs.
///
/// We patch this method rather than the manager constructors because
/// Il2CppInterop's IL2CPP-side patch backend rejects ctor patches on
/// derived types ("Derived classes must provide an implementation").
/// Method-level patches go through the normal handler path cleanly.
/// Patching the matcher's per-trigger entry point is also the earliest
/// reliable hook in the dispatch chain — anything later (e.g.
/// <c>TryFindSpeakerForRole</c>) runs after the candidate list has
/// already been snapshot, so injection there would miss the first
/// dispatch.
/// </summary>
internal sealed class ConversationManagerTrackingPatch : IHarmonyPatchModule
{
    private const string BaseManagerTypeName = "Il2CppMenace.Conversations.BaseConversationManager";
    private const string GetAvailableMethodName = "GetAvailableConversationTemplates";

    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        ConversationManagerRegistry.Init(context.Log);

        var baseType = ResolveType(BaseManagerTypeName);
        if (baseType == null)
        {
            _log.Warning($"Conversation manager tracking: type {BaseManagerTypeName} not found.");
            return;
        }

        // GetAvailableConversationTemplates is protected; include non-public
        // bindings so the lookup finds it.
        var target = baseType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == GetAvailableMethodName);
        if (target == null)
        {
            _log.Warning($"Conversation manager tracking: method {GetAvailableMethodName} not found on {BaseManagerTypeName}.");
            return;
        }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(
                typeof(ConversationManagerTrackingPatch),
                nameof(ManagerMethodPrefix)));
            _log.Msg($"Conversation manager tracking: prefix installed on {BaseManagerTypeName}.{GetAvailableMethodName}.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Conversation manager tracking: patch on {BaseManagerTypeName}.{GetAvailableMethodName} failed: {ex.Message}");
        }
    }

    public static void ManagerMethodPrefix(Il2CppObjectBase __instance)
    {
        if (__instance == null) return;
        try
        {
            ConversationManagerRegistry.RegisterManager(__instance);
        }
        catch (Exception ex)
        {
            _log?.Warning($"Conversation manager tracking: RegisterManager threw: {ex.Message}");
        }
    }

    private static Type ResolveType(string fqn)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fqn, throwOnError: false);
            if (t != null) return t;
        }
        return null;
    }
}
