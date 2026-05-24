using System.Reflection;
using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Installs early campaign-lifecycle hooks that ensure Jiangyu clone IDs are
/// present before MENACE starts a new campaign or deserialises a save.
/// </summary>
internal sealed class TemplateCloneEarlyInjectionPatch : IHarmonyPatchModule
{
    private static readonly string[] StrategyStateTypeCandidates =
    {
        "Il2CppMenace.States.StrategyState",
        "Il2CppMenace.Strategy.StrategyState",
        "Menace.States.StrategyState",
        "Menace.Strategy.StrategyState",
    };
    private static readonly string[] SaveSystemTypeCandidates =
    {
        "Il2CppMenace.Strategy.SaveSystem",
        "Menace.Strategy.SaveSystem",
    };
    private static readonly string[] SceneStateSettingsTypeCandidates =
    {
        "Il2CppMenace.States.SceneStateSettings",
        "Menace.States.SceneStateSettings",
    };
    private static readonly string[] GameStartConfigTypeCandidates =
    {
        "Il2CppMenace.GameStartConfig",
        "Menace.GameStartConfig",
    };
    private static readonly string[] SaveSystemLoadMethodNames =
    {
        "Load",
        "ExecLoad",
        "LoadSaveGameCoroutine",
        "TryLoadQuickSave",
    };
    private static readonly string[] SaveSystemDiscoveryMethodNames =
    {
        "TryGetLatestSaveState",
        "TryGetSaveState",
        "GetSortedSaveStates",
    };

    private static TemplateCloneApplier _templateCloneApplier;
    private static MelonLogger.Instance _log;

    public TemplateCloneEarlyInjectionPatch(TemplateCloneApplier templateCloneApplier)
    {
        _templateCloneApplier = templateCloneApplier;
    }

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        if (_templateCloneApplier == null || !_templateCloneApplier.HasConfiguredClones)
            return;

        var strategyStateType = ResolveStrategyStateType();
        if (strategyStateType != null)
            PatchCreateNewGame(harmony, strategyStateType);
        else
            _log.Warning("Template clone early injection: StrategyState type not found.");

        PatchStartupMethods(harmony);
        PatchLoadMethods(harmony, strategyStateType);
    }

    // JIANGYU-CONTRACT: In the current MENACE build validated by the
    // 2026-04-20 clone-persistence smoke run, clone registration at
    // SceneStateSettings.Awake occurs early enough for both save-slot
    // discovery and the later SaveSystem.Load/ExecLoad path. The additional
    // SaveSystem discovery/load prefixes keep Jiangyu on the same pre-load
    // boundary if MENACE reorders startup work, but scene-load polling alone
    // is too late for clone-backed saves.
    private static Type ResolveStrategyStateType()
    {
        foreach (var candidate in StrategyStateTypeCandidates)
        {
            var resolved = AccessTools.TypeByName(candidate);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private static void PatchCreateNewGame(HarmonyLib.Harmony harmony, Type strategyStateType)
    {
        var target = strategyStateType.GetMethod(
            "CreateNewGame",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (target == null)
        {
            _log.Warning("Template clone early injection: StrategyState.CreateNewGame not found.");
            return;
        }

        PatchMethod(harmony, target, nameof(CreateNewGamePrefix));
    }

    private static void PatchStartupMethods(HarmonyLib.Harmony harmony)
    {
        var sceneStateSettingsType = ResolveType(SceneStateSettingsTypeCandidates);
        if (sceneStateSettingsType != null)
        {
            var awake = sceneStateSettingsType.GetMethod(
                "Awake",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (awake != null)
                PatchMethod(harmony, awake, nameof(StartupEntryPrefix));
        }

        var gameStartConfigType = ResolveType(GameStartConfigTypeCandidates);
        if (gameStartConfigType != null)
        {
            var beforeSceneLoad = gameStartConfigType.GetMethod(
                "BeforeSceneLoad",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (beforeSceneLoad != null)
                PatchMethod(harmony, beforeSceneLoad, nameof(StartupEntryPrefix));

            var initializeGame = gameStartConfigType.GetMethod(
                "InitializeGame",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (initializeGame != null)
                PatchMethod(harmony, initializeGame, nameof(StartupEntryPrefix));
        }
    }

    private static void PatchLoadMethods(HarmonyLib.Harmony harmony, Type strategyStateType)
    {
        var patchedAny = false;
        var strategyStateTargets = strategyStateType == null
            ? Array.Empty<MethodInfo>()
            : strategyStateType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static method =>
                    method.Name.Contains("LoadGame", StringComparison.Ordinal) ||
                    method.Name.Contains("LoadSave", StringComparison.Ordinal))
                .ToArray();

        foreach (var target in strategyStateTargets)
        {
            PatchMethod(harmony, target, nameof(LoadEntryPrefix));
            patchedAny = true;
        }

        var saveSystemType = ResolveSaveSystemType();
        if (saveSystemType == null)
        {
            if (!patchedAny)
                _log.Warning("Template clone early injection: SaveSystem type not found and no StrategyState load methods found.");

            return;
        }

        patchedAny |= PatchNamedStaticMethods(
            harmony,
            saveSystemType,
            SaveSystemLoadMethodNames,
            nameof(LoadEntryPrefix));
        patchedAny |= PatchNamedStaticMethods(
            harmony,
            saveSystemType,
            SaveSystemDiscoveryMethodNames,
            nameof(LoadEntryPrefix));

        if (!patchedAny)
            _log.Warning("Template clone early injection: no recognised StrategyState/SaveSystem load methods found.");
    }

    private static bool PatchNamedStaticMethods(
        HarmonyLib.Harmony harmony,
        Type targetType,
        IEnumerable<string> methodNames,
        string prefixName)
    {
        var patchedAny = false;
        foreach (var methodName in methodNames)
        {
            var targets = targetType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();

            foreach (var target in targets)
            {
                PatchMethod(harmony, target, prefixName);
                patchedAny = true;
            }
        }

        return patchedAny;
    }

    private static void PatchMethod(HarmonyLib.Harmony harmony, MethodInfo target, string prefixName)
    {
        harmony.Patch(target, prefix: new HarmonyMethod(typeof(TemplateCloneEarlyInjectionPatch), prefixName));
        _log.Msg($"Patched {target.DeclaringType?.Name}.{target.Name} for early template clone injection.");
    }

    private static Type ResolveSaveSystemType()
        => ResolveType(SaveSystemTypeCandidates);

    private static Type ResolveType(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = AccessTools.TypeByName(candidate);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private static void CreateNewGamePrefix() => EnsureClonesNow("CreateNewGame");

    private static void StartupEntryPrefix(MethodBase __originalMethod)
    {
        var trigger = __originalMethod == null
            ? "Startup"
            : $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
        EnsureClonesNow(trigger);
    }

    private static void LoadEntryPrefix(MethodBase __originalMethod)
    {
        var trigger = __originalMethod == null
            ? "LoadGame"
            : $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
        EnsureClonesNow(trigger);
    }

    private static void EnsureClonesNow(string trigger)
    {
        if (_templateCloneApplier == null || !_templateCloneApplier.HasConfiguredClones)
            return;

        _templateCloneApplier.ResetApplyState();
        var applied = _templateCloneApplier.TryApply(_log);

        if (_templateCloneApplier.HasPendingClones)
        {
            _log.Warning(
                $"Template clone early injection via {trigger} completed with unresolved directives still pending.");
            return;
        }

        _log.Msg($"Template clone early injection via {trigger}: applied {applied} clone registration(s).");
    }
}
