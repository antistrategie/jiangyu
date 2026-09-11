using Il2CppInterop.Runtime;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Replacements;
using Jiangyu.Loader.Runtime.Localisation;
using Jiangyu.Loader.Runtime.Patching;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Templates;
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
    private readonly TemplatePatchCatalog _templatePatches;
    private readonly TemplatePatchApplier _templatePatchApplier;
    private readonly TemplateCloneCatalog _templateClones;
    private readonly TemplateCloneApplier _templateCloneApplier;
    private readonly LoaderHarmonyPatchInstaller _harmonyPatchInstaller;
    private LocaleApplier _localeApplier;
    // Per-SMR dedupe so an SMR already handled is skipped on later sweeps, alongside the
    // "[jiangyu]" sharedMesh marker the mesh-rebind path leaves.
    private readonly HashSet<int> _processedSmrInstanceIds = new();
    // Sweeps the activation-time texture pass has run per screen instance. A screen retires once
    // a sweep places something, or once the budget is spent, so a screen whose content never
    // carries a registered texture stops costing a sweep on every reactivation. Scene-scoped,
    // cleared with the scene's screens.
    private readonly Dictionary<int, int> _screenTextureSweeps = new();
    private const int ScreenTextureSweepBudget = 2;

    private bool _templateWorkSeen;
    private bool _templatesAppliedRaised;
    private bool _memoryAfterPassesReported;
    // Post-template work a pass could not complete (a chain rebuild whose map was not
    // available, a rebuild that threw): run again by the next pass, over this scope (null
    // covers every clone).
    private bool _postWorkPending;
    private HashSet<string> _postWorkScope;

    /// <summary>Invoked once, after the last authored template clone or patch has been
    /// applied to the live templates. Null until the runtime binds the mod host.</summary>
    public Action TemplatesApplied { get; set; }

    public ReplacementCoordinator()
    {
        _catalog = new BundleReplacementCatalog(_pinned);
        _materialReplacements = new MaterialReplacementService(_catalog.Assets);
        _textureMutation = new TextureMutationService(_catalog.Assets);
        _meshPreparation = new MeshPreparationService(_pinned);
        _directReplacements = new DirectMeshReplacementApplier(_materialReplacements, _meshPreparation);
        _prefabRebindApplier = new PrefabMeshRebindApplier(_catalog, _directReplacements);
        _templatePatches = new TemplatePatchCatalog();
        _templateClones = new TemplateCloneCatalog();
        var portraits = new DeferredStandingPortraits(_catalog, _templateClones);
        _templatePatchApplier = new TemplatePatchApplier(_templatePatches, new ModAssetResolver(_catalog, portraits));
        _templateCloneApplier = new TemplateCloneApplier(_templateClones);
        _templatePatchApplier.DeferToChainedReplay = _templateCloneApplier.IsChainedClone;
        _templateCloneApplier.SourcePatchesHeld = SourcePatchesHeld;
        _templateCloneApplier.Registered = _templatePatchApplier.OnTemplateRegistered;
        _templatePatchApplier.CloneHeld = (type, id) => _templateCloneApplier.LateSources.Contains(
            name => TemplateRuntimeAccess.SameTemplateSpace(type, name), id);
        TemplateCloneEarlyInjectionPatch.LatePass = ApplyLate;
        _harmonyPatchInstaller = new LoaderHarmonyPatchInstaller(
            new IHarmonyPatchModule[]
            {
                new StandingPortraitDisplayPatch(portraits),
                new TemplateCloneEarlyInjectionPatch(_templateCloneApplier, _templatePatches),
                new TemplateCloneAncestorPatch(_templateCloneApplier),
                new AudioReplacementPatch(_catalog.Assets),
                new Jiangyu.Loader.Replacements.ElementSpawnReplacementPatch(this),
                new ConversationManagerTrackingPatch(),
                new InventoryFilterPatch(),
                new ModularVehicleSpawnGuardPatch(),
                new SuppressionHandlerGuardPatch(),
                new LocaleReloadPatch(),
                new NumericPlaceholderPatch(),
                new UiInjectionActivatePatch(this),
                new Jiangyu.Loader.Sdk.Hooks.TacticalManagerStartPatch(),
                new Jiangyu.Loader.Sdk.Hooks.StrategyHarmonyPatch(),
                new Jiangyu.Loader.Sdk.Hooks.StrategyAttachPatch(),
                new Jiangyu.Loader.Sdk.State.ModStatePersistencePatch(),
            });
    }

    /// <summary>The mod's own bundled assets, keyed by mod id.</summary>
    public Jiangyu.Sdk.IModAssets AssetsFor(string modId, IModHostLog hostLog)
        => _catalog.AssetsFor(modId, hostLog);

    /// <summary>The mods the load plan resolved as loadable, in load order. Retained so
    /// code-mod initialisation reuses this discovery rather than re-walking Mods/: the plan
    /// already pairs each mod's id with the directory it was found in, whatever that
    /// directory is called and however deeply it is nested.</summary>
    public IReadOnlyList<DiscoveredMod> LoadableMods { get; private set; } = Array.Empty<DiscoveredMod>();

    /// <summary>Per-mod counts of what the session loaded, written as one line per mod once
    /// code mods are up.</summary>
    public ModLoadReport LoadReport { get; } = new();

    public BundleLoadSummary LoadBundles(string modsDir, MelonLogger.Instance log)
    {
        if (!Directory.Exists(modsDir))
            return new BundleLoadSummary(0, 0, 0);

        var plan = ModLoadPlanBuilder.Build(modsDir, BuildInfo.Version);
        LoadableMods = plan.LoadableMods;
        var summary = _catalog.LoadBundles(plan, new LoaderLog(log));
        // Read each mod's compiled template program once and feed both catalogs, rather
        // than each catalog re-reading and re-parsing the same templates.json.
        var templates = plan.LoadableMods
            .Select(mod => (Mod: mod, Templates: CompiledTemplatePatchManifest.TryLoad(mod.DirectoryPath)))
            .ToList();
        _templateClones.Load(templates, new LoaderLog(log));
        _templatePatches.Load(templates, new LoaderLog(log));
        foreach (var (mod, manifest) in templates)
        {
            var counts = LoadReport.For(mod.Name);
            counts.Bundles = _catalog.BundleCountFor(mod.Name);
            counts.Clones = manifest?.TemplateClones?.Count ?? 0;
            counts.PatchOps = manifest?.TemplatePatches?.Sum(patch => patch.Set?.Count ?? 0) ?? 0;
            counts.Locales = CountLocaleFiles(mod);
        }
        _localeApplier = new LocaleApplier(plan.LoadableMods, _templateClones, _templatePatches)
        {
            TemplateHeld = IsTemplateHeld,
            // A held clone has no fields to translate yet, whoever owns the text.
            BlockHeld = (owner, type, id) => _templatePatchApplier.HeldBlocks.Contains(
                    owner, name => TemplateRuntimeAccess.SameTemplateSpace(type, name), id)
                || _templateCloneApplier.LateSources.Contains(
                    name => TemplateRuntimeAccess.SameTemplateSpace(type, name), id),
        };
        LoaderDebug.Write(log, $"Replacement targets: {_catalog.Meshes.Count} mesh(es), {_catalog.Assets.TextureReplacementCount} texture name(s).");
        return summary;
    }

    /// <summary>True while template work waits on something outside this pass: a held patch
    /// block or clone waiting on a template another loader has not registered, a type whose
    /// templates are not live yet, or a scoped locale re-run that could not complete.</summary>
    public bool HasDeferredTemplateWork
        => !_templatePatchApplier.HeldBlocks.IsEmpty
            || !_templateCloneApplier.LateSources.IsEmpty
            || _templatePatchApplier.HasPendingPatches
            || _templateCloneApplier.HasPendingClones
            || _postWorkPending
            || (_localeApplier?.ScopePending ?? false);

    // A clone of a type without re-inheritance waits for its source's held patches, with one
    // exception: a source block that itself waits on this clone. Then the clone copies the
    // source as it stands, the block lands once the clone exists, and the copy keeps the
    // source's earlier state. That case is warned about when the block lands.
    private bool SourcePatchesHeld(string templateType, string sourceId, string cloneId)
    {
        Func<string, bool> sameSpace = name => TemplateRuntimeAccess.SameTemplateSpace(templateType, name);
        // Only a block that waits on something other than this very clone holds it back. A
        // reference to the clone may carry any name of its type or of a base of it.
        Func<TemplateRef, bool> isThisClone = missing =>
            string.Equals(missing.Id, cloneId, StringComparison.Ordinal)
            && TemplateRuntimeAccess.SameTemplateSpace(templateType, missing.Type);
        var held = _templatePatchApplier.HeldBlocks;
        if (held.ContainsTemplate(sameSpace, sourceId))
            return held.AnyBlockWaitsOnOther(sameSpace, sourceId, isThisClone);

        // Before the source's own patch pass has run, the block is not held yet but would be.
        return _templatePatchApplier.WouldHold(templateType, sourceId, isThisClone);
    }

    // A SoundBank addressed under any spelling or base of its type.
    private static bool IsSoundBankSpace(string templateType)
        => TemplateRuntimeAccess.IsSoundBankTypeName(templateType)
            || TemplateRuntimeAccess.SameTemplateSpace("SoundBank", templateType);

    // Whether the template waits on another loader under any name it is registered in: a
    // held patch block on it, or a held clone directive for it.
    private bool IsTemplateHeld(string templateType, string templateId)
    {
        Func<string, bool> sameSpace = name => TemplateRuntimeAccess.SameTemplateSpace(templateType, name);
        return _templatePatchApplier.HeldBlocks.ContainsTemplate(sameSpace, templateId)
            || _templateCloneApplier.LateSources.Contains(sameSpace, templateId);
    }

    private static int CountLocaleFiles(DiscoveredMod mod)
    {
        var localesDir = Path.Combine(mod.DirectoryPath, CompiledLayout.LocalesDirName);
        return Directory.Exists(localesDir)
            ? Directory.EnumerateFiles(localesDir, "*.po", SearchOption.AllDirectories).Count()
            : 0;
    }

    public void InstallHarmonyPatches(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        _harmonyPatchInstaller.Install(harmony, new LoaderHarmonyPatchContext(log));
        log.Msg($"Installed {HarmonyPatching.InstalledCount} loader patch(es).");
    }

    public bool HasMeshReplacements => _catalog.Meshes.Count > 0;

    public void OnSceneUnloaded()
    {
        // SMR instance IDs are scene-scoped; SMRs destroyed with the scene take
        // their IDs with them. Clearing avoids slowly accumulating dead entries
        // across scenes and avoids (theoretical) ID-recycling false-negatives
        // on later-scene SMRs that happen to reuse a destroyed ID.
        _processedSmrInstanceIds.Clear();
        _screenTextureSweeps.Clear();
        _textureMutation.OnSceneUnloaded();
        _meshPreparation.ClearPreparedAssignments();
        // Held work is reported once per scene's schedule.
        _templatePatchApplier.HeldBlocks.ResetReported();
        _templateCloneApplier.LateSources.ResetReported();
        _templatePatchApplier.OnSceneUnloaded();
        _templateCloneApplier.OnSceneUnloaded();
    }

    /// <summary>
    /// Texture mutations at screen-activation time, driven by the UI injection postfix.
    /// A screen's own textures only enter the object graph when that screen is built, which
    /// falls between the scene-load pass and the next poll tick, so a poll-driven swap lands up
    /// to five frames after the screen has already painted the original and reads as a flicker.
    /// The activation postfix runs in the frame the screen is created, before its first paint.
    /// </summary>
    public void ApplyScreenTextures(int screenId, MelonLogger.Instance log)
    {
        if (!_textureMutation.MayHaveUnresolvedTargets)
            return;

        // A sweep is two FindObjectsOfTypeAll scans, so each screen gets a bounded number. The
        // first empty sweep does not retire a screen: content built after activation, or a pooled
        // screen reopened with different content, gets one more look. A texture that enters the
        // graph after that is placed by the scene poll.
        _screenTextureSweeps.TryGetValue(screenId, out var sweeps);
        if (sweeps >= ScreenTextureSweepBudget)
            return;

        var mutated = _textureMutation.ApplyPending(log);
        _screenTextureSweeps[screenId] = mutated > 0 ? ScreenTextureSweepBudget : sweeps + 1;
        if (mutated > 0)
            log.Msg($"Applied {mutated} texture mutation(s) on screen activation.");
    }

    public void ApplyReplacements(MelonLogger.Instance log, bool includeTextures = true)
    {
        using var timing = StartupTimings.Measure("replacement pass");
        _templatePatchApplier.BeginPass();
        try
        {
            ApplyReplacementsPass(log, includeTextures);
        }
        finally
        {
            _templatePatchApplier.EndPass();
        }
    }

    private void ApplyReplacementsPass(MelonLogger.Instance log, bool includeTextures)
    {
        // A prefab loaded before the game's asset registry was populated waits here for
        // its script mirror, so a queued mirror is work even for a mod that ships nothing
        // else.
        if (_catalog.Meshes.Count == 0 &&
            _catalog.Assets.TextureReplacementCount == 0 &&
            !_catalog.PrefabMirrors.HasPending &&
            !_templatePatchApplier.HasPendingPatches &&
            !_templateCloneApplier.HasPendingClones &&
            !(_localeApplier?.Pending ?? false))
        {
            // Late template ids are the one job left once everything above has settled.
            // Their lookups are the only cost of this pass: no renderer sweep, no texture pass.
            var lateOnly = new HashSet<string>(StringComparer.Ordinal);
            var lateOnlyClones = new HashSet<string>(StringComparer.Ordinal);
            RetryLateTemplates(log, lateOnly, chainedToo: true, lateOnlyClones);
            // Registrations beyond the held clones came from a prefix's regular clone pass.
            if (_templateCloneApplier.TakeRegisteredSinceLastCall() > lateOnlyClones.Count)
                RunPostTemplateWork(log, changed: null);
            else if (lateOnly.Count > 0)
                RunPostTemplateWork(log, lateOnly);
            else if (_postWorkPending)
                RunPostTemplateWork(log, new HashSet<string>(StringComparer.Ordinal));
            // A scoped locale pass that could not complete on an earlier poll is retried here too.
            if (lateOnly.Count > 0 || (_localeApplier?.ScopePending ?? false))
                _localeApplier?.Apply(log);
            RaiseTemplatesAppliedIfSettled(log);
            return;
        }

        // Prefab-time rebind propagates by sharedMesh reference to every
        // Instantiate'd copy, so future spawns of the carrier (loadout
        // rebuilds, SaveSystem.Load reinstantiation) come out pre-swapped.
        // The per-instance sweep below catches replacements without a
        // TargetEntityName and any prefab not yet in Resources.
        var visualReplacements = 0;
        if (HasMeshReplacements)
        {
            using var timing = StartupTimings.Measure("mesh replacement scans");
            visualReplacements = _prefabRebindApplier.Apply(log);
            var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            foreach (var obj in skinnedRenderers)
            {
                if (TryApplyToRenderer(log, obj.Cast<SkinnedMeshRenderer>()))
                    visualReplacements++;
            }
        }

        var textureMutations = includeTextures ? _textureMutation.ApplyPending(log) : 0;

        if (visualReplacements > 0 || textureMutations > 0)
            log.Msg($"Applied {visualReplacements} visual replacement(s) and {textureMutations} texture mutation(s).");

        // Clones first, held clones included: patches may target the newly registered cloneIds,
        // and a ref to one resolves only if the clone exists when the op applies.
        if (_templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones)
            _templateWorkSeen = true;
        // Held clones with an outside source land before the regular patch pass, so a ref to
        // one of them resolves. Held clones chained on a sibling clone land after it, so they
        // copy the sibling as patched.
        var late = new HashSet<string>(StringComparer.Ordinal);
        var lateClones = new HashSet<string>(StringComparer.Ordinal);
        var clonesApplied = _templateCloneApplier.TryApply(new LoaderLog(log));
        RetryLateTemplates(log, late, chainedToo: false, lateClones);
        var patchesApplied = _templatePatchApplier.TryApply(log);
        RetryLateTemplates(log, late, chainedToo: true, lateClones);
        // Clones a prefix registered since the last pass count as this pass's regular work.
        var freshClones = _templateCloneApplier.TakeRegisteredSinceLastCall();

        // A pass with regular work rebuilds every chained clone. A pass with late work alone
        // rebuilds only the chains the late ids touched: by then a mod's OnTemplatesApplied
        // may have edited the others, and a full rebuild would put them back.
        if (clonesApplied > 0 || patchesApplied > 0 || freshClones > clonesApplied + lateClones.Count)
            RunPostTemplateWork(log, changed: null);
        else if (late.Count > 0)
            RunPostTemplateWork(log, late);

        // Active-language translations rewrite m_DefaultTranslation after the base patches set
        // the source text, so they overwrite English in the same pass the base patches land. Never
        // gated on the appliers settling: a patch for a type with no live template in this scene
        // leaves them pending for the whole session, and holding localisation back for that would
        // strand every unrelated UI string too. The inheritance pass inside carries its own retry.
        _localeApplier?.Apply(log);

        // Addition-prefab script-config mirrors deferred from
        // addition-prefab loading. Resources is populated by this pass
        // (clone applier had access to per-type ScriptableObject
        // inventories above), so the vanilla reference prefab lookup
        // that missed during early boot will now resolve. Drains the
        // queue on success.
        _catalog.PrefabMirrors.DrainPending(log);

        RaiseTemplatesAppliedIfSettled(log);
    }

    // Fires the one-shot templates-applied signal once every type's pass has run. Checked
    // after every pass that can settle the last type, the campaign-entry pass included.
    private void RaiseTemplatesAppliedIfSettled(MelonLogger.Instance log)
    {
        if (!_templateWorkSeen || _templatesAppliedRaised
            || _templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones)
            return;
        _templatesAppliedRaised = true;
        ReportSelfCheck(log);
        StartupTimings.MarkOnce("template patches applied", memory: true);
        using var timing = StartupTimings.Measure("mod templates-applied callbacks");
        TemplatesApplied?.Invoke();
    }

    // The late pass on its own, from the last prefix on a campaign entry point (new game, save
    // load, startup). Whatever another mod's prefix on the same method registered is patched
    // here, before the game reads the templates.
    /// <summary>The template work alone: the steady-state pass the scheduler runs every
    /// 300 frames while <see cref="HasDeferredTemplateWork"/>, with no renderer sweep, prefab
    /// rebind or texture pass.</summary>
    public void ApplyDeferredTemplateWork(MelonLogger.Instance log) => ApplyLate(log, "steady-state pass");

    private void ApplyLate(MelonLogger.Instance log, string trigger)
    {
        _templatePatchApplier.BeginPass();
        try
        {
            ApplyLatePass(log, trigger);
        }
        finally
        {
            _templatePatchApplier.EndPass();
        }
    }

    private void ApplyLatePass(MelonLogger.Instance log, string trigger)
    {
        // The same order as a regular pass: regular clones for a type that became live since
        // the polls, held clones with an outside source, the regular patch pass for types
        // that became live (a clone that landed here may hold its inline ops there), held
        // clones chained on siblings, and the held patches.
        var late = new HashSet<string>(StringComparer.Ordinal);
        var lateClones = new HashSet<string>(StringComparer.Ordinal);
        if (_templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones)
            _templateWorkSeen = true;
        var clonesApplied = _templateCloneApplier.HasPendingClones
            ? _templateCloneApplier.TryApply(new LoaderLog(log))
            : 0;
        RetryLateTemplates(log, late, chainedToo: false, lateClones);
        var patchesApplied = _templatePatchApplier.HasPendingPatches
            ? _templatePatchApplier.TryApply(log)
            : 0;
        RetryLateTemplates(log, late, chainedToo: true, lateClones);
        // Clones the normal-priority prefix registered just before this pass count as its
        // regular work: their chained ops wait on the post-template work that follows.
        var freshClones = _templateCloneApplier.TakeRegisteredSinceLastCall();
        if (freshClones > clonesApplied + lateClones.Count)
            clonesApplied = freshClones;

        if (late.Count == 0 && clonesApplied == 0 && patchesApplied == 0)
        {
            // Post-template work an earlier pass could not complete, and a scoped locale pass
            // that could not, are retried here too.
            if (_postWorkPending)
                RunPostTemplateWork(log, new HashSet<string>(StringComparer.Ordinal));
            if (_localeApplier?.ScopePending ?? false)
                _localeApplier.Apply(log);
            // A type may have latched here with every target held, which settles the passes.
            RaiseTemplatesAppliedIfSettled(log);
            LoaderDebug.Write(log, $"Template late pass at {trigger}: nothing new "
                + $"({_templatePatchApplier.HeldBlocks.Count} patch block(s) and {_templateCloneApplier.LateSources.Count} clone source(s) still held).");
            return;
        }

        if (clonesApplied > 0 || patchesApplied > 0)
            RunPostTemplateWork(log, changed: null);
        else
            RunPostTemplateWork(log, late);
        _localeApplier?.Apply(log);
        RaiseTemplatesAppliedIfSettled(log);
        log.Msg($"Template late pass at {trigger}: {late.Count} template(s) landed.");
    }

    // Held clones first, then held patches. A clone pass lands one link of a chain per pass
    // (it matches sources against one snapshot of the live templates), so it repeats while it
    // lands something. Between passes, a landed clone that a still-held clone is cloned from
    // gets its patches at once, so the next link copies a patched source. Every other held
    // patch waits until the chains are complete: a ref in one of them resolves only if its
    // target exists when the op applies. A clone a prefix registered since the last pass
    // counts as landed ahead of the first pass.
    // lateClones collects the held clones that landed, kept apart from the patch targets in
    // registered so a caller can tell a prefix's regular registrations from them.
    private void RetryLateTemplates(MelonLogger.Instance log, ISet<string> registered, bool chainedToo, ISet<string> lateClones)
    {
        var landedNow = new HashSet<string>(StringComparer.Ordinal);
        _templateCloneApplier.DrainLateRegistrations(landedNow);
        registered.UnionWith(landedNow);
        lateClones.UnionWith(landedNow);
        PatchHeldSources(log, registered, landedNow);

        while (!_templateCloneApplier.LateSources.IsEmpty)
        {
            var heldBefore = _templateCloneApplier.LateSources.Count;
            landedNow = new HashSet<string>(StringComparer.Ordinal);
            _templateCloneApplier.RetryLate(new LoaderLog(log), landedNow, chainedToo);
            registered.UnionWith(landedNow);
            lateClones.UnionWith(landedNow);
            if (_templateCloneApplier.LateSources.Count == heldBefore)
                break;
            PatchHeldSources(log, registered, landedNow);
        }

        // The remaining held patches run once every chain is complete, so a ref in one of
        // them can see any clone that landed. The chained phase is the last one. A block that
        // lands there may release a clone that waited on it, so the phase repeats while the
        // patches land something and clones are still held.
        if (!chainedToo)
            return;
        while (true)
        {
            var before = registered.Count;
            RetryLatePatches(log, registered);
            if (!_templateCloneApplier.LateSources.IsEmpty)
            {
                var released = new HashSet<string>(StringComparer.Ordinal);
                _templateCloneApplier.RetryLate(new LoaderLog(log), released, chainedToo: true);
                registered.UnionWith(released);
                lateClones.UnionWith(released);
                if (released.Count > 0)
                    PatchHeldSources(log, registered, released);
            }

            if (registered.Count == before)
                return;
        }
    }

    // The held patches on those of the landed clones that a still-held clone is cloned from.
    private void PatchHeldSources(MelonLogger.Instance log, ISet<string> registered, HashSet<string> landed)
    {
        if (landed.Count == 0 || _templatePatchApplier.HeldBlocks.IsEmpty)
            return;
        var sources = _templateCloneApplier.HeldSourceKeys();
        sources.IntersectWith(landed);
        if (sources.Count > 0)
            _templatePatchApplier.RetryLate(log, registered, (type, id) => LateTemplateSet.KeyedUnderAnyName(sources, type, id));
    }

    // Each held patch target that has turned up is applied and added to registered.
    private void RetryLatePatches(MelonLogger.Instance log, ISet<string> registered)
    {
        if (!_templatePatchApplier.HeldBlocks.IsEmpty)
            _templatePatchApplier.RetryLate(log, registered);
    }

    // Runs once per pass that registered a clone or applied an op. changed scopes the
    // chained-clone rebuild and the conversation refresh to the ids that landed this pass
    // (plus the chains rebuilt from them), null covers every clone.
    private void RunPostTemplateWork(MelonLogger.Instance log, ISet<string> changed)
    {
        // Work an earlier pass could not complete joins this one's scope.
        if (_postWorkPending)
        {
            if (changed == null || _postWorkScope == null)
                changed = null;
            else
            {
                var merged = new HashSet<string>(changed, StringComparer.Ordinal);
                merged.UnionWith(_postWorkScope);
                changed = merged;
            }
        }
        else if (changed is { Count: 0 })
            return;

        try
        {
            RunPostTemplateWorkCore(log, changed);
        }
        catch (Exception ex)
        {
            log.Warning($"Post-template work threw {ex.GetType().Name}: {ex.Message}; retried next pass.");
            RememberPostWork(changed);
        }
    }

    private void RememberPostWork(ISet<string> changed)
    {
        _postWorkPending = true;
        _postWorkScope = changed == null ? null : new HashSet<string>(changed, StringComparer.Ordinal);
    }

    private void RunPostTemplateWorkCore(MelonLogger.Instance log, ISet<string> changed)
    {
        // A clone whose source is itself a mod clone was instantiated from the
        // source's PRE-PATCH base (the clone pass runs before any patch), so it
        // inherited none of the source's own appends/sets. Now that patches have
        // landed, rebuild it from the fully patched source and replay its own
        // ops on top. A rebuild that could not run is retried by the next pass.
        _templateCloneApplier.ReinheritChainedClones(_templatePatchApplier, changed, new LoaderLog(log), out var incomplete);
        if (incomplete)
            RememberPostWork(changed);
        else
        {
            _postWorkPending = false;
            _postWorkScope = null;
        }

        // Type-specific post-patch registration. SoundBank clones need to
        // be registered with Stem's runtime SoundManager only after the
        // bankId patch lands, otherwise Stem indexes them under the source
        // bank's bankId and SAY/skill audio lookups by the modder's chosen
        // bankId resolve to nothing. Skipping this no-op when neither
        // applier did work avoids re-deserialising every clone on each of
        // the ~25 post-scene-load polls.
        _templateCloneApplier.RunPostPatchHooks(
            changed,
            _templatePatchApplier.PatchesLandedFor(IsSoundBankSpace),
            bankId => _templatePatchApplier.HeldBlocks.ContainsTemplate(IsSoundBankSpace, bankId),
            new LoaderLog(log));
        // Newly registered templates are the only reason the locale pass would see more than it
        // did last time, so it re-runs here rather than on every poll. A late-only pass re-runs
        // for the changed templates alone.
        _localeApplier?.NotifyTemplatesChanged(changed);
    }

    /// <summary>Warns once per scene for each held block and clone not yet reported, naming
    /// what it waits on. Called at the end of a scene's poll schedule and after each
    /// steady-state pass, so a block first held after the schedule is reported too.</summary>
    public void ReportLate(MelonLogger.Instance log)
    {
        _templateCloneApplier.ReportLate(new LoaderLog(log));
        _templatePatchApplier.ReportLate(log);
    }

    // The second memory line lands when the first scene's poll schedule has run to its end,
    // so it counts the lazy-loaded assets the later polls painted as well as the template
    // passes, and it does not wait on a template that never resolves. Later scenes do not
    // report: the figure of interest is what the mods hold before a campaign exists.
    // Late template ids still absent at this point are warned about once each here, and
    // stay held for the next scene.
    public void OnPollScheduleComplete(MelonLogger.Instance log)
    {
        ReportLate(log);

        if (_memoryAfterPassesReported)
            return;
        _memoryAfterPassesReported = true;
        MemoryReport.Write(log, "after replacement passes", describeMachine: false);
        StartupTimings.Complete();
    }

    // One consolidated line once every patch has been tried against the live game.
    // A non-zero mismatch count is the structural self-check's signal that the game
    // changed since the mods were built, framed so scattered per-type skip lines
    // above read as one cause rather than unrelated failures.
    private void ReportSelfCheck(MelonLogger.Instance log)
    {
        var check = _templatePatchApplier.SelfCheck;
        var lateClones = _templateCloneApplier.LateSources.Count;
        var late = check.Late > 0 || lateClones > 0
            ? $" {check.Late} op(s) and {lateClones} clone(s) wait on templates another mod has not registered yet."
            : string.Empty;
        if (check.Mismatches == 0)
        {
            var registeredClones = _templateClones.CloneCount - lateClones;
            var clones = registeredClones > 0 ? $"{registeredClones} clone(s) registered, " : string.Empty;
            log.Msg($"Template self-check: {clones}all {check.Applied} patch op(s) matched the current game.{late}");
            return;
        }

        log.Warning(
            $"Template self-check: {check.Applied} op(s) applied, {check.Mismatches} did not match the current game "
            + $"(unresolved types {check.UnresolvedTypes}, missing templates {check.MissingTemplates}, "
            + $"missing fields {check.MissingMembers}, value conversions {check.ConversionFailures}). "
            + $"If the game updated since these mods were built, that is expected.{late}");
    }

    public bool HasReplacementTargets()
    {
        if (_templatePatchApplier.HasPendingPatches || _templateCloneApplier.HasPendingClones
            || _catalog.PrefabMirrors.HasPending)
            return true;

        if (_catalog.Meshes.Count == 0 &&
            _catalog.Assets.TextureReplacementCount == 0)
            return false;

        if (_catalog.Meshes.Count > 0)
        {
            var skinnedRenderers = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true);
            foreach (var obj in skinnedRenderers)
            {
                var smr = obj.Cast<SkinnedMeshRenderer>();
                if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
                    continue;

                if (!TryResolveRendererTarget(smr, out var entityRoot, out var targetRendererPath))
                    continue;

                if (TryGetReplacementMesh(smr, entityRoot, targetRendererPath, out _))
                    return true;
            }
        }

        if (_textureMutation.HasPendingTargets())
            return true;

        return false;
    }

    // Apply mesh and driven-prefab replacements to a freshly spawned element's renderers.
    // Driven by the Element.OnSpawned Harmony postfix in place of the steady-state spawn
    // monitor: a unit's swap lands when its model is built, not on a frame poll. The
    // renderers come from the element's own list (they are not yet parented under its
    // model transform at this point). Texture, template, and locale work is scene-load
    // only and stays on the scheduled poll.
    public void ApplyToSpawnedRenderers(MelonLogger.Instance log, List<SkinnedMeshRenderer> renderers)
    {
        var hasReplacements = HasMeshReplacements;
        if (!hasReplacements && !LoaderDebug.Enabled)
            return;

        // Debug-gated proof that the Element.OnSpawned postfix fires per spawned element
        // with its renderers already built, independent of whether a replacement is set.
        if (LoaderDebug.Enabled)
            log.Debug($"[spawn] element skinnedRenderers={renderers?.Count ?? 0} meshOrPrefabReplacements={hasReplacements}");

        if (!hasReplacements || renderers == null)
            return;

        var applied = 0;
        foreach (var smr in renderers)
        {
            try
            {
                if (TryApplyToRenderer(log, smr))
                    applied++;
            }
            catch (Exception ex)
            {
                log.Error($"Spawn replacement on '{smr?.name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (applied > 0)
            LoaderDebug.Write(log, $"Applied {applied} visual replacement(s) to a spawned unit.");
    }

    // Resolve and apply the mesh or driven-prefab replacement for one live SMR. Shared by
    // the scene-load sweep and the per-element spawn postfix; idempotent via
    // _processedSmrInstanceIds, so an SMR handled by one path is skipped by the other.
    private bool TryApplyToRenderer(MelonLogger.Instance log, SkinnedMeshRenderer smr)
    {
        if (smr == null)
            return false;

        var smrInstanceId = smr.GetInstanceID();
        if (_processedSmrInstanceIds.Contains(smrInstanceId))
            return false;

        if (!IsLiveSceneRenderer(smr) || IsAlreadyProcessedMesh(smr.sharedMesh))
            return false;

        if (!TryResolveRendererTarget(smr, out var entityRoot, out var targetRendererPath))
            return false;

        if (TryGetReplacementMesh(smr, entityRoot, targetRendererPath, out var meshReplacement))
        {
            if (_directReplacements.Apply(log, smr, meshReplacement))
            {
                _processedSmrInstanceIds.Add(smrInstanceId);
                return true;
            }

            return false;
        }

        return false;
    }

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

        if (_catalog.Meshes.ContainsKey(rawPath))
        {
            resolvedPath = rawPath;
            return true;
        }

        var normalised = NormaliseRendererPath(rawPath);
        if (string.IsNullOrWhiteSpace(normalised))
            return false;

        if (_catalog.Meshes.ContainsKey(normalised))
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
