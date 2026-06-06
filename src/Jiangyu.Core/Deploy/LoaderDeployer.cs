namespace Jiangyu.Core.Deploy;

/// <summary>
/// Deploys the chosen loader build into the game's <c>Mods/</c> folder. The loader
/// ships as a single merged DLL (see Jiangyu.Loader/ILRepack.targets), so a deploy is
/// one file copy that overwrites <c>Mods/Jiangyu.Loader.dll</c>. The two variants
/// (user, dev) share that name and MelonInfo, so deploying one replaces the other
/// rather than running both alongside each other.
/// </summary>
public static class LoaderDeployer
{
    public const string LoaderDllName = "Jiangyu.Loader.dll";

    /// <summary>The <c>Mods/Jiangyu.Loader.dll</c> destination for a game install.</summary>
    public static string ResolveDestination(string gameDir) =>
        Path.Combine(gameDir, "Mods", LoaderDllName);

    /// <summary>
    /// Copy <paramref name="loaderDll"/> over <c>Mods/Jiangyu.Loader.dll</c>, creating
    /// <c>Mods/</c> if absent. Returns the destination path.
    /// </summary>
    public static string Deploy(string loaderDll, string gameDir)
    {
        if (!File.Exists(loaderDll))
            throw new FileNotFoundException($"loader DLL not found: {loaderDll}");

        var modsDir = Path.Combine(gameDir, "Mods");
        Directory.CreateDirectory(modsDir);

        var dest = Path.Combine(modsDir, LoaderDllName);
        File.Copy(loaderDll, dest, overwrite: true);
        return dest;
    }
}
