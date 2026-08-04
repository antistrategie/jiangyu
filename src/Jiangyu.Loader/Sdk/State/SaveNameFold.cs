using System.IO;
using Il2CppMenace.Strategy;

namespace Jiangyu.Loader.Sdk.State;

/// <summary>
/// The game's own save name fold, in one place: both the sidecar written on save and the sweep that
/// reattaches a stranded one have to land on the same file for a given name.
/// </summary>
internal static class SaveNameFold
{
    // JIANGYU-CONTRACT: a new named save is written to
    // StringExtensions.ToSnakeCaseFileName(_saveGameName) + ".save", which lowercases Latin
    // letters, expands the German umlauts and eszett, and folds every other character (spaces,
    // punctuation, Korean, CJK, Cyrillic) down to a single underscore. When only underscores
    // survive that fold the game writes manual_<timestamp>.save instead, whose name the save name
    // cannot yield. Valid for the current MENACE save system; verified by disassembling
    // Menace.Strategy.SaveSystem.Save and Menace.Tools.StringExtensions against saves on disk, see
    // docs/research/investigations/2026-08-04-save-file-name-derivation.md.
    /// <summary>
    /// The path the game writes <paramref name="saveGameName"/> to, or null when the name folds
    /// away to nothing and the game falls back to a name of its own.
    /// </summary>
    public static string PathForName(string saveGameName)
    {
        try
        {
            var fileName = Il2CppMenace.Tools.StringExtensions.ToSnakeCaseFileName(saveGameName);
            if (string.IsNullOrWhiteSpace(fileName?.Replace('_', ' ')))
                return null;
            return SaveSystem.GetSaveFilePath(fileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The path a save written under a raw, unfolded name belongs at, or null when its name folds
    /// away to nothing.
    /// </summary>
    public static string PathForSavePath(string savePath)
    {
        var name = Path.GetFileNameWithoutExtension(savePath);
        return string.IsNullOrEmpty(name) ? null : PathForName(name);
    }
}
