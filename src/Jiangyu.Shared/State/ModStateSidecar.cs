namespace Jiangyu.Shared.State;

/// <summary>
/// Naming for the per-save mod state sidecar: <c>&lt;savePath&gt;.jiangyu.&lt;modId&gt;.json</c>,
/// written beside the game's save file so state never leaks across slots.
/// </summary>
public static class ModStateSidecar
{
    private const string Infix = ".jiangyu.";
    private const string Extension = ".json";
    private const string OrphanSuffix = ".orphan";
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The sidecar <paramref name="modId"/> keeps beside <paramref name="savePath"/>.</summary>
    public static string PathFor(string savePath, string modId) => $"{savePath}{Infix}{modId}{Extension}";

    /// <summary>Every sidecar in a save folder, whoever owns it.</summary>
    public static string SearchPattern => $"*{Infix}*{Extension}";

    /// <summary>
    /// Where a sidecar goes once its state has nowhere to attach: kept for recovery, and outside
    /// <see cref="SearchPattern"/> so later sweeps pass over it.
    /// </summary>
    public static string OrphanPathFor(string sidecarPath) => $"{sidecarPath}{OrphanSuffix}";

    /// <summary>
    /// The save a sidecar belongs to, or null when <paramref name="sidecarPath"/> is not one of
    /// ours. The game folds every <c>.</c> out of a save file name, so the first infix in the file
    /// name always opens the suffix: a mod id may carry the infix, a save name cannot, and neither
    /// can a folder above them reach into the split.
    /// </summary>
    public static string? SavePathFor(string? sidecarPath)
    {
        var infix = InfixIndex(sidecarPath);
        return infix < 0 ? null : sidecarPath!.Substring(0, infix);
    }

    /// <summary>
    /// The mod that owns a sidecar, or null when <paramref name="sidecarPath"/> is not one of ours.
    /// </summary>
    public static string? ModIdFor(string? sidecarPath)
    {
        var infix = InfixIndex(sidecarPath);
        if (infix < 0)
            return null;
        var start = infix + Infix.Length;
        return sidecarPath!.Substring(start, sidecarPath.Length - Extension.Length - start);
    }

    // Where the sidecar suffix starts, or -1 when this path carries no whole one.
    private static int InfixIndex(string? sidecarPath)
    {
        if (string.IsNullOrEmpty(sidecarPath) || !sidecarPath!.EndsWith(Extension, StringComparison.Ordinal))
            return -1;
        var nameStart = sidecarPath.LastIndexOfAny(Separators) + 1;
        var infix = sidecarPath.IndexOf(Infix, nameStart, StringComparison.Ordinal);
        // A save name cannot be empty, and neither can a mod id, which cannot carry the extension.
        return infix > nameStart && sidecarPath.Length - Extension.Length - (infix + Infix.Length) > 0
            ? infix
            : -1;
    }
}
