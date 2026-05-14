using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Staging logic for addition prefab bundles. Walks one or more source
/// directories, copies <c>*.bundle</c> files into the compile output, and
/// records their logical names on the compiled manifest's
/// <see cref="ModManifest.AdditionPrefabs"/> list.
///
/// Two source dirs feed this today: pre-built bundles dropped by the modder
/// in <c>assets/additions/prefabs/</c> (the escape hatch), and freshly-built
/// bundles produced by Unity batchmode against the modder's <c>unity/</c>
/// project (output to <c>.jiangyu/unity-build/</c>). Both flow through the
/// same staging step. Later-listed sources override earlier ones on name
/// collision so a Unity-built bundle takes precedence over a stale
/// hand-shipped one of the same name.
///
/// Convention: each bundle's filename stem equals the Unity Object.name of
/// the GameObject inside, which is what KDL <c>asset=</c> references resolve
/// against at runtime via <c>ModAssetResolver</c>'s GameObject dispatch.
/// </summary>
internal static class AdditionPrefabStaging
{
    public static void Stage(
        IReadOnlyList<string> sourceDirs,
        string outputDir,
        ModManifest compiledManifest,
        ILogSink log)
    {
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sourceDir in sourceDirs)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                continue;

            foreach (var source in Directory.EnumerateFiles(sourceDir, "*.bundle", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(source);
                if (string.IsNullOrWhiteSpace(stem))
                {
                    log.Warning($"  Addition prefab bundle '{source}' has no usable filename stem; skipping.");
                    continue;
                }

                staged[stem] = source;
            }
        }

        if (staged.Count == 0)
            return;

        Directory.CreateDirectory(outputDir);
        var names = new List<string>(staged.Count);
        foreach (var (stem, source) in staged.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var dest = Path.Combine(outputDir, stem + ".bundle");
            File.Copy(source, dest, overwrite: true);
            names.Add(stem);
            log.Info($"  Staged addition prefab bundle: {stem}.bundle");
        }

        compiledManifest.AdditionPrefabs = names;
    }

    /// <summary>
    /// Returns true when the compile pipeline should invoke Unity batchmode
    /// to build addition prefab bundles from the modder's <c>unity/</c>
    /// project. False when the project isn't scaffolded or has no prefabs
    /// to build.
    /// </summary>
    public static bool ShouldInvokeUnityForPrefabs(string projectDir)
    {
        var prefabsDir = Path.Combine(projectDir, "unity", "Assets", "Prefabs");
        if (!Directory.Exists(prefabsDir))
            return false;
        return Directory.EnumerateFiles(prefabsDir, "*.prefab", SearchOption.AllDirectories).Any();
    }
}
