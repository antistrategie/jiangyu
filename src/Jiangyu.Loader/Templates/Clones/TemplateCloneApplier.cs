using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Jiangyu.Loader.Logging;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;
using Il2CppDictionary = Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppMenace.Tools.DataTemplate>;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: Template cloning deep-copies an existing live
// ScriptableObject-derived DataTemplate via UnityEngine.Object.Instantiate,
// overwrites the base-class m_ID via IL2CPP native field lookup by name
// (walks the class hierarchy via il2cpp_class_get_parent because m_ID is
// declared on the DataTemplate base), and registers the clone into
// DataTemplateLoader.m_TemplateMaps[type][cloneId] so Get<T>(id) / TryGet<T>
// resolve it. HideFlags.DontUnloadUnusedAsset prevents scene-change GC.
// m_TemplateArrays (the GetAll<T> enumeration backing store) is extended in
// the same pass so GetAll<T>() consumers see the clone. The clone is also
// mirrored into every ancestor m_TemplateMaps / m_TemplateArrays slot up to
// DataTemplate that the game has already materialised, because both dicts
// are keyed by exact runtime type and gameplay code typically enumerates by
// a base type (e.g. GetAll<BaseItemTemplate>() for the BlackMarket pool).
// Contract rationale and verification live in docs/research/verified/template-cloning.md.

/// <summary>
/// Applies merged template clone directives at runtime by deep-copying live
/// templates and registering the copies with <c>DataTemplateLoader</c>. Clones
/// must run before <see cref="TemplatePatchApplier"/> so patches can target
/// the newly registered clone IDs.
/// </summary>
internal sealed class TemplateCloneApplier
{
    private readonly TemplateCloneCatalog _catalog;

    // Tracks types whose apply pass has run this tick-cycle. Per-directive
    // idempotency lives in TryApplyType's innerMap.TryGetValue check, not here.
    private readonly HashSet<string> _appliedTypes = new(StringComparer.Ordinal);
    private readonly List<(UnityEngine.Object Clone, Type ResolvedType, string CloneId)> _pendingSoundBankRegistrations = new();

    public TemplateCloneApplier(TemplateCloneCatalog catalog)
    {
        _catalog = catalog;
    }

    public bool HasPendingClones
    {
        get
        {
            if (!_catalog.HasClones)
                return false;

            foreach (var typeEntry in _catalog.EnumerateByType())
            {
                if (!_appliedTypes.Contains(typeEntry.Key))
                    return true;
            }

            return false;
        }
    }

    public bool HasConfiguredClones => _catalog.HasClones;

    public void ResetApplyState() => _appliedTypes.Clear();

    /// <summary>Runs the post-patch registration steps for type-specific
    /// runtime indexes that the clone applier itself can't populate at
    /// clone time. Called from <see cref="Runtime.ReplacementCoordinator"/>
    /// after <c>_templatePatchApplier.TryApply</c> finishes, so any patches
    /// the modder applied (in particular SoundBank.bankId rewrites) have
    /// already landed.</summary>
    public void RunPostPatchHooks(LoaderLog log)
    {
        // Re-deserialise typed Requirements/m_Nodes on every registered
        // ConversationTemplate clone against its now-patched serialised
        // string lists. Clones are registered during the clone-applier
        // pass which runs BEFORE the patch applier; the per-injection
        // refresh in ConversationManagerRegistry captures pre-patch
        // typed state, so this hook is what closes the loop.
        ConversationManagerRegistry.OnPostPatch();

        if (_pendingSoundBankRegistrations.Count == 0) return;
        foreach (var pending in _pendingSoundBankRegistrations)
        {
            TryRegisterSoundBankWithStem(pending.Clone, pending.ResolvedType, log);
        }
        _pendingSoundBankRegistrations.Clear();
    }

    // Members never re-inherited: object identity and the Odin serialisation
    // blob (copying it would drag the source's id and serialised state in).
    private static readonly HashSet<string> NonInheritableMembers = new(StringComparer.Ordinal)
    {
        "name", "hideFlags", "m_CachedPtr", "Pointer", "m_ID", "serializationData",
    };

