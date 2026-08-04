using System;
using System.IO;
using Il2CppMenace.Strategy;
using Jiangyu.Shared.State;
using MelonLoader;

namespace Jiangyu.Loader.Sdk.State;

/// <summary>
/// Sweeps the save folder once a session for mod state sidecars with no save file behind them,
/// reattaching the ones whose save is still there under its folded name and deleting the rest.
/// </summary>
internal static class ModStateSidecarRepair
{
    private static bool _swept;

    /// <summary>Sweep on the first call of a session that gets through; later calls do nothing.</summary>
    public static void RunOnce(MelonLogger.Instance log)
    {
        if (_swept)
            return;

        try
        {
            var folder = SaveSystem.GetAndCreateSaveFileFolderPath();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return;

            var sidecars = Directory.GetFiles(folder, ModStateSidecar.SearchPattern);
            var repairs = ModStateSidecarRepairPlan.Build(sidecars, File.Exists, SaveNameFold.PathForSavePath);
            int reattached = 0;
            int discarded = 0;
            int retired = 0;
            foreach (var repair in repairs)
            {
                try
                {
                    switch (repair.Action)
                    {
                        case SidecarRepairAction.Reattach:
                            File.Move(repair.SidecarPath, repair.TargetPath);
                            reattached++;
                            log.Msg($"mod state: reattached {Path.GetFileName(repair.SidecarPath)} -> {Path.GetFileName(repair.TargetPath)}");
                            break;
                        case SidecarRepairAction.Retire:
                            // An orphan already parked under this name is the same sidecar from an
                            // earlier sweep, so leaving it is what keeps the older state readable.
                            if (!File.Exists(repair.TargetPath))
                            {
                                File.Move(repair.SidecarPath, repair.TargetPath);
                                retired++;
                                log.Msg($"mod state: retired {Path.GetFileName(repair.SidecarPath)}, its save already carries state");
                            }
                            break;
                        default:
                            File.Delete(repair.SidecarPath);
                            discarded++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"mod state: repair failed for {Path.GetFileName(repair.SidecarPath)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _swept = true;
            if (reattached > 0 || discarded > 0 || retired > 0)
                log.Msg($"mod state: swept sidecars, {reattached} reattached, {retired} retired, {discarded} discarded");
        }
        catch (Exception ex)
        {
            // The flag stays down, so a folder that was not readable yet gets another sweep.
            log.Error($"mod state: sidecar sweep failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
