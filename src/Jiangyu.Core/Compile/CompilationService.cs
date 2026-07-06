using System.Diagnostics;
using System.Text.Json;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Code;
using Jiangyu.Core.Config;
using Jiangyu.Core.Glb;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.IO;
using Jiangyu.Core.Localisation;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Unity;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Replacements;
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

    /// <summary>Force a full rebuild, ignoring (and overwriting) the incremental build state.
    /// Defaults to incremental.</summary>
    public bool Clean { get; init; }

    /// <summary>Build for release: exclude dev-only <c>*.Dev.cs</c> sources (dev verbs, debug probes)
    /// from the code DLL. Defaults to a dev build, which includes them for local testing.</summary>
    public bool Release { get; init; }
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

        var replacementRoot = Path.Combine(projectDir, "assets", "replacements");
        var additionRoot = Path.Combine(projectDir, "assets", "additions");

        // Auto-import host-game prefabs declared in manifest.imports,
        // and auto-build the asset index when stale. Both steps need
        // GameData, which is multi-second to load, so load it lazily and at
        // most once per compile.
        var importedRoot = Path.Combine(projectDir, "unity", "Assets", "Imported");
        var missingImports = manifest.Imports is { Count: > 0 }
            ? manifest.Imports
                .Where(name => !Directory.Exists(Path.Combine(importedRoot, name)))
                .ToList()
            : new List<string>();
        var needsIndex = HasAnyAssetSources(replacementRoot, additionRoot)
            && !assetPipeline.GetIndexStatus().IsCurrent;

        if (missingImports.Count > 0 || needsIndex)
        {
            var reasons = new List<string>();
            if (missingImports.Count > 0) reasons.Add($"{missingImports.Count} host prefab(s) to import");
            if (needsIndex) reasons.Add("asset index missing/stale");
            _log.Info($"Loading game data ({string.Join(", ", reasons)})...");

            using var session = assetPipeline.LoadAndProcessGameData();

            foreach (var name in missingImports)
            {
                _log.Info($"  importing host prefab: {name}");
                try
                {
                    assetPipeline.ImportPrefabAsUnityAssets(
                        session.GameData,
                        assetName: name,
                        destDir: Path.Combine(importedRoot, name),
                        collection: null,
                        pathId: -1);
                }
                catch (InvalidOperationException ex)
                {
                    return Fail($"Failed to import host prefab '{name}' from imports: {ex.Message}");
                }
            }

            if (needsIndex)
            {
                _log.Info("  building asset index...");
                assetPipeline.BuildIndexFromGameData(session.GameData);
            }
        }

        // Validate that every host-rip GUID referenced outside Imported/ has
        // its rip declared in imports. Catches the silent-pink-
        // material failure where a contributor bakes against a vanilla rip
        // but forgets to add it to the manifest.
        var importValidationError = ImportedPrefabValidator.Validate(projectDir, manifest);
        if (importValidationError != null)
            return Fail(importValidationError);

        // Stale Jiangyu-managed editor scripts silently skip new batchmode pipeline steps.
        var drifted = new UnityProjectScaffolder(_log).FindDriftedManagedFiles(projectDir);
        if (drifted.Count > 0)
        {
            _log.Warning(
                $"Jiangyu-managed Unity Editor scripts are out of sync with this Jiangyu version ({drifted.Count} file(s)):");
            foreach (var path in drifted)
                _log.Warning($"  {Path.GetRelativePath(projectDir, path)}");
            _log.Warning("Run 'jiangyu unity sync' to refresh. Compile will continue, but bundles may not build correctly.");
        }

        _log.Info($"  [timing] Setup/validation: {phaseSw.Elapsed.TotalSeconds:F1}s");
        phaseSw.Restart();
        var replacementEntries = DiscoverReplacementMeshEntries(projectDir, replacementRoot, config.GetCachePath(), gameDataPath, assetPipeline);
        var replacementTextures = ReplacementDiscovery.DiscoverReplacementTextureEntries(replacementRoot, config.GetCachePath(), _log);
        var spriteDiscovery = ReplacementDiscovery.DiscoverReplacementSpriteEntries(replacementRoot, config.GetCachePath(), _log);
        var replacementSprites = spriteDiscovery.UniqueSprites;
        var replacementAudio = ReplacementDiscovery.DiscoverReplacementAudioEntries(replacementRoot, config.GetCachePath(), _log);

        // Asset additions: files under assets/additions/<category>/ are
        // bundled with their relative path (extension stripped) as the
        // Unity Object name, so a template's asset="item/fancy-pen-icon"
        // resolves to the bundle entry of the same name.
        replacementTextures.AddRange(ReplacementDiscovery.DiscoverAdditionTextureEntries(additionRoot));
        replacementSprites.AddRange(ReplacementDiscovery.DiscoverAdditionSpriteEntries(additionRoot));
        replacementAudio.AddRange(ReplacementDiscovery.DiscoverAdditionAudioEntries(additionRoot));

        // Atlas compositing: for atlas-backed sprite replacements, composite the modder's
        // images into a copy of the original atlas and emit as Texture2D replacements.
        if (spriteDiscovery.AtlasGroups.Count > 0)
        {
            var atlasTextures = AtlasCompositor.Composite(
                spriteDiscovery.AtlasGroups,
                replacementTextures,
                (name, collection, pathId) => LoadAtlasBitmap(assetPipeline, name, collection, pathId, _log),
                _log);
            replacementTextures.AddRange(atlasTextures);
        }

        var totalSpriteCount = replacementSprites.Count + spriteDiscovery.AtlasGroups.Sum(g => g.Replacements.Count);

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
        var replacementAssetCount = replacementEntries.Count + replacementTextures.Count + totalSpriteCount + replacementAudio.Count;
        var totalCompileInputCount = modelFiles.Count + replacementTextures.Count + replacementSprites.Count + replacementAudio.Count;

        // Discover KDL template authoring files
        var templatesDir = Path.Combine(projectDir, "templates");
        var hasKdlTemplates = Directory.Exists(templatesDir)
            && Directory.EnumerateFiles(templatesDir, "*.kdl", SearchOption.AllDirectories).Any();

        // A localisation-only mod (e.g. one that only translates another mod) ships just locales/*.po
        // and must still compile so EmitLocalisationAsync emits its tables.
        var localesDir = Path.Combine(projectDir, LocaleLayout.SourceDirName);
        var hasLocales = Directory.Exists(localesDir)
            && Directory.EnumerateFiles(localesDir, "*.po", SearchOption.AllDirectories).Any();

        if (totalCompileInputCount == 0 && !hasKdlTemplates && !hasLocales)
        {
            _log.Info("No replacement assets or template patches found. Nothing to compile.");
            return new CompilationResult { Success = true };
        }

        _log.Info($"Compiling {manifest.Name}...");
        _log.Info(
            $"  {modelFiles.Count} model file(s), {replacementTextures.Count} texture replacement(s), {totalSpriteCount} sprite replacement(s), {replacementAudio.Count} audio replacement(s), {replacementEntries.Count} replacement mesh target(s)");

        var bundleName = manifest.Name.ToLowerInvariant().Replace(" ", "_");
        // Start every compile from a clean output tree: everything under compiled/ is build
        // output regenerated below, so wiping first is the one step that clears whatever a
        // previous compile left (a removed bundle, a now-empty templates.json, a renamed code
        // DLL, an older flat layout) instead of a separate stale-delete per artefact.
        var outputDir = CompiledOutput.Reset(projectDir);
        var bundlesDir = Path.Combine(outputDir, Jiangyu.Shared.Bundles.CompiledLayout.BundlesDirName);
        var userUnityDir = Path.Combine(projectDir, "unity");
        var unityBuildDir = Path.Combine(projectDir, ".jiangyu", "unity_build");
        var builtBundle = Path.Combine(unityBuildDir, bundleName);

        // Incremental build state: per-phase input fingerprints from the last successful
        // compile. --clean starts empty (rebuilds everything) and overwrites the file.
        // compiled/ is wiped and reassembled every compile regardless, so incremental only
        // skips expensive generation (the Unity prefab batchmode) when its inputs are
        // unchanged and its cached output is still present, never reuses a stale compiled/.
        var buildState = input.Clean ? new BuildState() : BuildState.Load(projectDir);

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

        // Build the mod's code/ C# project (if present), injecting game + SDK
        // paths so a fresh clone compiles without `code sync` first, then
        // cross-check every type="ns:Name" against the [JiangyuType]s it produced.
        var gameRoot = Path.GetDirectoryName(gameDataPath)!;
        var (codeSdkDir, _) = GlobalConfig.ResolveSdkDir(config);
        var codeBuild = await new CodeBuildService(_log).BuildAsync(projectDir, gameRoot, codeSdkDir, devSources: !input.Release);
        if (codeBuild is { Success: false })
            return Fail(codeBuild.Error!);

        var knownCodeTypes = codeBuild?.JiangyuTypeNames ?? (IReadOnlySet<string>)new HashSet<string>();
        var codeTypeErrors = ModCodeValidation.Validate(templatePatchResult.Patches, knownCodeTypes, _log);
        if (codeTypeErrors > 0)
            return Fail($"Code type validation failed with {codeTypeErrors} error(s). See errors above.");

        if (codeBuild is { Success: true })
        {
            var codeOutputDir = Path.Combine(outputDir, Jiangyu.Shared.Bundles.CompiledLayout.CodeDirName);
            Directory.CreateDirectory(codeOutputDir);
            foreach (var dll in codeBuild.DllPaths)
                File.Copy(dll, Path.Combine(codeOutputDir, Path.GetFileName(dll)), overwrite: true);
            _log.Info($"  Packaged {codeBuild.DllPaths.Count} code dll(s) -> {codeOutputDir}");
        }

        // Catalog-aware semantic validation: unknown template types / fields,
        // op-shape rules that depend on collection-vs-scalar (Set on a whole
        // collection, etc.), and NamedArray restrictions. The supplement is
        // optional — when it's present, NamedArray pairings flow through and
        // append/insert/remove on those fields fail loudly here too.
        var assemblyDir = Path.GetDirectoryName(gameDataPath);
        if (assemblyDir is not null
            && (templatePatchResult.Patches?.Count > 0 || templateCloneResult.Clones?.Count > 0))
        {
            var assemblyPath = Path.Combine(assemblyDir, "MelonLoader", "Il2CppAssemblies", "Assembly-CSharp.dll");
            if (File.Exists(assemblyPath))
            {
                var additionalSearchDirs = new List<string>();
                var melonNet6 = Path.Combine(assemblyDir, "MelonLoader", "net6");
                if (Directory.Exists(melonNet6))
                    additionalSearchDirs.Add(melonNet6);

                // Scan the mod's just-built code DLLs into the catalog so a type="ns:Name"
                // construction resolves to its [JiangyuType] and its fields are validated
                // (own plus inherited game members) rather than skipped.
                IReadOnlyList<string> codeAssemblies = codeBuild?.DllPaths ?? [];
                if (codeAssemblies.Count > 0)
                {
                    additionalSearchDirs.Add(Path.GetDirectoryName(codeAssemblies[0])!);
                    if (codeSdkDir is not null)
                        additionalSearchDirs.Add(codeSdkDir);
                }

                var supplement = Il2CppMetadataCache.LoadIfPresent(input.Config.GetCachePath());
                using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs, supplement, codeAssemblies);
                var additionsCatalog = new FileSystemAssetAdditionsCatalog(
                    additionRoot,
                    unityPrefabsDir: Path.Combine(projectDir, "unity", "Assets", "Prefabs"));
                foreach (var conflict in additionsCatalog.ConflictingNames)
                {
                    _log.Error($"Asset addition '{conflict}': two files share the same logical name in the same category. Rename one or remove the duplicate.");
                }
                // Load the vanilla game asset index so the validator also
                // accepts asset="..." references targeting in-game assets,
                // not just mod-shipped additions. Null when the index hasn't
                // been built yet — validator falls back to additions-only.
                var loadedAssetIndex = assetPipeline.LoadIndex();
                IGameAssetIndex? gameAssetIndex = loadedAssetIndex != null
                    ? new GameAssetIndex(loadedAssetIndex)
                    : null;

                // Build a SoundBank-name→bankId resolver from the asset index
                // for string-keyed Stem.ID.bankId fields. The names and ids
                // come from the asset index's per-SoundBank BankId metadata,
                // populated by the indexer's pre-pass. When the index is
                // unavailable we pass null; modders writing string-keyed
                // bankIds get a clean error instead of a stale hardcoded
                // answer.
                //
                // Mod-defined SoundBank clones are added too: their bankId
                // is FNV-1a(cloneId), the same hash the loader will apply
                // when patching the clone. Lets cross-references inside the
                // same compile unit (a SAY node's Sound.bankId pointing at
                // a freshly-cloned SoundBank) resolve at validate time.
                IBankIdResolver? bankIdResolver;
                {
                    var bankPairs = new List<KeyValuePair<string, int>>();
                    if (loadedAssetIndex?.Assets is { } indexedAssets)
                    {
                        bankPairs.AddRange(indexedAssets
                            .Where(a => a.Name != null && a.SoundBank?.BankId is not null)
                            .Select(a => new KeyValuePair<string, int>(a.Name!, a.SoundBank!.BankId!.Value)));
                    }
                    if (templateCloneResult.Clones is { } clones)
                    {
                        foreach (var clone in clones)
                        {
                            if (string.Equals(clone.TemplateType, "SoundBank", StringComparison.Ordinal)
                                && !string.IsNullOrEmpty(clone.CloneId))
                            {
                                bankPairs.Add(new KeyValuePair<string, int>(
                                    clone.CloneId,
                                    HashableIdFieldRegistry.Fnv1a32(clone.CloneId)));
                            }
                        }
                    }
                    bankIdResolver = new InMemoryBankIdResolver(bankPairs);
                }

                // Install the indexed tagged-discriminator allowlist so
                // ResolveTaggedDiscriminator only accepts vanilla forms.
                TaggedDiscriminatorIndex.Install(loadedAssetIndex?.TaggedDiscriminators);

                // Resolve modder-authored RoleGuid strings (e.g.
                // `set "RoleGuid" "Entity"` in a SAY composite) against
                // the source conversation's Roles list before the catalog
                // validator's numeric-coercion pass sees them. The pass
                // mutates compiled values in place: String → Int32.
                var roleResolveErrors = RoleGuidResolver.Apply(
                    templatePatchResult.Patches,
                    templateCloneResult.Clones,
                    loadedAssetIndex?.Assets,
                    _log);
                if (roleResolveErrors > 0)
                    return Fail($"Template validation failed with {roleResolveErrors} error(s). See errors above.");

                // Pre-validation auto-fills: clone-identity (bankId/Path)
                // injection. Patch-level — TemplateType + TemplateId are
                // already known from parse, no validator-side inference
                // needed.
                CompositeAutoFillers.ApplyPreValidation(templatePatchResult.Patches);

                var catalogErrors = TemplateCatalogValidator.Validate(
                    templatePatchResult.Patches,
                    templateCloneResult.Clones,
                    catalog,
                    _log,
                    additionsCatalog,
                    gameAssetIndex,
                    bankIdResolver: bankIdResolver);
                if (catalogErrors > 0 || additionsCatalog.ConflictingNames.Count > 0)
                    return Fail($"Template validation failed with {catalogErrors + additionsCatalog.ConflictingNames.Count} error(s). See errors above.");

                // Inject deterministic Guids on ConversationNode/Container
                // composites that the modder didn't number. Vanilla data
                // carries arbitrary editor-allocated ints; clones need
                // distinct values to avoid colliding with the source.
                NodeGuidAutoFiller.Apply(templatePatchResult.Patches, catalog);

                // Post-validation auto-fills that key off composite TypeName
                // (Stem.Sound.id from name; VariationCopyCount parallel-
                // array sync). Inferred composites only get a TypeName
                // after the validator's pass, so these fillers run last.
                CompositeAutoFillers.ApplyPostValidation(templatePatchResult.Patches);
            }
        }
        _log.Info($"  [timing] Template compilation: {phaseSw.Elapsed.TotalSeconds:F1}s");

        var compiledManifest = ModManifest.FromJson(manifest.ToJson());
        compiledManifest.CompiledForUnity = gameVersion?.ToString();
        compiledManifest.CompiledForJiangyu = JiangyuVersion.Current;
        // `imports` is a compile-time build input (which host rips to pull before the
        // bake), not runtime metadata. The loader never reads it, so keep it out of the
        // shipped manifest rather than leaking host-asset names into every distribution.
        compiledManifest.Imports = null;

        // The compiled template program ships beside the manifest in its own
        // templates.json, so jiangyu.json stays a lean identity record the loader scans
        // cheaply. Written only when there's something to ship; the clean output tree means
        // there is never a stale file to clear.
        var compiledTemplates = new CompiledTemplatePatchManifest
        {
            TemplatePatches = templatePatchResult.Patches,
            TemplateClones = templateCloneResult.Clones,
        };
        if (!compiledTemplates.IsEmpty)
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, CompiledTemplatePatchManifest.FileName),
                compiledTemplates.ToJson());

        await EmitLocalisationAsync(projectDir, outputDir, manifest.Name, compiledTemplates);

        // Detect the combined-Unity-pass case: a mod with both addition
        // prefabs AND mesh-replacement work pays two cold starts today. When
        // both have work we fold the prefab pass into the first Unity call
        // of GlbMeshBundleCompiler.BuildAsync below (one launch instead of
        // two); Stage runs after that returns. Otherwise the standalone
        // BuildUnityAdditionPrefabs runs here as before.
        var hasPrefabWork = AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(projectDir);
        var combinedUnityPass = hasPrefabWork && useRawGlbPipeline;

        const string PrefabPhase = "prefabs";
        const string AssetsPhase = "assets";
        // The unity/ project (editor scripts plus any prefabs) feeds both the prefab and the
        // raw-GLB passes, so fingerprint it once and reuse. Only computed when a pass that
        // reads it will run, since hashing the tree (which can include large imported rips)
        // is wasted otherwise.
        var unityInputsFingerprint = (useRawGlbPipeline || hasPrefabWork)
            ? PrefabInputsFingerprint(userUnityDir, gameVersion?.ToString())
            : string.Empty;
        // Reuse the raw-GLB Unity pass (the texture/sprite/mesh bundle, plus the folded prefab
        // build in the combined case) when its inputs are unchanged and the cached outputs
        // survive. This Unity cold start is the bulk of compile time, so skipping it is the
        // main incremental win. The mesh metadata it produces is cached alongside the
        // fingerprint and restored on reuse. --clean starts from an empty build state.
        var assetsFingerprint = useRawGlbPipeline
            ? AssetInputsFingerprint(projectDir, unityInputsFingerprint)
            : string.Empty;
        var reuseRawGlb = useRawGlbPipeline
            && buildState.Matches(AssetsPhase, assetsFingerprint)
            && File.Exists(builtBundle)
            && (!combinedUnityPass || (Directory.Exists(unityBuildDir) && Directory.EnumerateFiles(unityBuildDir, "*.bundle").Any()));
        if (!combinedUnityPass)
        {
            // Skip the prefab batchmode (a Unity cold start) when its inputs are unchanged
            // since the last successful compile and its cached bundles are still present.
            // Staging below always reruns from the cached output, so compiled/ is reassembled
            // either way; only the slow Unity invocation is avoided.
            var prefabFingerprint = hasPrefabWork ? unityInputsFingerprint : string.Empty;
            var reuseCachedPrefabs = hasPrefabWork
                && buildState.Matches(PrefabPhase, prefabFingerprint)
                && Directory.Exists(unityBuildDir)
                && Directory.EnumerateFiles(unityBuildDir, "*.bundle").Any();

            if (reuseCachedPrefabs)
            {
                _log.Info("  Incremental: prefab inputs unchanged, reusing cached bundles.");
            }
            else
            {
                await BuildUnityAdditionPrefabs(projectDir, unityBuildDir, unityEditorPath);
                if (hasPrefabWork)
                    buildState.Record(PrefabPhase, prefabFingerprint);
                else
                    buildState.Remove(PrefabPhase);
            }

            AdditionPrefabStaging.Stage(
                sourceDirs: [Path.Combine(additionRoot, Jiangyu.Shared.Replacements.AssetCategory.Prefabs), unityBuildDir],
                bundlesDir,
                compiledManifest,
                gameDataPath,
                _log);
        }
        else if (!reuseRawGlb)
        {
            // The folded prefab build is about to rerun in the raw-GLB pass; clear its stale
            // output first. When reusing, keep the cached prefab bundles for staging.
            AdditionPrefabStaging.ClearStaleBuildOutput(unityBuildDir);
        }

        // Template-only compile: no asset replacements, just emit the compiled manifest.
        if (totalCompileInputCount == 0)
        {
            await File.WriteAllTextAsync(Path.Combine(outputDir, ModManifest.FileName), compiledManifest.ToJson());
            buildState.Save(projectDir);
            _log.Info($"  -> {Path.Combine(outputDir, ModManifest.FileName)}");
            _log.Info("Done.");
            return new CompilationResult { Success = true };
        }

        if (useRawGlbPipeline)
        {
            if (reuseRawGlb)
            {
                _log.Info("  Incremental: asset inputs unchanged, reusing cached bundles.");
                compiledManifest.Meshes = buildState.GetData<Dictionary<string, MeshManifestEntry>>(AssetsPhase)
                    ?? new Dictionary<string, MeshManifestEntry>(StringComparer.Ordinal);
            }
            else
            {
                _log.Info("  Using direct mesh replacement pipeline...");
                phaseSw.Restart();
                Directory.CreateDirectory(unityBuildDir);
                var targetMeshNamesByBundleMesh = replacementEntries
                    .Where(entry => !entry.SuppressMeshContract)
                    .ToDictionary(entry => entry.BundleMeshName, entry => entry.TargetMeshName, StringComparer.Ordinal);

                try
                {
                    // The cold-project batchmode retry lives inside the Unity invoker
                    // (UnityBundleInvoker), so it fires per Unity pass here, before the mesh
                    // contract extractor or a second pass depends on the pass-1 bundle.
                    var buildResult = await GlbMeshBundleCompiler.BuildAsync(
                        unityEditorPath,
                        userUnityDir,
                        bundleName,
                        builtBundle,
                        replacementEntries,
                        replacementTextures,
                        replacementSprites,
                        replacementAudio,
                        gameDataPath,
                        targetMeshNamesByBundleMesh,
                        runPrefabs: combinedUnityPass,
                        log: _log);
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
                            }
                        };
                    }
                }
                catch (Exception ex)
                {
                    return Fail($"Direct mesh replacement pipeline failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                _log.Info($"  [timing] Asset bundle build: {phaseSw.Elapsed.TotalSeconds:F1}s");

                // Cache the mesh metadata so a later reuse can restore it without the Unity pass.
                buildState.Record(AssetsPhase, assetsFingerprint, compiledManifest.Meshes);
            }

            if (combinedUnityPass)
            {
                AdditionPrefabStaging.Stage(
                    sourceDirs: [Path.Combine(additionRoot, Jiangyu.Shared.Replacements.AssetCategory.Prefabs), unityBuildDir],
                    bundlesDir,
                    compiledManifest,
                    gameDataPath,
                    _log);
                // Prefabs ship from this same pass; record their fingerprint so a later
                // non-combined compile can reuse the cached bundles too.
                buildState.Record(PrefabPhase, unityInputsFingerprint);
            }
        }
        else
        {
            await StageRawModelFiles(userUnityDir, modelFiles);

            // The cold-project batchmode retry lives inside the Unity invoker, keyed on the
            // expected bundle path (set in InvokeUnityBuildForModels).
            var success = await InvokeUnityBuildForModels(unityEditorPath, userUnityDir, bundleName);
            if (!success)
                return Fail("Unity build failed. Check logs for details.");
        }

        // Collect output
        if (!File.Exists(builtBundle))
            return Fail(DescribeMissingBundle(projectDir, builtBundle, unityBuildDir, useRawGlbPipeline));

        var destBundle = Path.Combine(bundlesDir, $"{bundleName}.bundle");
        File.Copy(builtBundle, destBundle, overwrite: true);

        // Copy manifest alongside the bundle
        await File.WriteAllTextAsync(Path.Combine(outputDir, ModManifest.FileName), compiledManifest.ToJson());
        buildState.Save(projectDir);

        _log.Info($"  -> {destBundle}");
        _log.Info($"  [timing] Total: {totalSw.Elapsed.TotalSeconds:F1}s");
        _log.Info("Done.");

        return new CompilationResult { Success = true, BundlePath = destBundle };
    }

    // Relative paths under unity/Assets/ that Unity or Jiangyu regenerate during compile, so they
    // must NOT feed the input fingerprint: Assets/Jiangyu/Staging/ is repopulated every compile from
    // assets/additions/ (already hashed via AssetInputsFingerprint, so hashing it here is both
    // redundant AND unstable — its Unity .meta GUIDs and staged copies change run-to-run, which
    // otherwise defeats incremental reuse entirely), and Library/Temp/obj are editor caches.
    // Assets/Imported/ is left in: it is a real mesh-replacement input and only changes when the
    // mod's import list does.
    private static bool IsRegeneratedAssetPath(string relative)
        => relative.StartsWith("Jiangyu/Staging/", StringComparison.Ordinal)
            || relative.StartsWith("Library/", StringComparison.Ordinal)
            || relative.StartsWith("Temp/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);

    // Fingerprint of the inputs Unity reads when building the addition-prefab bundles: the
    // mod's unity/ project content. Build-generated subtrees (Library/, Temp/, build.log in the
    // unity/ root, plus Assets/Jiangyu/Staging/ and editor caches under Assets/) are excluded so
    // an unchanged source tree yields a stable fingerprint and incremental reuse can hit.
    private static string PrefabInputsFingerprint(string unityDir, string? gameVersion)
        => FileFingerprint.Combine(
            FileFingerprint.OfDirectory(Path.Combine(unityDir, "Assets"), IsRegeneratedAssetPath),
            FileFingerprint.OfDirectory(Path.Combine(unityDir, "ProjectSettings")),
            FileFingerprint.OfFile(Path.Combine(unityDir, "Packages", "manifest.json")),
            // Bundle format tracks the Unity editor (resolved from the game's Unity version),
            // and the cached mesh metadata schema and bundle-build logic track the Jiangyu
            // toolchain version, so a game or compiler upgrade must invalidate the cache even
            // when the mod's own files are unchanged.
            gameVersion ?? string.Empty,
            JiangyuVersion.Current);

    // Fingerprint of the inputs the raw-GLB Unity pass reads: the mod's authored assets plus
    // the (already-computed) unity/ project fingerprint, which carries the game and toolchain
    // versions.
    private static string AssetInputsFingerprint(string projectDir, string unityInputsFingerprint)
        => FileFingerprint.Combine(
            FileFingerprint.OfDirectory(Path.Combine(projectDir, "assets")),
            unityInputsFingerprint);

    /// <summary>
    /// Emits the localisation outputs under <c>compiled/locales/</c>: the source catalogue
    /// (<c>&lt;mod&gt;.pot</c>) extracted from the compiled template program, and one
    /// <c>&lt;code&gt;.json</c> translation table plus optional <c>&lt;code&gt;.ui.json</c> map
    /// per locale found under the mod's <c>locales/</c> folder. PO files are scanned recursively
    /// so a localisation-only mod can carry one target per subfolder. Multiple PO files sharing a
    /// locale code merge into one table.
    /// </summary>
    private async Task EmitLocalisationAsync(
        string projectDir, string outputDir, string modName, CompiledTemplatePatchManifest compiledTemplates)
    {
        var catalogue = LocalisationCompiler.ExtractCatalogue(compiledTemplates, modName, out var skippedNested);
        await AddUiKeysFromSourceAsync(projectDir, catalogue);

        var sourceDir = Path.Combine(projectDir, LocaleLayout.SourceDirName);
        var sourcePoFiles = Directory.Exists(sourceDir)
            ? Directory.EnumerateFiles(sourceDir, "*.po", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];

        if (catalogue.Entries.Count == 0 && sourcePoFiles.Count == 0)
            return;

        var localesOut = Path.Combine(outputDir, CompiledLayout.LocalesDirName);
        Directory.CreateDirectory(localesOut);

        if (catalogue.Entries.Count > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(localesOut, LocaleLayout.CatalogueFileName(modName)),
                PoFormat.Write(catalogue));
            var skipNote = skippedNested > 0 ? $" ({skippedNested} string(s) not extractable)" : string.Empty;
            _log.Info($"  Localisation: {catalogue.Entries.Count} translatable string(s) -> {LocaleLayout.CatalogueFileName(modName)}{skipNote}");
        }

        // Stage each hand-written .po into compiled/locales/ (preserving subfolders) so it deploys
        // with the mod. The loader reads the .po directly, so there is no compiled table. Compiling
        // here only validates the keys and copies the file.
        foreach (var poPath in sourcePoFiles)
        {
            var code = Path.GetFileNameWithoutExtension(poPath);
            if (!LocaleCatalogue.IsKnownCode(code))
            {
                _log.Warning($"  Localisation: '{Path.GetRelativePath(projectDir, poPath)}' is not a known locale code; skipping.");
                continue;
            }

            var result = LocaleTable.Compile(await File.ReadAllTextAsync(poPath));
            if (result.Malformed > 0)
                _log.Warning($"  Localisation: {result.Malformed} entry(ies) in '{Path.GetFileName(poPath)}' had an unparseable key.");

            var relative = Path.GetRelativePath(sourceDir, poPath);
            var dest = Path.Combine(localesOut, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(poPath, dest, overwrite: true);

            var fieldCount = (result.Translations.TemplatePatches ?? []).Sum(p => p.Set.Count);
            _log.Info($"  Localisation: staged {relative} ({fieldCount} translated field(s), {result.Ui.Count} UI string(s)).");
        }
    }

    // Add UI strings to the catalogue so code and UXML strings appear in the POT for translators
    // without a separate authoring step: literal Locale.Text("key","fallback") calls under code/, and
    // name="@key"-marked elements under unity/. Deduped against entries already present.
    private static async Task AddUiKeysFromSourceAsync(string projectDir, PoFile catalogue)
    {
        var seen = new HashSet<string>(
            catalogue.Entries.Where(e => e.Context != null).Select(e => e.Context!), StringComparer.Ordinal);

        void Add(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
                return;
            var entry = new PoEntry { Context = key, Id = fallback, Str = string.Empty };
            entry.ExtractedComments.Add($"UI · {key}");
            catalogue.Entries.Add(entry);
        }

        async Task ScanAsync(string subDir, string glob, Func<string, IEnumerable<(string Key, string Fallback)>> extract)
        {
            var dir = Path.Combine(projectDir, subDir);
            if (!Directory.Exists(dir))
                return;
            foreach (var file in Directory.EnumerateFiles(dir, glob, SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                string text;
                try { text = await File.ReadAllTextAsync(file); }
                catch { continue; }
                foreach (var (key, fallback) in extract(text))
                    Add(key, fallback);
            }
        }

        await ScanAsync("code", "*.cs", LocalisationCompiler.ExtractUiKeys);
        await ScanAsync("unity", "*.uxml", LocalisationCompiler.ExtractUxmlUiKeys);
    }

    /// <summary>
    /// Invokes Unity batchmode against the modder's <c>unity/</c> project to
    /// build each prefab under <c>Assets/Prefabs/</c> into its own AssetBundle.
    /// Bundles land in <c>&lt;projectDir&gt;/.jiangyu/unity_build/</c> where
    /// <see cref="AdditionPrefabStaging.Stage"/> picks them up. No-op when
    /// <see cref="AdditionPrefabStaging.ShouldInvokeUnityForPrefabs"/> returns
    /// false.
    /// </summary>
    private async Task BuildUnityAdditionPrefabs(string projectDir, string unityBuildOutputDir, string unityEditorPath)
    {
        AdditionPrefabStaging.ClearStaleBuildOutput(unityBuildOutputDir);

        if (!AdditionPrefabStaging.ShouldInvokeUnityForPrefabs(projectDir))
            return;

        var unityProjectDir = Path.Combine(projectDir, "unity");
        var prefabsDir = Path.Combine(unityProjectDir, "Assets", "Prefabs");
        var prefabFiles = Directory.EnumerateFiles(prefabsDir, "*.prefab", SearchOption.AllDirectories).ToArray();

        _log.Info($"  Building {prefabFiles.Length} prefab(s) from unity/Assets/Prefabs/...");

        var logFile = Path.Combine(unityProjectDir, "build.log");
        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = unityEditorPath,
            ProjectPath = unityProjectDir,
            ExecuteMethod = "Jiangyu.Mod.BuildBundles.BuildAll",
            LogFile = logFile,
        });

        if (!result.Success)
        {
            _log.Error($"Unity exited with code {result.ExitCode} building unity/ prefab bundles.");
            _log.Error($"  Log: {logFile}");
            foreach (var line in result.LogTailLines)
                _log.Error($"  {line}");
            throw new InvalidOperationException("Unity batchmode build for unity/ prefab bundles failed.");
        }

        var builtBundles = Directory.EnumerateFiles(unityBuildOutputDir, "*.bundle").Count();
        _log.Info($"  Unity built {builtBundles} prefab bundle(s) into {unityBuildOutputDir}");
    }

    internal static bool HasAnyAssetSources(string replacementRoot, string additionRoot)
    {
        return DirectoryHasAnyFile(replacementRoot) || DirectoryHasAnyFile(additionRoot);
    }

    private static bool DirectoryHasAnyFile(string path)
    {
        return Directory.Exists(path)
            && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
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
            if (!ReplacementPaths.TryParseReplacementAlias(targetAlias, out var targetName, out var targetPathId))
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
            var providedMeshCandidates = MeshRendererPathResolver.DiscoverReplacementMeshPathCandidates(file);
            var expectedTargets = AssetInspector.GetSkinnedMeshTargetsForIndexedObject(gameDataPath, target.Collection, target.PathId);
            if (expectedTargets.Count == 0)
                throw new InvalidOperationException(
                    $"Replacement target '{targetAlias}' [{target.CanonicalPath ?? $"{target.Collection}:{target.PathId}"}] has no skinned mesh renderer targets to replace.");
            var emitMeshDiscoveryDiagnostics = MeshRendererPathResolver.IsDiagnosticsEnabled();
            var ambiguousRuntimeMeshNames = GetAmbiguousRuntimeMeshNames(
                index,
                [.. expectedTargets
                    .Select(x => x.MeshName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)]);

            var resolvedTargets = MeshRendererPathResolver.ResolveReplacementRendererTargets(expectedTargets, providedMeshCandidates, out var missingTargetPaths);
            if (resolvedTargets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Replacement model '{Path.GetRelativePath(projectDir, file)}' does not contain any renderer paths that match target '{targetAlias}'. " +
                    $"Expected at least one of: {MeshRendererPathResolver.FormatNameListPreview(expectedTargets.Select(x => x.RendererPath), 16)}");
            }

            MeshRendererPathResolver.ValidateReplacementMeshPrimitiveContract(file, bindPoseReferencePath, resolvedTargets);

            if (emitMeshDiscoveryDiagnostics && missingTargetPaths.Length > 0)
            {
                _log.Warning(
                    $"Replacement target '{targetAlias}' has renderer paths with no replacement in '{Path.GetRelativePath(projectDir, file)}'. " +
                    $"Original game meshes will be kept for: {MeshRendererPathResolver.FormatNameListPreview(missingTargetPaths, 24)}");
            }

            if (emitMeshDiscoveryDiagnostics && ambiguousRuntimeMeshNames.Count > 0)
            {
                _log.Warning(
                    $"Replacement target '{targetAlias}' includes runtime-ambiguous mesh name(s): {MeshRendererPathResolver.FormatNameListPreview(ambiguousRuntimeMeshNames, 24)}. " +
                    "Jiangyu will skip mesh-contract injection for those meshes and rely on runtime live-skeleton binding.");
            }

            foreach (var resolved in resolvedTargets)
            {
                var bundleMeshName = MeshRendererPathResolver.BuildBundleMeshName(resolved.TargetRendererPath, resolved.TargetMeshName);
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

    internal static AssetIndex? LoadAssetIndex(string cachePath)
    {
        var indexPath = Path.Combine(cachePath, AssetIndexFileName);
        if (!File.Exists(indexPath))
            return null;

        return JsonSerializer.Deserialize<AssetIndex>(File.ReadAllText(indexPath));
    }

    /// <summary>
    /// Loads the original atlas Texture2D from game data and returns its decoded RGBA32 pixels.
    /// Used by atlas compositing to obtain the base image that sprite replacements are
    /// composited into.
    /// </summary>
    private static (int Width, int Height, byte[] Rgba)? LoadAtlasBitmap(
        AssetPipelineService assetPipeline,
        string atlasName,
        string collection,
        long pathId,
        ILogSink log)
    {
        try
        {
            return assetPipeline.LoadTexture2dRgba(atlasName, collection, pathId);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to load atlas texture '{atlasName}' (pathId={pathId}): {ex.Message}");
            return null;
        }
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

    internal static ReplacementTextureTarget ResolveReplacementTextureTarget(AssetIndex index, string alias, string targetName, long? targetPathId, ILogSink log)
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
            var canonicalPaths = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Texture2D/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            log.Warning(
                $"Replacement '{targetName}' will paint {candidates.Count} Texture2D instances: {string.Join(", ", canonicalPaths)}");
        }

        // The runtime paints every loaded Texture2D whose `name` matches, so the chosen
        // canonical entry only affects compile-log identity strings. Pick lowest pathId
        // for deterministic output.
        var canonical = candidates.OrderBy(entry => entry.PathId).First();
        return new ReplacementTextureTarget(alias, canonical.Name ?? targetName, canonical.PathId, canonical.Collection ?? string.Empty, canonical.CanonicalPath);
    }

    private static ReplacementAudioTarget ResolveReplacementAudioTarget(AssetIndex index, string alias, string targetName, long? targetPathId, ILogSink log)
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
            var canonicalPaths = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/AudioClip/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            log.Warning(
                $"Replacement '{targetName}' will substitute {candidates.Count} AudioClip instances: {string.Join(", ", canonicalPaths)}");
        }

        // Runtime substitution is by `clip.name`, so the chosen canonical only labels
        // log output. Lowest pathId for deterministic builds.
        var canonical = candidates.OrderBy(entry => entry.PathId).First();
        return new ReplacementAudioTarget(alias, canonical.Name ?? targetName, canonical.PathId, canonical.Collection ?? string.Empty, canonical.CanonicalPath);
    }

    private static ReplacementSpriteTarget ResolveReplacementSpriteTarget(AssetIndex index, string alias, string targetName, long? targetPathId, ILogSink log)
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
            var canonicalPaths = candidates
                .Select(entry => entry.CanonicalPath ?? $"{entry.Collection}/Sprite/{entry.Name}--{entry.PathId}")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            log.Warning(
                $"Replacement '{targetName}' will paint {candidates.Count} Sprite instances: {string.Join(", ", canonicalPaths)}");
        }

        // Runtime resolution is by `sprite.name`, so the canonical entry is purely a label.
        // Lowest pathId for deterministic builds.
        var canonical = candidates.OrderBy(entry => entry.PathId).First();
        return new ReplacementSpriteTarget(alias, canonical.Name ?? targetName, canonical.PathId, canonical.Collection ?? string.Empty, canonical.CanonicalPath);
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

    /// <summary>
    /// Classifies whether a sprite target's backing Texture2D is unique (no co-tenants)
    /// or atlas-backed (shared with other sprites). Returns the classification with the
    /// backing texture identity and co-tenant count. For atlas-backed sprites, the caller
    /// routes through compile-time atlas compositing instead of rejecting.
    /// </summary>
    internal static SpriteBackingClassification ClassifySpriteBackingTexture(AssetIndex index, ReplacementSpriteTarget target)
    {
        var targetEntry = index.Assets?.FirstOrDefault(entry =>
            string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
            string.Equals(entry.Name, target.Name, StringComparison.Ordinal) &&
            entry.PathId == target.PathId);

        if (targetEntry is null)
            return new SpriteBackingClassification { IsAtlasBacked = false };

        var spriteMeta = targetEntry.Sprite;
        if (spriteMeta?.BackingTexturePathId is null)
        {
            throw new InvalidOperationException(
                $"Replacement sprite target '{target.Alias}' is missing backing-texture information in the asset index. " +
                "Rebuild the index with 'jiangyu assets index' so the compiler can verify sprite atlas membership. " +
                "Sprite replacement requires backing-texture identity for atlas compositing.");
        }

        if (spriteMeta.TextureRectWidth is null || spriteMeta.TextureRectHeight is null)
        {
            throw new InvalidOperationException(
                $"Replacement sprite target '{target.Alias}' is missing textureRect metadata in the asset index. " +
                "Rebuild the index with 'jiangyu assets index' (the index format includes sprite atlas metadata).");
        }

        var backingPathId = spriteMeta.BackingTexturePathId.Value;
        var backingCollection = spriteMeta.BackingTextureCollection;

        var coTenants = index.Assets?
            .Where(entry =>
                string.Equals(entry.ClassName, "Sprite", StringComparison.Ordinal) &&
                entry.Sprite?.BackingTexturePathId == backingPathId &&
                string.Equals(entry.Sprite?.BackingTextureCollection, backingCollection, StringComparison.Ordinal) &&
                !(entry.PathId == target.PathId &&
                  string.Equals(entry.Collection, target.Collection, StringComparison.Ordinal)))
            .ToList()
            ?? [];

        return new SpriteBackingClassification
        {
            IsAtlasBacked = coTenants.Count > 0,
            BackingTexturePathId = backingPathId,
            BackingTextureCollection = backingCollection,
            BackingTextureName = spriteMeta.BackingTextureName,
            CoTenantCount = coTenants.Count,
            TextureRectX = spriteMeta.TextureRectX!.Value,
            TextureRectY = spriteMeta.TextureRectY!.Value,
            TextureRectWidth = spriteMeta.TextureRectWidth!.Value,
            TextureRectHeight = spriteMeta.TextureRectHeight!.Value,
            PackingRotation = spriteMeta.PackingRotation ?? 0,
        };
    }

    /// <summary>Result of <see cref="ClassifySpriteBackingTexture"/>.</summary>
    internal sealed class SpriteBackingClassification
    {
        public bool IsAtlasBacked { get; init; }
        public long BackingTexturePathId { get; init; }
        public string? BackingTextureCollection { get; init; }
        public string? BackingTextureName { get; init; }
        public int CoTenantCount { get; init; }
        public float TextureRectX { get; init; }
        public float TextureRectY { get; init; }
        public float TextureRectWidth { get; init; }
        public float TextureRectHeight { get; init; }
        public int PackingRotation { get; init; }
    }

    internal static (ReplacementSpriteTarget Target, SpriteBackingClassification Classification) ResolveAndClassifySpriteTarget(
        AssetIndex index,
        string alias,
        string targetName,
        long? targetPathId,
        ILogSink log)
    {
        var target = ResolveReplacementSpriteTarget(index, alias, targetName, targetPathId, log);
        var classification = ClassifySpriteBackingTexture(index, target);
        return (target, classification);
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
        var audioMeta = targetEntry.Audio;
        if (audioMeta?.Frequency is null || audioMeta.Channels is null) return;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".wav") return;

        if (!TryReadWavHeader(filePath, out var sourceFrequency, out var sourceChannels))
            return;

        if (sourceFrequency == audioMeta.Frequency.Value && sourceChannels == audioMeta.Channels.Value)
            return;

        throw new InvalidOperationException(
            $"Replacement audio target '{target.Alias}' has a WAV source at {sourceFrequency}Hz {sourceChannels}ch, " +
            $"but the target AudioClip is {audioMeta.Frequency.Value}Hz {audioMeta.Channels.Value}ch. " +
            "Unity would resample the mismatch at runtime, which pitch-shifts the sound. " +
            "Re-export the source at the target's rate and channel layout, or ship a format Jiangyu doesn't validate (OGG/MP3) if runtime resampling is acceptable.");
    }

    internal static ReplacementAudioTarget ResolveAndValidateAudioTarget(
        AssetIndex index,
        string alias,
        string targetName,
        long? targetPathId,
        string filePath,
        ILogSink log)
    {
        var target = ResolveReplacementAudioTarget(index, alias, targetName, targetPathId, log);
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
            if (MeshRendererPathResolver.TryResolveTargetMeshNameFromProvided(provided, expectedSet, out var targetName))
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
                .OrderBy(candidate => MeshRendererPathResolver.GetResolvedMeshNameSortKey(candidate, targetName))
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

    // JIANGYU-CONTRACT: the bind-pose retargeting reference is the cleaned authoring
    // export (ModelCleaner-normalised, metre-space, no LOD containers). Retarget
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
        var referenceDirectory = Path.Combine(projectDir, ".jiangyu", "bind_pose_references", ReplacementPaths.BuildModelReplacementAlias(target.Name, target.PathId));
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

    private Task StageRawModelFiles(string userUnityDir, IEnumerable<string> modelFiles)
    {
        var stagingDir = Path.Combine(userUnityDir, "Assets", "Jiangyu", "Staging", "Models");
        ResilientFs.DeleteDirectory(stagingDir);
        ResilientFs.CreateDirectory(stagingDir);

        var count = 0;
        foreach (var modelFile in modelFiles)
        {
            var destFile = Path.Combine(stagingDir, Path.GetFileName(modelFile));
            File.Copy(modelFile, destFile, overwrite: true);
            count++;
        }

        _log.Info($"  Staged {count} model file(s) at {stagingDir}");
        return Task.CompletedTask;
    }

    private async Task<bool> InvokeUnityBuildForModels(string unityEditor, string userUnityDir, string bundleName)
    {
        var modRoot = Path.GetFullPath(Path.Combine(userUnityDir, ".."));
        var logFile = Path.Combine(modRoot, ".jiangyu", "unity_build_models.log");
        var expectedBundle = Path.Combine(modRoot, ".jiangyu", "unity_build", bundleName);

        _log.Info("  Invoking Unity to build model bundle...");

        var result = await UnityBundleInvoker.InvokeAsync(new UnityBundleInvocation
        {
            UnityEditor = unityEditor,
            ProjectPath = userUnityDir,
            ExecuteMethod = "Jiangyu.Mod.BuildModelBundles.BuildAll",
            LogFile = logFile,
            ExtraArgs = [new("bundleName", bundleName)],
            // Retry a cold-project run that writes no bundle against the now-warm project.
            ExpectedOutputPath = expectedBundle,
            Log = _log,
        });

        if (!result.Success)
        {
            _log.Error($"Unity exited with code {result.ExitCode}");
            _log.Error($"  Log: {logFile}");
            _log.Error("");
            foreach (var line in result.LogTailLines)
                _log.Error($"  {line}");
            return false;
        }

        return true;
    }

    // Rich failure text for the "Unity exited 0 but wrote no bundle" case. The bare
    // "did not produce expected bundle" gave the reporter nowhere to look. It names the
    // expected path, the Unity build log to inspect, and what the build dir actually holds.
    private static string DescribeMissingBundle(string projectDir, string builtBundle, string unityBuildDir, bool rawGlbPipeline)
    {
        var logFile = Path.Combine(projectDir, ".jiangyu", rawGlbPipeline ? "unity_build_mesh.log" : "unity_build_models.log");
        var present = Directory.Exists(unityBuildDir)
            ? Directory.GetFiles(unityBuildDir).Select(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        var listing = present.Length > 0 ? string.Join(", ", present) : "(none)";
        return
            $"Unity build reported success but did not produce the expected bundle.{Environment.NewLine}" +
            $"  Expected: {builtBundle}{Environment.NewLine}" +
            $"  Files in {unityBuildDir}: {listing}{Environment.NewLine}" +
            $"  Unity build log: {logFile}{Environment.NewLine}" +
            "  A cold Unity project can run its first batchmode invocation as an import/compile pass without executing the build (Jiangyu retries once automatically). If it persists, check the Unity build log above: the usual causes are Unity licence activation not yet completed on this machine, or the wrong Unity Editor version.";
    }

    private CompilationResult Fail(string message)
    {
        _log.Error(message);
        return new CompilationResult { Success = false, ErrorMessage = message };
    }

}
