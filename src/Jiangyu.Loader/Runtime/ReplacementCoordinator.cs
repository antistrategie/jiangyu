using Il2CppInterop.Runtime;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Replacements;
using Jiangyu.Loader.Runtime.Patching;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Runtime;

internal class ReplacementCoordinator
{
    private readonly List<UnityEngine.Object> _pinned = new();
    private readonly BundleReplacementCatalog _catalog;
    private readonly MaterialReplacementService _materialReplacements;
    private readonly TextureMutationService _textureMutation;
    private readonly MeshPreparationService _meshPreparation;
    private readonly DirectMeshReplacementApplier _directReplacements;
    private readonly PrefabMeshRebindApplier _prefabRebindApplier;
    private readonly DrivenPrefabReplacementManager _drivenReplacements;
    private readonly TemplatePatchCatalog _templatePatches;
    private readonly TemplatePatchApplier _templatePatchApplier;
    private readonly TemplateCloneCatalog _templateClones;
    private readonly TemplateCloneApplier _templateCloneApplier;
    private readonly LoaderHarmonyPatchInstaller _harmonyPatchInstaller;
    // Per-instance dedupe for the driven-prefab branch. DrivenPrefabReplacementManager
    // does not leave a "[jiangyu]" marker on smr.sharedMesh, so the IsAlreadyProcessedMesh
    // check below cannot tell a driven-handled SMR apart from an un-handled one. The
    // mesh-rebind branches mark sharedMesh and dedupe via that suffix, but this set is
    // still cheap insurance against the spawn monitor re-firing driven creation every tick.
    private readonly HashSet<int> _processedSmrInstanceIds = new();

    private bool _templateWorkSeen;
    private bool _templatesAppliedRaised;

    /// <summary>Invoked once, after the last authored template clone or patch has been
    /// applied to the live templates. Null until the runtime binds the mod host.</summary>
    public Action TemplatesApplied { get; set; }

    public ReplacementCoordinator()
    {
        _catalog = new BundleReplacementCatalog(_pinned);
        _materialReplacements = new MaterialReplacementService(_catalog.ReplacementTextures);
        _textureMutation = new TextureMutationService(_catalog.ReplacementTextures);
        _meshPreparation = new MeshPreparationService(_pinned);
        _directReplacements = new DirectMeshReplacementApplier(_materialReplacements, _meshPreparation);
        _prefabRebindApplier = new PrefabMeshRebindApplier(_catalog, _directReplacements);
        _drivenReplacements = new DrivenPrefabReplacementManager(_pinned);
        _templatePatches = new TemplatePatchCatalog();
        _templatePatchApplier = new TemplatePatchApplier(_templatePatches, new ModAssetResolver(_catalog));
        _templateClones = new TemplateCloneCatalog();
        _templateCloneApplier = new TemplateCloneApplier(_templateClones);
        _harmonyPatchInstaller = new LoaderHarmonyPatchInstaller(
            new IHarmonyPatchModule[]
            {
                new TemplateCloneEarlyInjectionPatch(_templateCloneApplier),
                new AudioReplacementPatch(_catalog.ReplacementAudioClips),
                new ConversationManagerTrackingPatch(),
                new InventoryFilterPatch(),
                new Jiangyu.Loader.Sdk.StrategyHarmonyPatch(),
                new Jiangyu.Loader.Sdk.ModStatePersistencePatch(),
            });
    }

    /// <summary>The mod's own bundled assets, keyed by mod folder name.</summary>
    public Jiangyu.Sdk.IModAssets AssetsFor(string modFolder, IModHostLog hostLog)
        => _catalog.AssetsFor(modFolder, hostLog);

    public BundleLoadSummary LoadBundles(string modsDir, MelonLogger.Instance log)
    {
        if (!Directory.Exists(modsDir))
            return new BundleLoadSummary(0, 0, 0);

        var plan = ModLoadPlanBuilder.Build(modsDir);
        var summary = _catalog.LoadBundles(plan, new LoaderLog(log));
        _templateClones.Load(plan.LoadableMods, new LoaderLog(log));
        _templatePatches.Load(plan.LoadableMods, new LoaderLog(log));
        return summary;
    }

    public void InstallHarmonyPatches(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        _harmonyPatchInstaller.Install(harmony, new LoaderHarmonyPatchContext(log));
    }

    public bool HasMeshOrPrefabReplacements =>
        _catalog.Meshes.Count > 0 || _catalog.Prefabs.Count > 0;

