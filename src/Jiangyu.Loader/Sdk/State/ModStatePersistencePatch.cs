using System;
using System.Reflection;
using HarmonyLib;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Sdk.State;

/// <summary>
/// Persists mod state across save/load by Harmony-patching the game's save system:
/// after a save, each mod's state is written to a sidecar beside the new save file;
/// after a load, each mod's state is read back from the sidecar. The store is created
/// after these patches install, so it is handed in via the static <see cref="Store"/>.
/// </summary>
internal sealed class ModStatePersistencePatch : IHarmonyPatchModule
{
    internal static ModStateStore Store;
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        Patch(harmony, "Il2CppMenace.Strategy.SaveSystem", "Save", nameof(SavePostfix));
        Patch(harmony, "Il2CppMenace.Strategy.SaveSystem", "ExecLoad", nameof(ExecLoadPostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, string typeName, string method, string postfix)
        => HarmonyPatching.TryPostfix(harmony, typeName, method, typeof(ModStatePersistencePatch), postfix, _log, "mod state");

    private static void SavePostfix(string __1, string __2)
    {
        var store = Store;
        if (store == null)
            return;
        try
        {
            var slot = ResolveSavePath(__1, __2);
            var latest = SaveSystem.GetLatestSaveFilePath();
            _log.Msg($"mod state: save -> {slot}");
            store.WriteAll(slot);
            if (!string.IsNullOrEmpty(latest) && !string.Equals(latest, slot, StringComparison.Ordinal))
                store.WriteAll(latest);
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: save postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ExecLoadPostfix(SaveState __0)
    {
        try
        {
            if (__0 == null)
                return;
            var path = __0.GetFilePath();
            _log.Msg($"mod state: load <- {path}");
            Store?.LoadAll(path);
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: load postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The slot the game is writing: the explicit path, else one derived from the save
    // name, else the latest-save alias.
    private static string ResolveSavePath(string filePath, string saveGameName)
    {
        if (!string.IsNullOrEmpty(filePath))
            return filePath;
        if (!string.IsNullOrEmpty(saveGameName))
            return SaveSystem.GetSaveFilePath(saveGameName);
        return SaveSystem.GetLatestSaveFilePath();
    }
}
