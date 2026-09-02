using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Tracks live <c>BaseConversationManager</c> subclass instances and
/// <c>ConversationTemplate</c> clones, and injects clones into the managers'
/// internal indexes so MENACE's conversation matcher considers them at
/// runtime.
///
/// <para>Each <c>BaseConversationManager</c> builds two immutable indexes
/// at construction:</para>
/// <list type="bullet">
///   <item><description><c>m_ConversationTemplates</c>: a flat
///     <c>Il2CppReferenceArray&lt;ConversationTemplate&gt;</c> filtered by the
///     manager's <c>ConversationType</c>.</description></item>
///   <item><description><c>m_AvailableTemplatesByTriggerType</c>: a dictionary
///     keyed by <c>ConversationTriggerType</c> holding the per-trigger
///     candidate list, which is what the matcher consults at trigger time.</description></item>
/// </list>
/// <para>A clone created at runtime after the manager's constructor has run
/// is invisible to those indexes. This registry handles both orderings:
/// clones registered before any matching manager is constructed (injected at
/// manager-construction time), and managers constructed before a clone is
/// registered (injected at clone-registration time).</para>
///
/// <para>Per-manager and per-clone-type reflection is cached on first use.
/// The replay path (RegisterManager → all known clones) batches the master-
/// array rebuild into a single allocation, so adding N clones costs one
/// O(existing+N) rebuild instead of N rebuilds. Typed-state refresh
/// (<c>OnAfterDeserialize</c> on Roles/Nodes) is NOT cached: the registry
/// has no signal for "patches finished", and a clone may be registered
/// before patches mutate its serialised strings. Refreshing on every
/// injection keeps the wrapper's typed state in sync regardless of order.</para>
/// </summary>
internal static class ConversationManagerRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, ManagerCache> ManagerCaches = new();
    private static readonly Dictionary<Type, CloneTypeCache> CloneTypeCaches = new();
    private static readonly List<PreparedClone> Clones = new();

    private static MelonLogger.Instance _log;

    public static void Init(MelonLogger.Instance log) => _log = log;

    /// <summary>Called by the Harmony postfix on <c>BaseConversationManager</c>
    /// subclass constructors. Records the manager and replays any pre-existing
    /// clone registrations into it.</summary>
    public static void RegisterManager(Il2CppObjectBase manager)
    {
        if (manager == null) return;
        var pointer = manager.Pointer;
        if (pointer == IntPtr.Zero) return;

        // Build the cache and register it atomically with the clones snapshot.
        // A two-lock sequence would open a window where a concurrent
        // RegisterConversationClone snapshots ManagerCaches without this
        // manager AND this method snapshots Clones without that clone, losing
        // the injection. Cache build is just a handful of GetProperty/GetMethod
        // calls and only runs once per pointer, so holding Sync across it is cheap.
        ManagerCache managerCache;
        PreparedClone[] clonesSnapshot;
        lock (Sync)
        {
            // Dedup: this method gets called from a per-trigger hot prefix, so the same
            // manager pointer arrives many times. Register only the first call per LIVE
            // manager; the matcher's index is stable from then on.
            if (ManagerCaches.TryGetValue(pointer, out var existing))
            {
                // Still the manager we injected into: its indexes already carry our clones.
                if (existing.IsAlive)
                    return;

                // The manager at this address was freed and IL2CPP has handed the address to a
                // new one. Nothing has put our clones in its indexes, so it registers afresh.
                // Keying on the raw pointer alone would skip it for good, and every doll would go
                // quiet for the rest of the session while vanilla conversations, which the manager
                // builds natively, kept working. The append checks the manager's indexes first, so
                // a replay is idempotent.
                existing.Release();
                ManagerCaches.Remove(pointer);
            }

            // The dictionary only ever grew, one entry per manager the session had seen.
            // Dropping the collected ones keeps it to the live set and keeps a recycled
            // address from finding a stale entry in the first place.
            PruneCollectedManagers();

            managerCache = TryBuildManagerCache(manager);
            if (managerCache == null) return;
            ManagerCaches[pointer] = managerCache;
            clonesSnapshot = Clones.ToArray();
        }

        var managerTypeName = manager.GetType().FullName ?? "<unknown>";
        _log?.Debug($"  Conversation manager registered: {managerTypeName} (replaying {clonesSnapshot.Length} known clone(s)).");
        BatchInjectClonesIntoManager(managerCache, clonesSnapshot);
    }

    // Entries whose manager has been collected. Called under Sync.
    private static void PruneCollectedManagers()
    {
        List<IntPtr> dead = null;
        foreach (var (pointer, cache) in ManagerCaches)
        {
            if (cache.IsAlive)
                continue;
            (dead ??= new List<IntPtr>()).Add(pointer);
        }
        if (dead == null)
            return;
        foreach (var pointer in dead)
        {
            ManagerCaches[pointer].Release();
            ManagerCaches.Remove(pointer);
        }
    }

    /// <summary>Called by <see cref="TemplateCloneApplier"/> after a
    /// <c>ConversationTemplate</c> clone is registered into Unity's object
    /// graph. Records the clone (with its typed wrapper Type, since the
    /// caller statically saw it as <c>UnityEngine.Object</c> and reflection
    /// on that base class can't find the type-specific properties we need)
    /// and injects it into every live manager whose <c>ConversationType</c>
    /// filter matches.</summary>
    public static void RegisterConversationClone(Il2CppObjectBase clone, Type resolvedType)
    {
        if (clone == null || resolvedType == null) return;
        var prepared = PrepareClone(clone, resolvedType);
        if (prepared == null) return;

        ManagerCache[] managersSnapshot;
        lock (Sync)
        {
            Clones.Add(prepared);
            managersSnapshot = ManagerCaches.Values.ToArray();
        }

        foreach (var managerCache in managersSnapshot)
            InjectSingleCloneIntoManager(managerCache, prepared);
    }

    /// <summary>Called by <see cref="TemplateCloneApplier.RunPostPatchHooks"/>
    /// after <c>TemplatePatchApplier.TryApply</c> finishes. Rebuilds typed
    /// Requirements / m_Nodes on every known clone from its (now-patched)
    /// serialised string lists.
    ///
    /// <para>Necessary because clones are registered DURING the apply
    /// coroutine BEFORE patches mutate <c>m_SerializedRequirements</c> /
    /// <c>m_SerializedNodes</c>. The per-injection refresh captures
    /// pre-patch state; without this hook, the wrapper's typed lists stay
    /// stale and the matcher walks pre-patch Requirements. <c>OnAfterDeserialize</c>
    /// is idempotent, so re-running it from here is safe even for clones
    /// that didn't need rebuilding.</para></summary>
    public static void OnPostPatch()
    {
        PreparedClone[] snapshot;
        lock (Sync) snapshot = Clones.ToArray();
        if (snapshot.Length == 0) return;

        foreach (var prepared in snapshot)
            Refresh(prepared);

        _log?.Debug($"  Conversation clone post-patch refresh: {snapshot.Length} clone(s) re-deserialised against patched strings.");
    }

    // -----------------------------------------------------------------
    // Single-clone injection (post-manager-construction live add).
    // -----------------------------------------------------------------

    private static void InjectSingleCloneIntoManager(ManagerCache managerCache, PreparedClone prepared)
    {
        if (managerCache.ConversationType.HasValue
            && prepared.ConversationType != managerCache.ConversationType.Value)
            return;
        Refresh(prepared);
        if (!prepared.Active)
        {
            _log?.Debug("  Conversation clone injection: clone is Active=False; skipped (matches vanilla bucket population).");
            return;
        }

        var bucketsAdded = 0;
        foreach (var trigger in prepared.Triggers)
        {
            if (TryAppendToTriggerBucket(managerCache, trigger, prepared.TypedWrapper))
                bucketsAdded++;
        }

        var masterAdded = TryAppendToMasterArraySingle(managerCache, prepared.TypedWrapper);

        _log?.Debug(
            $"  Conversation clone injection: into {managerCache.ManagerType.FullName} "
            + $"(ConversationType={prepared.ConversationType}, "
            + $"{bucketsAdded}/{prepared.Triggers.Length} bucket(s) updated, "
            + $"masterArrayAdded={masterAdded}).");
    }

    // -----------------------------------------------------------------
    // Batched replay (used by RegisterManager).
    // -----------------------------------------------------------------

    private static void BatchInjectClonesIntoManager(ManagerCache managerCache, PreparedClone[] clones)
    {
        if (clones.Length == 0) return;
        var matching = new List<PreparedClone>(clones.Length);
        var inactive = 0;
        foreach (var prepared in clones)
        {
            if (managerCache.ConversationType.HasValue
                && prepared.ConversationType != managerCache.ConversationType.Value)
                continue;
            Refresh(prepared);
            if (!prepared.Active) { inactive++; continue; }
            matching.Add(prepared);
        }

        if (inactive > 0)
            _log?.Debug($"  Conversation clone injection: {inactive} Active=False clone(s) skipped (match vanilla bucket population).");

        // Per-trigger bucket appends one-at-a-time (each is just a List.Add).
        var bucketAppends = 0;
        foreach (var prepared in matching)
        {
            foreach (var trigger in prepared.Triggers)
            {
                if (TryAppendToTriggerBucket(managerCache, trigger, prepared.TypedWrapper))
                    bucketAppends++;
            }
        }

        // Master array rebuilt once for the whole batch.
        var masterAddedCount = TryAppendToMasterArrayBatch(
            managerCache, matching.Select(p => p.TypedWrapper).ToArray());

        if (matching.Count > 0)
        {
            _log?.Debug(
                $"  Conversation clone injection: {matching.Count} clone(s) injected "
                + $"into {managerCache.ManagerType.FullName} "
                + $"({bucketAppends} bucket append(s), masterArrayAdded={masterAddedCount}).");
        }
    }

    // -----------------------------------------------------------------
    // Trigger-bucket dictionary append (cheap, individual).
    // -----------------------------------------------------------------

    private static bool TryAppendToTriggerBucket(ManagerCache managerCache, int trigger, object clone)
    {
        if (managerCache.BucketDictProp == null) return false;
        if (!managerCache.TryGetManager(out var manager)) return false;
        var dict = managerCache.BucketDictProp.GetValue(manager);
        if (dict == null) return false;

        var triggerKey = Enum.ToObject(managerCache.BucketTriggerType, trigger);
        object list;
        var hasKey = (bool)managerCache.BucketContainsKey.Invoke(dict, new[] { triggerKey });
        if (hasKey)
        {
            list = managerCache.BucketGetItem.Invoke(dict, new[] { triggerKey });
            if (AlreadyHolds(list, clone))
                return false;
        }
        else
        {
            list = managerCache.BucketListCtor.Invoke(null);
            managerCache.BucketSetItem.Invoke(dict, new[] { triggerKey, list });
        }
        managerCache.BucketListAdd.Invoke(list, new[] { clone });
        return true;
    }

    // -----------------------------------------------------------------
    // Master array rebuild (the expensive part — batched when possible).
    // -----------------------------------------------------------------

    // Injection is idempotent, decided by what the manager's own indexes already hold rather than
    // by whether we remember registering it. Wrapper identity cannot carry that memory: Il2CppInterop
    // pools wrappers weakly and rebuilds them after a GC, so a manager we injected into an hour ago
    // is reached through a different wrapper now, and a registry that trusted wrapper liveness would
    // replay all of its clones into indexes that already have them. The collection is the one stable
    // native record of what went in.
    private static bool AlreadyHolds(object collection, object clone)
    {
        if (clone is not Il2CppObjectBase typed)
            return false;
        var present = NativePointersIn(collection, "appending");
        return present != null && present.Contains(typed.Pointer);
    }

    // The native pointers a collection already holds. Native identity, not the managed wrapper's:
    // Il2CppInterop pools wrappers weakly and rebuilds them after a GC, so one native object is
    // reached through different wrappers over its life. Null when the collection cannot be read,
    // which the callers treat as "cannot tell, append anyway".
    private static HashSet<IntPtr> NativePointersIn(object collection, string fallbackAction)
    {
        if (!Il2CppCollectionReflection.TryReadElements(collection, out var elements, out var error))
        {
            _log?.Debug($"  Conversation clone injection: membership check unavailable ({error}); {fallbackAction}.");
            return null;
        }

        var pointers = new HashSet<IntPtr>();
        foreach (var element in elements)
            if (element is Il2CppObjectBase typed)
                pointers.Add(typed.Pointer);
        return pointers;
    }

    private static IReadOnlyList<object> WithoutAlreadyPresent(object collection, IReadOnlyList<object> clones)
    {
        var present = NativePointersIn(collection, "appending all");
        if (present == null)
            return clones;

        List<object> kept = null;
        for (var i = 0; i < clones.Count; i++)
        {
            if (clones[i] is Il2CppObjectBase typed && present.Contains(typed.Pointer))
            {
                kept ??= new List<object>(clones.Take(i));
                continue;
            }
            kept?.Add(clones[i]);
        }
        return kept ?? clones;
    }

    private static bool TryAppendToMasterArraySingle(ManagerCache managerCache, object clone)
        => TryAppendToMasterArrayBatch(managerCache, new[] { clone }) > 0;

    private static int TryAppendToMasterArrayBatch(ManagerCache managerCache, IReadOnlyList<object> clones)
    {
        if (managerCache.MasterArrayProp == null || clones.Count == 0) return 0;
        if (!managerCache.TryGetManager(out var manager)) return 0;
        var array = managerCache.MasterArrayProp.GetValue(manager);
        if (array == null)
        {
            _log?.Warning("  Conversation clone injection: m_ConversationTemplates is null.");
            return 0;
        }

        clones = WithoutAlreadyPresent(array, clones);
        if (clones.Count == 0) return 0;

        if (!Il2CppCollectionReflection.TryRebuildReferenceArrayBatch(
                array, managerCache.MasterArrayType, managerCache.MasterArrayElementType,
                clones, out var newArray, out var error))
        {
            _log?.Warning($"  Conversation clone injection: master-array rebuild failed: {error}");
            return 0;
        }

        try { managerCache.MasterArrayProp.SetValue(manager, newArray); return clones.Count; }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone injection: writing master array threw: {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------
    // Manager-cache build (once per manager).
    // -----------------------------------------------------------------

    private static ManagerCache TryBuildManagerCache(Il2CppObjectBase manager)
    {
        ManagerCache cache = null;
        try
        {
            var managerType = manager.GetType();
            cache = new ManagerCache(manager);

            cache.MasterArrayProp = managerType.GetProperty("m_ConversationTemplates",
                BindingFlags.Public | BindingFlags.Instance);
            if (cache.MasterArrayProp != null)
            {
                var sampleArray = cache.MasterArrayProp.GetValue(manager);
                if (sampleArray != null)
                {
                    cache.MasterArrayType = sampleArray.GetType();
                    cache.MasterArrayElementType =
                        Il2CppCollectionReflection.GetArrayElementType(cache.MasterArrayType);
                    cache.ConversationType = SampleManagerConversationType(sampleArray, cache.MasterArrayType);
                }
            }

            cache.BucketDictProp = managerType.GetProperty("m_AvailableTemplatesByTriggerType",
                BindingFlags.Public | BindingFlags.Instance);
            if (cache.BucketDictProp != null)
            {
                var sampleDict = cache.BucketDictProp.GetValue(manager);
                if (sampleDict != null)
                {
                    var dictType = sampleDict.GetType();
                    cache.BucketTriggerType = dictType.GetGenericArguments()[0];
                    cache.BucketListType = dictType.GetGenericArguments()[1];
                    cache.BucketContainsKey = dictType.GetMethod("ContainsKey", new[] { cache.BucketTriggerType });
                    cache.BucketGetItem = dictType.GetMethod("get_Item", new[] { cache.BucketTriggerType });
                    cache.BucketSetItem = dictType.GetMethod("set_Item",
                        new[] { cache.BucketTriggerType, cache.BucketListType });
                    cache.BucketListAdd = cache.BucketListType.GetMethod("Add");
                    cache.BucketListCtor = cache.BucketListType.GetConstructor(Type.EmptyTypes);
                }
            }

            return cache;
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation manager cache build failed: {ex.Message}");
            cache?.Release();
            return null;
        }
    }

    private static int? SampleManagerConversationType(object array, Type arrayType)
    {
        if (array == null) return null;
        var lengthProp = arrayType.GetProperty("Length",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var indexer = Il2CppIndexerLookup.FindIntIndexer(arrayType);
        if (lengthProp == null || indexer == null) return null;

        var length = (int)lengthProp.GetValue(array);
        var args = new object[1];
        for (var i = 0; i < length; i++)
        {
            args[0] = i;
            var element = indexer.GetValue(array, args) as Il2CppObjectBase;
            if (element == null) continue;
            var t = ReadEnumIntProperty(element, "ConversationType");
            if (t != null) return t;
        }
        return null;
    }

    private static int? ReadEnumIntProperty(Il2CppObjectBase obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return null;
        var raw = prop.GetValue(obj);
        return raw == null ? null : (int?)Convert.ToInt32(raw);
    }

    // -----------------------------------------------------------------
    // Clone preparation (once per clone, regardless of manager count).
    // -----------------------------------------------------------------

    private static CloneTypeCache GetOrBuildCloneTypeCache(Type cloneType)
    {
        lock (Sync)
        {
            if (CloneTypeCaches.TryGetValue(cloneType, out var existing)) return existing;
        }
        var cache = new CloneTypeCache
        {
            CloneType = cloneType,
            ConversationTypeProp = cloneType.GetProperty("ConversationType", BindingFlags.Public | BindingFlags.Instance),
            ActiveProp = cloneType.GetProperty("Active", BindingFlags.Public | BindingFlags.Instance),
            TriggersProp = cloneType.GetProperty("Triggers", BindingFlags.Public | BindingFlags.Instance),
            RolesProp = cloneType.GetProperty("Roles", BindingFlags.Public | BindingFlags.Instance),
            NodesProp = cloneType.GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance),
        };
        lock (Sync) CloneTypeCaches[cloneType] = cache;
        return cache;
    }

    private static PreparedClone PrepareClone(Il2CppObjectBase clone, Type cloneType)
    {
        try
        {
            var cloneCache = GetOrBuildCloneTypeCache(cloneType);
            var typed = Il2CppReflectiveCast.CastOrNull(clone, cloneType);
            if (typed == null)
            {
                _log?.Warning($"  Conversation clone prep: failed to cast clone to {cloneType.FullName}.");
                return null;
            }

            // DeepCopyRoles must run BEFORE patches, otherwise modder-side
            // `set "Roles" index=N { ... }` mutations land on the source
            // template's shared Role objects. Refresh, on the other hand,
            // must run AFTER patches (deferred to injection time below),
            // because patches change m_SerializedRequirements/m_SerializedNodes
            // and the typed state has to be rebuilt from those.
            DeepCopyRoles(typed, cloneCache);

            // ConversationType/Active/Triggers are read from the typed
            // wrapper here for pre-injection filtering. These properties
            // aren't mutated by KDL patches in practice (no Jiangyu-shaped
            // patch targets them), so reading them once at registration is
            // safe.
            var conversationTypeRaw = cloneCache.ConversationTypeProp?.GetValue(typed);
            if (conversationTypeRaw == null)
            {
                _log?.Warning($"  Conversation clone prep: clone (type {cloneType.FullName}) exposes no ConversationType; skipping.");
                return null;
            }
            var conversationType = Convert.ToInt32(conversationTypeRaw);

            return new PreparedClone
            {
                TypedWrapper = typed,
                CloneType = cloneType,
                CloneCache = cloneCache,
                ConversationType = conversationType,
            };
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone prep threw: {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Idempotent refresh: rebuilds typed Requirements/m_Nodes from the
    /// serialised string lists (via OnAfterDeserialize), then re-reads
    /// Active and Triggers from the typed wrapper. Called on every
    /// injection because the registry has no signal for "patches done":
    /// a clone may be registered before patches run, in which case the
    /// first refresh captures pre-patch state; later RegisterManager
    /// replays must re-refresh against the now-patched strings. The
    /// underlying OnAfterDeserialize calls are idempotent, so re-running
    /// is safe.
    /// </summary>
    private static void Refresh(PreparedClone prepared)
    {
        try
        {
            RefreshConversationTemplateTypedState(prepared.TypedWrapper, prepared.CloneType, prepared.CloneCache);

            var active = true;
            if (prepared.CloneCache.ActiveProp != null)
            {
                var raw = prepared.CloneCache.ActiveProp.GetValue(prepared.TypedWrapper);
                active = raw is bool b && b;
            }
            prepared.Active = active;
            prepared.Triggers = ReadEnumIntList(prepared.TypedWrapper, prepared.CloneCache.TriggersProp)
                ?? Array.Empty<int>();
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone refresh threw: {ex.InnerException?.Message ?? ex.Message}");
            prepared.Triggers = Array.Empty<int>();
        }
    }

    private static int[] ReadEnumIntList(object typed, PropertyInfo triggersProp)
    {
        if (triggersProp == null) return null;
        var list = triggersProp.GetValue(typed);
        if (list == null) return null;

        var listType = list.GetType();
        var countProp = listType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        var indexer = listType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        if (countProp == null || indexer == null) return null;

        var count = (int)countProp.GetValue(list);
        var result = new int[count];
        var args = new object[1];
        for (var i = 0; i < count; i++)
        {
            args[0] = i;
            result[i] = Convert.ToInt32(indexer.GetValue(list, args));
        }
        return result;
    }

    // -----------------------------------------------------------------
    // Per-clone deep copies (Roles + Nodes), unchanged in behaviour.
    // -----------------------------------------------------------------

    private static void DeepCopyRoles(object typedClone, CloneTypeCache cloneCache)
    {
        try
        {
            if (cloneCache.RolesProp == null || !cloneCache.RolesProp.CanRead) return;
            var roles = cloneCache.RolesProp.GetValue(typedClone);
            if (roles == null) return;

            var rolesType = roles.GetType();
            var countProp = rolesType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var indexer = rolesType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            var setItem = indexer?.GetSetMethod();
            if (countProp == null || indexer == null || setItem == null) return;

            var count = (int)countProp.GetValue(roles);
            if (count == 0) return;

            var elementType = rolesType.IsGenericType ? rolesType.GenericTypeArguments[0] : null;
            if (elementType == null) return;

            var readArgs = new object[1];
            for (var i = 0; i < count; i++)
            {
                readArgs[0] = i;
                var sourceRole = indexer.GetValue(roles, readArgs);
                if (sourceRole == null) continue;
                var freshRole = TemplateCloneApplier.CloneValueObjectByFieldReflection(
                    sourceRole, elementType, "Conversation clone Role copy", new Jiangyu.Loader.Logging.LoaderLog(_log));
                if (freshRole == null) continue;
                setItem.Invoke(roles, new[] { i, freshRole });
            }
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone DeepCopyRoles threw: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void RefreshConversationTemplateTypedState(object typedClone, Type cloneType, CloneTypeCache cloneCache)
    {
        try
        {
            // ConversationNodeContainer.OnAfterDeserialize(ConversationTemplate)
            // and Role.OnAfterDeserialize(ConversationTemplate) rebuild
            // typed m_Nodes / Requirements from their string list backings.
            var nodes = cloneCache.NodesProp?.GetValue(typedClone);
            if (nodes != null)
            {
                var nodesType = nodes.GetType();
                var deserialise = nodesType.GetMethod("OnAfterDeserialize",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null, types: new[] { cloneType }, modifiers: null);
                deserialise?.Invoke(nodes, new[] { typedClone });
            }

            var roles = cloneCache.RolesProp?.GetValue(typedClone);
            if (roles != null)
            {
                var rolesType = roles.GetType();
                var countProp = rolesType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                var indexer = rolesType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
                if (countProp != null && indexer != null)
                {
                    var count = (int)countProp.GetValue(roles);
                    var args = new object[1];
                    for (var i = 0; i < count; i++)
                    {
                        args[0] = i;
                        var role = indexer.GetValue(roles, args);
                        if (role == null) continue;
                        var roleType = role.GetType();
                        var deserialise = roleType.GetMethod("OnAfterDeserialize",
                            BindingFlags.Public | BindingFlags.Instance,
                            binder: null, types: new[] { cloneType }, modifiers: null);
                        deserialise?.Invoke(role, new[] { typedClone });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone refresh failed: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    // Cache types.
    // -----------------------------------------------------------------

    private sealed class ManagerCache
    {
        // Liveness is the NATIVE manager's, through a weak IL2CPP GC handle. A managed wrapper is
        // pooled weakly and rebuilt after a GC, so its collection says nothing about the manager.
        // The handle's target drops to zero exactly when the IL2CPP GC frees the manager, which
        // is the only point at which its address can be handed to another one, and RegisterManager
        // treats that as "not registered". Weak so the registry never pins a destroyed manager.
        private readonly IntPtr _pointer;
        private nint _weakHandle;
        private WeakReference<Il2CppObjectBase> _wrapper;

        public Type ManagerType;
        public int? ConversationType;

        public PropertyInfo MasterArrayProp;
        public Type MasterArrayType;
        public Type MasterArrayElementType;

        public PropertyInfo BucketDictProp;
        public Type BucketTriggerType;
        public Type BucketListType;
        public MethodInfo BucketContainsKey;
        public MethodInfo BucketGetItem;
        public MethodInfo BucketSetItem;
        public MethodInfo BucketListAdd;
        public ConstructorInfo BucketListCtor;

        public ManagerCache(Il2CppObjectBase manager)
        {
            _pointer = manager.Pointer;
            _weakHandle = IL2CPP.il2cpp_gchandle_new_weakref(_pointer, false);
            _wrapper = new WeakReference<Il2CppObjectBase>(manager);
            ManagerType = manager.GetType();
        }

        public bool IsAlive
            => _weakHandle != 0 && IL2CPP.il2cpp_gchandle_get_target(_weakHandle) == _pointer;

        // The manager's wrapper, rebuilt over the same native object when the pooled one has
        // been collected. Reflection through the cached PropertyInfos needs one of the manager's
        // own type.
        public bool TryGetManager(out Il2CppObjectBase target)
        {
            if (_wrapper.TryGetTarget(out target))
                return true;
            if (!IsAlive)
            {
                target = null;
                return false;
            }
            target = (Il2CppObjectBase)Activator.CreateInstance(ManagerType, _pointer);
            _wrapper = new WeakReference<Il2CppObjectBase>(target);
            return true;
        }

        // Frees the handle. Called when the entry leaves the registry.
        public void Release()
        {
            if (_weakHandle == 0)
                return;
            IL2CPP.il2cpp_gchandle_free(_weakHandle);
            _weakHandle = 0;
        }
    }

    private sealed class CloneTypeCache
    {
        public Type CloneType;
        public PropertyInfo ConversationTypeProp;
        public PropertyInfo ActiveProp;
        public PropertyInfo TriggersProp;
        public PropertyInfo RolesProp;
        public PropertyInfo NodesProp;
    }

    private sealed class PreparedClone
    {
        public object TypedWrapper;
        public Type CloneType;
        public CloneTypeCache CloneCache;
        public int ConversationType;
        public bool Active;
        public int[] Triggers;
    }
}
