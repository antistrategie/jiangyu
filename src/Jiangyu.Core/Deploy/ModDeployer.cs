using Jiangyu.Core.IO;

namespace Jiangyu.Core.Deploy;

/// <summary>
/// Deploys a compiled mod into the game's <c>Mods/&lt;name&gt;/</c> folder. The deploy
/// is clean: the destination folder is removed first, so stale artifacts from a
/// previous build (renamed code DLLs, dropped bundles, backup files) never linger
/// and get loaded alongside the current ones.
/// </summary>
public static class ModDeployer
{
    /// <summary>
    /// Replace <paramref name="destDir"/> entirely with the contents of
    /// <paramref name="compiledDir"/>.
    /// </summary>
    public static void Deploy(string compiledDir, string destDir)
    {
        if (!Directory.Exists(compiledDir))
            throw new DirectoryNotFoundException($"compiled directory not found: {compiledDir}");

        // Route through ResilientFs: the destination lives under the game's Mods/ folder, so this
        // is the delete most likely to hit a live lock (the game holding a just-loaded bundle),
        // and the helper both retries transient locks and reports a "close MENACE" hint on a hard
        // failure instead of a bare IOException.
        ResilientFs.DeleteDirectory(destDir);

        DirectoryCopier.Copy(compiledDir, destDir, overwrite: true);
    }

    /// <summary>
    /// The <c>Mods/&lt;name&gt;</c> destination for a mod, validating that
    /// <paramref name="modName"/> is a plain folder name. <see cref="Deploy"/> clears the
    /// destination with a recursive delete, so a name carrying path separators, <c>..</c>,
    /// or a rooted path could escape <c>Mods/</c> and wipe an unrelated directory. Rejecting
    /// those here keeps every caller (CLI and Studio) safe by construction.
    /// </summary>
    public static string ResolveModDestination(string gameDir, string modName)
    {
        if (string.IsNullOrWhiteSpace(modName)
            || modName.Contains(Path.DirectorySeparatorChar)
            || modName.Contains(Path.AltDirectorySeparatorChar)
            || modName.Contains("..")
            || Path.IsPathRooted(modName))
            throw new ArgumentException(
                $"invalid mod name '{modName}': must be a plain folder name with no path separators.", nameof(modName));

        return Path.Combine(gameDir, "Mods", modName);
    }
}
