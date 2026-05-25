using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Config;

public static class ProjectScaffold
{
    /// <summary>
    /// Scaffolds a new mod project in <paramref name="projectDir"/>.
    /// Creates <c>jiangyu.json</c>, a default <c>.gitignore</c>, and the
    /// per-mod <c>unity/</c> Editor project.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>jiangyu.json</c> already exists in the directory.
    /// </exception>
    public static async Task<string> InitAsync(string projectDir, ILogSink? log = null)
    {
        var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
        if (File.Exists(manifestPath))
            throw new InvalidOperationException($"{ModManifest.FileName} already exists in {projectDir}");

        var dirName = Path.GetFileName(projectDir) ?? "MyMod";
        var manifest = ModManifest.CreateDefault(dirName);

        await File.WriteAllTextAsync(manifestPath, manifest.ToJson());

        var gitignorePath = Path.Combine(projectDir, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            await File.WriteAllTextAsync(gitignorePath, ".jiangyu/\ncompiled/\n");
        }

        new UnityProjectScaffolder(log ?? NullLogSink.Instance).Init(projectDir);

        return dirName;
    }
}
