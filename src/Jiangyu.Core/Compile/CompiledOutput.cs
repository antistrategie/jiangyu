using Jiangyu.Core.IO;
using Jiangyu.Shared.Bundles;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Housekeeping for a mod's <c>compiled/</c> output tree. Everything under it is build
/// output, regenerated each compile, so each compile either resets it up front or, with
/// nothing to build, deletes it, and nothing a previous compile left behind survives.
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
        ResilientFs.DeleteDirectory(outputDir);
        // Recreate through the retrying helper too: on Windows the recursive delete above can
        // return before NTFS finalises removal, and a bare CreateDirectory on the same path then
        // faults with the directory still in a pending-deletion state.
        ResilientFs.CreateDirectory(outputDir);
        ResilientFs.CreateDirectory(Path.Combine(outputDir, CompiledLayout.BundlesDirName));
        return outputDir;
    }

    /// <summary>Delete the project's <c>compiled/</c> tree, for a compile with no inputs.</summary>
    public static void Clear(string projectDir) =>
        ResilientFs.DeleteDirectory(Path.Combine(projectDir, "compiled"));
}