    public void OnSceneUnloaded()
    {
        // SMR instance IDs are scene-scoped; SMRs destroyed with the scene take
        // their IDs with them. Clearing avoids slowly accumulating dead entries
        // across scenes and avoids (theoretical) ID-recycling false-negatives
        // on later-scene SMRs that happen to reuse a destroyed ID.
        _processedSmrInstanceIds.Clear();
        _meshPreparation.ClearPreparedAssignments();
    }

    public void ApplyReplacements(MelonLogger.Instance log, bool includeTextures = true)
    {
        if (_catalog.Meshes.Count == 0 &&
            _catalog.Prefabs.Count == 0 &&
            _catalog.ReplacementTextures.Count == 0 &&
            _catalog.ReplacementSprites.Count == 0 &&
            _catalog.ReplacementAudioClips.Count == 0 &&
            !_templatePatchApplier.HasPendingPatches &&
            !_templateCloneApplier.HasPendingClones)
            return;

        // Prefab-time rebind propagates by sharedMesh reference to every
        // Instantiate'd copy, so future spawns of the carrier (loadout
        // rebuilds, SaveSystem.Load reinstantiation) come out pre-swapped.
        // The per-instance sweep below catches replacements without a
        // TargetEntityName and any prefab not yet in Resources.
        var visualReplacements = _prefabRebindApplier.Apply(log);

        var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);

        foreach (var obj in skinnedRenderers)
        {
            var smr = obj.Cast<SkinnedMeshRenderer>();
            if (smr == null)
                continue;

            var smrInstanceId = smr.GetInstanceID();
            if (_processedSmrInstanceIds.Contains(smrInstanceId))
                continue;

            if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                continue;

            if (!TryResolveRendererTarget(smr, out var entityRoot, out var targetRendererPath))
                continue;

            if (TryGetReplacementMesh(smr, entityRoot, targetRendererPath, out var meshReplacement))
            {
                if (_directReplacements.Apply(log, smr, meshReplacement))
                {
                    visualReplacements++;
                    _processedSmrInstanceIds.Add(smrInstanceId);
                }

                continue;
            }

            if (_catalog.Prefabs.TryGetValue(targetRendererPath, out var prefabReplacement) &&
                IsReplacementScopeMatch(smr, entityRoot, prefabReplacement.TargetEntityName))
            {
                if (!_drivenReplacements.HasReplacementFor(entityRoot) &&
                    _drivenReplacements.TryCreate(log, entityRoot, smr, prefabReplacement))
                {
                    visualReplacements++;
                    _processedSmrInstanceIds.Add(smrInstanceId);
                }
            }
        }

        var textureMutations = includeTextures ? _textureMutation.ApplyPending(log) : 0;

        if (visualReplacements > 0 || textureMutations > 0)
            log.Msg($"Applied {visualReplacements} visual replacement(s) and {textureMutations} texture mutation(s).");

        // Clones first: patches may target the newly registered cloneIds.
        if (_templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones)
            _templateWorkSeen = true;
        var clonesApplied = _templateCloneApplier.TryApply(new LoaderLog(log));
        var patchesApplied = _templatePatchApplier.TryApply(log);

        // Type-specific post-patch registration. SoundBank clones need to
        // be registered with Stem's runtime SoundManager only after the
        // bankId patch lands, otherwise Stem indexes them under the source
        // bank's bankId and SAY/skill audio lookups by the modder's chosen
        // bankId resolve to nothing. Skipping this no-op when neither
        // applier did work avoids re-deserialising every clone on each of
        // the ~25 post-scene-load polls.
        if (clonesApplied > 0 || patchesApplied > 0)
            _templateCloneApplier.RunPostPatchHooks(new LoaderLog(log));

        // Humanoid-addition prefab script-config mirrors deferred from
        // addition-prefab loading. Resources is populated by this pass
        // (clone applier had access to per-type ScriptableObject
        // inventories above), so the vanilla reference soldier lookup
        // that missed during early boot will now resolve. Drains the
        // queue on success.
        _catalog.HumanoidMirror.DrainPending(log);

