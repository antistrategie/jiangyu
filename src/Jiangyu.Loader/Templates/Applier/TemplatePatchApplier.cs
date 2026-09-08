using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: Live template identity for patching is the serialised
// m_ID string. The applier resolves a patch to its target via
// DataTemplateLoader.TryGet<T>(m_ID), which reads m_TemplateMaps and so
// sees both game-native templates and Jiangyu-injected clones. (collection,
// pathId) and native pointers are not used; m_ID is the only identifier
// that's stable across game runs and persistable in source. Subtypes
// without a readable m_ID surface as a clear log warning rather than
// silently dropping patches. Field paths are walked as dotted segments
// with optional [N] indexers; intermediate segments may be reference
// types or value types (the latter are read-boxed-and-written-back after
// the terminal set).

/// <summary>
/// One-shot applier that takes the merged catalogue of compiled template
/// patches and writes each scalar set operation into the matching live
/// template once the game has materialised the DataTemplate cache for that
/// type. Callers are expected to invoke <see cref="TryApply"/> repeatedly;
/// it no-ops until templates are present, then applies once per type and
/// latches for that type. One mod's ops on one template form a block. A block
/// applies only when its template and every template its values refer to exist.
/// Otherwise it is held in <see cref="HeldBlocks"/>, nothing in it written, and
/// <see cref="RetryLate"/> applies it in order the pass its last missing template
/// appears.
/// </summary>
internal sealed partial class TemplatePatchApplier
{
    private readonly TemplatePatchCatalog _catalog;
    private readonly ModAssetResolver _assetResolver;
    private readonly HashSet<string> _appliedTypes = new(StringComparer.Ordinal);
    // Chained-clone blocks whose replay the self-check has already counted, keyed type\0id\0owner.
    private readonly HashSet<string> _replayedEntries = new(StringComparer.Ordinal);
    // Template entries (catalogue type name \0 id) whose ops a pass took, under that name or
    // an alias of it.
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    // The set ops applied per template (live type \0 id). A set that lands after a later
    // mod's set on the same slot is skipped, so the later mod still wins.
    private readonly Dictionary<string, List<LoadedPatchOperation>> _appliedSets = new(StringComparer.Ordinal);
    // The current pass's existence probe, and the ScriptableObject types whose Resources
    // folder a probe reloaded this scene.
    private ReferenceProbe _probe;
    private readonly HashSet<string> _reloadedThisScene = new(StringComparer.Ordinal);
    // The templates the current pass's probe found, so an op resolving a reference the probe
    // has just checked takes the object it found rather than enumerating again.
    private static Dictionary<TemplateRef, Il2CppObjectBase> _passResolved;

    private int _appliedTotal;
    private int _unresolvedTypeTotal;
    private int _missingTemplateTotal;
    private int _missingMemberTotal;
    private int _conversionFailedTotal;

    public TemplatePatchApplier(TemplatePatchCatalog catalog, ModAssetResolver assetResolver = null)
    {
        _catalog = catalog;
        _assetResolver = assetResolver;
    }

    /// <summary>Set by the coordinator: names the templates whose ops the
    /// chained-clone replay owns (a clone of another mod clone). The first
    /// pass skips them, because at that point the template still holds its
    /// vanilla-derived base: index ops would resolve against the wrong
    /// elements and warn about members those elements lack, all of it thrown
    /// away by the rebase-and-replay that follows.</summary>
    internal Func<string, string, bool> DeferToChainedReplay { get; set; }

    /// <summary>Set by the coordinator: whether a clone directive for the template (type
    /// name, id) is held. A reference to a held clone counts as missing even when another
    /// loader has registered that id: the block waits for the clone the mod declared.</summary>
    internal Func<string, string, bool> CloneHeld { get; set; }

    /// <summary>Running tally of how the patch ops fared against the live game,
    /// accumulated as each type latches and as each chained clone's ops are replayed,
    /// so it covers every op the catalogue loaded. A non-zero mismatch count means
    /// the live game no longer matches what the mods were compiled against.</summary>
    public TemplateApplySelfCheck SelfCheck =>
        new(_appliedTotal, _unresolvedTypeTotal, _missingTemplateTotal, _missingMemberTotal, _conversionFailedTotal, HeldBlocks.PendingOps);

    /// <summary>The blocks waiting on a template that does not exist yet: their own target,
    /// or one a value refers to. They do not count as pending: the type is latched and the
    /// self-check runs without them. <see cref="RetryLate"/> applies each the pass its last
    /// missing template turns up.</summary>
    public HeldPatchBlocks HeldBlocks { get; } = new();

    /// <summary>True once the pass for <paramref name="templateTypeName"/> has run, or when
    /// no mod patches that type. Post-clone registration that depends on a patched field
    /// (a SoundBank's bankId) waits on this.</summary>
    public bool PatchesLandedFor(string templateTypeName)
        => PatchesLandedFor(name => string.Equals(name, templateTypeName, StringComparison.Ordinal));

    /// <summary>True once the pass for every type <paramref name="matches"/> has run.</summary>
    public bool PatchesLandedFor(Func<string, bool> matches)
    {
        foreach (var typeEntry in _catalog.EnumerateByType())
        {
            if (matches(typeEntry.Key) && !_appliedTypes.Contains(typeEntry.Key))
                return false;
        }

        return true;
    }

    public bool HasPendingPatches
    {
        get
        {
            if (!_catalog.HasPatches)
                return false;

            foreach (var typeEntry in _catalog.EnumerateByType())
            {
                if (!_appliedTypes.Contains(typeEntry.Key))
                    return true;
            }

            return false;
        }
    }

    public int TryApply(MelonLogger.Instance log)
    {
        if (!_catalog.HasPatches)
            return 0;

        var totalApplied = 0;

        foreach (var typeEntry in _catalog.EnumerateByType())
        {
            if (_appliedTypes.Contains(typeEntry.Key))
                continue;

            totalApplied += TryApplyType(typeEntry.Key, typeEntry.Value, log);
        }

        return totalApplied;
    }

