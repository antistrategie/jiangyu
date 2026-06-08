using Jiangyu.Shared.Bundles;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Housekeeping for a mod's <c>compiled/</c> output tree. Everything under it is build
/// output, regenerated each compile, so a clean slate up front is the single step that
/// clears whatever a previous compile left behind.
/// </summary>
internal static class CompiledOutput
{
    /// <summary>
    /// Wipe and recreate the project's <c>compiled/</c> tree, leaving an empty
    /// <c>bundles/</c> subfolder ready to fill, and return the <c>compiled/</c> path.
    /// </summary>
    public static string Reset(string projectDir)
    {
        var outputDir = Path.Combine(projectDir, "compiled");
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(outputDir, CompiledLayout.BundlesDirName));
        return outputDir;
    }
}