        if (_templateWorkSeen && !_templatesAppliedRaised
            && !_templatePatchApplier.HasPendingPatches && !_templateCloneApplier.HasPendingClones)
        {
            _templatesAppliedRaised = true;
            ReportSelfCheck(log);
            TemplatesApplied?.Invoke();
        }
    }

    // One consolidated line once every patch has been tried against the live game.
    // A non-zero mismatch count is the structural self-check's signal that the game
    // changed since the mods were built, framed so scattered per-type skip lines
    // above read as one cause rather than unrelated failures.
    private void ReportSelfCheck(MelonLogger.Instance log)
    {
        var check = _templatePatchApplier.SelfCheck;
        if (check.Mismatches == 0)
        {
            log.Msg($"Template self-check: all {check.Applied} patch op(s) matched the current game.");
            return;
        }

        log.Warning(
            $"Template self-check: {check.Applied} op(s) applied, {check.Mismatches} did not match the current game "
            + $"(unresolved types {check.UnresolvedTypes}, missing templates {check.MissingTemplates}, "
            + $"missing fields {check.MissingMembers}, value conversions {check.ConversionFailures}). "
            + "If the game updated since these mods were built, that is expected.");
    }

    public bool HasReplacementTargets()
    {
        if (_templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones)
            return true;

        if (_catalog.Meshes.Count == 0 &&
            _catalog.Prefabs.Count == 0 &&
            _catalog.ReplacementTextures.Count == 0 &&
            _catalog.ReplacementSprites.Count == 0 &&
            _catalog.ReplacementAudioClips.Count == 0)
            return false;

        if (_catalog.Meshes.Count > 0 || _catalog.Prefabs.Count > 0)
        {
            var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            foreach (var obj in skinnedRenderers)
            {
                var smr = obj.Cast<SkinnedMeshRenderer>();
                if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                    continue;

                if (!TryResolveRendererTarget(smr, out var entityRoot, out var targetRendererPath))
                    continue;

                if (TryGetReplacementMesh(smr, entityRoot, targetRendererPath, out _) ||
                    (_catalog.Prefabs.TryGetValue(targetRendererPath, out var prefabReplacement) &&
                     IsReplacementScopeMatch(smr, entityRoot, prefabReplacement.TargetEntityName)))
                    return true;
            }
        }

        if (_textureMutation.HasPendingTargets())
            return true;

        return false;
    }

    public void UpdateDrivenReplacements(MelonLogger.Instance log)
        => _drivenReplacements.Update(log);

    private static bool IsLiveSceneRenderer(SkinnedMeshRenderer smr)
    {
        if (smr == null || smr.sharedMesh == null)
            return false;

        if (smr.hideFlags != HideFlags.None)
            return false;

        return IsLiveSceneObject(smr.gameObject);
    }

    private static bool IsLiveSceneObject(GameObject gameObject)
    {
        if (gameObject == null || gameObject.hideFlags != HideFlags.None)
            return false;

        var scene = gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static bool IsAlreadyProcessedMesh(Mesh mesh)
        => mesh != null && mesh.name.EndsWith(" [jiangyu]", StringComparison.Ordinal);

    private bool TryGetReplacementMesh(SkinnedMeshRenderer smr, GameObject entityRoot, string targetRendererPath, out ReplacementMesh replacement)
    {
        if (_catalog.Meshes.TryGetValue(targetRendererPath, out replacement) &&
            IsReplacementScopeMatch(smr, entityRoot, replacement.TargetEntityName))
        {
            return true;
        }

        return false;
    }

    private static bool IsReplacementScopeMatch(SkinnedMeshRenderer smr, GameObject resolvedEntityRoot, string targetEntityName)
    {
        if (string.IsNullOrWhiteSpace(targetEntityName))
            return true;

        var expected = NormaliseSceneObjectName(targetEntityName);
        if (string.IsNullOrEmpty(expected))
            return true;

        if (resolvedEntityRoot != null &&
            IsEntityNameMatch(expected, NormaliseSceneObjectName(resolvedEntityRoot.name)))
            return true;

        var entityRoot = FindEntityRoot(smr);
        if (entityRoot != null &&
            IsEntityNameMatch(expected, NormaliseSceneObjectName(entityRoot.name)))
            return true;

        var sceneRoot = smr.transform?.root;
        if (sceneRoot != null &&
            IsEntityNameMatch(expected, NormaliseSceneObjectName(sceneRoot.name)))
            return true;

        var current = smr.transform;
        while (current != null)
        {
            if (IsEntityNameMatch(expected, NormaliseSceneObjectName(current.name)))
                return true;

            current = current.parent;
        }

        return false;
    }

    private bool TryResolveRendererTarget(SkinnedMeshRenderer smr, out GameObject entityRoot, out string targetRendererPath)
    {
        entityRoot = null;
        targetRendererPath = string.Empty;
        if (smr == null || smr.transform == null)
            return false;

        var candidateRoots = new List<Transform>();
        AddCandidateRoot(candidateRoots, FindEntityRoot(smr)?.transform);
        AddCandidateRoot(candidateRoots, smr.transform.root);

        var current = smr.transform.parent;
        while (current != null)
        {
            AddCandidateRoot(candidateRoots, current);
            current = current.parent;
        }

        GameObject bestRoot = null;
        string bestPath = string.Empty;
        var bestScore = -1;

        foreach (var root in candidateRoots)
        {
            var rawPath = BuildRelativeTransformPath(root, smr.transform);
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            if (!TryResolveCatalogPath(rawPath, out var resolvedPath))
                continue;

            var score = resolvedPath.Count(c => c == '/');
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestRoot = root.gameObject;
            bestPath = resolvedPath;
        }

        if (bestRoot != null && !string.IsNullOrWhiteSpace(bestPath))
        {
            entityRoot = bestRoot;
            targetRendererPath = bestPath;
            return true;
        }

        var fallbackRoot = FindEntityRoot(smr);
        if (fallbackRoot == null)
            return false;

        var fallbackRawPath = BuildRelativeTransformPath(fallbackRoot.transform, smr.transform);
        if (string.IsNullOrWhiteSpace(fallbackRawPath))
            return false;

        if (!TryResolveCatalogPath(fallbackRawPath, out var fallbackPath))
            return false;

        entityRoot = fallbackRoot;
        targetRendererPath = fallbackPath;
        return true;
    }

    private bool TryResolveCatalogPath(string rawPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        if (_catalog.Meshes.ContainsKey(rawPath) || _catalog.Prefabs.ContainsKey(rawPath))
        {
            resolvedPath = rawPath;
            return true;
        }

        var normalised = NormaliseRendererPath(rawPath);
        if (string.IsNullOrWhiteSpace(normalised))
            return false;

        if (_catalog.Meshes.ContainsKey(normalised) || _catalog.Prefabs.ContainsKey(normalised))
        {
            resolvedPath = normalised;
            return true;
        }

        return false;
    }

    private static string NormaliseRendererPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormaliseRendererPathSegment);

        return string.Join("/", segments);
    }

    private static string NormaliseRendererPathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return string.Empty;

        var normalised = segment;
        if (TryStripBlenderNumericSuffix(normalised, out var strippedSegment))
            normalised = strippedSegment;

        const string containerSuffix = "_container";
        if (normalised.EndsWith(containerSuffix, StringComparison.Ordinal))
            normalised = normalised[..^containerSuffix.Length];

        return normalised;
    }

    private static bool TryStripBlenderNumericSuffix(string value, out string stripped)
    {
        stripped = value;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var dotIndex = value.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= value.Length - 1)
            return false;

        var suffix = value[(dotIndex + 1)..];
        if (suffix.Length < 3 || !suffix.All(char.IsDigit))
            return false;

        stripped = value[..dotIndex];
        return !string.IsNullOrWhiteSpace(stripped);
    }

    private static void AddCandidateRoot(List<Transform> roots, Transform candidate)
    {
        if (candidate == null)
            return;

        if (roots.Contains(candidate))
            return;

        roots.Add(candidate);
    }

    private static string NormaliseSceneObjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        const string cloneSuffix = "(Clone)";
        var normalised = name.Trim();
        if (normalised.EndsWith(cloneSuffix, StringComparison.Ordinal))
            normalised = normalised[..^cloneSuffix.Length].TrimEnd();

        return normalised;
    }

    private static bool IsEntityNameMatch(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            return false;

        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return true;

        // Treat variant suffixes as the same entity family (e.g. *_black, *_white).
        if (actual.StartsWith(expected + "_", StringComparison.Ordinal))
            return true;
        if (expected.StartsWith(actual + "_", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static string BuildRelativeTransformPath(Transform root, Transform leaf)
    {
        if (root == null || leaf == null)
            return string.Empty;

        var segments = new List<string>();
        var current = leaf;

        while (current != null && current != root)
        {
            if (string.IsNullOrWhiteSpace(current.name))
                return string.Empty;

            segments.Add(current.name);
            current = current.parent;
        }

        if (current != root || segments.Count == 0)
            return string.Empty;

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static GameObject FindEntityRoot(SkinnedMeshRenderer smr)
    {
        if (smr == null)
            return null;

        Transform bestNamedCandidate = null;
        var current = smr.transform;
        while (current != null)
        {
            if (current.GetComponent<Animator>() != null)
                return current.gameObject;

            var name = current.name;
            if (!string.IsNullOrEmpty(name) &&
                !name.Contains("_LOD", StringComparison.OrdinalIgnoreCase) &&
                (name.StartsWith("rmc_", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("soldier", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("enemy", StringComparison.OrdinalIgnoreCase)))
            {
                bestNamedCandidate = current;
            }

            current = current.parent;
        }

        if (bestNamedCandidate != null)
            return bestNamedCandidate.gameObject;

        return smr.rootBone?.root?.gameObject ?? smr.transform.root.gameObject;
    }
}
