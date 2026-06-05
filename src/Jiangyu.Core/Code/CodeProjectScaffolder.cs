using System.Text;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Code;

/// <summary>
/// Scaffolds the per-mod C# project at <c>&lt;modRoot&gt;/code/</c>, where modders
/// author code mods that compile against <c>Jiangyu.Sdk</c> and the game's IL2CPP
/// proxy assemblies.
///
/// Layout (Jiangyu-managed vs modder-owned):
/// <code>
/// &lt;modRoot&gt;/
/// ├── &lt;Mod&gt;.slnx             seeded once, modder-owned
/// └── code/
///     ├── Directory.Build.props     Jiangyu-managed (re-sync overwrites)
///     ├── Directory.Build.targets   Jiangyu-managed (SDK + game references)
///     ├── README.md                 Jiangyu-managed
///     ├── .gitignore                Jiangyu-managed
///     ├── local.props               generated each sync, gitignored
///     └── &lt;Mod&gt;.csproj          seeded once, modder-owned
/// </code>
///
/// The solution sits at the mod root so an IDE opened there (not only on
/// <c>code/</c>) discovers and loads the C# project.
///
/// <see cref="Init"/> is idempotent: re-running overwrites only the managed build
/// files and rewrites <c>local.props</c> with freshly resolved paths. The modder's
/// <c>.csproj</c> and the solution are seeded only when absent, and are never
/// touched afterwards. Build settings and references live in the managed
/// props/targets so a mod's project file stays minimal.
/// </summary>
public sealed class CodeProjectScaffolder
{
    private static readonly IReadOnlyList<(string TemplateLogicalPath, string DestFileName)> OverwriteManagedTemplates = new[]
    {
        ("CodeProject/Directory.Build.props", "Directory.Build.props"),
        ("CodeProject/Directory.Build.targets", "Directory.Build.targets"),
        ("CodeProject/README.md", "README.md"),
        ("CodeProject/.gitignore", ".gitignore"),
    };

    private readonly ILogSink _log;

    public CodeProjectScaffolder(ILogSink log)
    {
        _log = log;
    }

    /// <summary>
    /// Scaffold or refresh <c>code/</c>. <paramref name="gameDir"/> and
    /// <paramref name="sdkDir"/> (resolved from global config) are written into
    /// <c>local.props</c> for IDE / bare <c>dotnet build</c> use; pass null to skip
    /// that file (e.g. when paths are unresolved and will be injected at compile).
    /// </summary>
    public ScaffoldResult Init(string projectRoot, string? gameDir, string? sdkDir)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must be a non-empty path.", nameof(projectRoot));

        var codeDir = Path.Combine(projectRoot, "code");
        Directory.CreateDirectory(codeDir);

        var result = new ScaffoldResult { RootDir = codeDir };

        foreach (var (templatePath, destFileName) in OverwriteManagedTemplates)
        {
            var content = ScaffoldFiles.LoadEmbeddedTemplate(templatePath);
            ScaffoldFiles.WriteFile(Path.Combine(codeDir, destFileName), content, overwrite: true, result);
        }

        // Seed a minimal project file, but only when no .csproj exists yet, so an
        // existing mod's project is never clobbered. No example source is written;
        // the modder authors their own JiangyuSystem subclasses (see README.md).
        if (!HasCsproj(codeDir))
        {
            var modName = SanitiseModName(Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            ScaffoldFiles.WriteIfMissing(Path.Combine(codeDir, $"{modName}.csproj"), ScaffoldFiles.LoadEmbeddedTemplate("CodeProject/Mod.csproj"), result);
        }

        WriteSolutionIfMissing(projectRoot, codeDir, result);

        if (gameDir is not null || sdkDir is not null)
            ScaffoldFiles.WriteFile(Path.Combine(codeDir, "local.props"), BuildLocalProps(gameDir, sdkDir), overwrite: true, result);

        _log.Info($"Scaffolded code project at {codeDir}");
        if (sdkDir is null)
            _log.Warning("Jiangyu SDK path unresolved; `dotnet build` needs code/local.props. `jiangyu compile` injects it.");

        return result;
    }

    private static bool HasCsproj(string codeDir)
        => Directory.EnumerateFiles(codeDir, "*.csproj", SearchOption.TopDirectoryOnly).Any();

    private static string SanitiseModName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Mod";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '.' ? c : '_');
        var name = sb.ToString().Trim('.', '_');
        if (name.Length == 0 || !char.IsLetter(name[0]) && name[0] != '_')
            name = "Mod_" + name;
        return name;
    }

    private static string BuildLocalProps(string? gameDir, string? sdkDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine();
        sb.AppendLine("  <!-- Written by `jiangyu code sync` from your global config. Gitignored. -->");
        sb.AppendLine("  <PropertyGroup>");
        if (gameDir is not null)
            sb.AppendLine($"    <GameDir>{gameDir}</GameDir>");
        if (sdkDir is not null)
            sb.AppendLine($"    <JiangyuSdkDir>{sdkDir}</JiangyuSdkDir>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static void WriteSolutionIfMissing(string projectRoot, string codeDir, ScaffoldResult result)
    {
        var hasSolution = Directory.EnumerateFiles(projectRoot, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(projectRoot, "*.slnx", SearchOption.TopDirectoryOnly).Any();
        if (hasSolution)
            return;

        var csprojFiles = Directory.EnumerateFiles(codeDir, "*.csproj", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (csprojFiles.Count == 0)
            return;

        var solutionName = SanitiseModName(Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        ScaffoldFiles.WriteFile(Path.Combine(projectRoot, $"{solutionName}.slnx"), BuildSolution(csprojFiles!), overwrite: false, result);
    }

    private static string BuildSolution(IReadOnlyList<string> codeCsprojFileNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");
        foreach (var file in codeCsprojFileNames)
            sb.AppendLine($"  <Project Path=\"code/{file}\" />");
        sb.AppendLine("</Solution>");
        return sb.ToString();
    }

}