    /// <summary>
    /// Phase 3: rebuild each clone whose source is itself a mod clone so it
    /// inherits the source AS AUTHORED, then re-applies its own ops on top.
    ///
    /// A clone is <c>Object.Instantiate</c>d from its source during the clone
    /// pass, which runs before ANY patch op. So a clone of another mod clone
    /// carries only the source's vanilla-derived base, never the source's own
    /// appends/sets (those land in the later patch pass) — the SSR skill that
    /// clones a modded skill lost the modded EventHandlers, keeping only the
    /// vanilla knife handler. This pass, after the patch applier:
    /// <list type="number">
    /// <item>copies each collection field from the fully-patched source (deep,
    ///   owned elements copied) so the clone starts from the source's real
    ///   list, and inherits any non-collection member the clone did not author
    ///   itself;</item>
    /// <item>replays the clone's own patch ops on top, so its appends land
    ///   after the inherited elements and its sets/index-writes/clears apply to
    ///   the inherited base.</item>
    /// </list>
    /// A third pass rather than reordering the two existing ones: a clone's
    /// inline ops can reference sibling clones declared later, so applying them
    /// at clone-creation time would break those references, and standalone
    /// <c>patch</c> blocks must stay order-independent. Running after everything
    /// exists sidesteps both.
    /// </summary>
    public int ReinheritChainedClones(TemplatePatchApplier patchApplier, LoaderLog log)
    {
        if (patchApplier == null || !_catalog.HasClones)
            return 0;

        var done = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var typeEntry in _catalog.EnumerateByType())
            total += ReinheritType(typeEntry.Key, typeEntry.Value, patchApplier, done, log);
        return total;
    }

    private int ReinheritType(
        string templateTypeName,
        Dictionary<string, LoadedCloneDirective> directives,
        TemplatePatchApplier patchApplier,
        HashSet<string> done,
        LoaderLog log)
    {
        var liveTemplates = TemplateRuntimeAccess.GetAllTemplates(templateTypeName, out var resolvedType, out _);
        // Only DataTemplate types are m_TemplateMaps-registered and reachable
        // by id here; the ScriptableObject-only path has no chained-clone use.
        if (resolvedType == null
            || liveTemplates.Count == 0
            || !typeof(DataTemplate).IsAssignableFrom(resolvedType)
            || !TryGetTemplateMap(resolvedType, out var innerMap))
            return 0;

        var applied = 0;
        foreach (var directive in directives.Values)
            applied += ReinheritOne(templateTypeName, resolvedType, innerMap, directives, directive.CloneId, patchApplier, done, log);
        return applied;
    }

    // Re-inherit one clone, first ensuring its source (if itself a clone of
    // this type) has been re-inherited, so a 3-deep chain resolves base-first
    // regardless of directive iteration order.
    private int ReinheritOne(
        string templateTypeName,
        Type resolvedType,
        Il2CppDictionary innerMap,
        Dictionary<string, LoadedCloneDirective> directives,
        string cloneId,
        TemplatePatchApplier patchApplier,
        HashSet<string> done,
        LoaderLog log)
    {
        if (!done.Add(templateTypeName + "\0" + cloneId))
            return 0;
        if (!directives.TryGetValue(cloneId, out var directive))
            return 0;
        var sourceId = directive.SourceId;
        // No source (a 'create'), or a vanilla source: the base instantiate
        // already gave the correct inheritance, nothing to re-sync.
        if (string.IsNullOrEmpty(sourceId) || !directives.ContainsKey(sourceId))
            return 0;

        var applied = ReinheritOne(templateTypeName, resolvedType, innerMap, directives, sourceId, patchApplier, done, log);

        // Set after the source recursion, which leaves the log tagged with the
        // source directive's owner.
        log.Mod = directive.OwnerLabel;

        if (!innerMap.TryGetValue(cloneId, out var cloneObj) || cloneObj == null
            || !innerMap.TryGetValue(sourceId, out var sourceObj) || sourceObj == null)
            return applied;

        var touched = patchApplier.TouchedTopLevelFields(templateTypeName, cloneId);
        RebaseOntoSource(cloneObj, sourceObj, resolvedType, touched, cloneId, log);
        // Re-apply the clone's own ops so its appends/sets land on top of the
        // freshly inherited base (resolves the concrete template by id itself).
        patchApplier.ReapplyTemplateEntry(templateTypeName, cloneId, log.Raw);
        log.Debug($"Template clone '{cloneId}': re-inherited from cloned source '{sourceId}' and replayed own ops.");
        return applied + 1;
    }

    // Reset the clone's fields to the patched source's values ahead of an op
    // replay: every collection field takes a fresh deep copy of the source's
    // list (owned elements copied, so mutating one never leaks to the other),
    // and each non-collection member the clone did NOT author itself inherits
    // the source's value. Authored non-collection members are left alone — the
    // clone already holds its own value from the clone pass, and copying a
    // source sub-object the clone then patches would corrupt the source.
    private static void RebaseOntoSource(
        Il2CppObjectBase clone, Il2CppObjectBase source, Type concreteType,
        HashSet<string> touched, string cloneId, LoaderLog log)
    {
        var cloneTarget = ReflectionTargetForConcreteType(clone, concreteType, cloneId, log);
        var sourceTarget = ReflectionTargetForConcreteType(source, concreteType, cloneId, log);
        if (cloneTarget == null || sourceTarget == null)
            return;

        var type = cloneTarget.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length != 0 || !prop.CanRead || !prop.CanWrite) continue;
            if (!seen.Add(prop.Name)) continue;
            CopyFromSource(cloneTarget, sourceTarget,
                () => prop.GetValue(sourceTarget), () => prop.GetValue(cloneTarget),
                v => prop.SetValue(cloneTarget, v), prop.PropertyType, prop.Name, touched, cloneId, log);
        }

        foreach (var field in type.GetFields(flags))
        {
            if (field.IsInitOnly || !seen.Add(field.Name)) continue;
            CopyFromSource(cloneTarget, sourceTarget,
                () => field.GetValue(sourceTarget), () => field.GetValue(cloneTarget),
                v => field.SetValue(cloneTarget, v), field.FieldType, field.Name, touched, cloneId, log);
        }
    }

    private static void CopyFromSource(
        object cloneTarget, object sourceTarget,
        Func<object> sourceReader, Func<object> cloneReader, Action<object> cloneWriter,
        Type memberType, string memberName, HashSet<string> touched, string cloneId, LoaderLog log)
    {
        if (NonInheritableMembers.Contains(memberName))
            return;

        var listElement = Il2CppCollectionReflection.GetListElementType(memberType);
        var isCollection = listElement != null || Il2CppCollectionReflection.GetArrayElementType(memberType) != null;

        // Non-collection the clone authored itself: keep the clone's value. The
        // replay re-applies its ops, and this avoids sharing a source sub-object
        // the clone patches.
        if (!isCollection && touched.Contains(memberName))
            return;

        object sourceValue;
        try { sourceValue = sourceReader(); }
        catch { return; }

        try { cloneWriter(sourceValue); }
        catch (Exception ex)
        {
            log.Warning($"Template clone '{cloneId}': rebase of '{memberName}' threw: {ex.Message}");
            return;
        }

        // The clone now shares the source's container/elements: reallocate the
        // container so the clone owns it, then deep-copy owned elements. The op
        // replay then mutates only the clone's copies.
        if (isCollection)
        {
            TryReallocCollectionContainer(cloneReader, cloneWriter, memberType, memberName, cloneId, log);
            if (listElement != null && IsOwnedElementType(listElement))
                TryDeepCopyMember(cloneTarget, listElement, cloneReader, memberName, cloneId, log, out _);
        }
    }

    public int TryApply(LoaderLog log)
    {
        if (!_catalog.HasClones)
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

    // Order clone directives so a clone-of-clone always applies after its source clone, wherever
    // the two are declared. sourceRegistered reports whether an id already exists in the live
    // template map (a vanilla template, or a clone registered in an earlier session). Anything
    // whose source is neither live nor a sibling clone lands in unresolved (missing source), as do
    // cyclic chains; the caller warns for each.
    internal static List<LoadedCloneDirective> OrderBySourceAvailability(
        IEnumerable<LoadedCloneDirective> directives,
        Func<string, bool> sourceRegistered,
        out List<LoadedCloneDirective> unresolved)
    {
        var ordered = new List<LoadedCloneDirective>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        unresolved = new List<LoadedCloneDirective>();
        var pending = new List<LoadedCloneDirective>(directives);
        for (var progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            var deferred = new List<LoadedCloneDirective>();
            foreach (var directive in pending)
            {
                if (string.IsNullOrEmpty(directive.SourceId)
                    || sourceRegistered(directive.SourceId)
                    || emitted.Contains(directive.SourceId))
                {
                    ordered.Add(directive);
                    emitted.Add(directive.CloneId);
                    progress = true;
                }
                else if (!pending.Exists(d => d.CloneId == directive.SourceId))
                {
                    // The source is neither live nor a sibling clone: it will never appear.
                    unresolved.Add(directive);
                    progress = true;
                }
                else
                {
                    // The source is a later sibling clone: defer to the next round.
                    deferred.Add(directive);
                }
            }
            pending = deferred;
        }
        // Whatever remains forms a cycle (or clones itself).
        unresolved.AddRange(pending);
        return ordered;
    }

    private int TryApplyType(
        string templateTypeName,
        Dictionary<string, LoadedCloneDirective> directives,
        LoaderLog log)
    {
        // Call GetAllTemplates once for two reasons: resolve the managed Type
        // for Il2CppType.From below, and trigger DataTemplateLoader.GetAll<T>()
        // which materialises both m_TemplateMaps and m_TemplateArrays for this
        // type. An empty result means the cache isn't ready yet — return 0 and
        // let the scheduled apply coroutine retry.
        var liveTemplates = TemplateRuntimeAccess.GetAllTemplates(
            templateTypeName, out var resolvedType, out var resolveError);

        if (resolvedType == null)
        {
            log.Warning(
                $"Template clone: cannot resolve type '{templateTypeName}' ({resolveError}); "
                + $"skipping {directives.Count} directive(s).");
            _appliedTypes.Add(templateTypeName);
            return 0;
        }

        if (liveTemplates.Count == 0)
            return 0;

        // Non-DataTemplate ScriptableObjects (e.g. PerkTreeTemplate,
        // SpeakerTemplate ancestors that don't inherit DataTemplate) aren't
        // registered with DataTemplateLoader. Their by-name lookup resolver
        // (TemplateRuntimeAccess.TryGetTemplateById → FindObjectsOfTypeAll
        // + Object.name match) finds anything Unity has loaded, so cloning
        // them is just "Instantiate + rename + DontDestroyOnLoad" with no
        // m_TemplateMaps insertion. The clone becomes discoverable by name
        // the moment Instantiate returns.
        if (!typeof(DataTemplate).IsAssignableFrom(resolvedType))
        {
            return TryApplyScriptableObjectType(
                templateTypeName, resolvedType, liveTemplates, directives, log);
        }

        if (!TryGetTemplateMap(resolvedType, out var innerMap))
            return 0;

        var applied = 0;

        // Order so every clone-of-clone applies after its source: a single pass would skip a clone
        // whose source is a mod clone declared LATER (another KDL file, or later in the same one),
        // so declaration order must never decide whether a chained clone registers.
        var ordered = OrderBySourceAvailability(directives.Values, innerMap.ContainsKey, out var unresolved);
        foreach (var directive in ordered)
            ApplyOne(directive);
        // Unresolvable sources (no live template, no sibling clone) and cycles: warn once each,
        // as the single pass used to.
        foreach (var directive in unresolved)
        {
            log.Mod = directive.OwnerLabel;
            log.Warning(
                $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                + "source template not found in DataTemplateLoader.");
        }

        _appliedTypes.Add(templateTypeName);
        return applied;

        void ApplyOne(LoadedCloneDirective directive)
        {
            log.Mod = directive.OwnerLabel;
            if (!innerMap.TryGetValue(directive.CloneId, out var clone))
            {
                // An empty sourceId is a 'create' directive: instantiate a fresh
                // template of this type rather than copying a source.
                var isCreate = string.IsNullOrEmpty(directive.SourceId);
                if (isCreate)
                {
                    if (!TryCreateTemplate(resolvedType, directive.CloneId, log, out clone))
                        return;
                }
                else
                {
                    if (!innerMap.TryGetValue(directive.SourceId, out var source))
                    {
                        log.Warning(
                            $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                            + "source template not found in DataTemplateLoader.");
                        return;
                    }

                    if (!TryCloneTemplate(source, directive.CloneId, log, out clone))
                        return;

                    // Object.Instantiate shallow-copies the clone's collection
                    // containers: clone.List<T> / clone.T[] field instances are
                    // the SAME object as the source's. Any clear/append/index-set
                    // on the clone leaks straight into the source. Reallocate
                    // each container with the same element refs so the clone
                    // mutates its own data.
                    DeepCopyCollectionContainers(clone, resolvedType, directive.CloneId, log);

                    // Object.Instantiate also shallow-copies PPtr element refs,
                    // so abstract-polymorphic ScriptableObject elements
                    // (EventHandlers and similar) are still shared. Patches
                    // through the clone would mutate the source's handlers.
                    // Deep-copy each owned element so the clone has its own.
                    // resolvedType is the concrete managed wrapper type (e.g.
                    // PerkTemplate); the clone variable is DataTemplate-typed,
                    // so reflection on it sees only base-class members. We
                    // re-cast inside the helper.
                    DeepCopyOwnedReferences(clone, resolvedType, directive.CloneId, log);
                }

                RegisterCloneIntoSlot(resolvedType, innerMap, resolvedType, directive.CloneId, clone, log);

                log.Debug(isCreate
                    ? $"Template create registered: {templateTypeName}:{directive.CloneId}."
                    : $"Template clone registered: {templateTypeName}:{directive.SourceId} -> {directive.CloneId}.");
                applied++;
            }

            MirrorCloneToAncestors(resolvedType, directive.CloneId, clone, log);
        }
    }

    /// <summary>
    /// Clone path for non-<c>DataTemplate</c> <c>ScriptableObject</c> types
    /// (e.g. <c>PerkTreeTemplate</c>, <c>SpeakerTemplate</c> on builds where
    /// it doesn't inherit DataTemplate). Identity is the asset's
    /// <c>Object.name</c>; resolution at apply time walks
    /// <c>Resources.FindObjectsOfTypeAll&lt;T&gt;</c>. The clone is just
    /// <c>Object.Instantiate</c> + renamed + <c>DontDestroyOnLoad</c>; no
    /// <c>DataTemplateLoader</c> insertion is needed and there are no
    /// ancestor m_TemplateMaps slots to mirror to.
    /// </summary>
    private int TryApplyScriptableObjectType(
        string templateTypeName,
        Type resolvedType,
        IReadOnlyList<Il2CppObjectBase> liveTemplates,
        Dictionary<string, LoadedCloneDirective> directives,
        LoaderLog log)
    {
        var identityField = NonDataTemplateIdentityRegistry.GetIdentityField(templateTypeName, resolvedType);

        var applied = 0;
        foreach (var directive in directives.Values)
        {
            log.Mod = directive.OwnerLabel;
            // Idempotency: skip if a clone with this id already exists
            // (e.g. session re-registration after a save reload).
            if (FindBySourceId(liveTemplates, directive.CloneId, identityField, resolvedType) != null)
                continue;

            // An empty sourceId is a 'create' directive: a fresh ScriptableObject
            // of this type rather than a copy of a source.
            var isCreate = string.IsNullOrEmpty(directive.SourceId);

            UnityEngine.Object cloneObj;
            if (isCreate)
            {
                try
                {
                    cloneObj = UnityEngine.ScriptableObject.CreateInstance(Il2CppType.From(resolvedType));
                }
                catch (Exception ex)
                {
                    log.Warning(
                        $"Template create '{templateTypeName}:{directive.CloneId}': "
                        + $"ScriptableObject.CreateInstance threw: {ex.Message}.");
                    continue;
                }
                if (cloneObj == null)
                {
                    log.Warning($"Template create '{templateTypeName}:{directive.CloneId}': CreateInstance returned null.");
                    continue;
                }
            }
            else
            {
                var source = FindBySourceId(liveTemplates, directive.SourceId, identityField, resolvedType);
                if (source == null)
                {
                    var lookupNote = identityField != null
                        ? $"source ScriptableObject not found by name or by {identityField}."
                        : "source ScriptableObject not found by name.";
                    log.Warning(
                        $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                        + lookupNote);
                    continue;
                }

                try
                {
                    cloneObj = UnityEngine.Object.Instantiate(source.Cast<UnityEngine.Object>());
                }
                catch (Exception ex)
                {
                    log.Warning(
                        $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                        + $"Object.Instantiate threw: {ex.Message}.");
                    continue;
                }
            }

            cloneObj.name = directive.CloneId;
            cloneObj.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset;
            UnityEngine.Object.DontDestroyOnLoad(cloneObj);

            // Reallocate every collection container so a clone owns its own
            // List<T>/T[] instances. Without this, Object.Instantiate leaves the
            // clone sharing the source's container references, and a clear/append
            // on the clone wipes the source's vanilla data. A freshly-created
            // template already owns its own (empty) containers, so this is
            // clone-only. See DeepCopyCollectionContainers for the full rationale.
            if (!isCreate)
                DeepCopyCollectionContainers(cloneObj, resolvedType, directive.CloneId, log);

            // Types with an override identity field (e.g. ConversationTemplate.Path)
            // need that field set to the new CloneId too, so the matcher can
            // distinguish this clone from its source and from sibling clones.
            // Modder-side `set` patches against the same field will overwrite
            // with the same value harmlessly.
            if (identityField != null)
            {
                if (!TrySetIdentityField(cloneObj, resolvedType, identityField, directive.CloneId, out var assignError))
                {
                    log.Warning(
                        $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                        + $"could not set identity field '{identityField}': {assignError}.");
                }
            }

            log.Debug(isCreate
                ? $"Template create registered: {templateTypeName}:{directive.CloneId}."
                : $"Template clone registered: {templateTypeName}:{directive.SourceId} -> {directive.CloneId}.");
            applied++;

            // For ConversationTemplate, hand the clone to the registry so it
            // gets injected into every live conversation manager's per-trigger
            // bucket dictionary. Cache invalidation alone isn't enough because
            // each BaseConversationManager subclass builds its trigger buckets
            // once in its constructor from a snapshot; clones added later are
            // invisible without explicit injection.
            if (templateTypeName == "ConversationTemplate"
                || resolvedType?.FullName == "Il2CppMenace.Conversations.ConversationTemplate")
            {
                ConversationManagerRegistry.RegisterConversationClone(cloneObj, resolvedType);
            }

            // For SoundBank, defer Stem registration to a post-patch pass.
            // The clone is a shallow copy that still carries the source's
            // bankId, and patches haven't run yet to set the real bankId.
            // Registering here would index the bank in Stem under the wrong
            // id, leaving lookups broken even after patches mutate the
            // serialised bankId field.
            if (templateTypeName == "SoundBank"
                || resolvedType?.FullName == "Il2CppStem.SoundBank")
            {
                _pendingSoundBankRegistrations.Add((cloneObj, resolvedType, directive.CloneId));
            }
        }

        // ConversationTemplate carries a static cache (s_AllConversationTemplates)
        // that some callers consult. Null it so the next GetAll() rebuilds and
        // any code path that uses the cache picks up our clone too.
        if (applied > 0
            && (templateTypeName == "ConversationTemplate"
                || resolvedType?.FullName == "Il2CppMenace.Conversations.ConversationTemplate"))
        {
            InvalidateConversationTemplateCache(resolvedType, log);
        }

        _appliedTypes.Add(templateTypeName);
        return applied;
    }

    // Il2CppStem.SoundManager.RegisterBank(SoundBank) lives in Assembly-CSharp-firstpass,
    // which Jiangyu.Loader does not reference statically, so the type is found by scanning the
    // loaded assemblies. Resolved once and cached so repeat SoundBank-clone registrations skip
    // the domain walk. A miss is not cached: an early call before Stem is loaded then retries.
    private static Type _soundManagerType;

    private static Type ResolveSoundManagerType()
    {
        if (_soundManagerType != null)
            return _soundManagerType;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = asm.GetType("Il2CppStem.SoundManager", throwOnError: false);
            if (type != null)
                return _soundManagerType = type;
        }
        return null;
    }

    private static void TryRegisterSoundBankWithStem(
        UnityEngine.Object cloneObj, Type resolvedType, LoaderLog log)
    {
        if (cloneObj == null || resolvedType == null) return;

        var soundManagerType = ResolveSoundManagerType();
        if (soundManagerType == null)
        {
            log.Warning("  SoundBank registration: Il2CppStem.SoundManager type not found.");
            return;
        }

        var register = soundManagerType.GetMethod(
            "RegisterBank",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { resolvedType },
            modifiers: null);
        if (register == null)
        {
            log.Warning("  SoundBank registration: SoundManager.RegisterBank(SoundBank) method not found.");
            return;
        }

        // Cast the clone wrapper to SoundBank so the method signature matches.
        var typedBank = Il2CppReflectiveCast.CastOrNull(cloneObj, resolvedType);
        if (typedBank == null)
        {
            log.Warning("  SoundBank registration: failed to cast clone to SoundBank.");
            return;
        }

        try
        {
            var ok = (bool)register.Invoke(null, new[] { typedBank });
            if (ok)
                log.Debug("  SoundBank registration: Stem.SoundManager.RegisterBank returned true.");
            else
                log.Warning("  SoundBank registration: Stem.SoundManager.RegisterBank returned false; the SoundBank clone was not registered.");
        }
        catch (Exception ex)
        {
            log.Warning($"  SoundBank registration: RegisterBank threw: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void InvalidateConversationTemplateCache(Type resolvedType, LoaderLog log)
    {
        if (resolvedType == null) return;
        // Il2CppInterop wraps native static fields as PUBLIC static properties
        // on the wrapper, routed through il2cpp_field_static_set_value. The
        // managed C# `private static` modifier from the original source is
        // dropped in the wrapper.
        var prop = resolvedType.GetProperty(
            "s_AllConversationTemplates",
            BindingFlags.Public | BindingFlags.Static);
        if (prop == null || !prop.CanWrite)
        {
            log.Warning("  Conversation cache invalidation: s_AllConversationTemplates property not found or not writable.");
            return;
        }
        try
        {
            prop.SetValue(null, null);
            log.Debug("  Conversation cache invalidated: next GetAll() will rebuild from live objects.");
        }
        catch (Exception ex)
        {
            log.Warning($"  Conversation cache invalidation failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static Il2CppObjectBase FindBySourceId(
        IReadOnlyList<Il2CppObjectBase> candidates,
        string id,
        string identityField,
        Type resolvedType)
    {
        // Try Object.name first (the default identity), which is fastest
        // and covers most non-DataTemplate types unchanged.
        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            var unityObj = candidate.TryCast<UnityEngine.Object>();
            if (unityObj == null) continue;
            if (string.Equals(unityObj.name, id, StringComparison.Ordinal))
                return candidate;
        }

        // Fall back to the registered identity field (e.g. ConversationTemplate.Path)
        // for types whose Object.name is non-unique.
        if (identityField == null || resolvedType == null) return null;

        var prop = resolvedType.GetProperty(identityField, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanRead) return null;

        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            string value;
            try { value = prop.GetValue(candidate) as string; }
            catch { continue; }
            if (string.Equals(value, id, StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }

    private static bool TrySetIdentityField(
        UnityEngine.Object target, Type resolvedType, string fieldName, string value, out string error)
    {
        error = null;
        var prop = resolvedType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null)
        {
            error = $"property not found on {resolvedType.Name}";
            return false;
        }
        if (!prop.CanWrite)
        {
            error = $"property is read-only on {resolvedType.Name}";
            return false;
        }

        // Object.Instantiate returns a UnityEngine.Object wrapper, but the
        // typed property lives on the concrete Il2Cpp wrapper (e.g.
        // ConversationTemplate). .NET reflection's SetValue rejects the
        // base-class wrapper as "Object does not match target type". The
        // Il2CppInterop convention is to call the generic Cast<T>() on the
        // wrapper to obtain a wrapper of the requested type, which shares
        // the same native pointer.
        if (!Il2CppReflectiveCast.TryCast(target, resolvedType, out var typedTarget, out var castError))
        {
            error = castError;
            return false;
        }

        try
        {
            prop.SetValue(typedTarget, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }
    }

    // Inserts <clone> at <cloneId> into the type slot's m_TemplateMaps inner
    // dict and extends the corresponding m_TemplateArrays bucket. Array-extend
    // failure is non-fatal: m_TemplateMaps still resolves Get<T>(id)/TryGet<T>;
    // only GetAll<slotType> consumers miss the clone. Caller gates idempotency,
    // since calling twice for the same cloneId would double-extend the array.
    private static void RegisterCloneIntoSlot(
        Type slotType,
        Il2CppDictionary slotMap,
        Type declaredType,
        string cloneId,
        DataTemplate clone,
        LoaderLog log)
    {
        slotMap[cloneId] = clone;

        if (!TryExtendTemplateArray(slotType, clone, log))
        {
            log.Warning(
                $"Template clone '{declaredType.Name}:{cloneId}': failed to extend "
                + $"m_TemplateArrays[{slotType.Name}]; GetAll<{slotType.Name}> consumers "
                + "won't see this clone (see earlier warnings for cause).");
        }
    }

    /// <summary>
    /// Mirrors <paramref name="clone"/> into every ancestor
    /// <c>m_TemplateMaps</c>/<c>m_TemplateArrays</c> slot, walking
    /// <paramref name="resolvedType"/>.<c>BaseType</c> upward to
    /// <c>DataTemplate</c>. Caller must have registered the clone into the
    /// most-derived slot first; this method handles ancestors only.
    /// Force-materialises each ancestor slot before mirroring (via
    /// <c>DataTemplateLoader.GetAll&lt;Ancestor&gt;()</c>) so MENACE's
    /// lazy-snapshot consumers (e.g. <c>OwnedItems.Init</c>,
    /// <c>BaseConversationManager</c>) see the clone in their first
    /// enumeration of an ancestor type. Idempotent: each ancestor is
    /// independently gated on <c>ContainsKey(cloneId)</c>.
    /// </summary>
    private static void MirrorCloneToAncestors(
        Type resolvedType, string cloneId, DataTemplate clone, LoaderLog log)
    {
        var dataTemplateType = typeof(DataTemplate);
        var current = resolvedType.BaseType;
        while (current != null && dataTemplateType.IsAssignableFrom(current))
        {
            // Force the ancestor slot into existence before reading. Without
            // this, a slot the game hasn't yet materialised gets skipped, and
            // any consumer that later calls GetAll<Ancestor>() to snapshot
            // its own dict (OwnedItems.m_ItemInstances keyed by
            // BaseItemTemplate is the canonical case) caches a clone-free
            // result and the save deserialiser throws KeyNotFoundException
            // on the missing clone key.
            TemplateRuntimeAccess.EnsureDataTemplateSlotMaterialised(current);

            if (TryGetTemplateMap(current, out var ancestorMap)
                && !ancestorMap.ContainsKey(cloneId))
            {
                RegisterCloneIntoSlot(current, ancestorMap, resolvedType, cloneId, clone, log);
            }

            current = current.BaseType;
        }
    }

    private static bool TryGetTemplateMap(Type resolvedType, out Il2CppDictionary templateMap)
    {
        templateMap = null;

        if (resolvedType == null)
            return false;

        var singleton = DataTemplateLoader.GetSingleton();
        if (singleton == null)
            return false;

        var templateMaps = singleton.m_TemplateMaps;
        if (templateMaps == null)
            return false;

        var il2CppType = Il2CppType.From(resolvedType);
        if (il2CppType == null)
            return false;

        return templateMaps.TryGetValue(il2CppType, out templateMap) && templateMap != null;
    }

    // Cache: per-element-type, "is this element type abstract polymorphic
    // and non-DataTemplate" decision. Element-type set is small (~the
    // SerializedScriptableObject family on the Skill/Perk side).
    private static readonly Dictionary<Type, bool> OwnedElementTypeCache = new();

    /// <summary>
    /// After <see cref="UnityEngine.Object.Instantiate"/> creates the clone,
    /// re-allocate every collection-typed field on the clone so the clone
    /// holds an independent container with the source's element references
    /// copied in. Without this, <c>Object.Instantiate</c> on an IL2CPP
    /// ScriptableObject keeps the source's <c>List&lt;T&gt;</c> / array
    /// instance shared with the clone, and any <c>clear</c> / <c>append</c>
    /// / element <c>set</c> on the clone leaks straight into the source's
    /// live data. Element references stay shared (intentional registry
    /// semantics for DataTemplate/PPtr lists); only the container instance
    /// is reallocated. Pairs with <see cref="DeepCopyOwnedReferences"/>
    /// which then replaces the abstract-polymorphic owned elements inside
    /// the new container with fresh Instantiated copies.
    /// </summary>
    private static void DeepCopyCollectionContainers(
        Il2CppObjectBase clone, Type concreteType, string cloneId, LoaderLog log)
    {
        if (clone == null) return;

        var reflectionTarget = ReflectionTargetForConcreteType(clone, concreteType, cloneId, log);
        if (reflectionTarget == null) return;

        var type = reflectionTarget.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (!seen.Add("P:" + prop.Name)) continue;
            TryReallocCollectionContainer(
                () => prop.GetValue(reflectionTarget),
                v => prop.SetValue(reflectionTarget, v),
                prop.PropertyType,
                prop.Name,
                cloneId,
                log);
        }

        foreach (var field in type.GetFields(flags))
        {
            if (!seen.Add("F:" + field.Name)) continue;
            if (field.IsInitOnly) continue;
            TryReallocCollectionContainer(
                () => field.GetValue(reflectionTarget),
                v => field.SetValue(reflectionTarget, v),
                field.FieldType,
                field.Name,
                cloneId,
                log);
        }
    }

    private static bool TryReallocCollectionContainer(
        Func<object> reader,
        Action<object> writer,
        Type memberType,
        string memberName,
        string cloneId,
        LoaderLog log)
    {
        var listElement = Il2CppCollectionReflection.GetListElementType(memberType);
        if (listElement != null)
            return TryRebuildAndWrite(
                reader, writer,
                src => Il2CppCollectionReflection.TryRebuildList(src, memberType, listElement, out var f, out var e)
                    ? (f, (string)null) : ((object)null, e),
                "list", memberName, cloneId, log);

        var arrayElement = Il2CppCollectionReflection.GetArrayElementType(memberType);
        if (arrayElement != null)
            return TryRebuildAndWrite(
                reader, writer,
                src => Il2CppCollectionReflection.TryRebuildReferenceArray(
                        src, memberType, arrayElement, appendedElement: null, out var f, out var e)
                    ? (f, (string)null) : ((object)null, e),
                "array", memberName, cloneId, log);

        return false;
    }

    // Common read-rebuild-write skeleton with logging. The rebuild step is
    // injected so this skeleton serves both list and array variants without
    // either of them re-implementing reader/writer plumbing.
    private static bool TryRebuildAndWrite(
        Func<object> reader,
        Action<object> writer,
        Func<object, (object fresh, string error)> rebuild,
        string kind,
        string memberName,
        string cloneId,
        LoaderLog log)
    {
        object source;
        try { source = reader(); }
        catch { return false; }
        if (source == null) return false;

        var (fresh, error) = rebuild(source);
        if (fresh == null)
        {
            if (error != null)
                log.Warning($"Template clone '{cloneId}': rebuilding {kind} for '{memberName}' failed: {error}");
            return false;
        }

        try { writer(fresh); return true; }
        catch (Exception ex)
        {
            log.Warning(
                $"Template clone '{cloneId}': writing fresh {kind} back to '{memberName}' "
                + $"threw: {ex.Message}.");
            return false;
        }
    }

    private static object ReflectionTargetForConcreteType(
        Il2CppObjectBase clone, Type concreteType, string cloneId, LoaderLog log)
    {
        object target = clone;
        if (concreteType == null || concreteType == clone.GetType()) return target;
        try
        {
            var tryCast = typeof(Il2CppObjectBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "TryCast"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                ?.MakeGenericMethod(concreteType);
            if (tryCast == null) return target;
            var cast = tryCast.Invoke(clone, null);
            if (cast != null) target = cast;
        }
        catch (Exception ex)
        {
            log.Warning(
                $"Template clone '{cloneId}': TryCast<{concreteType.FullName}> threw: {ex.Message}");
        }
        return target;
    }

    /// <summary>
    /// After <see cref="UnityEngine.Object.Instantiate"/> creates the clone,
    /// walk every collection-typed member and deep-copy each element of any
    /// list whose element type is an abstract-polymorphic non-DataTemplate
    /// ScriptableObject. This is the &quot;owned&quot; pattern: the parent
    /// declares a <c>List&lt;AbstractBase&gt;</c> and live concrete elements
    /// are unique to that parent. Without this pass, clone and source share
    /// PPtrs and any patch through the clone leaks into the source.
    /// DataTemplate-element lists (Skills, Items) and concrete-element
    /// wrapper lists (SkillGroups, DefectGroups) stay shared because those
    /// are intentional registry references.
    /// </summary>
    private static void DeepCopyOwnedReferences(DataTemplate clone, Type concreteType, string cloneId, LoaderLog log)
    {
        if (clone == null)
        {
            log.Debug($"Template clone '{cloneId}': deep-copy skipped, clone is null.");
            return;
        }

        // The clone arrives as a DataTemplate wrapper; reflection on it sees
        // only base-class members. Re-cast to the concrete wrapper so the
        // subclass's own collection fields (e.g. PerkTemplate.EventHandlers)
        // become visible to GetProperties / GetFields.
        object reflectionTarget = clone;
        if (concreteType != null && concreteType != typeof(DataTemplate))
        {
            try
            {
                var tryCast = typeof(Il2CppObjectBase)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "TryCast"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 0)
                    ?.MakeGenericMethod(concreteType);
                if (tryCast != null)
                {
                    var cast = tryCast.Invoke(clone, null);
                    if (cast != null) reflectionTarget = cast;
                }
            }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': TryCast<{concreteType.FullName}> threw: {ex.Message}");
            }
        }

        var type = reflectionTarget.GetType();
        var deepCopiedCount = 0;
        var listsTouched = 0;

        // GetProperties / GetFields without DeclaredOnly already return
        // inherited members on the concrete wrapper, so a single pass on the
        // leaf type covers SkillTemplate.EventHandlers via PerkTemplate and
        // similar inheritance shapes. Walking the BaseType chain in addition
        // would visit the same member multiple times.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            if (!prop.CanRead) continue;
            if (!seen.Add("P:" + prop.Name)) continue;

            var elementType = Il2CppCollectionReflection.GetListElementType(prop.PropertyType);
            if (elementType == null) continue;
            if (!IsOwnedElementType(elementType)) continue;

            if (TryDeepCopyMember(reflectionTarget, elementType, () => prop.GetValue(reflectionTarget), prop.Name, cloneId, log, out var copied)
                && copied > 0)
            {
                listsTouched++;
                deepCopiedCount += copied;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (!seen.Add("F:" + field.Name)) continue;

            var elementType = Il2CppCollectionReflection.GetListElementType(field.FieldType);
            if (elementType == null) continue;
            if (!IsOwnedElementType(elementType)) continue;

            if (TryDeepCopyMember(reflectionTarget, elementType, () => field.GetValue(reflectionTarget), field.Name, cloneId, log, out var copied)
                && copied > 0)
            {
                listsTouched++;
                deepCopiedCount += copied;
            }
        }

        if (deepCopiedCount > 0)
        {
            log.Debug(
                $"Template clone '{cloneId}': deep-copied {deepCopiedCount} owned-PPtr element(s) "
                + $"across {listsTouched} list field(s) so clone-side patches don't leak into the source.");
        }
    }

    private static bool TryDeepCopyMember(
        object clone,
        Type elementType,
        Func<object> reader,
        string memberName,
        string cloneId,
        LoaderLog log,
        out int copiedCount)
    {
        copiedCount = 0;

        object listObject;
        try { listObject = reader(); }
        catch { return false; }
        if (listObject == null) return false;

        var listType = listObject.GetType();
        var countProp = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        var indexer = listType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
        if (countProp == null || indexer == null) return false;

        int count;
        try { count = (int)countProp.GetValue(listObject); }
        catch { return false; }

        // Indexer.SetValue requires the value to be the wrapper type that
        // matches the list's declared element. Object.Instantiate gives us a
        // UnityEngine.Object wrapper; we have to TryCast back to the
        // element-type wrapper before assigning. Cache the TryCast<element>
        // method handle once per element type — same reflection cost as the
        // path-walk applier's runtime cast.
        MethodInfo tryCastToElement;
        try
        {
            tryCastToElement = typeof(Il2CppObjectBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "TryCast"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0)
                ?.MakeGenericMethod(elementType);
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone '{cloneId}': could not bind TryCast<{elementType.Name}>: {ex.Message}");
            return false;
        }
        if (tryCastToElement == null)
        {
            log.Warning($"Template clone '{cloneId}': TryCast<{elementType.Name}> not found.");
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            object element;
            try { element = indexer.GetValue(listObject, new object[] { i }); }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': read of '{memberName}[{i}]' threw: {ex.Message}");
                continue;
            }
            if (element is not Il2CppObjectBase il2cpp) continue;

            // A mod-injected element must stay SHARED. Object.Instantiate
            // memcpys the native object including the GC handle that links it
            // to its managed instance, so the copy's finaliser tears down a
            // handle it does not own (boot crash via ClassInjector.Finalize).
            // Sharing is safe: injected handler templates are stateless
            // factories, and a clone-side patch never addresses them by index
            // without re-authoring them.
            if (Sdk.Types.JiangyuTypeRegistry.IsInjectedInstance(il2cpp))
            {
                log.Debug($"Template clone '{cloneId}': '{memberName}[{i}]' is a mod-injected type, kept shared with the source.");
                continue;
            }

            UnityEngine.Object asUnity;
            try { asUnity = il2cpp.Cast<UnityEngine.Object>(); }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': cast of '{memberName}[{i}]' to UnityEngine.Object threw: {ex.Message}");
                continue;
            }

            UnityEngine.Object instance;
            try { instance = UnityEngine.Object.Instantiate(asUnity); }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': Object.Instantiate on '{memberName}[{i}]' threw: {ex.Message}");
                continue;
            }

            try { instance.hideFlags = HideFlags.DontUnloadUnusedAsset; } catch { }

            // Cast the freshly-instantiated UnityEngine.Object back to the
            // element type so the typed indexer accepts it.
            object instanceAsElement;
            try { instanceAsElement = tryCastToElement.Invoke(instance, null); }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': TryCast<{elementType.Name}> on instantiated '{memberName}[{i}]' threw: {(ex.InnerException ?? ex).Message}");
                continue;
            }
            if (instanceAsElement == null)
            {
                log.Warning($"Template clone '{cloneId}': TryCast<{elementType.Name}> on instantiated '{memberName}[{i}]' returned null.");
                continue;
            }

            try { indexer.SetValue(listObject, instanceAsElement, new object[] { i }); }
            catch (Exception ex)
            {
                log.Warning($"Template clone '{cloneId}': write of '{memberName}[{i}]' threw: {ex.Message}");
                continue;
            }

            copiedCount++;
        }

        return true;
    }

    /// <summary>
    /// Element types we treat as &quot;owned by parent&quot; and therefore
    /// deep-copy when cloning the parent: abstract-polymorphic ScriptableObject
    /// subclasses that aren't DataTemplate. SkillEventHandlerTemplate matches
    /// (abstract, has 119+ subclasses, no m_ID); concrete wrappers like
    /// SkillGroup don't (no subtypes); DataTemplate elements don't (intentional
    /// registry sharing). Internal so the test project (via
    /// InternalsVisibleTo) can exercise the structural decision against
    /// fixture types — the rule is what determines correctness when MENACE
    /// adds new owned-element fields.
    /// </summary>
    internal static bool IsOwnedElementType(Type elementType)
        => IsOwnedElementTypeCore(elementType, typeof(DataTemplate), typeof(UnityEngine.ScriptableObject));

    // Parameterised core, factored out so tests can pass synthetic base
    // types instead of typeof(DataTemplate) / typeof(UnityEngine.ScriptableObject).
    // Production calls the no-arg IsOwnedElementType which binds the
    // real game types — resolving those at JIT time pulls in
    // Assembly-CSharp + the full Il2Cpp* transitive closure, which works
    // at game runtime but breaks in CI (the stripped game DLLs we ship
    // there can't fully resolve). The test runtime only sees plain
    // managed fixtures and the parameterised overload, so JIT never
    // touches a game-type token.
    internal static bool IsOwnedElementTypeCore(
        Type elementType,
        Type dataTemplateBase,
        Type scriptableObjectBase)
    {
        if (OwnedElementTypeCache.TryGetValue(elementType, out var cached))
            return cached;

        bool decision;
        try
        {
            if (dataTemplateBase != null && dataTemplateBase.IsAssignableFrom(elementType))
                decision = false;
            else if (scriptableObjectBase == null || !scriptableObjectBase.IsAssignableFrom(elementType))
                decision = false;
            else
                decision = HasStrictDescendant(elementType);
        }
        catch
        {
            decision = false;
        }

        OwnedElementTypeCache[elementType] = decision;
        return decision;
    }

    internal static bool HasStrictDescendant(Type baseType)
    {
        Type[] types;
        try { types = baseType.Assembly.GetTypes(); }
        catch { return false; }

        foreach (var t in types)
        {
            if (ReferenceEquals(t, baseType)) continue;
            if (baseType.IsAssignableFrom(t)) return true;
        }
        return false;
    }

    /// <summary>
    /// Build a fresh instance of <paramref name="type"/> and copy each
    /// property and field from <paramref name="source"/> onto it.
    /// Reference-type collection fields that the patch system might
    /// mutate in place (today: <c>List&lt;string&gt;</c>) are
    /// reallocated so per-index modder edits on the clone don't leak
    /// into the source. Other reference fields keep their refs shared
    /// (the elements are themselves either immutable strings or
    /// external assets we don't own here).
    ///
    /// Used by clone-pipeline passes that need to break sharing on
    /// non-<see cref="UnityEngine.Object"/> value objects, where
    /// <see cref="UnityEngine.Object.Instantiate"/> isn't available.
    /// Today that's <c>ConversationTemplate.Roles[i]</c> (each Role
    /// holds <c>m_SerializedRequirements: List&lt;string&gt;</c>); other
    /// list-element types with the same shape can opt in by calling
    /// this from a structurally appropriate pass.
    /// </summary>
    internal static object CloneValueObjectByFieldReflection(
        object source, Type type, string contextLabel, LoaderLog log)
    {
        if (source == null || type == null) return null;

        object fresh;
        try
        {
            if (typeof(Il2CppObjectBase).IsAssignableFrom(type))
                fresh = Il2CppInstanceAllocator.TryAllocateOrNull(type);
            else
                fresh = Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            log?.Warning($"  {contextLabel}: could not allocate fresh {type.FullName}: {ex.Message}");
            return null;
        }
        if (fresh == null) return null;

        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var prop in type.GetProperties(flags))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            if (!prop.CanRead || !prop.CanWrite) continue;
            try { prop.SetValue(fresh, ReseatStringListIfPresent(prop.GetValue(source))); }
            catch (Exception ex) { log?.Warning($"  {contextLabel}.{prop.Name}: {ex.Message}"); }
        }
        foreach (var field in type.GetFields(flags))
        {
            if (field.IsInitOnly) continue;
            try { field.SetValue(fresh, ReseatStringListIfPresent(field.GetValue(source))); }
            catch (Exception ex) { log?.Warning($"  {contextLabel}.{field.Name}: {ex.Message}"); }
        }
        return fresh;
    }

    // Auto-reallocate List<string> values during value-object cloning so the
    // clone owns its own string list (patch-system mutates these in place
    // via index replacement). Non-string lists stay shared because deep
    // copying them risks breaking intentional sharing on unrelated types.
    private static object ReseatStringListIfPresent(object value)
    {
        if (value == null) return null;
        var listType = value.GetType();
        var elementType = Il2CppCollectionReflection.GetListElementType(listType);
        if (elementType == null) return value;
        if (elementType.FullName != "System.String"
            && elementType.FullName != "Il2CppSystem.String")
            return value;
        return Il2CppCollectionReflection.TryRebuildList(value, listType, elementType, out var fresh, out _)
            ? fresh
            : value;
    }

    private static bool TryCloneTemplate(
        Il2CppObjectBase source,
        string cloneId,
        LoaderLog log,
        out DataTemplate clone)
    {
        clone = null;
        try
        {
            var sourceAsUnityObject = source.Cast<UnityEngine.Object>();
            var cloneUnityObject = UnityEngine.Object.Instantiate(sourceAsUnityObject);
            cloneUnityObject.name = cloneId;
            cloneUnityObject.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (!TryWriteTemplateId(cloneUnityObject, cloneId, log))
                return false;

            clone = cloneUnityObject.TryCast<DataTemplate>();
            if (clone == null)
            {
                log.Warning(
                    $"Template clone for '{cloneId}': Instantiate result does not cast to DataTemplate.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(
                $"Template clone for '{cloneId}' threw during Instantiate: {ex.Message}");
            return false;
        }
    }

    // Create a fresh DataTemplate (no source to copy) for a 'create' directive:
    // instantiate the ScriptableObject-rooted type via the Il2CppType factory
    // (raw il2cpp_object_new corrupts SO-rooted types), stamp m_ID, and cast.
    // Required fields are left at their defaults for the modder's patch to set.
    private static bool TryCreateTemplate(
        Type templateType,
        string templateId,
        LoaderLog log,
        out DataTemplate template)
    {
        template = null;
        try
        {
            var fresh = UnityEngine.ScriptableObject.CreateInstance(Il2CppType.From(templateType));
            if (fresh == null)
            {
                log.Warning($"Template create '{templateId}': ScriptableObject.CreateInstance returned null.");
                return false;
            }

            fresh.name = templateId;
            fresh.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (!TryWriteTemplateId(fresh, templateId, log))
                return false;

            template = fresh.TryCast<DataTemplate>();
            if (template == null)
            {
                log.Warning($"Template create '{templateId}': fresh instance does not cast to DataTemplate.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Template create '{templateId}' threw: {ex.Message}");
            return false;
        }
    }

    // Walks the class hierarchy to find m_ID (inherited from DataTemplate base),
    // reads its IL2CPP offset by name, and writes the new Il2Cpp string pointer.
    private static bool TryWriteTemplateId(Il2CppObjectBase clone, string cloneId, LoaderLog log)
    {
        try
        {
            var objectPointer = clone.Pointer;
            if (objectPointer == IntPtr.Zero)
            {
                log.Warning($"Template clone '{cloneId}': object pointer is zero.");
                return false;
            }

            var klass = IL2CPP.il2cpp_object_get_class(objectPointer);
            var idField = Il2CppFieldLookup.FindFieldInHierarchy(klass, "m_ID");
            if (idField == IntPtr.Zero)
            {
                log.Warning($"Template clone '{cloneId}': no m_ID field in class hierarchy.");
                return false;
            }

            var offset = IL2CPP.il2cpp_field_get_offset(idField);
            if (offset == 0)
            {
                log.Warning($"Template clone '{cloneId}': m_ID field offset is zero.");
                return false;
            }

            var il2CppString = IL2CPP.ManagedStringToIl2Cpp(cloneId);
            Marshal.WriteIntPtr(objectPointer + (int)offset, il2CppString);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone '{cloneId}': m_ID write threw: {ex.Message}");
            return false;
        }
    }

    private static readonly Type[] IntPtrCtorSignature = { typeof(IntPtr) };

    // Appends the clone to DataTemplateLoader.m_TemplateArrays[resolvedType].
    //
    // Why a fresh native allocation: IL2CPP arrays are immutable in length, so
    // we allocate a length+1 array, copy the existing element pointers across,
    // append the clone, and replace the dict entry.
    //
    // Why il2cpp_array_new with the original's element class: an earlier
    // attempt used `new Il2CppReferenceArray<DataTemplate>(managedArray)`,
    // whose ctor allocates with element class = DataTemplate (the wrapper's
    // generic T). The dict actually stores arrays whose IL2CPP element class
    // is the concrete subtype (UnitLeaderTemplate, BaseItemTemplate, ...), so
    // the rebuilt array's element class mismatched the original's. The game's
    // own GetAll<T> consumer then hung on it (frozen new-game start, 2026-04-20).
    // Reading the element class off the original's native pointer keeps the
    // replacement byte-identical to what the dict slot expects.
    //
    // The wrapper itself uses the original's runtime type so the dict's
    // typed indexer setter accepts the assignment regardless of whether the
    // game stores base-typed (Il2CppReferenceArray<DataTemplate>) or
    // concrete-typed wrappers.
    private static bool TryExtendTemplateArray(
        Type resolvedType, Il2CppObjectBase clone, LoaderLog log)
    {
        var singleton = DataTemplateLoader.GetSingleton();
        if (singleton == null)
            return false;

        var arraysProperty = typeof(DataTemplateLoader).GetProperty(
            "m_TemplateArrays", BindingFlags.Public | BindingFlags.Instance);
        if (arraysProperty == null)
        {
            log.Warning("Template clone array extend: DataTemplateLoader.m_TemplateArrays property not found.");
            return false;
        }

        var arrays = arraysProperty.GetValue(singleton);
        if (arrays == null)
            return false;

        var arraysType = arrays.GetType();
        var il2CppType = Il2CppType.From(resolvedType);
        if (il2CppType == null)
            return false;

        var tryGetValue = FindTryGetValue(arraysType);
        if (tryGetValue == null)
        {
            log.Warning($"Template clone array extend: TryGetValue not found on {arraysType.FullName}.");
            return false;
        }

        var lookup = new object[] { il2CppType, null };
        if (!(bool)tryGetValue.Invoke(arrays, lookup) || lookup[1] is not Il2CppObjectBase oldArray)
            return false;

        var oldArrayType = oldArray.GetType();
        var lengthProperty = oldArrayType.GetProperty("Length")
            ?? oldArrayType.GetProperty("Count");
        if (lengthProperty == null)
        {
            log.Warning($"Template clone array extend: no Length/Count on {oldArrayType.FullName}.");
            return false;
        }

        int oldLength;
        try
        {
            oldLength = (int)lengthProperty.GetValue(oldArray)!;
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone array extend: read Length threw: {ex.Message}");
            return false;
        }

        var oldArrayPointer = oldArray.Pointer;
        if (oldArrayPointer == IntPtr.Zero)
            return false;

        var arrayClass = IL2CPP.il2cpp_object_get_class(oldArrayPointer);
        var elementClass = IL2CPP.il2cpp_class_get_element_class(arrayClass);
        if (elementClass == IntPtr.Zero)
        {
            log.Warning($"Template clone array extend: il2cpp_class_get_element_class returned null for {oldArrayType.FullName}.");
            return false;
        }

        var newNativeArray = IL2CPP.il2cpp_array_new(elementClass, (ulong)(oldLength + 1));
        if (newNativeArray == IntPtr.Zero)
        {
            log.Warning($"Template clone array extend: il2cpp_array_new returned null for {oldArrayType.FullName}.");
            return false;
        }

        var wrapperCtor = oldArrayType.GetConstructor(IntPtrCtorSignature);
        if (wrapperCtor == null)
        {
            log.Warning($"Template clone array extend: {oldArrayType.FullName} has no (IntPtr) ctor.");
            return false;
        }

        object newArray;
        try
        {
            newArray = wrapperCtor.Invoke(new object[] { newNativeArray });
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone array extend: (IntPtr) ctor threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }

        var indexer = FindIntIndexer(oldArrayType);
        if (indexer == null)
        {
            log.Warning($"Template clone array extend: no int-indexer on {oldArrayType.FullName}.");
            return false;
        }

        try
        {
            var slot = new object[1];
            for (var i = 0; i < oldLength; i++)
            {
                slot[0] = i;
                var element = indexer.GetValue(oldArray, slot);
                indexer.SetValue(newArray, element, slot);
            }

            slot[0] = oldLength;
            indexer.SetValue(newArray, clone, slot);
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone array extend: indexer copy threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }

        var dictIndexer = Il2CppIndexerLookup.FindIndexerByKeyType(arraysType, il2CppType.GetType());
        if (dictIndexer == null)
        {
            log.Warning($"Template clone array extend: no Il2CppType-keyed indexer on {arraysType.FullName}.");
            return false;
        }

        try
        {
            dictIndexer.SetValue(arrays, newArray, new object[] { il2CppType });
        }
        catch (Exception ex)
        {
            log.Warning($"Template clone array extend: dict indexer set threw: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }

        return true;
    }

    private static MethodInfo FindTryGetValue(Type dictType)
    {
        foreach (var method in dictType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "TryGetValue") continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 2 && parameters[1].ParameterType.IsByRef)
                return method;
        }
        return null;
    }

    private static PropertyInfo FindIntIndexer(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
                return property;
        }
        return null;
    }

}
