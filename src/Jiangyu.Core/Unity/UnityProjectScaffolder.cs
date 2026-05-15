using System.Reflection;
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

        var result = new ScaffoldResult { UnityDir = unityDir };

        // Jiangyu-managed directories created (or confirmed) each run.
        Directory.CreateDirectory(Path.Combine(unityDir, "Assets", "Jiangyu", "Editor"));
        Directory.CreateDirectory(Path.Combine(unityDir, "Assets", "Prefabs"));
        Directory.CreateDirectory(Path.Combine(unityDir, "Packages"));

        // Jiangyu-owned files: overwrite on every run.
        WriteFromTemplate(
            unityDir,
            templateLogicalPath: "UnityProject/Assets/Jiangyu/Editor/BuildBundles.cs",
            destRelative: Path.Combine("Assets", "Jiangyu", "Editor", "BuildBundles.cs"),
            result);

        WriteFromTemplate(
            unityDir,
            templateLogicalPath: "UnityProject/Assets/Jiangyu/Editor/ImportedPrefabPostProcessor.cs",
            destRelative: Path.Combine("Assets", "Jiangyu", "Editor", "ImportedPrefabPostProcessor.cs"),
            result);

        WriteFromTemplate(
            unityDir,
            templateLogicalPath: "UnityProject/Assets/Jiangyu/Editor/BakeHumanoid.cs",
            destRelative: Path.Combine("Assets", "Jiangyu", "Editor", "BakeHumanoid.cs"),
            result);

        WriteFromTemplate(
            unityDir,
            templateLogicalPath: "UnityProject/Assets/Jiangyu/README.md",
            destRelative: Path.Combine("Assets", "Jiangyu", "README.md"),
            result);

        WriteFromTemplate(
            unityDir,
            templateLogicalPath: "UnityProject/.gitignore",
            destRelative: ".gitignore",
            result);

        // Modder-extensible: seed only if missing.
        WriteFromTemplateIfMissing(
            unityDir,
            templateLogicalPath: "UnityProject/Packages/manifest.json",
            destRelative: Path.Combine("Packages", "manifest.json"),
            result);

        // .gitkeep so the Prefabs dir survives a fresh git checkout even
        // when the modder has not committed any prefabs yet.
        WriteIfMissing(
            Path.Combine(unityDir, "Assets", "Prefabs", ".gitkeep"),
            string.Empty,
            result);

        _log.Info($"Scaffolded Unity project at {unityDir}");
        _log.Info("  Open in Unity Editor 6000.0.72f1 to bootstrap ProjectSettings/ and Library/.");
        _log.Info("  Author prefabs under unity/Assets/Prefabs/.");

        return result;
    }

    private void WriteFromTemplate(
        string unityDir,
        string templateLogicalPath,
        string destRelative,
        ScaffoldResult result)
    {
        var content = LoadEmbeddedTemplate(templateLogicalPath);
        var destPath = Path.Combine(unityDir, destRelative);
        WriteFile(destPath, content, overwrite: true, result);
    }

    private void WriteFromTemplateIfMissing(
        string unityDir,
        string templateLogicalPath,
        string destRelative,
        ScaffoldResult result)
    {
        var destPath = Path.Combine(unityDir, destRelative);
        if (File.Exists(destPath))
        {
            result.PreservedFiles.Add(destPath);
            return;
        }

        var content = LoadEmbeddedTemplate(templateLogicalPath);
        WriteFile(destPath, content, overwrite: false, result);
    }

    private void WriteIfMissing(string destPath, string content, ScaffoldResult result)
    {
        if (File.Exists(destPath))
        {
            result.PreservedFiles.Add(destPath);
            return;
        }
        WriteFile(destPath, content, overwrite: false, result);
    }

    private static void WriteFile(string destPath, string content, bool overwrite, ScaffoldResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var existed = File.Exists(destPath);
        File.WriteAllText(destPath, content);
        if (existed && overwrite)
            result.OverwrittenFiles.Add(destPath);
        else
            result.CreatedFiles.Add(destPath);
    }

    private static string LoadEmbeddedTemplate(string logicalPath)
    {
        var assembly = typeof(UnityProjectScaffolder).Assembly;
        using var stream = assembly.GetManifestResourceStream(logicalPath)
            ?? throw new InvalidOperationException(
                $"Embedded template not found: {logicalPath}. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class ScaffoldResult
{
    public string UnityDir { get; set; } = string.Empty;
    public List<string> CreatedFiles { get; } = new();
    public List<string> OverwrittenFiles { get; } = new();
    public List<string> PreservedFiles { get; } = new();
}
