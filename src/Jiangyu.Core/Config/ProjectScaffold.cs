using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Config;

public static class ProjectScaffold
{
    /// <summary>
    /// Scaffolds a new mod project in <paramref name="projectDir"/>.
    /// Creates <c>jiangyu.json</c>, a default <c>.gitignore</c>, the per-mod
    /// <c>unity/</c> Editor project, and the per-mod <c>code/</c> C# project.
    /// Both stay dormant until used: an empty <c>code/</c> ships nothing, the same
    /// way an empty <c>unity/</c> builds no bundles.
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

        var sink = log ?? NullLogSink.Instance;
        new UnityProjectScaffolder(sink).Init(projectDir);

        // Scaffold code/ too, resolving game + SDK paths best-effort so the IDE
        // has them in local.props. Unresolved paths are fine: `jiangyu compile`
        // injects them, and `jiangyu code sync` rewrites local.props later.
        var config = GlobalConfig.Load();
        var (gameDir, _) = GlobalConfig.ResolveGamePath(config);
        var (sdkDir, _) = GlobalConfig.ResolveSdkDir(config);
        new CodeProjectScaffolder(sink).Init(projectDir, gameDir, sdkDir);

        return dirName;
    }
}