    /// <summary>Starts a pass. The existence lookups made during it share one enumeration
    /// per ScriptableObject type, so a pass reads each type once however many templates it
    /// checks. The coordinator brackets every pass with this and <see cref="EndPass"/>.</summary>
    public void BeginPass()
    {
        _passResolved = new Dictionary<TemplateRef, Il2CppObjectBase>();
        _probe = new ReferenceProbe(_reloadedThisScene, _passResolved);
    }

    public void EndPass()
    {
        _probe = null;
        _passResolved = null;
    }

    /// <summary>A clone of <paramref name="templateTypeName"/> registered during the pass:
    /// the probe's enumeration of that type is stale, so it is taken again on the next
    /// lookup.</summary>
    public void OnTemplateRegistered(string templateTypeName) => _probe?.Forget();

    /// <summary>A scene change may unload assets a probe found: a probe in the next scene may
    /// reload a type's Resources folder once more.</summary>
    public void OnSceneUnloaded() => _reloadedThisScene.Clear();

    private ReferenceProbe Probe
        => _probe ??= new ReferenceProbe(_reloadedThisScene, _passResolved ??= new Dictionary<TemplateRef, Il2CppObjectBase>());

    // The top-level members a template's ops write to, under every alias of the type.
    // Passthrough for the clone applier's chained-clone rebase (which keeps authored
    // non-collection members and inherits the rest from the patched source).
    internal HashSet<string> TouchedTopLevelFields(string templateTypeName, string templateId)
        => _catalog.TouchedTopLevelFields(templateTypeName, templateId);

    /// <summary>Whether a block on the template would be held now, for a reason other than
    /// waiting on a template <paramref name="isExcepted"/> accepts. Asked before the type's
    /// own pass has run, when <see cref="HeldBlocks"/> does not know the template yet.</summary>
    public bool WouldHold(string templateTypeName, string templateId, Func<TemplateRef, bool> isExcepted)
    {
        foreach (var (_, blockOps) in SplitBlocks(_catalog.OperationsAcrossAliases(templateTypeName, templateId)))
        {
            foreach (var missing in MissingReferences(blockOps, Probe))
            {
                if (!isExcepted(missing))
                    return true;
            }
        }

        return false;
    }

    // Replay one template's compiled patch ops in authored order. The
    // chained-clone re-inheritance uses this: it rebuilds a clone from its
    // patched source and then re-applies the clone's own ops so its appends/sets
    // land on top of the inherited fields. Resolves the template by id the same
    // way TryApplyType does, so the ops apply against the CONCRETE wrapper type
    // (the field visitor reflects on the runtime type; a base DataTemplate
    // wrapper would expose none of the concrete members). Returns ops applied.
    //
    // The outcomes feed the self-check once per template: the first pass skipped
    // these ops, and the chained-clone pass runs again whenever a later poll
    // registers more templates, replaying the same ops onto the same clone.
    //
    // OnAfterDeserialize is re-invoked even when the template has no ops of its
    // own: the caller has just rebased the clone's fields from its patched
    // source, so any deserialise-derived caches are stale regardless of replay.
    internal int ReapplyTemplateEntry(string templateTypeName, string templateId, MelonLogger.Instance log)
    {
        var probe = Probe;
        if (probe.Lookup(templateTypeName, templateId, out var template, out var lookupError) != Presence.Found)
        {
            log.Warning($"Template re-inherit: no live {templateTypeName} '{templateId}' to replay onto ({lookupError ?? "not registered"}).");
            return 0;
        }

        template = AsConcrete(template);
        // The rebase rebuilt the template from its source: nothing written before is on it.
        _appliedSets.Remove(CanonicalKey(template, templateId));

        var applied = 0;
        var ops = _catalog.OperationsAcrossAliases(templateTypeName, templateId);
        if (ops.Count > 0)
        {
            // The replay is made of blocks like any pass: a mod's block whose values refer
            // to a template that does not exist yet is held, on the freshly rebased clone,
            // and the other mods' blocks replay. Each block's first replay is the one the
            // self-check counts, so a block held on the first replay counts when it runs.
            foreach (var (owner, blockOps) in SplitBlocks(ops))
            {
                // The replay owns the block now: one released to it is removed, one whose
                // reference is still missing is held again, under one entry.
                var released = HeldBlocks.Remove(owner, templateTypeName, templateId);
                var missing = MissingReferences(blockOps, probe);
                if (missing.Count > 0)
                {
                    Hold(new HeldPatchBlock(templateTypeName, templateId, owner, blockOps, missing) { Reported = released?.Reported ?? false }, log);
                    continue;
                }

                var appliedThisBlock = 0;
                var missingMember = 0;
                var conversionFailed = 0;
                ApplyOps(template, templateTypeName, templateId, blockOps, log, ref appliedThisBlock, ref missingMember, ref conversionFailed);
                applied += appliedThisBlock;
                if (_replayedEntries.Add(templateTypeName + "\0" + templateId + "\0" + owner))
                {
                    _appliedTotal += appliedThisBlock;
                    _missingMemberTotal += missingMember;
                    _conversionFailedTotal += conversionFailed;
                }
            }
        }

        TryInvokeOnAfterDeserialize(template, templateTypeName, templateId, log);
        return applied;
    }

