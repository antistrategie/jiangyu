using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Jiangyu.Compiler.Glb;
using Jiangyu.Compiler.Models;

namespace Jiangyu.Compiler.Commands;

public static class CompileCommand
{
    private static readonly JsonSerializerOptions MappingsJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var projectDir = Directory.GetCurrentDirectory();

        // Load manifest
        var manifestPath = Path.Combine(projectDir, ModManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: {ModManifest.FileName} not found. Run 'jiangyu init' first.");
            return 1;
        }

        var manifest = ModManifest.FromJson(await File.ReadAllTextAsync(manifestPath));

        // Load config
        var configPath = Path.Combine(projectDir, ProjectConfig.FilePath);
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Error: {ProjectConfig.FilePath} not found. Run 'jiangyu init' first.");
            return 1;
        }

        var config = ProjectConfig.FromJson(await File.ReadAllTextAsync(configPath));

        if (string.IsNullOrEmpty(config.UnityEditor))
        {
            Console.Error.WriteLine("Error: unityEditor not set in .jiangyu/config.json");
            return 1;
        }

        if (!File.Exists(config.UnityEditor))
        {
            Console.Error.WriteLine($"Error: Unity editor not found at: {config.UnityEditor}");
            return 1;
        }

        // Parse mesh mappings and collect model files
        var meshMappings = new Dictionary<string, string>(); // game mesh name → bundle mesh name
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
                {
                    Console.Error.WriteLine($"Error: model file not found: {sourceRef}");
                    return 1;
                }

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
        foreach (var file in CollectAssetFiles(modelsDir, "*.glb", "*.fbx", "*.obj"))
        {
            modelFiles.Add(file);
        }

        if (modelFiles.Count == 0)
        {
            Console.Error.WriteLine("No model files found. Nothing to compile.");
            return 0;
        }

        Console.WriteLine($"Compiling {manifest.Name}...");
        Console.WriteLine($"  {modelFiles.Count} model(s), {meshMappings.Count} mapping(s)");

        var bundleName = manifest.Name.ToLowerInvariant().Replace(" ", "_");
        var outputDir = Path.Combine(projectDir, "compiled");
        Directory.CreateDirectory(outputDir);
        var builtBundle = Path.Combine(Path.Combine(projectDir, ".jiangyu", "unity_project"), "AssetBundles", bundleName);
        var unityProjectDir = Path.Combine(projectDir, ".jiangyu", "unity_project");

        var useRawGlbPipeline = meshMappings.Count > 0 &&
                                mappedGlbEntries.Count == meshMappings.Count &&
                                mappedGlbEntries.Count > 0;

        ModManifest compiledManifest = manifest;
        if (useRawGlbPipeline)
        {
            Console.WriteLine("  Using experimental GLB mesh pipeline...");
            var bundleOutputPath = Path.Combine(unityProjectDir, "AssetBundles", bundleName);
            Directory.CreateDirectory(Path.GetDirectoryName(bundleOutputPath)!);
            var gameDataPath = string.IsNullOrWhiteSpace(config.Game)
                ? null
                : Path.Combine(config.Game, "Menace_Data");
            var targetMeshNamesByBundleMesh = meshMappings
                .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

            try
            {
                var buildResult = await GlbMeshBundleCompiler.BuildAsync(
                    config.UnityEditor,
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
                Console.Error.WriteLine($"Error: experimental GLB mesh pipeline failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            // Set up Unity project
            await SetupUnityProject(unityProjectDir, modelFiles);

            var success = await InvokeUnityBuild(config.UnityEditor, unityProjectDir, bundleName);
            if (!success)
                return 1;
        }

        // Collect output
        if (!File.Exists(builtBundle))
        {
            Console.Error.WriteLine("Error: Unity build did not produce expected bundle.");
            return 1;
        }

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

        Console.WriteLine($"  -> {destBundle}");
        Console.WriteLine("Done.");

        return 0;
    }

    /// <summary>
    /// Parses "models/file.fbx#meshname" into (absolute file path, mesh name).
    /// If no #fragment, the mesh name defaults to the filename without extension.
    /// </summary>
    private static ParsedAssetReference ParseAssetReference(string reference, string projectDir)
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

    private static bool IsGlbPath(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".glb", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetExtension(filePath), ".gltf", StringComparison.OrdinalIgnoreCase);

    private static string[] CollectAssetFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
            return [];

        return patterns
            .SelectMany(p => Directory.GetFiles(directory, p, SearchOption.AllDirectories))
            .OrderBy(f => f)
            .ToArray();
    }

    private static async Task SetupUnityProject(string unityProjectDir, IEnumerable<string> modelFiles)
    {
        var assetsDir = Path.Combine(unityProjectDir, "Assets");
        var editorDir = Path.Combine(assetsDir, "Editor");
        var modelsImportDir = Path.Combine(assetsDir, "Models");

        Directory.CreateDirectory(editorDir);
        Directory.CreateDirectory(modelsImportDir);

        // Write the build script from embedded resource
        var buildScript = LoadEmbeddedResource("Jiangyu.Compiler.Unity.BundleBuilder.template");
        await File.WriteAllTextAsync(Path.Combine(editorDir, "BundleBuilder.cs"), buildScript);

        // Copy model files into the Unity project
        foreach (var modelFile in modelFiles)
        {
            var destFile = Path.Combine(modelsImportDir, Path.GetFileName(modelFile));
            File.Copy(modelFile, destFile, overwrite: true);
        }

        Console.WriteLine($"  Unity project prepared at {unityProjectDir}");
    }

    private static async Task<bool> InvokeUnityBuild(string unityEditor, string unityProjectDir, string bundleName)
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

        Console.WriteLine("  Invoking Unity...");

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
            Console.Error.WriteLine($"Error: Unity exited with code {process.ExitCode}");
            Console.Error.WriteLine($"  Log: {logFile}");

            if (File.Exists(logFile))
            {
                var lines = await File.ReadAllLinesAsync(logFile);
                var tail = lines.Skip(Math.Max(0, lines.Length - 20));
                Console.Error.WriteLine();
                foreach (var line in tail)
                    Console.Error.WriteLine($"  {line}");
            }

            return false;
        }

        return true;
    }

    internal static string LoadEmbeddedResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private readonly record struct ParsedAssetReference(string FilePath, string MeshName, bool HasExplicitMeshName);
}
