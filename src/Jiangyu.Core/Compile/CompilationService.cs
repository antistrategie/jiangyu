using System.Diagnostics;
using System.Text.Json;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using Jiangyu.Core.Unity;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Parsed reference to an asset file, e.g. "assets/replacements/file.gltf#meshname".
/// </summary>
public readonly record struct ParsedAssetReference(string FilePath, string MeshName, bool HasExplicitMeshName);
internal readonly record struct ReplacementModelTarget(string Alias, string Name, long PathId, string Collection, string? CanonicalPath);

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
    private const string AssetIndexFileName = "asset-index.json";
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

        var replacementRoot = Path.Combine(projectDir, "assets", "replacements");
        var additionRoot = Path.Combine(projectDir, "assets", "additions");
        var replacementEntries = DiscoverReplacementMeshEntries(projectDir, replacementRoot, config.GetCachePath(), gameDataPath);
        var replacementModelFiles = replacementEntries
            .Select(entry => entry.SourceFilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additionModelFiles = CollectAssetFiles(additionRoot, "*.gltf", "*.glb", "*.fbx", "*.obj")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelFiles = replacementModelFiles
            .Concat(additionModelFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (modelFiles.Count == 0)
        {
            _log.Info("No model files found. Nothing to compile.");
            return new CompilationResult { Success = true };
        }

        _log.Info($"Compiling {manifest.Name}...");
        _log.Info($"  {modelFiles.Count} model file(s), {replacementEntries.Count} replacement mesh target(s)");

        var bundleName = manifest.Name.ToLowerInvariant().Replace(" ", "_");
        var outputDir = Path.Combine(projectDir, "compiled");
        Directory.CreateDirectory(outputDir);
        var unityProjectDir = Path.Combine(projectDir, ".jiangyu", "unity_project");
        var builtBundle = Path.Combine(unityProjectDir, "AssetBundles", bundleName);

        var useRawGlbPipeline = replacementEntries.Count > 0;

        ModManifest compiledManifest = manifest;
        if (useRawGlbPipeline)
        {
            _log.Info("  Using experimental GLB mesh pipeline...");
            var bundleOutputPath = Path.Combine(unityProjectDir, "AssetBundles", bundleName);
            Directory.CreateDirectory(Path.GetDirectoryName(bundleOutputPath)!);
            var targetMeshNamesByBundleMesh = replacementEntries
                .ToDictionary(entry => entry.BundleMeshName, entry => entry.BundleMeshName, StringComparer.Ordinal);

            try
            {
                var buildResult = await GlbMeshBundleCompiler.BuildAsync(
                    unityEditorPath,
                    unityProjectDir,
                    bundleName,
                    bundleOutputPath,
                    replacementEntries,
                    gameDataPath,
                    targetMeshNamesByBundleMesh);
                compiledManifest = ModManifest.FromJson(manifest.ToJson());
                compiledManifest.Meshes = new Dictionary<string, MeshManifestEntry>(StringComparer.Ordinal);
                foreach (var entry in replacementEntries)
                {
                    var bundleMeshName = entry.BundleMeshName;
                    if (!buildResult.MeshBoneNames.TryGetValue(bundleMeshName, out var boneNames))
                        continue;

                    compiledManifest.Meshes[bundleMeshName] = new MeshManifestEntry
                    {
                        Source = entry.SourceReference ?? bundleMeshName,
                        Compiled = new CompiledMeshMetadata
                        {
                            BoneNames = boneNames,
                            Materials = buildResult.MeshMaterialBindings.GetValueOrDefault(bundleMeshName),
                        }
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

    public static string BuildModelReplacementAlias(string targetName, long pathId)
        => $"{targetName}--{pathId}";

    public static string BuildModelReplacementRelativePath(string targetName, long pathId, string fileName = "model.gltf")
        => Path.Combine("assets", "replacements", "models", BuildModelReplacementAlias(targetName, pathId), fileName)
            .Replace(Path.DirectorySeparatorChar, '/');

    public static bool TryParseModelReplacementAlias(string alias, out string targetName, out long pathId)
    {
        targetName = string.Empty;
        pathId = 0;

        if (string.IsNullOrWhiteSpace(alias))
            return false;

        var separatorIndex = alias.LastIndexOf("--", StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex + 2 >= alias.Length)
            return false;

        targetName = alias[..separatorIndex];
        return long.TryParse(alias[(separatorIndex + 2)..], out pathId);
    }

    private List<GlbMeshBundleCompiler.MeshSourceEntry> DiscoverReplacementMeshEntries(string projectDir, string replacementsRoot, string cachePath, string gameDataPath)
    {
        if (!Directory.Exists(replacementsRoot))
            return [];

        var modelsRoot = Path.Combine(replacementsRoot, "models");
        if (!Directory.Exists(modelsRoot))
            return [];

        var unsupportedFiles = CollectAssetFiles(modelsRoot, "*.fbx", "*.obj");
        if (unsupportedFiles.Length > 0)
        {
            var relativeFiles = unsupportedFiles
                .Select(file => Path.GetRelativePath(projectDir, file))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(
                "Convention-first replacements currently require .gltf or .glb files under assets/replacements/. " +
                $"Unsupported files: {string.Join(", ", relativeFiles)}");
        }

        AssetIndex index = LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var entries = new List<GlbMeshBundleCompiler.MeshSourceEntry>();
        foreach (var targetDirectory in Directory.GetDirectories(modelsRoot))
        {
            var targetAlias = Path.GetFileName(targetDirectory);
            if (!TryParseModelReplacementAlias(targetAlias, out var targetName, out var targetPathId))
            {
                throw new InvalidOperationException(
                    $"Replacement target directory '{targetDirectory}' must be named '<target-name>--<pathId>'.");
            }

            var target = ResolveReplacementModelTarget(index, targetAlias, targetName, targetPathId);
            var modelFiles = CollectAssetFiles(targetDirectory, "*.gltf", "*.glb");
            if (modelFiles.Length == 0)
                throw new InvalidOperationException($"Replacement target '{targetAlias}' does not contain a .gltf or .glb model.");
            if (modelFiles.Length > 1)
            {
                var relativeFiles = modelFiles
                    .Select(file => Path.GetRelativePath(projectDir, file))
                    .OrderBy(path => path, StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"Replacement target '{targetAlias}' must contain exactly one .gltf or .glb model. Found: {string.Join(", ", relativeFiles)}");
            }

            var file = modelFiles[0];
            var providedMeshNames = DiscoverReplacementMeshNames(file);
            var expectedMeshNames = AssetInspectionService.GetSkinnedMeshNamesForIndexedObject(gameDataPath, target.Collection, target.PathId);
            if (expectedMeshNames.Count == 0)
                throw new InvalidOperationException(
                    $"Replacement target '{targetAlias}' [{target.CanonicalPath ?? $"{target.Collection}:{target.PathId}"}] has no skinned mesh names to replace.");

            ValidateUniqueRuntimeMeshNames(index, target, expectedMeshNames);

            var unexpectedMeshNames = providedMeshNames
                .Where(name => !expectedMeshNames.Contains(name, StringComparer.Ordinal))
                .ToArray();
            if (unexpectedMeshNames.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Replacement model '{Path.GetRelativePath(projectDir, file)}' contains mesh names that do not belong to target '{targetAlias}': " +
                    $"{string.Join(", ", unexpectedMeshNames.OrderBy(name => name, StringComparer.Ordinal))}. " +
                    $"Expected: {string.Join(", ", expectedMeshNames)}");
            }

            var missingMeshNames = expectedMeshNames
                .Where(name => !providedMeshNames.Contains(name, StringComparer.Ordinal))
                .ToArray();
            if (missingMeshNames.Length > 0)
            {
                _log.Warning(
                    $"Replacement target '{targetAlias}' is missing expected mesh names: {string.Join(", ", missingMeshNames)}. " +
                    "Only provided meshes will be replaced.");
            }

            foreach (var meshName in providedMeshNames)
            {
                entries.Add(new GlbMeshBundleCompiler.MeshSourceEntry
                {
                    SourceFilePath = file,
                    BundleMeshName = meshName,
                    HasExplicitMeshName = true,
                    SourceReference = BuildSourceReference(projectDir, file, meshName),
                });
            }
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.BundleMeshName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        if (duplicateTargets.Count > 0)
        {
            var messages = duplicateTargets.Select(group =>
            {
                var sources = group
                    .Select(entry => entry.SourceReference ?? entry.SourceFilePath)
                    .OrderBy(path => path, StringComparer.Ordinal);
                return $"{group.Key} <- {string.Join(", ", sources)}";
            });

            throw new InvalidOperationException(
                "Multiple replacement meshes resolve to the same target name under assets/replacements/: " +
                string.Join("; ", messages));
        }

        return [.. entries
            .OrderBy(entry => entry.SourceReference, StringComparer.Ordinal)
            .ThenBy(entry => entry.BundleMeshName, StringComparer.Ordinal)];
    }

    private static AssetIndex? LoadAssetIndex(string cachePath)
    {
        var indexPath = Path.Combine(cachePath, AssetIndexFileName);
        if (!File.Exists(indexPath))
            return null;

        return JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(indexPath));
    }

    private static ReplacementModelTarget ResolveReplacementModelTarget(AssetIndex index, string alias, string targetName, long targetPathId)
    {
        var candidates = index.Assets?
            .Where(entry =>
                (string.Equals(entry.ClassName, "GameObject", StringComparison.Ordinal) ||
                 string.Equals(entry.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal)) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal) &&
                entry.PathId == targetPathId)
            .ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replacement target '{alias}' could not be resolved in the asset index. " +
                "Use 'jiangyu assets search <name> --type PrefabHierarchyObject' to find the preferred target, or '--type GameObject' for the lower-level fallback.");

        if (candidates.Count > 1)
        {
            var matches = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' is ambiguous in the asset index. Matches: {string.Join(", ", matches)}");
        }

        var candidate = candidates[0];
        if (string.Equals(candidate.ClassName, "GameObject", StringComparison.Ordinal))
        {
            return new ReplacementModelTarget(alias, candidate.Name ?? targetName, candidate.PathId, candidate.Collection ?? string.Empty, candidate.CanonicalPath);
        }

        // JIANGYU-CONTRACT: PrefabHierarchyObject is the preferred modder-facing model target. Compile-time mesh
        // discovery resolves it back to a single same-named GameObject, then inspects that object through the
        // AssetRipper-processed game view. This is valid for the current proven export/replacement path and must
        // fail on ambiguity rather than guess.
        var backingGameObjects = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "GameObject", StringComparison.Ordinal) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal))
            .ToList()
            ?? [];

        if (backingGameObjects.Count == 0)
        {
            throw new InvalidOperationException(
                $"Replacement target '{alias}' resolves to PrefabHierarchyObject '{candidate.CanonicalPath}', but no matching GameObject named '{targetName}' exists in the asset index.");
        }

        if (backingGameObjects.Count > 1)
        {
            var matches = backingGameObjects
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' resolves to PrefabHierarchyObject '{candidate.CanonicalPath}', but the backing GameObject is ambiguous. Matches: {string.Join(", ", matches)}");
        }

        var backingGameObject = backingGameObjects[0];
        return new ReplacementModelTarget(alias, backingGameObject.Name ?? targetName, backingGameObject.PathId, backingGameObject.Collection ?? string.Empty, backingGameObject.CanonicalPath);
    }

    private static void ValidateUniqueRuntimeMeshNames(AssetIndex index, ReplacementModelTarget target, IReadOnlyList<string> expectedMeshNames)
    {
        var duplicateRuntimeTargets = expectedMeshNames
            .Select(name => new
            {
                Name = name,
                Matches = index.Assets?
                    .Where(entry =>
                        string.Equals(entry.ClassName, "Mesh", StringComparison.Ordinal) &&
                        string.Equals(entry.Name, name, StringComparison.Ordinal))
                    .ToList()
                    ?? []
            })
            .Where(x => x.Matches.Count > 1)
            .ToList();

        if (duplicateRuntimeTargets.Count == 0)
            return;

        var details = duplicateRuntimeTargets.Select(duplicate =>
        {
            var canonicalPaths = duplicate.Matches
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Mesh/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            return $"{duplicate.Name}: {string.Join(", ", canonicalPaths)}";
        });

        throw new InvalidOperationException(
            $"Replacement target '{target.Alias}' cannot be compiled safely because one or more target mesh names are ambiguous at runtime. " +
            $"The loader still matches live meshes by sharedMesh.name. Ambiguous mesh names: {string.Join("; ", details)}");
    }

    private static IReadOnlyList<string> DiscoverReplacementMeshNames(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var meshNames = model.LogicalNodes
            .Where(node => node.Mesh != null)
            .Select(node => GetReplacementMeshName(node, filePath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (meshNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"No mesh nodes were found in replacement file '{filePath}'.");
        }

        return meshNames;
    }

    private static string GetReplacementMeshName(Node node, string filePath)
    {
        var mesh = node.Mesh ?? throw new InvalidOperationException("Replacement mesh discovery expected a node with a mesh.");
        if (!string.IsNullOrWhiteSpace(node.Name))
            return node.Name;
        if (!string.IsNullOrWhiteSpace(mesh.Name))
            return mesh.Name;
        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string BuildSourceReference(string projectDir, string filePath, string meshName)
    {
        var relativePath = Path.GetRelativePath(projectDir, filePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return $"{relativePath}#{meshName}";
    }

    /// <summary>
    /// Parses "assets/replacements/file.fbx#meshname" into (absolute file path, mesh name).
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
