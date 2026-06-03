using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Unity;

/// <summary>
/// Scaffolds the per-mod Unity Editor project at <c>&lt;modRoot&gt;/unity/</c>.
/// The scaffolded project is where modders author prefabs for the
/// <c>assets/additions/prefabs/</c> bundle pipeline.
///
/// Layout (Jiangyu-managed vs modder-owned):
/// <code>
/// unity/
/// ├── Assets/
/// │   ├── Jiangyu/                  Jiangyu-managed (re-init overwrites)
/// │   │   ├── Editor/BuildBundles.cs
/// │   │   └── README.md
/// │   └── Prefabs/                  modder-authored (.gitkeep only on init)
/// ├── Packages/manifest.json        Jiangyu seeds, modder may extend
/// ├── ProjectSettings/              Unity-owned (auto-created on first open)
/// └── .gitignore                    Jiangyu-managed
/// </code>
///
/// <see cref="Init"/> is idempotent: re-running overwrites only files under
/// <c>Assets/Jiangyu/</c> plus <c>.gitignore</c>. Modder content under
/// <c>Assets/Prefabs/</c> (and elsewhere) is never touched. The Packages
/// manifest is seeded once and left alone on subsequent runs so a modder
/// can declare extra package dependencies.
/// </summary>
public sealed class UnityProjectScaffolder
{
    private static readonly IReadOnlyList<(string TemplateLogicalPath, string[] DestRelativeParts)> OverwriteManagedTemplates = new[]
    {
        ("UnityProject/Assets/Jiangyu/Editor/BuildBundles.cs", new[] { "Assets", "Jiangyu", "Editor", "BuildBundles.cs" }),
        ("UnityProject/Assets/Jiangyu/Editor/BuildModelBundles.cs", new[] { "Assets", "Jiangyu", "Editor", "BuildModelBundles.cs" }),
        ("UnityProject/Assets/Jiangyu/Editor/BuildMeshReplacementBundle.cs", new[] { "Assets", "Jiangyu", "Editor", "BuildMeshReplacementBundle.cs" }),
        ("UnityProject/Assets/Jiangyu/Editor/ImportedPrefabPostProcessor.cs", new[] { "Assets", "Jiangyu", "Editor", "ImportedPrefabPostProcessor.cs" }),
        ("UnityProject/Assets/Jiangyu/Editor/BakeHumanoid.cs", new[] { "Assets", "Jiangyu", "Editor", "BakeHumanoid.cs" }),
        ("UnityProject/Assets/Jiangyu/Editor/BakeWeapon.cs", new[] { "Assets", "Jiangyu", "Editor", "BakeWeapon.cs" }),
        ("UnityProject/Assets/Jiangyu/README.md", new[] { "Assets", "Jiangyu", "README.md" }),
        ("UnityProject/.gitignore", new[] { ".gitignore" }),
    };

    private readonly ILogSink _log;

    public UnityProjectScaffolder(ILogSink log)
    {
        _log = log;
    }

    public ScaffoldResult Init(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("Project root must be a non-empty path.", nameof(projectRoot));

        var unityDir = Path.Combine(projectRoot, "unity");
        Directory.CreateDirectory(unityDir);

        var result = new ScaffoldResult { RootDir = unityDir };

        // Jiangyu-managed directories created (or confirmed) each run.
        Directory.CreateDirectory(Path.Combine(unityDir, "Assets", "Jiangyu", "Editor"));
        Directory.CreateDirectory(Path.Combine(unityDir, "Assets", "Prefabs"));
        Directory.CreateDirectory(Path.Combine(unityDir, "Packages"));

        // Jiangyu-owned files: overwrite on every run.
        foreach (var (templatePath, destParts) in OverwriteManagedTemplates)
        {
            WriteFromTemplate(
                unityDir,
                templateLogicalPath: templatePath,
                destRelative: Path.Combine(destParts),
                result);
        }

        // Modder-extensible: seed only if missing.
        WriteFromTemplateIfMissing(
            unityDir,
            templateLogicalPath: "UnityProject/Packages/manifest.json",
            destRelative: Path.Combine("Packages", "manifest.json"),
            result);

        // .gitkeep so the Prefabs dir survives a fresh git checkout even
        // when the modder has not committed any prefabs yet.
        ScaffoldFiles.WriteIfMissing(
            Path.Combine(unityDir, "Assets", "Prefabs", ".gitkeep"),
            string.Empty,
            result);

        _log.Info($"Scaffolded Unity project at {unityDir}");
        _log.Info("  Open in Unity Editor 6000.0.72f1 to bootstrap ProjectSettings/ and Library/.");
        _log.Info("  Author prefabs under unity/Assets/Prefabs/.");

        return result;
    }

    /// <summary>
    /// Returns absolute paths of Jiangyu-managed files under <c>unity/</c>
    /// whose contents differ from their embedded templates in this Jiangyu
    /// version. Missing files count as drift. Empty when <c>unity/</c> is absent.
    /// </summary>
    public IReadOnlyList<string> FindDriftedManagedFiles(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return Array.Empty<string>();

        var unityDir = Path.Combine(projectRoot, "unity");
        if (!Directory.Exists(unityDir))
            return Array.Empty<string>();

        var drifted = new List<string>();
        foreach (var (templatePath, destParts) in OverwriteManagedTemplates)
        {
            var destPath = Path.Combine(unityDir, Path.Combine(destParts));
            if (!File.Exists(destPath))
            {
                drifted.Add(destPath);
                continue;
            }

            var expected = ScaffoldFiles.LoadEmbeddedTemplate(templatePath);
            var actual = File.ReadAllText(destPath);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                drifted.Add(destPath);
        }
        return drifted;
    }

    private void WriteFromTemplate(
        string unityDir,
        string templateLogicalPath,
        string destRelative,
        ScaffoldResult result)
    {
        var content = ScaffoldFiles.LoadEmbeddedTemplate(templateLogicalPath);
        var destPath = Path.Combine(unityDir, destRelative);
        ScaffoldFiles.WriteFile(destPath, content, overwrite: true, result);
    }

    private void WriteFromTemplateIfMissing(
        string unityDir,
        string templateLogicalPath,
        string destRelative,
        ScaffoldResult result)
    {
        var destPath = Path.Combine(unityDir, destRelative);
        var content = ScaffoldFiles.LoadEmbeddedTemplate(templateLogicalPath);
        ScaffoldFiles.WriteIfMissing(destPath, content, result);
    }
}

public sealed class ScaffoldResult
{
    public string RootDir { get; set; } = string.Empty;
    public List<string> CreatedFiles { get; } = new();
    public List<string> OverwrittenFiles { get; } = new();
    public List<string> PreservedFiles { get; } = new();
}
