using System;
using System.Collections.Generic;

namespace Jiangyu.Shared.State;

/// <summary>What to do with a mod state sidecar whose save file is gone.</summary>
public enum SidecarRepairAction
{
    /// <summary>Move it onto the save it was always meant for.</summary>
    Reattach,

    /// <summary>Delete it: the save it belongs to no longer exists.</summary>
    Discard,

    /// <summary>Park it out of the way: its save exists but already carries newer state.</summary>
    Retire,
}

/// <summary>One planned repair. <see cref="TargetPath"/> is set for everything but a discard.</summary>
public readonly struct SidecarRepair
{
    public SidecarRepair(string sidecarPath, SidecarRepairAction action, string? targetPath = null)
    {
        SidecarPath = sidecarPath;
        Action = action;
        TargetPath = targetPath;
    }

    public string SidecarPath { get; }
    public SidecarRepairAction Action { get; }
    public string? TargetPath { get; }
}

/// <summary>
/// Decides what becomes of the sidecars in a save folder that no longer sit beside a save file.
/// Two things strand a sidecar: the game deleting a save (autosave rotation, a manual delete), and
/// a sidecar written under the typed save name before the loader folded that name the way the game
/// does. The first is dead weight; the second still holds the state its save should have loaded, so
/// it is reattached, or retired intact when the save it wants already carries state of its own.
/// </summary>
public static class ModStateSidecarRepairPlan
{
    /// <summary>
    /// Plan the repairs for <paramref name="sidecarPaths"/>. <paramref name="fileExists"/> reports
    /// whether a path is present on disk; <paramref name="foldSavePath"/> maps a save path to the one
    /// the game's own name fold would have produced, or null when it cannot. Sidecars that still sit
    /// beside their save, and paths that are not sidecars at all, yield nothing.
    /// </summary>
    public static IReadOnlyList<SidecarRepair> Build(
        IEnumerable<string> sidecarPaths,
        Func<string, bool> fileExists,
        Func<string, string?> foldSavePath)
    {
        if (sidecarPaths == null)
            throw new ArgumentNullException(nameof(sidecarPaths));
        if (fileExists == null)
            throw new ArgumentNullException(nameof(fileExists));
        if (foldSavePath == null)
            throw new ArgumentNullException(nameof(foldSavePath));

        var repairs = new List<SidecarRepair>();
        // Two typed names can fold to one save, so a target claimed earlier in this batch is as
        // taken as one already on disk: the second sidecar is retired rather than moved onto it.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sidecarPath in sidecarPaths)
        {
            var savePath = ModStateSidecar.SavePathFor(sidecarPath);
            var modId = ModStateSidecar.ModIdFor(sidecarPath);
            if (savePath == null || modId == null || fileExists(savePath))
                continue;

            var folded = foldSavePath(savePath);
            if (folded == null || folded == savePath || !fileExists(folded))
            {
                repairs.Add(new SidecarRepair(sidecarPath, SidecarRepairAction.Discard));
                continue;
            }

            // The suffix carries the mod id, so it rides along to the save being reattached to.
            var target = ModStateSidecar.PathFor(folded, modId);
            repairs.Add(fileExists(target) || !claimed.Add(target)
                ? new SidecarRepair(sidecarPath, SidecarRepairAction.Retire, ModStateSidecar.OrphanPathFor(sidecarPath))
                : new SidecarRepair(sidecarPath, SidecarRepairAction.Reattach, target));
        }

        return repairs;
    }
}