    private int TryApplyType(
        string templateTypeName,
        Dictionary<string, List<LoadedPatchOperation>> patchesForType,
        MelonLogger.Instance log)
    {
        // Enumerate once only to trigger materialisation and detect "templates not
        // ready yet" (return 0, retry next scene) vs terminal type-resolution failure.
        // The pass's probe does it, sharing a ScriptableObject enumeration with the
        // lookups below, which read m_TemplateMaps for a DataTemplate and so see both
        // game-native templates and Jiangyu clones.
        var probe = Probe;
        IReadOnlyList<Il2CppObjectBase> liveTemplates;
        Type resolvedType;
        string resolveError;
        try
        {
            liveTemplates = probe.LiveTemplates(templateTypeName, out resolvedType, out resolveError);
        }
        catch (Exception ex)
        {
            // An enumeration that throws leaves the type pending for the next pass, and the
            // pass goes on to the other types and the held blocks.
            LoaderDebug.Write(log, $"Template patch: enumerating {templateTypeName} threw {ex.GetType().Name}: {ex.Message}; retried next pass.");
            return 0;
        }

        if (resolvedType == null)
        {
            var expectedOps = patchesForType.Values.Sum(inner => inner.Count);
            log.Warning(
                $"Template patch: cannot resolve type '{templateTypeName}' ({resolveError}); "
                + $"skipping {expectedOps} op(s).");
            _unresolvedTypeTotal++;
            _appliedTypes.Add(templateTypeName);
            return 0;
        }

        if (liveTemplates.Count == 0)
            return 0;

        var applied = 0;
        var missingTemplate = 0;
        var missingMember = 0;
        var conversionFailed = 0;

        foreach (var templateId in patchesForType.Keys)
        {
            // A template's ops under this name and under every alias of it (an ancestor or
            // descendant name, another spelling) are one contribution: taken together here,
            // in load order, and skipped by the alias's own pass.
            if (!_consumed.Add(templateTypeName + "\0" + templateId))
                continue;
            foreach (var alias in _catalog.AliasNames(templateTypeName))
                _consumed.Add(alias + "\0" + templateId);
            var ops = _catalog.OperationsAcrossAliases(templateTypeName, templateId);
            var targetKey = new TemplateRef(NormalisedTypeName(templateTypeName), templateId);

            var target = probe.Lookup(templateTypeName, templateId, out var template, out var lookupError);
            template = AsConcrete(template);
            if (DeferToChainedReplay?.Invoke(templateTypeName, templateId) == true)
            {
                // The replay that follows the clone pass owns these ops. A chained clone that
                // does not exist yet (its chain is held) is held here too, so the wait is
                // counted and reported, and released once the clone registers.
                if (target == Presence.Unresolvable)
                {
                    missingTemplate += ops.Count;
                    log.Warning(
                        $"Template patch: no live {templateTypeName} with m_ID '{templateId}' ({lookupError}); "
                        + $"skipping {ops.Count} op(s).");
                }
                else if (target == Presence.Missing)
                {
                    foreach (var (owner, blockOps) in SplitBlocks(ops))
                        Hold(new HeldPatchBlock(templateTypeName, templateId, owner, blockOps, new[] { targetKey }), log);
                }
                continue;
            }

            // A lookup that failed for a reason other than the id being absent is a
            // mismatch with the game, not a late registration.
            if (target == Presence.Unresolvable)
            {
                missingTemplate += ops.Count;
                log.Warning(
                    $"Template patch: no live {templateTypeName} with m_ID '{templateId}' ({lookupError}); "
                    + $"skipping {ops.Count} op(s).");
                continue;
            }

            // Each mod's ops on the template are one block. A block whose target, or a
            // template one of its values refers to, is absent is held whole: another loader
            // may register it on a later pass, and the block then applies in one piece.
            foreach (var (owner, blockOps) in SplitBlocks(ops))
            {
                var missing = MissingReferences(blockOps, probe);
                if (target == Presence.Missing)
                    missing.Insert(0, targetKey);
                if (missing.Count > 0)
                {
                    Hold(new HeldPatchBlock(templateTypeName, templateId, owner, blockOps, missing), log);
                    continue;
                }

                applied += ApplyTemplateOps(
                    template, templateTypeName, templateId, blockOps, log,
                    ref missingMember, ref conversionFailed);
            }
        }

        _appliedTotal += applied;
        _missingTemplateTotal += missingTemplate;
        _missingMemberTotal += missingMember;
        _conversionFailedTotal += conversionFailed;
        _appliedTypes.Add(templateTypeName);
        LoaderDebug.Write(log,
            $"Applied {applied} {templateTypeName} patch op(s). "
            + $"[skipped: missingTemplate={missingTemplate} missingMember={missingMember} conversion={conversionFailed}] "
            + $"[held: {HeldBlocks.Count} block(s)]");
        return applied;
    }

    // A missing template is recorded under its canonical type name, so a reference written
    // with a qualified or ancestor name and a clone directive name the same thing.
    private static string NormalisedTypeName(string templateTypeName)
        => TemplateRuntimeAccess.CanonicalTypeName(templateTypeName);

    // The template's key in the applied-set record: its live type, whatever name the ops
    // addressed it under.
    private static string CanonicalKey(Il2CppObjectBase template, string templateId)
        => template.GetType().FullName + "\0" + templateId;

    // A group's ops may be declared under a descendant of the name the target was looked up
    // by, so they apply against the wrapper of the object's live concrete type, which has
    // every member. A cast that fails leaves the looked-up wrapper in place.
    private static Il2CppObjectBase AsConcrete(Il2CppObjectBase template)
        => template != null && TryCastToLiveConcreteType(template, template.GetType(), out var cast, out _)
            ? (Il2CppObjectBase)cast
            : template;

    // The record a late block's override check reads. A set, clear, insert or remove resets
    // what sits under its slot (a set or clear replaces it, an insert or remove moves the
    // elements after it), so the sets recorded there no longer hold and are forgotten: from
    // then on the slot belongs to whichever block lands next, which is the landing-order
    // contract for a structural conflict between two mods' blocks on one template.
    private void NoteApplied(string canonicalKey, LoadedPatchOperation op)
    {
        _appliedSets.TryGetValue(canonicalKey, out var sets);
        if (sets != null && op.Op != CompiledTemplateOp.Append)
        {
            var reset = ResetPath(op);
            sets.RemoveAll(set => IsUnder(SlotPath(set), reset));
        }

        if (op.Op != CompiledTemplateOp.Set)
            return;
        if (sets == null)
            _appliedSets[canonicalKey] = sets = new List<LoadedPatchOperation>();
        sets.Add(op);
    }

