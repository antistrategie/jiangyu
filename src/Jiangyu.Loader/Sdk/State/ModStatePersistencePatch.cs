using System;
using System.Collections.Generic;
using System.IO;
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
            var latest = SaveSystem.GetLatestSaveFilePath();
            // Autosaves and quicksaves pass no explicit path, so ResolveSavePath falls back to the
            // latest alias and the actual timestamped file must be recovered by mtime. A NAMED save
            // already gives us the exact file, so skip the mtime guess there: it could otherwise grab a
            // concurrent autosave that happens to be newer and attach the sidecar to the wrong save.
            bool pathless = string.IsNullOrEmpty(__1) && string.IsNullOrEmpty(__2);
            var targets = new[]
            {
                ResolveSavePath(__1, __2),
                latest,
                pathless ? GetJustWrittenSavePath(latest) : null,
            };
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in targets)
            {
                if (string.IsNullOrEmpty(path) || !written.Add(path))
                    continue;
                _log.Msg($"mod state: save -> {path}");
                store.WriteAll(path);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: save postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The file the game just wrote: the newest .save in the folder that is not the latest alias.
    // Save derives autosave and quicksave paths internally, so this recovers the path the postfix
    // is not handed.
    private static string GetJustWrittenSavePath(string latest)
    {
        try
        {
            var paths = SaveSystem.GetSaveFilePaths();
            if (paths == null)
                return null;
            string newest = null;
            var newestTime = DateTime.MinValue;
            for (int i = 0; i < paths.Length; i++)
            {
                var p = paths[i];
                if (string.IsNullOrEmpty(p) || string.Equals(p, latest, StringComparison.Ordinal))
                    continue;
                DateTime t;
                try
                {
                    if (!File.Exists(p))
                        continue;
                    t = File.GetLastWriteTimeUtc(p);
                }
                catch { continue; }
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = p;
                }
            }
            return newest;
        }
        catch
        {
            return null;
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
