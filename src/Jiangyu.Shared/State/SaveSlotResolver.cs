using System;

namespace Jiangyu.Shared.State;

/// <summary>
/// Picks the save file a per-save mod state sidecar belongs beside, from the arguments the game's
/// save call was handed. Returns null when the file cannot be named from those arguments alone,
/// which is the caller's signal to recover the just-written file some other way.
/// </summary>
public static class SaveSlotResolver
{
    /// <summary>
    /// Resolve the save file being written. <paramref name="filePath"/> is the explicit path the
    /// caller passed, if any; <paramref name="saveGameName"/> is the name typed for a new save.
    /// <paramref name="derivePathFromName"/> turns that name into the path the game would write it
    /// to, returning null when the name yields no derivable file. A path that
    /// <paramref name="fileExists"/> rejects is treated as underivable too, so a derivation that
    /// has drifted from the game degrades to null instead of orphaning the sidecar.
    /// </summary>
    public static string? Resolve(
        string? filePath,
        string? saveGameName,
        Func<string, string?> derivePathFromName,
        Func<string, bool> fileExists)
    {
        if (derivePathFromName == null)
            throw new ArgumentNullException(nameof(derivePathFromName));
        if (fileExists == null)
            throw new ArgumentNullException(nameof(fileExists));

        var path = !string.IsNullOrEmpty(filePath)
            ? filePath
            : string.IsNullOrEmpty(saveGameName) ? null : derivePathFromName(saveGameName);
        if (string.IsNullOrEmpty(path))
            return null;
        return fileExists(path) ? path : null;
    }
}
