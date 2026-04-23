using System.Diagnostics;
using System.Text.Json;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Unity;
using Jiangyu.Shared.Templates;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Parsed reference to an asset file, e.g. "assets/replacements/file.gltf#meshname".
/// </summary>
public readonly record struct ParsedAssetReference(string FilePath, string MeshName, bool HasExplicitMeshName);
internal readonly record struct ReplacementModelTarget(string Alias, string Name, long PathId, string Collection, string? CanonicalPath);
internal readonly record struct ReplacementTextureTarget(string Alias, string Name, long PathId, string Collection, string? CanonicalPath);
internal readonly record struct ReplacementSpriteTarget(string Alias, string Name, long PathId, string Collection, string? CanonicalPath);
internal readonly record struct ReplacementAudioTarget(string Alias, string Name, long PathId, string Collection, string? CanonicalPath);
internal readonly record struct ResolvedReplacementMeshName(string TargetMeshName, string SourceMeshName);
internal readonly record struct ResolvedReplacementRendererTarget(string TargetRendererPath, string TargetMeshName, string SourceSelector, float TargetMeshMaxHalfExtent);
internal readonly record struct ReplacementMeshPathCandidate(string PathSelector, string MatchPath);

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
    private const string BindPoseReferenceManifestFileName = "reference-manifest.json";
    private const string MeshDiagnosticsEnvVar = "JIANGYU_MESH_DISCOVERY_DIAGNOSTICS";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILogSink _log = log;
    private readonly IProgressSink _progress = progress;

    public async Task<CompilationResult> CompileAsync(CompilationInput input)
    {
        var manifest = input.Manifest;
        var config = input.Config;
        var projectDir = input.ProjectDirectory;
        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();

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

        var assetPipeline = new AssetPipelineService(gameDataPath, config.GetCachePath(), _progress, _log);
        var assetIndexStatus = assetPipeline.GetIndexStatus();
        if (!assetIndexStatus.IsCurrent)
            return Fail(assetIndexStatus.Reason ?? "Asset index is missing or stale. Run 'jiangyu assets index' first.");

        _log.Info($"  [timing] Setup/validation: {phaseSw.Elapsed.TotalSeconds:F1}s");
        phaseSw.Restart();

        var replacementRoot = Path.Combine(projectDir, "assets", "replacements");
        var additionRoot = Path.Combine(projectDir, "assets", "additions");
        var replacementEntries = DiscoverReplacementMeshEntries(projectDir, replacementRoot, config.GetCachePath(), gameDataPath, assetPipeline);
        var replacementTextures = DiscoverReplacementTextureEntries(projectDir, replacementRoot, config.GetCachePath());
        var replacementSprites = DiscoverReplacementSpriteEntries(projectDir, replacementRoot, config.GetCachePath());
        var replacementAudio = DiscoverReplacementAudioEntries(projectDir, replacementRoot, config.GetCachePath());
        _log.Info($"  [timing] Discovery: {phaseSw.Elapsed.TotalSeconds:F1}s");
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
        var replacementAssetCount = replacementEntries.Count + replacementTextures.Count + replacementSprites.Count + replacementAudio.Count;
        var totalCompileInputCount = modelFiles.Count + replacementTextures.Count + replacementSprites.Count + replacementAudio.Count;

        // Discover KDL template authoring files
        var templatesDir = Path.Combine(projectDir, "templates");
        var hasKdlTemplates = Directory.Exists(templatesDir)
            && Directory.EnumerateFiles(templatesDir, "*.kdl", SearchOption.AllDirectories).Any();

        if (totalCompileInputCount == 0 && !hasKdlTemplates)
        {
            _log.Info("No replacement assets or template patches found. Nothing to compile.");
            return new CompilationResult { Success = true };
        }

        _log.Info($"Compiling {manifest.Name}...");
        _log.Info(
            $"  {modelFiles.Count} model file(s), {replacementTextures.Count} texture replacement(s), {replacementSprites.Count} sprite replacement(s), {replacementAudio.Count} audio replacement(s), {replacementEntries.Count} replacement mesh target(s)");

        var bundleName = manifest.Name.ToLowerInvariant().Replace(" ", "_");
        var outputDir = Path.Combine(projectDir, "compiled");
        Directory.CreateDirectory(outputDir);
        var unityProjectDir = Path.Combine(projectDir, ".jiangyu", "unity_project");
        var builtBundle = Path.Combine(unityProjectDir, "AssetBundles", bundleName);

        var useRawGlbPipeline = replacementAssetCount > 0;

        // Emit templatePatches + templateClones from KDL: sugar rewrite, path/value
        // validation, clone field checks. Runs before the Unity build so
        // malformed inputs fail fast instead of surfacing as loader-time drops
        // after a slow build succeeded.
        List<CompiledTemplatePatch>? patchSource = null;
        List<CompiledTemplateClone>? cloneSource = null;

        phaseSw.Restart();
        if (hasKdlTemplates)
        {
            var kdlResult = KdlTemplateParser.ParseAll(templatesDir, _log);
            if (kdlResult.ErrorCount > 0)
                return Fail($"KDL template parsing failed with {kdlResult.ErrorCount} error(s). See errors above.");
            _log.Info($"  Parsed {kdlResult.Patches.Count} template patch(es) and {kdlResult.Clones.Count} clone(s) from KDL.");
            patchSource = kdlResult.Patches;
            cloneSource = kdlResult.Clones;
        }

        var templatePatchResult = TemplatePatchEmitter.Emit(patchSource, _log);
        var templateCloneResult = TemplatePatchEmitter.EmitClones(cloneSource, _log);
        var totalEmitErrors = templatePatchResult.ErrorCount + templateCloneResult.ErrorCount;
        if (totalEmitErrors > 0)
            return Fail($"Template compilation failed with {totalEmitErrors} error(s). See errors above.");
        if (templatePatchResult.RewriteCount > 0)
            _log.Info($"  Rewrote {templatePatchResult.RewriteCount} template patch path(s) to canonical indexed form.");
        _log.Info($"  [timing] Template compilation: {phaseSw.Elapsed.TotalSeconds:F1}s");

        var compiledManifest = ModManifest.FromJson(manifest.ToJson());
        compiledManifest.TemplatePatches = templatePatchResult.Patches;
        compiledManifest.TemplateClones = templateCloneResult.Clones;

        // Template-only compile: no asset replacements, just emit the compiled manifest.
        if (totalCompileInputCount == 0)
        {
            await File.WriteAllTextAsync(Path.Combine(outputDir, ModManifest.FileName), compiledManifest.ToJson());
            _log.Info($"  -> {Path.Combine(outputDir, ModManifest.FileName)} (template-only, no bundle)");
            _log.Info("Done.");
            return new CompilationResult { Success = true };
        }

        if (useRawGlbPipeline)
        {
            _log.Info("  Using direct mesh replacement pipeline...");
            phaseSw.Restart();
            var bundleOutputPath = Path.Combine(unityProjectDir, "AssetBundles", bundleName);
            Directory.CreateDirectory(Path.GetDirectoryName(bundleOutputPath)!);
            var targetMeshNamesByBundleMesh = replacementEntries
                .Where(entry => !entry.SuppressMeshContract)
                .ToDictionary(entry => entry.BundleMeshName, entry => entry.TargetMeshName, StringComparer.Ordinal);

            try
            {
                var buildResult = await GlbMeshBundleCompiler.BuildAsync(
                    unityEditorPath,
                    unityProjectDir,
                    bundleName,
                    bundleOutputPath,
                    replacementEntries,
                    replacementTextures,
                    replacementSprites,
                    replacementAudio,
                    gameDataPath,
                    targetMeshNamesByBundleMesh,
                    _log);
                compiledManifest.Meshes = new Dictionary<string, MeshManifestEntry>(StringComparer.Ordinal);
                foreach (var entry in replacementEntries)
                {
                    var bundleMeshName = entry.BundleMeshName;
                    if (!buildResult.MeshBoneNames.TryGetValue(bundleMeshName, out var boneNames))
                        continue;

                    compiledManifest.Meshes[entry.TargetRendererPath] = new MeshManifestEntry
                    {
                        Source = entry.SourceReference ?? bundleMeshName,
                        Compiled = new CompiledMeshMetadata
                        {
                            BoneNames = boneNames,
                            Materials = buildResult.MeshMaterialBindings.GetValueOrDefault(bundleMeshName),
                            TargetRendererPath = entry.TargetRendererPath,
                            TargetMeshName = entry.TargetMeshName,
                            TargetEntityName = entry.TargetEntityName,
                            TargetEntityPathId = entry.TargetEntityPathId,
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail($"Direct mesh replacement pipeline failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            _log.Info($"  [timing] Asset bundle build: {phaseSw.Elapsed.TotalSeconds:F1}s");
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
        _log.Info($"  [timing] Total: {totalSw.Elapsed.TotalSeconds:F1}s");
        _log.Info("Done.");

        return new CompilationResult { Success = true, BundlePath = destBundle };
    }

    public static string BuildModelReplacementAlias(string targetName, long pathId)
        => $"{targetName}--{pathId}";

    public static string BuildReplacementAlias(string targetName, long? pathId)
        => pathId.HasValue ? $"{targetName}--{pathId.Value}" : targetName;

    public static string BuildModelReplacementRelativePath(string targetName, long pathId, string fileName = "model.gltf")
        => Path.Combine("assets", "replacements", "models", BuildModelReplacementAlias(targetName, pathId), fileName)
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildModelReplacementRelativePath(string targetName, long? pathId, string fileName = "model.gltf")
        => Path.Combine("assets", "replacements", "models", BuildReplacementAlias(targetName, pathId), fileName)
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildTextureReplacementRelativePath(string targetName, long pathId, string extension = ".png")
        => Path.Combine("assets", "replacements", "textures", $"{BuildModelReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildTextureReplacementRelativePath(string targetName, long? pathId, string extension = ".png")
        => Path.Combine("assets", "replacements", "textures", $"{BuildReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildSpriteReplacementRelativePath(string targetName, long pathId, string extension = ".png")
        => Path.Combine("assets", "replacements", "sprites", $"{BuildModelReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildSpriteReplacementRelativePath(string targetName, long? pathId, string extension = ".png")
        => Path.Combine("assets", "replacements", "sprites", $"{BuildReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildAudioReplacementRelativePath(string targetName, long pathId, string extension = ".wav")
        => Path.Combine("assets", "replacements", "audio", $"{BuildModelReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    public static string BuildAudioReplacementRelativePath(string targetName, long? pathId, string extension = ".wav")
        => Path.Combine("assets", "replacements", "audio", $"{BuildReplacementAlias(targetName, pathId)}{NormalizeReplacementExtension(extension)}")
            .Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Parses a replacement alias into a target name and optional pathId.
    /// Accepts both <c>name--pathId</c> and bare <c>name</c> forms.
    /// Only the <c>--&lt;digits&gt;</c> suffix is treated as a pathId; if the
    /// text after <c>--</c> is non-numeric the whole alias is a bare name.
    /// </summary>
    public static bool TryParseReplacementAlias(string alias, out string targetName, out long? pathId)
    {
        targetName = string.Empty;
        pathId = null;

        if (string.IsNullOrWhiteSpace(alias))
            return false;

        var separatorIndex = alias.LastIndexOf("--", StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex + 2 < alias.Length &&
            long.TryParse(alias[(separatorIndex + 2)..], out var parsed))
        {
            targetName = alias[..separatorIndex];
            pathId = parsed;
            return true;
        }

        // Bare name — no pathId suffix, or non-numeric suffix after --.
        targetName = alias;
        return true;
    }

    public static bool TryParseModelReplacementAlias(string alias, out string targetName, out long pathId)
    {
        var result = TryParseReplacementAlias(alias, out targetName, out var nullablePathId);
        pathId = nullablePathId ?? 0;
        return result && nullablePathId.HasValue;
    }

    private static string NormalizeReplacementExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        return extension.StartsWith('.') ? extension : $".{extension}";
    }

    private List<GlbMeshBundleCompiler.MeshSourceEntry> DiscoverReplacementMeshEntries(string projectDir, string replacementsRoot, string cachePath, string gameDataPath, AssetPipelineService assetPipeline)
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
            if (!TryParseReplacementAlias(targetAlias, out var targetName, out var targetPathId))
            {
                throw new InvalidOperationException(
                    $"Replacement target directory '{targetDirectory}' has an invalid name.");
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
            var bindPoseReferencePath = EnsureBindPoseReferenceExport(projectDir, target, assetPipeline);
            var providedMeshCandidates = DiscoverReplacementMeshPathCandidates(file);
            var expectedTargets = AssetInspectionService.GetSkinnedMeshTargetsForIndexedObject(gameDataPath, target.Collection, target.PathId);
            if (expectedTargets.Count == 0)
                throw new InvalidOperationException(
                    $"Replacement target '{targetAlias}' [{target.CanonicalPath ?? $"{target.Collection}:{target.PathId}"}] has no skinned mesh renderer targets to replace.");
            var emitMeshDiscoveryDiagnostics = IsMeshDiscoveryDiagnosticsEnabled();
            var ambiguousRuntimeMeshNames = GetAmbiguousRuntimeMeshNames(
                index,
                [.. expectedTargets
                    .Select(x => x.MeshName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)]);

            var resolvedTargets = ResolveReplacementRendererTargets(expectedTargets, providedMeshCandidates, out var missingTargetPaths);
            if (resolvedTargets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Replacement model '{Path.GetRelativePath(projectDir, file)}' does not contain any renderer paths that match target '{targetAlias}'. " +
                    $"Expected at least one of: {FormatNameListPreview(expectedTargets.Select(x => x.RendererPath), 16)}");
            }

            ValidateReplacementMeshPrimitiveContract(file, bindPoseReferencePath, resolvedTargets);

            if (emitMeshDiscoveryDiagnostics && missingTargetPaths.Length > 0)
            {
                _log.Warning(
                    $"Replacement target '{targetAlias}' has renderer paths with no replacement in '{Path.GetRelativePath(projectDir, file)}'. " +
                    $"Original game meshes will be kept for: {FormatNameListPreview(missingTargetPaths, 24)}");
            }

            if (emitMeshDiscoveryDiagnostics && ambiguousRuntimeMeshNames.Count > 0)
            {
                _log.Warning(
                    $"Replacement target '{targetAlias}' includes runtime-ambiguous mesh name(s): {FormatNameListPreview(ambiguousRuntimeMeshNames, 24)}. " +
                    "Jiangyu will skip mesh-contract injection for those meshes and rely on runtime live-skeleton binding.");
            }

            foreach (var resolved in resolvedTargets)
            {
                var bundleMeshName = BuildBundleMeshName(resolved.TargetRendererPath, resolved.TargetMeshName);
                entries.Add(new GlbMeshBundleCompiler.MeshSourceEntry
                {
                    SourceFilePath = file,
                    BundleMeshName = bundleMeshName,
                    SourceMeshName = resolved.SourceSelector,
                    TargetMeshName = resolved.TargetMeshName,
                    TargetRendererPath = resolved.TargetRendererPath,
                    TargetMeshMaxHalfExtent = resolved.TargetMeshMaxHalfExtent,
                    HasExplicitMeshName = true,
                    SourceReference = BuildSourceReference(projectDir, file, bundleMeshName),
                    BindPoseReferencePath = bindPoseReferencePath,
                    TargetEntityName = target.Name,
                    TargetEntityPathId = target.PathId,
                    SuppressMeshContract = ambiguousRuntimeMeshNames.Contains(resolved.TargetMeshName),
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
                "Multiple replacement meshes resolve to the same compiled bundle mesh under assets/replacements/: " +
                string.Join("; ", messages));
        }

        return [.. entries
            .OrderBy(entry => entry.SourceReference, StringComparer.Ordinal)
            .ThenBy(entry => entry.BundleMeshName, StringComparer.Ordinal)];
    }

    private static List<GlbMeshBundleCompiler.CompiledTexture> DiscoverReplacementTextureEntries(string projectDir, string replacementsRoot, string cachePath)
    {
        var texturesRoot = Path.Combine(replacementsRoot, "textures");
        if (!Directory.Exists(texturesRoot))
            return [];

        var index = LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CollectAssetFiles(texturesRoot, "*.png", "*.jpg", "*.jpeg");
        if (files.Length == 0)
            return [];

        var entries = new List<GlbMeshBundleCompiler.CompiledTexture>();
        foreach (var file in files)
        {
            var alias = Path.GetFileNameWithoutExtension(file);
            if (!TryParseReplacementAlias(alias, out var targetName, out var targetPathId))
            {
                throw new InvalidOperationException(
                    $"Replacement texture '{Path.GetRelativePath(projectDir, file)}' has an invalid name.");
            }

            var target = ResolveReplacementTextureTarget(index, alias, targetName, targetPathId);
            ValidateUniqueRuntimeTextureNames(index, target);

            entries.Add(new GlbMeshBundleCompiler.CompiledTexture
            {
                Name = target.Name,
                Content = File.ReadAllBytes(file),
                Linear = GlbMeshBundleCompiler.IsLinearTextureName(target.Name),
            });
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement textures resolve to the same target name under assets/replacements/textures: {string.Join(", ", duplicateTargets)}");
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static List<GlbMeshBundleCompiler.ImportedSpriteAsset> DiscoverReplacementSpriteEntries(string projectDir, string replacementsRoot, string cachePath)
    {
        var spritesRoot = Path.Combine(replacementsRoot, "sprites");
        if (!Directory.Exists(spritesRoot))
            return [];

        var index = LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CollectAssetFiles(spritesRoot, "*.png", "*.jpg", "*.jpeg");
        if (files.Length == 0)
            return [];

        var entries = new List<GlbMeshBundleCompiler.ImportedSpriteAsset>();
        foreach (var file in files)
        {
            var alias = Path.GetFileNameWithoutExtension(file);
            if (!TryParseReplacementAlias(alias, out var targetName, out var targetPathId))
            {
                throw new InvalidOperationException(
                    $"Replacement sprite '{Path.GetRelativePath(projectDir, file)}' has an invalid name.");
            }

            var target = ResolveAndValidateSpriteTarget(index, alias, targetName, targetPathId);

            entries.Add(new GlbMeshBundleCompiler.ImportedSpriteAsset
            {
                Name = target.Name,
                SourceFilePath = file,
                Extension = Path.GetExtension(file),
                // Staging filename carries the clean target name so the Unity-side
                // template (which strips the "sprite_source__" prefix) recovers
                // exactly the runtime sprite.name the loader will look up. Using
                // the alias (target--pathId) would bake the pathId into the bundled
                // sprite.name and break catalog lookups.
                StagingName = $"sprite_source__{target.Name}",
            });
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement sprite files resolve to the same target name under assets/replacements/sprites: {string.Join(", ", duplicateTargets)}");
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static List<GlbMeshBundleCompiler.ImportedAudioAsset> DiscoverReplacementAudioEntries(string projectDir, string replacementsRoot, string cachePath)
    {
        var audioRoot = Path.Combine(replacementsRoot, "audio");
        if (!Directory.Exists(audioRoot))
            return [];

        var index = LoadAssetIndex(cachePath)
            ?? throw new InvalidOperationException(
                $"Asset index not found or unreadable at '{Path.Combine(cachePath, AssetIndexFileName)}'. Run 'jiangyu assets index' first.");

        var files = CollectAssetFiles(audioRoot, "*.wav", "*.ogg", "*.mp3");
        if (files.Length == 0)
            return [];

        var entries = new List<GlbMeshBundleCompiler.ImportedAudioAsset>();
        foreach (var file in files)
        {
            var alias = Path.GetFileNameWithoutExtension(file);
            if (!TryParseReplacementAlias(alias, out var targetName, out var targetPathId))
            {
                throw new InvalidOperationException(
                    $"Replacement audio '{Path.GetRelativePath(projectDir, file)}' has an invalid name.");
            }

            var target = ResolveAndValidateAudioTarget(index, alias, targetName, targetPathId, file);

            entries.Add(new GlbMeshBundleCompiler.ImportedAudioAsset
            {
                Name = target.Name,
                SourceFilePath = file,
                Extension = Path.GetExtension(file),
            });
        }

        var duplicateTargets = entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateTargets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Multiple replacement audio files resolve to the same target name under assets/replacements/audio: {string.Join(", ", duplicateTargets)}");
        }

        return [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)];
    }

    private static AssetIndex? LoadAssetIndex(string cachePath)
    {
        var indexPath = Path.Combine(cachePath, AssetIndexFileName);
        if (!File.Exists(indexPath))
            return null;

        return JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(indexPath));
    }

    private static ReplacementModelTarget ResolveReplacementModelTarget(AssetIndex index, string alias, string targetName, long? targetPathId)
    {
        var candidates = index.Assets?
            .Where(entry =>
                (string.Equals(entry.ClassName, "GameObject", StringComparison.Ordinal) ||
                 string.Equals(entry.ClassName, "PrefabHierarchyObject", StringComparison.Ordinal)) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal) &&
                (!targetPathId.HasValue || entry.PathId == targetPathId.Value))
            .ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replacement target '{alias}' could not be resolved in the asset index. " +
                "Use 'jiangyu assets search <name> --type PrefabHierarchyObject' to find the preferred target, or '--type GameObject' for the lower-level fallback.");

        // When pathId was not specified, collapse PHO→backing GO pairs so a
        // name that exists as both PHO and GO counts as one effective target.
        if (!targetPathId.HasValue && candidates.Count > 1)
        {
            var deduped = new Dictionary<(string Collection, long PathId), AssetEntry>();
            foreach (var entry in candidates)
            {
                var resolved = AssetPipelineService.ResolveGameObjectBacking(index, entry);
                var key = (resolved.Collection ?? string.Empty, resolved.PathId);
                deduped.TryAdd(key, resolved);
            }
            candidates = [.. deduped.Values];
        }

        if (candidates.Count > 1)
        {
            var matches = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' is ambiguous in the asset index. " +
                $"Disambiguate with '--pathId', e.g. '{targetName}--<pathId>'. Candidates: {string.Join(", ", matches)}");
        }

        var candidate = candidates[0];
        var finalResolved = AssetPipelineService.ResolveGameObjectBacking(index, candidate);
        return new ReplacementModelTarget(alias, finalResolved.Name ?? targetName, finalResolved.PathId, finalResolved.Collection ?? string.Empty, finalResolved.CanonicalPath);
    }

    private static ReplacementTextureTarget ResolveReplacementTextureTarget(AssetIndex index, string alias, string targetName, long? targetPathId)
    {
        var candidates = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Texture2D", StringComparison.Ordinal) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal) &&
                (!targetPathId.HasValue || entry.PathId == targetPathId.Value))
            .ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replacement target '{alias}' could not be resolved as a Texture2D in the asset index. Use 'jiangyu assets search <name> --type Texture2D'.");

        if (candidates.Count > 1)
        {
            var matches = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Texture2D/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' is ambiguous in the asset index. " +
                $"Disambiguate with '--pathId', e.g. '{targetName}--<pathId>'. Candidates: {string.Join(", ", matches)}");
        }

        var candidate = candidates[0];
        return new ReplacementTextureTarget(alias, candidate.Name ?? targetName, candidate.PathId, candidate.Collection ?? string.Empty, candidate.CanonicalPath);
    }

    private static ReplacementAudioTarget ResolveReplacementAudioTarget(AssetIndex index, string alias, string targetName, long? targetPathId)
    {
        var candidates = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "AudioClip", StringComparison.Ordinal) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal) &&
                (!targetPathId.HasValue || entry.PathId == targetPathId.Value))
            .ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replacement target '{alias}' could not be resolved as an AudioClip in the asset index. Use 'jiangyu assets search <name> --type AudioClip'.");

        if (candidates.Count > 1)
        {
            var matches = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/AudioClip/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' is ambiguous in the asset index. " +
                $"Disambiguate with '--pathId', e.g. '{targetName}--<pathId>'. Candidates: {string.Join(", ", matches)}");
        }

        var candidate = candidates[0];
        return new ReplacementAudioTarget(alias, candidate.Name ?? targetName, candidate.PathId, candidate.Collection ?? string.Empty, candidate.CanonicalPath);
    }

    private static ReplacementSpriteTarget ResolveReplacementSpriteTarget(AssetIndex index, string alias, string targetName, long? targetPathId)
    {
        var candidates = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
                string.Equals(entry.Name, targetName, StringComparison.Ordinal) &&
                (!targetPathId.HasValue || entry.PathId == targetPathId.Value))
            .ToList()
            ?? [];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replacement target '{alias}' could not be resolved as a Sprite in the asset index. Use 'jiangyu assets search <name> --type Sprite'.");

        if (candidates.Count > 1)
        {
            var matches = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Sprite/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal);
            throw new InvalidOperationException(
                $"Replacement target '{alias}' is ambiguous in the asset index. " +
                $"Disambiguate with '--pathId', e.g. '{targetName}--<pathId>'. Candidates: {string.Join(", ", matches)}");
        }

        var candidate = candidates[0];
        return new ReplacementSpriteTarget(alias, candidate.Name ?? targetName, candidate.PathId, candidate.Collection ?? string.Empty, candidate.CanonicalPath);
    }

    private static HashSet<string> GetAmbiguousRuntimeMeshNames(AssetIndex index, IReadOnlyList<string> expectedMeshNames)
    {
        return [.. expectedMeshNames
            .Where(name =>
            {
                var matchCount = index.Assets?
                    .Count(entry =>
                        string.Equals(entry.ClassName, "Mesh", StringComparison.Ordinal) &&
                        string.Equals(entry.Name, name, StringComparison.Ordinal))
                    ?? 0;
                return matchCount > 1;
            })
            .Distinct(StringComparer.Ordinal)];
    }

    private static void ValidateUniqueRuntimeTextureNames(AssetIndex index, ReplacementTextureTarget target)
    {
        var matches = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Texture2D", StringComparison.Ordinal) &&
                string.Equals(entry.Name, target.Name, StringComparison.Ordinal))
            .ToList()
            ?? [];

        if (matches.Count <= 1)
            return;

        var canonicalPaths = matches
            .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Texture2D/{entry.Name}--{entry.PathId}")
            .OrderBy(path => path, StringComparer.Ordinal);

        throw new InvalidOperationException(
            $"Replacement target '{target.Alias}' cannot be compiled safely because texture name '{target.Name}' is ambiguous at runtime. " +
            $"The loader currently matches live textures by texture.name. Ambiguous texture assets: {string.Join(", ", canonicalPaths)}");
    }

    private static void ValidateUniqueRuntimeSpriteNames(AssetIndex index, ReplacementSpriteTarget target)
    {
        var collisions = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
                string.Equals(entry.Name, target.Name, StringComparison.Ordinal) &&
                entry.PathId != target.PathId)
            .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Sprite/{entry.Name}--{entry.PathId}")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray()
            ?? [];

        if (collisions.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Replacement sprite target '{target.Alias}' resolves to runtime sprite name '{target.Name}', but that sprite name is ambiguous across indexed assets. " +
            $"Current target: {target.CanonicalPath ?? $"{target.Collection}/Sprite/{target.Name}--{target.PathId}"}. " +
            $"Conflicts: {string.Join(", ", collisions)}. " +
            "Jiangyu currently resolves live sprite replacements by sprite name at runtime, so ambiguous sprite targets must be rejected until a stronger runtime identity exists.");
    }

    /// <summary>
    /// Rejects sprite targets whose backing Texture2D is shared by more than one indexed Sprite.
    /// Sprite replacement lands via in-place mutation of the sprite's backing Texture2D (see
    /// docs/research/verified/sprite-replacement.md); an atlas-backed sprite cannot be
    /// individually replaced without corrupting every other sprite drawn from the same atlas.
    /// </summary>
    internal static void ValidateSpriteBackingTextureIsUnique(AssetIndex index, ReplacementSpriteTarget target)
    {
        var targetEntry = index.Assets?.FirstOrDefault(entry =>
            string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
            string.Equals(entry.Name, target.Name, StringComparison.Ordinal) &&
            entry.PathId == target.PathId);

        if (targetEntry is null)
            return;

        if (targetEntry.SpriteBackingTexturePathId is null)
        {
            throw new InvalidOperationException(
                $"Replacement sprite target '{target.Alias}' is missing backing-texture information in the asset index. " +
                "Rebuild the index with 'jiangyu assets index' so the compiler can verify that the sprite is not atlas-backed. " +
                "Sprite replacement now requires backing-texture identity to reject atlas-backed targets safely.");
        }

        var backingPathId = targetEntry.SpriteBackingTexturePathId.Value;
        var backingCollection = targetEntry.SpriteBackingTextureCollection;

        var coTenants = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
                entry.SpriteBackingTexturePathId == backingPathId &&
                string.Equals(entry.SpriteBackingTextureCollection, backingCollection, StringComparison.Ordinal) &&
                !(entry.PathId == target.PathId &&
                  string.Equals(entry.Collection, target.Collection, StringComparison.Ordinal)))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.PathId)
            .ToList()
            ?? [];

        if (coTenants.Count == 0)
            return;

        const int previewCount = 5;
        var previewNames = coTenants
            .Take(previewCount)
            .Select(entry => entry.Name ?? $"pathId={entry.PathId}")
            .ToArray();
        var preview = string.Join(", ", previewNames);
        if (coTenants.Count > previewCount)
            preview += $", and {coTenants.Count - previewCount} more";

        var backingLabel = !string.IsNullOrWhiteSpace(targetEntry.SpriteBackingTextureName)
            ? $"{targetEntry.SpriteBackingTextureName} (pathId={backingPathId})"
            : $"pathId={backingPathId}";

        throw new InvalidOperationException(
            $"Replacement sprite target '{target.Alias}' is backed by Texture2D '{backingLabel}', which is also the backing texture for {coTenants.Count} other indexed Sprite(s): {preview}. " +
            $"This texture is a shared atlas, so mutating it to replace '{target.Name}' would corrupt the other sprites. " +
            "Atlas-backed sprites cannot be individually replaced — only sprites backed by a unique Texture2D can be replaced via in-place mutation.");
    }

    internal static ReplacementSpriteTarget ResolveAndValidateSpriteTarget(
        AssetIndex index,
        string alias,
        string targetName,
        long? targetPathId)
    {
        var target = ResolveReplacementSpriteTarget(index, alias, targetName, targetPathId);
        ValidateUniqueRuntimeSpriteNames(index, target);
        ValidateSpriteBackingTextureIsUnique(index, target);
        return target;
    }

    private static void ValidateUniqueRuntimeAudioNames(AssetIndex index, ReplacementAudioTarget target)
    {
        var matches = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "AudioClip", StringComparison.Ordinal) &&
                string.Equals(entry.Name, target.Name, StringComparison.Ordinal))
            .ToList()
            ?? [];

        if (matches.Count <= 1)
            return;

        var canonicalPaths = matches
            .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/AudioClip/{entry.Name}--{entry.PathId}")
            .OrderBy(path => path, StringComparer.Ordinal);

        throw new InvalidOperationException(
            $"Replacement target '{target.Alias}' cannot be compiled safely because audio clip name '{target.Name}' is ambiguous at runtime. " +
            $"The loader currently matches live audio by clip.name. Ambiguous audio assets: {string.Join(", ", canonicalPaths)}");
    }

    /// <summary>
    /// Rejects WAV replacements whose header sample rate or channel count differ
    /// from the indexed target. Unity resamples mismatches at playback time,
    /// which pitch-shifts the sound; a compile-time rejection is more useful to
    /// a modder than silent pitch distortion. Non-WAV formats (OGG, MP3) are
    /// not inspected here and the check is skipped with no error — those
    /// modders get Unity's runtime resampling if they mismatch.
    /// </summary>
    internal static void ValidateAudioFileMatchesTarget(AssetIndex index, ReplacementAudioTarget target, string filePath)
    {
        var targetEntry = index.Assets?.FirstOrDefault(entry =>
            string.Equals(entry.ClassName, "AudioClip", StringComparison.Ordinal) &&
            string.Equals(entry.Name, target.Name, StringComparison.Ordinal) &&
            entry.PathId == target.PathId);

        if (targetEntry is null) return;
        if (targetEntry.AudioFrequency is null || targetEntry.AudioChannels is null) return;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".wav") return;

        if (!TryReadWavHeader(filePath, out var sourceFrequency, out var sourceChannels))
            return;

        if (sourceFrequency == targetEntry.AudioFrequency.Value && sourceChannels == targetEntry.AudioChannels.Value)
            return;

        throw new InvalidOperationException(
            $"Replacement audio target '{target.Alias}' has a WAV source at {sourceFrequency}Hz {sourceChannels}ch, " +
            $"but the target AudioClip is {targetEntry.AudioFrequency.Value}Hz {targetEntry.AudioChannels.Value}ch. " +
            "Unity would resample the mismatch at runtime, which pitch-shifts the sound. " +
            "Re-export the source at the target's rate and channel layout, or ship a format Jiangyu doesn't validate (OGG/MP3) if runtime resampling is acceptable.");
    }

    internal static ReplacementAudioTarget ResolveAndValidateAudioTarget(
        AssetIndex index,
        string alias,
        string targetName,
        long? targetPathId,
        string filePath)
    {
        var target = ResolveReplacementAudioTarget(index, alias, targetName, targetPathId);
        ValidateUniqueRuntimeAudioNames(index, target);
        ValidateAudioFileMatchesTarget(index, target, filePath);
        return target;
    }

    internal static IReadOnlyList<ResolvedReplacementMeshName> ResolveReplacementMeshNames(
        IReadOnlyList<string> expectedMeshNames,
        IReadOnlyList<string> providedMeshNames,
        out string[] unexpectedMeshNames,
        out string[] collapsedDuplicateMeshNames)
    {
        var expectedSet = new HashSet<string>(expectedMeshNames, StringComparer.Ordinal);
        var candidatesByTarget = expectedMeshNames.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var provided in providedMeshNames)
        {
            if (TryResolveTargetMeshNameFromProvided(provided, expectedSet, out var targetName))
            {
                candidatesByTarget[targetName].Add(provided);
                continue;
            }

            unresolved.Add(provided);
        }

        var resolved = new List<ResolvedReplacementMeshName>();
        var collapsedDuplicates = new List<string>();
        foreach (var targetName in expectedMeshNames)
        {
            if (!candidatesByTarget.TryGetValue(targetName, out var candidates) || candidates.Count == 0)
                continue;

            var selected = candidates
                .OrderBy(candidate => GetResolvedMeshNameSortKey(candidate, targetName))
                .ThenBy(candidate => candidate, StringComparer.Ordinal)
                .First();

            foreach (var candidate in candidates.Where(candidate => !candidate.Equals(selected, StringComparison.Ordinal)))
            {
                collapsedDuplicates.Add(candidate);
            }

            resolved.Add(new ResolvedReplacementMeshName(targetName, selected));
        }

        unexpectedMeshNames = [.. unresolved.OrderBy(name => name, StringComparer.Ordinal)];
        collapsedDuplicateMeshNames = [.. collapsedDuplicates.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)];
        return resolved;
    }

    private static IReadOnlyList<ReplacementMeshPathCandidate> DiscoverReplacementMeshPathCandidates(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var candidates = model.LogicalNodes
            .Where(node => node.Mesh != null)
            .SelectMany(node => BuildPathCandidates(node, filePath))
            .Distinct()
            .OrderBy(candidate => candidate.PathSelector, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No mesh nodes were found in replacement file '{filePath}'.");
        }

        return candidates;
    }

    private static IReadOnlyList<ResolvedReplacementRendererTarget> ResolveReplacementRendererTargets(
        IReadOnlyList<SkinnedMeshTarget> expectedTargets,
        IReadOnlyList<ReplacementMeshPathCandidate> providedCandidates,
        out string[] missingTargetPaths)
    {
        var byNormalisedPath = providedCandidates
            .GroupBy(candidate => candidate.MatchPath, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(candidate => candidate.PathSelector)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var usedSourceSelectors = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<ResolvedReplacementRendererTarget>();
        var missing = new List<string>();

        foreach (var target in expectedTargets.OrderBy(target => target.RendererPath, StringComparer.Ordinal))
        {
            var normalisedTargetPath = NormaliseRendererPath(target.RendererPath);
            if (!byNormalisedPath.TryGetValue(normalisedTargetPath, out var selectors))
            {
                missing.Add(target.RendererPath);
                continue;
            }

            var sourceSelector = selectors.FirstOrDefault(selector => !usedSourceSelectors.Contains(selector));
            if (string.IsNullOrWhiteSpace(sourceSelector))
            {
                missing.Add(target.RendererPath);
                continue;
            }

            usedSourceSelectors.Add(sourceSelector);
            resolved.Add(new ResolvedReplacementRendererTarget(target.RendererPath, target.MeshName, sourceSelector, target.TargetMeshMaxHalfExtent));
        }

        missingTargetPaths = [.. missing.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)];
        return resolved;
    }

    private static IEnumerable<ReplacementMeshPathCandidate> BuildPathCandidates(Node node, string filePath)
    {
        foreach (var path in BuildNodePathAliases(node))
        {
            var normalisedPath = NormaliseRendererPath(path);
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(normalisedPath))
                yield return new ReplacementMeshPathCandidate(path, normalisedPath);
        }

        foreach (var fallbackAlias in GetReplacementMeshAliases(node, filePath))
        {
            var normalisedAlias = NormaliseRendererPath(fallbackAlias);
            if (!string.IsNullOrWhiteSpace(fallbackAlias) && !string.IsNullOrWhiteSpace(normalisedAlias))
                yield return new ReplacementMeshPathCandidate(fallbackAlias, normalisedAlias);
        }
    }

    private static IEnumerable<string> BuildNodePathAliases(Node node)
    {
        var withRoot = BuildNodePath(node, includeRoot: true);
        if (!string.IsNullOrWhiteSpace(withRoot))
            yield return withRoot;

        var withoutRoot = BuildNodePath(node, includeRoot: false);
        if (!string.IsNullOrWhiteSpace(withoutRoot))
            yield return withoutRoot;
    }

    private static string BuildNodePath(Node node, bool includeRoot)
    {
        var segments = new List<string>();
        var current = node;

        while (current != null)
        {
            if (current.VisualParent == null && !includeRoot)
                break;

            if (string.IsNullOrWhiteSpace(current.Name))
                return string.Empty;

            segments.Add(current.Name);
            current = current.VisualParent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static string NormaliseRendererPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var normalised = segment;
                if (TryStripBlenderNumericSuffix(segment, out var strippedSegment, out _))
                    normalised = strippedSegment;

                if (TryStripContainerPathSuffix(normalised, out var strippedContainerSegment))
                    normalised = strippedContainerSegment;

                return normalised;
            });

        return string.Join("/", segments);
    }

    private static bool TryStripContainerPathSuffix(string segment, out string strippedSegment)
    {
        const string containerSuffix = "_container";

        strippedSegment = segment;
        if (string.IsNullOrWhiteSpace(segment) ||
            !segment.EndsWith(containerSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        strippedSegment = segment[..^containerSuffix.Length];
        return !string.IsNullOrWhiteSpace(strippedSegment);
    }

    private static string BuildBundleMeshName(string targetRendererPath, string targetMeshName)
    {
        var safeMeshName = SanitizeBundleNameToken(targetMeshName);
        var pathHash = ComputeStableShortHash(targetRendererPath);
        return $"{safeMeshName}__{pathHash}";
    }

    private static string SanitizeBundleNameToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mesh";

        var chars = value.Select(c =>
        {
            if (char.IsLetterOrDigit(c))
                return c;

            return c switch
            {
                '_' => '_',
                '-' => '-',
                _ => '_',
            };
        }).ToArray();

        return new string(chars);
    }

    private static string ComputeStableShortHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
    }

    internal static bool TryStripBlenderNumericSuffix(string name, out string strippedName, out int suffixValue)
    {
        strippedName = name;
        suffixValue = -1;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        var dotIndex = name.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= name.Length - 1)
            return false;

        var suffix = name[(dotIndex + 1)..];
        if (suffix.Length < 3 || !suffix.All(char.IsDigit))
            return false;

        if (!int.TryParse(suffix, out suffixValue))
            return false;

        strippedName = name[..dotIndex];
        return !string.IsNullOrEmpty(strippedName);
    }

    private static bool TryResolveTargetMeshNameFromProvided(string providedName, HashSet<string> expectedSet, out string targetName)
    {
        targetName = string.Empty;

        if (expectedSet.Contains(providedName))
        {
            targetName = providedName;
            return true;
        }

        if (TryStripBlenderNumericSuffix(providedName, out var stripped, out _) &&
            expectedSet.Contains(stripped))
        {
            targetName = stripped;
            return true;
        }

        return false;
    }

    private static int GetResolvedMeshNameSortKey(string providedName, string targetName)
    {
        if (providedName.Equals(targetName, StringComparison.Ordinal))
            return 0;

        if (TryStripBlenderNumericSuffix(providedName, out var strippedTargetCandidate, out var targetSuffixValue) &&
            strippedTargetCandidate.Equals(targetName, StringComparison.Ordinal))
        {
            return 100 + targetSuffixValue;
        }

        return int.MaxValue;
    }

    private static bool IsMeshDiscoveryDiagnosticsEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(MeshDiagnosticsEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Equals("1", StringComparison.Ordinal) ||
               raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNameListPreview(IEnumerable<string> names, int maxItems)
    {
        var ordered = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length <= maxItems)
            return string.Join(", ", ordered);

        var preview = ordered.Take(maxItems);
        return $"{string.Join(", ", preview)} (+{ordered.Length - maxItems} more)";
    }

    private static bool TryReadWavHeader(string path, out int sampleRate, out int channels)
    {
        sampleRate = 0;
        channels = 0;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            // RIFF header: "RIFF" (4) + size (4) + "WAVE" (4).
            if (reader.ReadUInt32() != 0x46464952u) return false; // "RIFF"
            reader.ReadUInt32();
            if (reader.ReadUInt32() != 0x45564157u) return false; // "WAVE"

            // Walk chunks until we find "fmt ".
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = reader.ReadUInt32();
                var chunkSize = reader.ReadUInt32();

                if (chunkId == 0x20746d66u) // "fmt "
                {
                    if (chunkSize < 16) return false;
                    reader.ReadUInt16(); // audio format
                    channels = reader.ReadUInt16();
                    sampleRate = (int)reader.ReadUInt32();
                    return channels > 0 && sampleRate > 0;
                }

                // Skip this chunk. Chunks are word-aligned.
                var skip = chunkSize + (chunkSize % 2);
                if (stream.Position + skip > stream.Length) return false;
                stream.Seek(skip, SeekOrigin.Current);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateReplacementMeshPrimitiveContract(
        string replacementFilePath,
        string referenceFilePath,
        IReadOnlyList<ResolvedReplacementRendererTarget> resolvedTargets)
    {
        var replacementPrimitiveCounts = DiscoverReplacementMeshPrimitiveCounts(replacementFilePath);
        var referencePrimitiveCounts = DiscoverReplacementMeshPrimitiveCounts(referenceFilePath);

        var mismatches = resolvedTargets
            .Where(resolved =>
                replacementPrimitiveCounts.TryGetValue(resolved.SourceSelector, out var replacementCount) &&
                referencePrimitiveCounts.TryGetValue(resolved.TargetRendererPath, out var referenceCount) &&
                replacementCount != referenceCount)
            .Select(resolved => $"{resolved.TargetRendererPath} (replacement {replacementPrimitiveCounts[resolved.SourceSelector]}, target {referencePrimitiveCounts[resolved.TargetRendererPath]})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (mismatches.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Replacement model '{replacementFilePath}' does not match the target mesh primitive/material-slot contract. " +
            $"Direct mesh replacement currently requires the same primitive count per target mesh as the target export. " +
            $"Mismatches: {string.Join(", ", mismatches)}");
    }

    internal static IReadOnlyDictionary<string, int> DiscoverReplacementMeshPrimitiveCounts(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in model.LogicalNodes.Where(node => node.Mesh != null))
        {
            var primitiveCount = node.Mesh!.Primitives.Count;
            foreach (var alias in GetReplacementMeshAliases(node, filePath))
            {
                if (result.TryGetValue(alias, out var existingCount) && existingCount != primitiveCount)
                {
                    throw new InvalidOperationException(
                        $"Replacement file '{filePath}' maps alias '{alias}' to multiple mesh primitive counts ({existingCount} and {primitiveCount}).");
                }

                result[alias] = primitiveCount;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> GetReplacementMeshAliases(Node node, string filePath)
    {
        var mesh = node.Mesh ?? throw new InvalidOperationException("Replacement mesh discovery expected a node with a mesh.");
        var aliases = new List<string>();

        foreach (var pathAlias in BuildNodePathAliases(node))
        {
            if (!aliases.Contains(pathAlias, StringComparer.Ordinal))
                aliases.Add(pathAlias);
        }

        if (!string.IsNullOrWhiteSpace(mesh.Name))
            aliases.Add(mesh.Name);

        if (!string.IsNullOrWhiteSpace(node.Name) &&
            !aliases.Contains(node.Name, StringComparer.Ordinal))
        {
            aliases.Add(node.Name);
        }

        if (aliases.Count == 0)
            aliases.Add(Path.GetFileNameWithoutExtension(filePath));

        return aliases;
    }

    // JIANGYU-CONTRACT: the bind-pose retargeting reference is the cleaned authoring
    // export (ModelCleanupService-normalised, metre-space, no LOD containers). Retarget
    // correction is expressed relative to the modder's authoring space, and modders
    // start from the same cleaned export — so authored and reference bindposes live
    // in the same coordinate system. Using raw game-native bindposes as the reference
    // breaks retargeting across the board because authored vertices are in cleaned
    // space while reference bindposes would be in raw centimetre-scale. Pass-2 of
    // the Unity build (MeshContractExtractor + MeshBundleBuilder) separately stamps
    // the final mesh bindposes back to game-native values; the drift between cleaned
    // and game-native bindposes currently shows as wheel scale/orbit on vehicle rigs
    // and is tracked as a separate correctness work item. Scope: proven skinned
    // replacement path.
    private static string EnsureBindPoseReferenceExport(string projectDir, ReplacementModelTarget target, AssetPipelineService assetPipeline)
    {
        var referenceDirectory = Path.Combine(projectDir, ".jiangyu", "bind_pose_references", BuildModelReplacementAlias(target.Name, target.PathId));
        var referenceModelPath = Path.Combine(referenceDirectory, "model.gltf");
        var referenceManifestPath = Path.Combine(referenceDirectory, BindPoseReferenceManifestFileName);
        var assetIndexManifest = assetPipeline.LoadManifest()
            ?? throw new InvalidOperationException("Asset index manifest could not be loaded while preparing bind-pose references.");

        if (File.Exists(referenceModelPath)
            && TryLoadBindPoseReferenceManifest(referenceManifestPath, out var referenceManifest)
            && string.Equals(referenceManifest.GameAssemblyHash, assetIndexManifest.GameAssemblyHash, StringComparison.Ordinal))
        {
            return referenceModelPath;
        }

        if (Directory.Exists(referenceDirectory))
            Directory.Delete(referenceDirectory, recursive: true);

        assetPipeline.ExportModel(target.Name, referenceDirectory, clean: true, target.Collection, target.PathId);
        if (!File.Exists(referenceModelPath))
        {
            throw new InvalidOperationException(
                $"Failed to auto-export bind-pose reference model for target '{target.Name}--{target.PathId}'.");
        }

        Directory.CreateDirectory(referenceDirectory);
        File.WriteAllText(
            referenceManifestPath,
            JsonSerializer.Serialize(
                new BindPoseReferenceManifest { GameAssemblyHash = assetIndexManifest.GameAssemblyHash },
                JsonOptions));

        return referenceModelPath;
    }

    private static bool TryLoadBindPoseReferenceManifest(string path, out BindPoseReferenceManifest manifest)
    {
        manifest = new BindPoseReferenceManifest();
        if (!File.Exists(path))
            return false;

        try
        {
            var loaded = JsonSerializer.Deserialize<BindPoseReferenceManifest>(File.ReadAllText(path), JsonOptions);
            if (loaded is null || string.IsNullOrWhiteSpace(loaded.GameAssemblyHash))
                return false;

            manifest = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class BindPoseReferenceManifest
    {
        public string? GameAssemblyHash { get; init; }
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

    internal static IReadOnlyList<string> BuildIncompleteLodWarnings(
        string targetAlias,
        IReadOnlyList<string> expectedMeshNames,
        IReadOnlyList<string> providedMeshNames)
    {
        var expectedFamilies = BuildLodFamilies(expectedMeshNames);
        if (expectedFamilies.Count == 0)
            return [];

        var providedSet = new HashSet<string>(providedMeshNames, StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var family in expectedFamilies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var expectedFamilyMeshes = family.Value.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var providedFamilyMeshes = expectedFamilyMeshes.Where(providedSet.Contains).OrderBy(name => name, StringComparer.Ordinal).ToArray();

            if (providedFamilyMeshes.Length == 0 || providedFamilyMeshes.Length == expectedFamilyMeshes.Length)
                continue;

            var missingFamilyMeshes = expectedFamilyMeshes
                .Except(providedFamilyMeshes, StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);

            warnings.Add(
                $"Replacement target '{targetAlias}' provides only part of LOD family '{family.Key}': " +
                $"provided {string.Join(", ", providedFamilyMeshes)}; missing {string.Join(", ", missingFamilyMeshes)}. " +
                "Runtime will use the nearest available replacement within the family for missing LODs.");
        }

        return warnings;
    }

    private static Dictionary<string, List<string>> BuildLodFamilies(IReadOnlyList<string> meshNames)
    {
        var families = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var meshName in meshNames)
        {
            if (!TryParseLodName(meshName, out var baseName, out _))
                continue;

            if (!families.TryGetValue(baseName, out var family))
            {
                family = [];
                families[baseName] = family;
            }

            family.Add(meshName);
        }

        return families;
    }

    internal static bool TryParseLodName(string meshName, out string baseName, out int lodIndex)
    {
        baseName = string.Empty;
        lodIndex = -1;

        if (string.IsNullOrWhiteSpace(meshName))
            return false;

        var markerIndex = meshName.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            return false;

        var suffix = meshName[(markerIndex + 4)..];
        if (!int.TryParse(suffix, out lodIndex))
            return false;

        baseName = meshName[..markerIndex];
        return !string.IsNullOrWhiteSpace(baseName);
    }

    private async Task SetupUnityProject(string unityProjectDir, IEnumerable<string> modelFiles)
    {
        var assetsDir = Path.Combine(unityProjectDir, "Assets");
        var editorDir = Path.Combine(assetsDir, "Editor");
        var modelsImportDir = Path.Combine(assetsDir, "Models");

        Directory.CreateDirectory(editorDir);
        Directory.CreateDirectory(modelsImportDir);

        var buildScript = GlbMeshBundleCompiler.LoadEmbeddedResource("Jiangyu.Core.Unity.BundleBuilder.template");
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

        _log.Info("  Invoking Unity to build bundle...");
        _log.Info("  (First run can take several minutes while Unity imports the staging project. Subsequent runs are incremental.)");

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