    // The path an op writes, as segments: each descent step's field and index, the inner
    // field path's members and indexers, then the op's own index or cell. An op under
    // another has the other's path as a proper prefix.
    internal static List<string> SlotPath(LoadedPatchOperation op)
    {
        var path = new List<string>();
        if (op.Descent != null)
        {
            foreach (var step in op.Descent)
            {
                path.Add(step.Field);
                if (step.Index.HasValue)
                    path.Add("[" + step.Index.Value + "]");
            }
        }

        foreach (var segment in (op.FieldPath ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
            {
                path.Add(segment);
                continue;
            }

            path.Add(segment[..bracket]);
            foreach (var index in segment[bracket..].Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                path.Add("[" + index + "]");
        }

        if (op.Index.HasValue)
            path.Add("[" + op.Index.Value + "]");
        if (op.IndexPath != null)
        {
            foreach (var index in op.IndexPath)
                path.Add("[" + index + "]");
        }

        return path;
    }

    // The container an op resets: the op's own slot for a set or clear, the collection for an
    // insert or remove.
    internal static List<string> ResetPath(LoadedPatchOperation op)
    {
        var path = SlotPath(op);
        if (op.Op is CompiledTemplateOp.InsertAt or CompiledTemplateOp.Remove && path.Count > 0 && path[^1].StartsWith('['))
            path.RemoveAt(path.Count - 1);
        return path;
    }

    internal static bool IsUnder(IReadOnlyList<string> path, IReadOnlyList<string> prefix)
    {
        if (path.Count <= prefix.Count)
            return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(path[i], prefix[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    // Whether a later-loaded mod's set on the same slot has already applied to the template.
    // The op is then skipped: load order says the later value stays. Slots compare by path,
    // so a dotted write and a descent block on the same member are one slot.
    private bool OverriddenByApplied(string canonicalKey, LoadedPatchOperation op)
    {
        if (op.Op != CompiledTemplateOp.Set || !_appliedSets.TryGetValue(canonicalKey, out var applied))
            return false;
        var slot = SlotPath(op);
        foreach (var other in applied)
        {
            if (other.Sequence > op.Sequence && SlotPath(other).SequenceEqual(slot))
                return true;
        }

        return false;
    }

    private void Hold(HeldPatchBlock block, MelonLogger.Instance log)
    {
        if (!HeldBlocks.Add(block))
            return;
        LoaderDebug.Write(log,
            $"[{block.OwnerLabel}] Template patch '{block.TemplateType}:{block.TemplateId}': "
            + $"waiting on {string.Join(", ", block.Missing)}; holding {block.Ops.Count} op(s).");
    }

    // One block per mod: all of a mod's ops on the template, in the order given. The catalogue
    // lays a template's ops out mod by mod in load order, so the blocks come out in load order.
    internal static List<(string Owner, List<LoadedPatchOperation> Ops)> SplitBlocks(IReadOnlyList<LoadedPatchOperation> ops)
    {
        var blocks = new List<(string Owner, List<LoadedPatchOperation> Ops)>();
        var byOwner = new Dictionary<string, List<LoadedPatchOperation>>(StringComparer.Ordinal);
        foreach (var op in ops)
        {
            var owner = op.OwnerLabel ?? string.Empty;
            if (!byOwner.TryGetValue(owner, out var block))
            {
                byOwner[owner] = block = new List<LoadedPatchOperation>();
                blocks.Add((op.OwnerLabel, block));
            }

            block.Add(op);
        }

        return blocks;
    }

    // The templates the ops refer to that do not exist. A reference to a clone this loader
    // still holds counts as missing. A reference without a type (a manifest compiled without
    // the game assembly) is resolved when its op applies, as is one whose type the game no
    // longer has.
    private List<TemplateRef> MissingReferences(IEnumerable<LoadedPatchOperation> ops, ReferenceProbe probe)
    {
        var missing = new List<TemplateRef>();
        var seen = new HashSet<TemplateRef>();
        foreach (var op in ops)
        {
            foreach (var reference in TemplateReferences.Of(op.Value))
            {
                if (string.IsNullOrEmpty(reference.TemplateType) || string.IsNullOrEmpty(reference.TemplateId))
                    continue;
                var key = new TemplateRef(NormalisedTypeName(reference.TemplateType), reference.TemplateId);
                if (!seen.Add(key))
                    continue;
                if (CloneHeld?.Invoke(reference.TemplateType, reference.TemplateId) == true
                    || probe.Lookup(reference.TemplateType, reference.TemplateId, out _, out _) == Presence.Missing)
                    missing.Add(key);
            }
        }

        return missing;
    }

    private enum Presence
    {
        Found,
        Missing,
        // The type does not resolve or its lookup threw: a mismatch with the game, which
        // the op reports when it applies, not a template still to come.
        Unresolvable,
    }

    // Existence checks for the templates a pass refers to. A DataTemplate id is a map
    // lookup. A ScriptableObject id is matched by name against one enumeration of its type
    // per pass, keyed by the resolved type so every spelling shares it. On a miss the type's
    // Resources folder is loaded again, once per type per scene (the game unloads an asset
    // nothing references), and the enumeration taken again. A lookup that throws, or a type
    // that is neither, is a mismatch with the game, reported by the op when it applies, not
    // a template still to come, and it never aborts the pass.
    private sealed class ReferenceProbe
    {
        private readonly Dictionary<string, IReadOnlyList<Il2CppObjectBase>> _scriptableObjects = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reloadedThisScene;

        private readonly Dictionary<TemplateRef, Il2CppObjectBase> _resolved;

        public ReferenceProbe(HashSet<string> reloadedThisScene, Dictionary<TemplateRef, Il2CppObjectBase> resolved)
        {
            _reloadedThisScene = reloadedThisScene;
            _resolved = resolved;
        }

        private Presence Found(string templateTypeName, string templateId, Il2CppObjectBase template)
        {
            _resolved[new TemplateRef(TemplateRuntimeAccess.CanonicalTypeName(templateTypeName), templateId)] = template;
            return Presence.Found;
        }

        /// <summary>Drops the enumerations: a template registered since they were taken.</summary>
        public void Forget() => _scriptableObjects.Clear();

        /// <summary>The live templates of a type, the way <see cref="TemplateRuntimeAccess.GetAllTemplates"/>
        /// answers, a ScriptableObject type's enumeration shared with the lookups of the pass.</summary>
        public IReadOnlyList<Il2CppObjectBase> LiveTemplates(string templateTypeName, out Type resolvedType, out string resolveError)
        {
            resolvedType = TemplateRuntimeAccess.ResolveTemplateType(templateTypeName, out resolveError);
            if (resolvedType == null)
                return Array.Empty<Il2CppObjectBase>();
            if (TemplateRuntimeAccess.IsDataTemplateType(resolvedType) || !TemplateRuntimeAccess.IsScriptableObjectType(resolvedType))
                return TemplateRuntimeAccess.GetAllTemplates(templateTypeName, out resolvedType, out resolveError);
            return Enumerate(templateTypeName, resolvedType);
        }

        public Presence Lookup(string templateTypeName, string templateId, out Il2CppObjectBase template, out string error)
        {
            template = null;
            try
            {
                var resolvedType = TemplateRuntimeAccess.ResolveTemplateType(templateTypeName, out error);
                if (resolvedType == null)
                {
                    error ??= $"cannot resolve type '{templateTypeName}'.";
                    return Presence.Unresolvable;
                }

                if (TemplateRuntimeAccess.IsDataTemplateType(resolvedType))
                {
                    if (TemplateRuntimeAccess.TryGetTemplateById(resolvedType, templateId, out template, out error))
                        return Found(templateTypeName, templateId, template);
                    return string.IsNullOrEmpty(error) ? Presence.Missing : Presence.Unresolvable;
                }

                if (!TemplateRuntimeAccess.IsScriptableObjectType(resolvedType))
                {
                    error = $"template type {resolvedType.FullName} is neither DataTemplate nor ScriptableObject.";
                    return Presence.Unresolvable;
                }

                var live = Enumerate(templateTypeName, resolvedType);
                if (TemplateRuntimeAccess.TryFindByName(live, templateId, out template))
                    return Found(templateTypeName, templateId, template);
                if (!_reloadedThisScene.Add(resolvedType.FullName))
                    return Presence.Missing;

                live = TemplateRuntimeAccess.ReloadScriptableObjects(templateTypeName, resolvedType);
                _scriptableObjects[resolvedType.FullName] = live;
                return TemplateRuntimeAccess.TryFindByName(live, templateId, out template)
                    ? Found(templateTypeName, templateId, template)
                    : Presence.Missing;
            }
            catch (Exception ex)
            {
                error = $"lookup threw: {ex.Message}";
                return Presence.Unresolvable;
            }
        }

        // One enumeration per ScriptableObject type per pass. The probe alone decides the
        // reloads: an empty type reloads its folder once per scene, here, and a miss reloads
        // once per scene in Lookup, the same allowance.
        private IReadOnlyList<Il2CppObjectBase> Enumerate(string templateTypeName, Type resolvedType)
        {
            var key = resolvedType.FullName;
            if (_scriptableObjects.TryGetValue(key, out var live))
                return live;
            live = TemplateRuntimeAccess.GetAllTemplates(templateTypeName, out _, out _, reloadOnEmpty: false);
            if (live.Count == 0 && _reloadedThisScene.Add(key))
                live = TemplateRuntimeAccess.ReloadScriptableObjects(templateTypeName, resolvedType);
            _scriptableObjects[key] = live;
            return live;
        }
    }

    /// <summary>Checks each held block again and applies, in order and whole, every block
    /// whose template and referenced templates now all exist. Blocks land in load order. The
    /// template of each block that applies is added to <paramref name="registered"/> under
    /// <see cref="LateTemplateSet.Key"/>. <paramref name="only"/> restricts the pass to the
    /// blocks on the templates it accepts (type name and id), null tries every held block.
    /// Returns the ops applied.</summary>
    public int RetryLate(MelonLogger.Instance log, ISet<string> registered, Func<string, string, bool> only = null)
    {
        if (HeldBlocks.IsEmpty)
            return 0;

        var probe = Probe;
        var applied = 0;
        foreach (var block in HeldBlocks.Snapshot().OrderBy(held => held.Ops[0].Sequence))
        {
            if (only != null && !only(block.TemplateType, block.TemplateId))
                continue;
            var target = probe.Lookup(block.TemplateType, block.TemplateId, out var template, out _);
            template = AsConcrete(template);
            if (target == Presence.Unresolvable)
            {
                // The target's type or lookup is broken: a mismatch with the game, not a
                // template still to come. The block leaves the held set and is reported.
                HeldBlocks.Remove(block);
                _missingTemplateTotal += block.Ops.Count;
                log.Warning(
                    $"[{block.OwnerLabel}] Template patch '{block.TemplateType}:{block.TemplateId}': "
                    + $"the template cannot be looked up; skipping {block.Ops.Count} op(s).");
                continue;
            }

            // What the block waits on is taken again in full, so the report names what is
            // still missing rather than what was when the block was first held.
            var missing = MissingReferences(block.Ops, probe);
            if (target == Presence.Missing)
                missing.Insert(0, new TemplateRef(NormalisedTypeName(block.TemplateType), block.TemplateId));
            if (missing.Count > 0)
            {
                block.Missing = missing;
                continue;
            }

            // A chained clone's ops belong to the rebase-and-replay that follows the pass:
            // the template is marked changed here, the rebase rebuilds it from its source,
            // and the replay applies the block and removes it. Until that replay has run the
            // block stays held, so a rebuild that cannot run this pass is retried by the next.
            // Applying it here as well would run it twice on the members the rebase keeps.
            if (DeferToChainedReplay?.Invoke(block.TemplateType, block.TemplateId) == true)
            {
                registered?.Add(block.TemplateKey);
                if (!block.ReleasedToReplay)
                {
                    block.ReleasedToReplay = true;
                    log.Msg(
                        $"[{block.OwnerLabel}] Template patch '{block.TemplateType}:{block.TemplateId}': "
                        + "every template it refers to is registered, replayed with its clone chain.");
                }

                continue;
            }

            HeldBlocks.Remove(block);
            registered?.Add(block.TemplateKey);

            var missingMember = 0;
            var conversionFailed = 0;
            var appliedThisBlock = ApplyTemplateOps(
                template, block.TemplateType, block.TemplateId, block.Ops, log,
                ref missingMember, ref conversionFailed);
            _appliedTotal += appliedThisBlock;
            _missingMemberTotal += missingMember;
            _conversionFailedTotal += conversionFailed;
            applied += appliedThisBlock;
            log.Msg(
                $"[{block.OwnerLabel}] Template patch '{block.TemplateType}:{block.TemplateId}': "
                + $"every template it refers to is registered, applied {appliedThisBlock} of {block.Ops.Count} op(s).");
        }

        return applied;
    }

    /// <summary>Warns for each held block not yet reported this scene, naming what it waits
    /// on. Held ops count as waiting in the self-check, not as mismatches. The block stays
    /// held, so a later pass can still apply it.</summary>
    public void ReportLate(MelonLogger.Instance log)
    {
        foreach (var block in HeldBlocks.TakeUnreported())
        {
            log.Warning(
                $"[{block.OwnerLabel}] Template patch '{block.TemplateType}:{block.TemplateId}': "
                + $"waiting on {string.Join(", ", block.Missing)}; {block.Ops.Count} op(s) held. "
                + "Applied once every one of them is registered.");
        }
    }

    // Applies a block's ops in order. A set that lands after a later-loaded mod's set on the
    // same slot (its block was held while the other applied) is skipped and counted as
    // applied: the slot holds the later value, which is what load order promises.
    private void ApplyOps(
        Il2CppObjectBase template,
        string templateTypeName,
        string templateId,
        IReadOnlyList<LoadedPatchOperation> ops,
        MelonLogger.Instance log,
        ref int applied,
        ref int missingMember,
        ref int conversionFailed)
    {
        var canonicalKey = CanonicalKey(template, templateId);
        foreach (var op in ops)
        {
            if (OverriddenByApplied(canonicalKey, op))
            {
                applied++;
                LoaderDebug.Write(log, FormatPrefix(templateTypeName, templateId, op) + "a later-loaded mod's value is on the slot already; skipped.");
                continue;
            }

            ApplyOutcome outcome;
            try
            {
                outcome = TryApplyOperation(template, templateTypeName, templateId, op, _assetResolver, log);
            }
            catch (Exception ex)
            {
                // An op that throws is a mismatch with the game, like one that fails cleanly:
                // counted, and the block's other ops still apply.
                log.Warning(FormatPrefix(templateTypeName, templateId, op) + $"threw {ex.GetType().Name}: {ex.Message}.");
                outcome = ApplyOutcome.ConversionFailed;
            }

            switch (outcome)
            {
                case ApplyOutcome.Applied:
                    applied++;
                    NoteApplied(canonicalKey, op);
                    break;
                case ApplyOutcome.MemberMissing:
                    missingMember++;
                    break;
                case ApplyOutcome.ConversionFailed:
                    conversionFailed++;
                    break;
            }
        }
    }

    private int ApplyTemplateOps(
        Il2CppObjectBase template,
        string templateTypeName,
        string templateId,
        IReadOnlyList<LoadedPatchOperation> ops,
        MelonLogger.Instance log,
        ref int missingMember,
        ref int conversionFailed)
    {
        var applied = 0;
        ApplyOps(template, templateTypeName, templateId, ops, log, ref applied, ref missingMember, ref conversionFailed);

        // After applying ops to a template, invoke OnAfterDeserialize if
        // the type exposes it. Unity calls OnAfterDeserialize when an
        // asset is loaded; subsequent runtime field writes don't trigger
        // it again. Types like Stem.SoundBank keep a derived runtime
        // cache (SoundBankRuntime: id->Sound dictionary) that's built
        // from the serialised list during OnAfterDeserialize. Patching
        // the list without re-running the rebuild leaves the cache
        // stale, so playback lookups miss our new entries. Generic
        // reflection invocation keeps the hook usable for any type
        // following the ISerializationCallbackReceiver convention.
        if (applied > 0)
        {
            // SoundBank-specific pre-OnAfterDeserialize alignment.
            // busIndices is a parallel array to sounds; if the modder's
            // patch grew sounds[] without growing busIndices,
            // OnAfterDeserialize would throw IndexOutOfRange. Align
            // busIndices to sounds.Count (extending with 0 = default
            // bus) so the modder never has to do this manually.
            if (TemplateRuntimeAccess.IsSoundBankTypeName(templateTypeName))
                TrySoundBankAlignBusIndices(template, templateId, log);

            TryInvokeOnAfterDeserialize(template, templateTypeName, templateId, log);
            // Type-specific post-apply wiring. SoundBank needs each new
            // Sound back-pointed at the bank and registered in the
            // SoundBankRuntime cache that playback consults. The bank's
            // own AddSound(Sound) method handles this, but we already
            // appended to sounds[] manually; calling AddSound here just
            // does the wiring side-effects (m_Bank, m_Bus, runtime
            // register) without re-appending. See SoundBank type dump:
            // AddSound checks IndexOfSound and short-circuits the
            // append when the sound is already in the list.
            if (TemplateRuntimeAccess.IsSoundBankTypeName(templateTypeName))
                TrySoundBankPostApplyWiring(template, templateId, log);
        }

        return applied;
    }

    private static string FormatPrefix(string templateTypeName, string templateId, LoadedPatchOperation op)
        => $"[{op.OwnerLabel}] Template patch '{templateTypeName}:{templateId}.{op.FieldPath}': ";

    // SoundBank-specific pre-OnAfterDeserialize alignment. busIndices is a
    // parallel array to sounds (one int per sound, pointing into buses[]).
    // OnAfterDeserialize iterates both in lockstep and throws
    // IndexOutOfRange if busIndices is shorter than sounds. When a modder
    // appends to sounds without explicitly appending matching busIndices
    // entries, align the lengths by extending busIndices with 0 (default
    // bus). One resize to the target length, so a bank costs a single
    // array rebuild no matter how many sounds the patch appended.
    private static void TrySoundBankAlignBusIndices(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase bank,
        string bankId,
        MelonLogger.Instance log)
    {
        if (bank == null) return;

        if (!TryReadMember(bank, "sounds", out var soundsObj, out var soundsType, out _) || soundsObj == null)
            return;
        if (!TryReadMember(bank, "busIndices", out var busObj, out _, out _) || busObj == null)
            return;

        var soundsCount = (int)(soundsType.GetProperty("Count")?.GetValue(soundsObj) ?? 0);

        // An unreadable length can't be told apart from an aligned one, and
        // resizing on the assumption of 0 would drop every existing entry.
        var busType = busObj.GetType();
        var busLengthProp = busType.GetProperty("Length") ?? busType.GetProperty("Count");
        if (busLengthProp == null)
        {
            log.Warning(
                $"Template patch 'SoundBank:{bankId}': busIndices ({busType.FullName}) exposes no "
                + "Length or Count; leaving it untouched. OnAfterDeserialize may throw on a length mismatch.");
            return;
        }

        int busCount;
        try
        {
            busCount = (int)(busLengthProp.GetValue(busObj) ?? 0);
        }
        catch
        {
            return;
        }

        if (busCount >= soundsCount) return;

        if (!TryGetWritableMember(bank, "busIndices", out _, out var busSetter, out _))
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': busIndices is not writable; cannot align to sounds.Count.");
            return;
        }

        // New entries stay at the element default, which is bus 0.
        if (!Il2CppCollectionReflection.TryResizeArray(busObj, soundsCount, out var resized, out var resizeError))
        {
            log.Warning(
                $"Template patch 'SoundBank:{bankId}': cannot resize busIndices to {soundsCount} "
                + $"({resizeError}); OnAfterDeserialize may throw on the length mismatch.");
            return;
        }

        try
        {
            busSetter(resized);
        }
        catch (Exception ex)
        {
            log.Warning(
                $"Template patch 'SoundBank:{bankId}': writing the resized busIndices threw "
                + $"{ex.GetType().Name}: {ex.Message}.");
            return;
        }

        log.Debug(
            $"Template patch 'SoundBank:{bankId}': auto-extended busIndices by {soundsCount - busCount} "
            + $"(sounds.Count={soundsCount}, prior busIndices.Length={busCount}).");
    }

    // SoundBank-specific post-apply fixup. Iterates bank.sounds[]; for any
    // Sound whose m_Bank back-pointer is null (the shape modder-appended
    // entries land in), invokes SoundBank.AddSound(sound) so the bank
    // performs the same wiring vanilla Sounds receive: set m_Bank, set
    // m_Bus from the default bus, and register the Sound with the
    // SoundBankRuntime cache that playback consults at lookup time.
    private static void TrySoundBankPostApplyWiring(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase bank,
        string bankId,
        MelonLogger.Instance log)
    {
        if (bank == null) return;

        if (!TryReadMember(bank, "sounds", out var soundsObj, out var soundsType, out var readError) || soundsObj == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': cannot read sounds for post-apply wiring ({readError}).");
            return;
        }

        var countProp = soundsType.GetProperty("Count");
        var getItem = soundsType.GetMethod("get_Item", new[] { typeof(int) });
        if (countProp == null || getItem == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': sounds collection {soundsType.FullName} has no Count/get_Item; skipping post-apply wiring.");
            return;
        }

        var bankType = bank.GetType();
        var addSound = bankType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "AddSound"
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.Name == "Sound");
        if (addSound == null)
        {
            log.Warning($"Template patch 'SoundBank:{bankId}': no AddSound(Sound) method; skipping post-apply wiring.");
            return;
        }

        var count = (int)countProp.GetValue(soundsObj);
        // Il2CppInterop wrappers expose typed members as PROPERTIES, not
        // .NET fields. The "m_Bank" field shown by the catalogue is a
        // property on the wrapper that delegates to the native field.
        // GetField("m_Bank") returns the wrapper's NativeFieldInfoPtr
        // IntPtr at best, not the managed SoundBank value we want.
        System.Reflection.PropertyInfo bankProp = null;
        System.Reflection.PropertyInfo idProp = null;
        for (var i = 0; i < count; i++)
        {
            object sound;
            try { sound = getItem.Invoke(soundsObj, new object[] { i }); }
            catch { continue; }
            if (sound == null) continue;

            var soundType = sound.GetType();
            bankProp ??= soundType.GetProperty("m_Bank") ?? soundType.GetProperty("Bank");
            idProp ??= soundType.GetProperty("id") ?? soundType.GetProperty("ID");
            if (bankProp == null) continue;

            object currentBank;
            try { currentBank = bankProp.GetValue(sound); }
            catch { continue; }
            if (currentBank != null) continue;

            int soundId = -1;
            try { soundId = idProp != null ? (int)idProp.GetValue(sound) : -1; }
            catch { }

            try
            {
                addSound.Invoke(bank, new[] { sound });
                log.Debug($"Template patch 'SoundBank:{bankId}': wired Sound id={soundId} (index {i}) via AddSound.");
            }
            catch (Exception ex)
            {
                var root = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                log.Warning($"Template patch 'SoundBank:{bankId}': AddSound at index {i} (sound id={soundId}) threw {root.GetType().Name}: {root.Message}.");
            }
        }
    }

    private static void TryInvokeOnAfterDeserialize(
        Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase template,
        string templateTypeName,
        string templateId,
        MelonLogger.Instance log)
    {
        if (template == null) return;
        var type = template.GetType();
        var method = type.GetMethod(
            "OnAfterDeserialize",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (method == null) return;
        try
        {
            method.Invoke(template, null);
        }
        catch (Exception ex)
        {
            // TargetInvocationException wraps the real exception; surface the
            // inner cause and stack so callers can diagnose null fields,
            // index-out-of-bounds in parallel arrays, etc.
            var root = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException
                : ex;
            log.Warning(
                $"Template patch '{templateTypeName}:{templateId}': OnAfterDeserialize threw {root.GetType().Name}: {root.Message}. "
                + "Derived state may be stale; subsequent lookups against patched collections may miss new entries.");
            if (!string.IsNullOrEmpty(root.StackTrace))
                log.Warning($"  at: {root.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    // For Il2Cpp wrappers, identity is the native object pointer, not
    // Il2CppObjectBase.Equals (which compares per-wrapper GC handles and so
    // returns false for two wrappers pooled over the same native object).
    // Everything else (scalar boxed values, strings, enums) goes through
    // object.Equals as normal.
    //
    // Il2Cpp value types are the exception to the pointer rule. A non-blittable
    // value type is projected as a class over a boxed native instance, so a
    // collection Add copies the value into the collection's own storage and the
    // readback indexer boxes a fresh copy at a new address. The two pointers
    // never match, including when the write landed, so a value type compares
    // field by field through its generated properties instead.
    private static bool ReadbackMatches(object written, object readback)
    {
        if (written is Il2CppObjectBase writtenObj && readback is Il2CppObjectBase readbackObj)
        {
            if (writtenObj.Pointer == readbackObj.Pointer)
                return true;

            var type = written.GetType();
            return readback.GetType() == type
                   && IsIl2CppValueType(type)
                   && GeneratedPropertiesMatch(written, readback);
        }

        return Equals(written, readback);
    }

    // Ask the IL2CPP runtime whether a wrapper type projects a value type.
    // Type.IsValueType answers only for the blittable ones, which
    // Il2CppInterop projects as real C# structs. The rest arrive as classes
    // deriving from Il2CppSystem.ValueType, and the native class flag is what
    // separates those from ordinary reference wrappers.
    private static bool IsIl2CppValueType(Type type)
    {
        try
        {
            var klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
            return klass != IntPtr.Zero && IL2CPP.il2cpp_class_is_valuetype(klass);
        }
        catch
        {
            return false;
        }
    }

    // Compare two wrappers over one type field by field, reading through the
    // properties Il2CppInterop generates for the native fields. Each pair goes
    // back through ReadbackMatches, so a nested value type recurses and a
    // reference-typed field compares by pointer. Recursion terminates: a value
    // type cannot contain itself.
    //
    // A getter that throws leaves that field unverified and the walk carries
    // on, and a type projecting no readable field reports a match, which is the
    // same answer as skipping verification. Both are the right trade for a
    // diagnostic, where a warning on every write costs more than a missed one.
    // Internal so the reflection can be tested against plain managed fixtures
    // without a live game.
    internal static bool GeneratedPropertiesMatch(object written, object readback)
    {
        foreach (var property in GeneratedFieldProperties(written.GetType()))
        {
            object writtenValue;
            object readbackValue;
            try
            {
                writtenValue = property.GetValue(written);
                readbackValue = property.GetValue(readback);
            }
            catch
            {
                continue;
            }

            if (!ReadbackMatches(writtenValue, readbackValue))
                return false;
        }

        return true;
    }

    // The properties Il2CppInterop projects from a type's native fields.
    // Pointer and WasCollected come from Il2CppObjectBase rather than the
    // native type and differ between any two wrappers, so anything declared
    // there is left out along with the indexers and the write-only properties.
    private static IEnumerable<PropertyInfo> GeneratedFieldProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0
                               && property.GetGetMethod() != null
                               && (property.DeclaringType == null
                                   || !property.DeclaringType.IsAssignableFrom(typeof(Il2CppObjectBase))));

    // Identity formatter for log lines. Each template base class has a
    // different identity field, so dispatch by type rather than probe-then-
    // guess. DataTemplate subtypes expose `m_ID`; non-DataTemplate
    // ScriptableObjects expose `Object.name`; freshly-constructed composite
    // support types (e.g. a new `Perk`) have no identity — log the wrapper
    // type name alone, which is accurate (it's a brand-new object).
    private static string FormatValue(object value)
    {
        if (value is not Il2CppObjectBase il2Cpp)
            return value?.ToString() ?? "null";

        var typeName = value.GetType().Name;
        string id;
        if (typeof(Il2CppMenace.Tools.DataTemplate).IsAssignableFrom(value.GetType()))
            id = TemplateRuntimeAccess.ReadTemplateId(il2Cpp);
        else if (il2Cpp is UnityEngine.Object unityObj)
            id = unityObj.name;
        else
            id = null;

        if (!string.IsNullOrWhiteSpace(id))
            return $"{typeName} '{id}'";

        // A value type carries no identity of its own, so the type name alone
        // tells a reader nothing about which entry a line is about. Spell out
        // the fields instead. Reference-typed fields recurse to pick up their
        // template ids.
        if (IsIl2CppValueType(value.GetType()))
        {
            var fields = new List<string>();
            foreach (var property in GeneratedFieldProperties(value.GetType()))
            {
                try
                {
                    fields.Add($"{property.Name}={FormatValue(property.GetValue(value))}");
                }
                catch (Exception ex)
                {
                    fields.Add($"{property.Name}=<{ex.GetType().Name}>");
                }
            }

            if (fields.Count > 0)
                return $"{typeName}({string.Join(", ", fields)})";
        }

        return typeName;
    }

    private enum ApplyOutcome
    {
        Applied,
        MemberMissing,
        ConversionFailed,
    }
}

/// <summary>How the template patch ops fared against the live game. A non-zero
/// <see cref="Mismatches"/> means the running game no longer matches the templates
/// the mods were built against (a type, template id, field, or value type changed).
/// <see cref="Late"/> ops sit in a held block, waiting on a template another loader has
/// not registered. They are neither applied nor mismatches.</summary>
internal readonly record struct TemplateApplySelfCheck(
    int Applied,
    int UnresolvedTypes,
    int MissingTemplates,
    int MissingMembers,
    int ConversionFailures,
    int Late)
{
    public int Mismatches => UnresolvedTypes + MissingTemplates + MissingMembers + ConversionFailures;
}
