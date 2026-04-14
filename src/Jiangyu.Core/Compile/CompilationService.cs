using System.Diagnostics;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Config;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Parsed reference to an asset file, e.g. "models/file.gltf#meshname".
/// </summary>
public readonly record struct ParsedAssetReference(string FilePath, string MeshName, bool HasExplicitMeshName);

/// <summary>
/// Input to <see cref="CompilationService.CompileAsync"/>.
/// </summary>
public sealed class CompilationInput
{
    public required ModManifest Manifest { get; init; }
    public required GlobalConfig Config { get; init; }
    public required string ProjectDirectory { get; init; }

}

/// <summary>
/// Result from <see cref="CompilationService.CompileAsync"/>.
/// </summary>
public sealed class CompilationResult
{
    public required bool Success { get; init; }
    public string? BundlePath { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Orchestrates compilation of a mod project into an asset bundle.
/// Decides between the GLB mesh pipeline and the FBX/Unity pipeline,
/// invokes the appropriate compiler, and copies outputs.
/// </summary>
public sealed class CompilationService(ILogSink log, IProgressSink progress)
{
    private readonly ILogSink _log = log;
    private readonly IProgressSink _progress = progress;

    public async Task<CompilationResult> CompileAsync(CompilationInput input)
    {
        var manifest = input.Manifest;
        var config = input.Config;
        var projectDir = input.ProjectDirectory;

        // Resolve game data first — needed to detect the Unity version for editor discovery
        var (gameDataPath, gameDataError) = GlobalConfig.ResolveGameDataPath(config);
        if (gameDataPath is null)
            return Fail(gameDataError ?? "Could not resolve game data path.");

        // Detect game Unity version and use it to find the matching editor
        var gameVersion = UnityVersionValidationService.DetectGameVersion(gameDataPath);
        var (unityEditorPath, editorError) = GlobalConfig.ResolveUnityEditorPath(config, gameVersion?.ToString());
        if (unityEditorPath is null)
            return Fail(editorError!);

        var versionValidation = await new UnityVersionValidationService(_log).ValidateAsync(gameDataPath, unityEditorPath);
        if (!versionValidation.Success)
            return Fail(versionValidation.ErrorMessage ?? "Unity version validation failed.");

        // Parse mesh mappings and collect model files
        var meshMappings = new Dictionary<string, string>();
        var modelFiles = new HashSet<string>();
        var mappedGlbEntries = new List<GlbMeshBundleCompiler.MeshSourceEntry>();

        if (manifest.Meshes is { Count: > 0 })
        {
            foreach (var (gameMeshName, meshEntry) in manifest.Meshes)
            {
                var sourceRef = meshEntry.Source;
                var assetRef = ParseAssetReference(sourceRef, projectDir);
                var filePath = assetRef.FilePath;
                var meshName = assetRef.MeshName;

                if (!File.Exists(filePath))
                    return Fail($"Model file not found: {sourceRef}");

                modelFiles.Add(filePath);
                meshMappings[gameMeshName] = meshName;

                if (IsGlbPath(filePath))
                {
                    mappedGlbEntries.Add(new GlbMeshBundleCompiler.MeshSourceEntry
                    {
                        SourceFilePath = filePath,
                        BundleMeshName = meshName,
                        HasExplicitMeshName = assetRef.HasExplicitMeshName,
                    });
                }
            }
        }

        // Also collect any model files not referenced by mappings (from models/ directory)
        var modelsDir = Path.Combine(projectDir, "models");
        foreach (var file in CollectAssetFiles(modelsDir, "*.gltf", "*.glb", "*.fbx", "*.obj"))
        {
            modelFiles.Add(file);
        }

        if (modelFiles.Count == 0)
        {
            _log.Info("No model files found. Nothing to compile.");
            return new CompilationResult { Success = true };
        }

        _log.Info($"Compiling {manifest.Name}...");
        _log.Info($"  {modelFiles.Count} model(s), {meshMappings.Count} mapping(s)");

        var bundleName = manifest.Name.ToLowerInvariant().Replace(" ", "_");
        var outputDir = Path.Combine(projectDir, "compiled");
        Directory.CreateDirectory(outputDir);
        var unityProjectDir = Path.Combine(projectDir, ".jiangyu", "unity_project");
        var builtBundle = Path.Combine(unityProjectDir, "AssetBundles", bundleName);

        var useRawGlbPipeline = meshMappings.Count > 0 &&
                                mappedGlbEntries.Count == meshMappings.Count &&
                                mappedGlbEntries.Count > 0;

        ModManifest compiledManifest = manifest;
        if (useRawGlbPipeline)
        {
            _log.Info("  Using experimental GLB mesh pipeline...");
            var bundleOutputPath = Path.Combine(unityProjectDir, "AssetBundles", bundleName);
            Directory.CreateDirectory(Path.GetDirectoryName(bundleOutputPath)!);
            var targetMeshNamesByBundleMesh = meshMappings
                .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

            try
            {
                var buildResult = await GlbMeshBundleCompiler.BuildAsync(
                    unityEditorPath,
                    unityProjectDir,
                    bundleName,
                    bundleOutputPath,
                    mappedGlbEntries,
                    gameDataPath,
                    targetMeshNamesByBundleMesh);
                compiledManifest = ModManifest.FromJson(manifest.ToJson());
                foreach (var (gameMeshName, meshEntry) in compiledManifest.Meshes ?? [])
                {
                    var parsed = ParseAssetReference(meshEntry.Source, projectDir);
                    var bundleMeshName = parsed.MeshName;
                    if (!buildResult.MeshBoneNames.TryGetValue(bundleMeshName, out var boneNames))
                        continue;

                    meshEntry.Compiled = new CompiledMeshMetadata
                    {
                        BoneNames = boneNames,
                        TexturePrefix = buildResult.MeshTexturePrefixes.GetValueOrDefault(bundleMeshName),
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail($"Experimental GLB mesh pipeline failed: {ex.Message}");
            }
        }
        else
        {
            await SetupUnityProject(unityProjectDir, modelFiles);

            var success = await InvokeUnityBuild(unityEditorPath, unityProjectDir, bundleName);
            if (!success)
                return Fail("Unity build failed. Check logs for details.");
        }

        // Collect output
        if (!File.Exists(builtBundle))
            return Fail("Unity build did not produce expected bundle.");

        var destBundle = Path.Combine(outputDir, $"{bundleName}.bundle");
        File.Copy(builtBundle, destBundle, overwrite: true);

        var sourceDiagnostics = builtBundle + ".source-diagnostics.json";
        if (File.Exists(sourceDiagnostics))
            File.Copy(sourceDiagnostics, destBundle + ".source-diagnostics.json", overwrite: true);

        var unityDiagnostics = builtBundle + ".unity-diagnostics.json";
        if (File.Exists(unityDiagnostics))
            File.Copy(unityDiagnostics, destBundle + ".unity-diagnostics.json", overwrite: true);

        // Copy manifest alongside the bundle
        await File.WriteAllTextAsync(Path.Combine(outputDir, ModManifest.FileName), compiledManifest.ToJson());

        _log.Info($"  -> {destBundle}");
        _log.Info("Done.");

        return new CompilationResult { Success = true, BundlePath = destBundle };
    }

    /// <summary>
    /// Parses "models/file.fbx#meshname" into (absolute file path, mesh name).
    /// If no #fragment, the mesh name defaults to the filename without extension.
    /// </summary>
    public static ParsedAssetReference ParseAssetReference(string reference, string projectDir)
    {
        var hashIndex = reference.IndexOf('#');
        if (hashIndex >= 0)
        {
            var filePart = reference[..hashIndex];
            var meshName = reference[(hashIndex + 1)..];
            return new ParsedAssetReference(Path.GetFullPath(filePart, projectDir), meshName, HasExplicitMeshName: true);
        }

        var fullPath = Path.GetFullPath(reference, projectDir);
        return new ParsedAssetReference(fullPath, Path.GetFileNameWithoutExtension(fullPath), HasExplicitMeshName: false);
    }

    public static bool IsGlbPath(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".glb", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetExtension(filePath), ".gltf", StringComparison.OrdinalIgnoreCase);

    public static string[] CollectAssetFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
            return [];

        return [.. patterns
            .SelectMany(p => Directory.GetFiles(directory, p, SearchOption.AllDirectories))
            .OrderBy(f => f)];
    }

    private async Task SetupUnityProject(string unityProjectDir, IEnumerable<string> modelFiles)
    {
        var assetsDir = Path.Combine(unityProjectDir, "Assets");
        var editorDir = Path.Combine(assetsDir, "Editor");
        var modelsImportDir = Path.Combine(assetsDir, "Models");

        Directory.CreateDirectory(editorDir);
        Directory.CreateDirectory(modelsImportDir);

        var buildScript = Glb.GlbMeshBundleCompiler.LoadEmbeddedResource("Jiangyu.Core.Unity.BundleBuilder.template");
        await File.WriteAllTextAsync(Path.Combine(editorDir, "BundleBuilder.cs"), buildScript);

        // Copy model files into the Unity project
        foreach (var modelFile in modelFiles)
        {
            var destFile = Path.Combine(modelsImportDir, Path.GetFileName(modelFile));
            File.Copy(modelFile, destFile, overwrite: true);
        }

        _log.Info($"  Unity project prepared at {unityProjectDir}");
    }

    private async Task<bool> InvokeUnityBuild(string unityEditor, string unityProjectDir, string bundleName)
    {
        var logFile = Path.Combine(unityProjectDir, "build.log");
        var bundleOutputDir = Path.Combine(unityProjectDir, "AssetBundles");
        Directory.CreateDirectory(bundleOutputDir);

        var arguments = string.Join(" ",
            "-batchmode",
            "-nographics",
            "-quit",
            $"-projectPath \"{unityProjectDir}\"",
            "-executeMethod BundleBuilder.Build",
            $"-logFile \"{logFile}\"",
            $"-bundleName {bundleName}",
            $"-bundleOutputPath \"{bundleOutputDir}\"");

        _log.Info("  Invoking Unity...");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = unityEditor,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _log.Error($"Unity exited with code {process.ExitCode}");
            _log.Error($"  Log: {logFile}");

            if (File.Exists(logFile))
            {
                var lines = await File.ReadAllLinesAsync(logFile);
                var tail = lines.Skip(Math.Max(0, lines.Length - 20));
                _log.Error("");
                foreach (var line in tail)
                    _log.Error($"  {line}");
            }

            return false;
        }

        return true;
    }

    private CompilationResult Fail(string message)
    {
        _log.Error(message);
        return new CompilationResult { Success = false, ErrorMessage = message };
    }
}
