using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

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
/// </summary>
internal static class ConversationManagerRegistry
{
    private static readonly object Sync = new();
    private static readonly List<WeakReference<Il2CppObjectBase>> Managers = new();
    private static readonly HashSet<IntPtr> KnownManagerPointers = new();
    private static readonly List<(Il2CppObjectBase Wrapper, Type WrapperType)> Clones = new();

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

        (Il2CppObjectBase Wrapper, Type WrapperType)[] clonesSnapshot;
        lock (Sync)
        {
            // Dedup: this method gets called from a per-trigger hot postfix,
            // so the same manager pointer will arrive many times. Register
            // only the first call per pointer; the matcher's index is
            // stable from then on.
            if (!KnownManagerPointers.Add(pointer)) return;
            Managers.Add(new WeakReference<Il2CppObjectBase>(manager));
            clonesSnapshot = Clones.ToArray();
        }

        var managerTypeName = manager.GetType().FullName ?? "<unknown>";
        _log?.Msg($"  Conversation manager registered: {managerTypeName} (replaying {clonesSnapshot.Length} known clone(s)).");
        foreach (var (cloneWrapper, cloneType) in clonesSnapshot)
            TryInjectCloneIntoManager(manager, cloneWrapper, cloneType);
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
        WeakReference<Il2CppObjectBase>[] managersSnapshot;
        lock (Sync)
        {
            Clones.Add((clone, resolvedType));
            managersSnapshot = Managers.ToArray();
        }

        foreach (var weakRef in managersSnapshot)
        {
            if (!weakRef.TryGetTarget(out var manager) || manager == null) continue;
            TryInjectCloneIntoManager(manager, clone, resolvedType);
        }
    }

    private static void TryInjectCloneIntoManager(Il2CppObjectBase manager, Il2CppObjectBase clone, Type cloneType)
    {
        try
        {
            var managerType = manager.GetType();
            var managerConversationType = SampleManagerConversationType(manager, managerType);
            var cloneConversationType = ReadEnumIntPropertyOnType(clone, cloneType, "ConversationType");

            if (cloneConversationType == null)
            {
                _log?.Warning($"  Conversation clone injection: clone (type {cloneType.FullName}) exposes no ConversationType; skipping.");
                return;
            }
            if (managerConversationType != null && managerConversationType.Value != cloneConversationType.Value)
                return;

            // The list element type is the typed wrapper (ConversationTemplate),
            // not Il2CppObjectBase. Cast through the wrapper Type so List.Add
            // accepts the value.
            var typedClone = AsTypedWrapper(clone, cloneType);
            if (typedClone == null)
            {
                _log?.Warning($"  Conversation clone injection: failed to cast clone to {cloneType.FullName}.");
                return;
            }

            // UnityEngine.Object.Instantiate on a ConversationTemplate
            // shallow-copies the Roles List<Role>. Even after the loader's
            // container-deep-copy gives the clone its own list instance, each
            // Role element inside is still the same object as the source
            // template's Role. Modder-side patches that descend into a Role
            // (`set "Roles" index=N { ... }`) and replace its
            // m_SerializedRequirements entries mutate the source's Role in
            // place. After patches, both clone and source point at the same
            // mutated Role with the new tag — vanilla sy's click_bark now
            // requires "voymastina" tag and stops firing for sy. Replace
            // each Role on the clone with a fresh Role carrying field-level
            // copies so subsequent patches stay on the clone.
            DeepCopyRoles(typedClone, cloneType);

            // KDL patches mutate m_SerializedRequirements on Roles and
            // m_SerializedNodes on Nodes. Each of those serialised string
            // lists has a typed counterpart (Requirements / m_Nodes) that the
            // matcher consumes, and is normally rebuilt from the strings by
            // Unity's deserialisation callbacks. A runtime-patched template
            // skips those callbacks, so the typed state is stale or empty
            // and the matcher walks it into NREs. Manually invoke
            // OnAfterDeserialize on the relevant sub-objects to rebuild.
            RefreshConversationTemplateTypedState(typedClone, cloneType);

            // Vanilla MENACE only places Active=True conversations into the
            // per-trigger bucket. Inherit that behaviour: if the clone's
            // Active flag is false (inherited from a vanilla template that
            // was disabled at game-design time), keep it out of the bucket
            // so the bark dispatcher's random-tie pick doesn't include it.
            var activeProp = cloneType.GetProperty("Active", BindingFlags.Public | BindingFlags.Instance);
            if (activeProp != null)
            {
                var raw = activeProp.GetValue(typedClone);
                var isActive = raw is bool b && b;
                if (!isActive)
                {
                    _log?.Msg(
                        $"  Conversation clone injection: clone is Active=False; skipped (matches vanilla bucket population).");
                    return;
                }
            }

            // Add to the per-trigger bucket dictionary so the matcher's hot
            // path (GetAvailableConversationTemplates) finds the clone.
            var triggers = ReadEnumIntListOnType(clone, cloneType, "Triggers");
            if (triggers == null || triggers.Count == 0)
            {
                _log?.Warning(
                    $"  Conversation clone injection: clone has no Triggers; skipped per-trigger bucket update.");
                return;
            }

            var bucketsAdded = 0;
            foreach (var trigger in triggers)
            {
                if (TryAppendToTriggerBucket(manager, managerType, trigger, typedClone))
                    bucketsAdded++;
            }

            // Also append to m_ConversationTemplates master array. MENACE's
            // dispatch picks click_bark deterministically for vanilla sy but
            // cycles through all eligibles when only the per-trigger bucket
            // is updated. The master array is the canonical "all known
            // conversations" list. Bucket-only entries get a different
            // selection treatment; only entries present in the master array
            // participate in the deterministic pick.
            var masterAdded = TryAppendToMasterArray(manager, managerType, typedClone);

            _log?.Msg(
                $"  Conversation clone injection: into {managerType.FullName} (ConversationType={cloneConversationType}, "
                + $"{bucketsAdded}/{triggers.Count} bucket(s) updated, masterArrayAdded={masterAdded}).");
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone injection failed for {manager?.GetType().FullName}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>
    /// Append a clone to the manager's <c>m_ConversationTemplates</c> master
    /// array. Il2CppReferenceArray&lt;T&gt; is fixed-size, so we rebuild it
    /// length+1 with the new element at the end and write it back.
    /// </summary>
    private static bool TryAppendToMasterArray(
        Il2CppObjectBase manager, Type managerType, object clone)
    {
        var prop = managerType.GetProperty("m_ConversationTemplates",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanRead || !prop.CanWrite)
        {
            _log?.Warning("  Conversation clone injection: m_ConversationTemplates not readable/writable.");
            return false;
        }

        var array = prop.GetValue(manager);
        if (array == null)
        {
            _log?.Warning("  Conversation clone injection: m_ConversationTemplates is null.");
            return false;
        }

        var arrayType = array.GetType();
        var elementType = Il2CppCollectionReflection.GetArrayElementType(arrayType);
        if (elementType == null)
        {
            _log?.Warning(
                $"  Conversation clone injection: m_ConversationTemplates is {arrayType.FullName}, expected Il2CppReferenceArray.");
            return false;
        }

        if (!Il2CppCollectionReflection.TryRebuildReferenceArray(
                array, arrayType, elementType, appendedElement: clone, out var newArray, out var error))
        {
            _log?.Warning($"  Conversation clone injection: master-array rebuild failed: {error}");
            return false;
        }

        try { prop.SetValue(manager, newArray); return true; }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone injection: writing master array threw: {ex.Message}");
            return false;
        }
    }

    private static bool TryAppendToTriggerBucket(
        Il2CppObjectBase manager, Type managerType, int trigger, object clone)
    {
        var dictProp = managerType.GetProperty("m_AvailableTemplatesByTriggerType",
            BindingFlags.Public | BindingFlags.Instance);
        if (dictProp == null || !dictProp.CanRead)
        {
            _log?.Warning("  Conversation clone injection: m_AvailableTemplatesByTriggerType property missing.");
            return false;
        }

        var dict = dictProp.GetValue(manager);
        if (dict == null) return false;

        var dictType = dict.GetType();
        var triggerType = dictType.GetGenericArguments()[0];
        var listType = dictType.GetGenericArguments()[1];
        var triggerKey = Enum.ToObject(triggerType, trigger);

        var containsKey = dictType.GetMethod("ContainsKey", new[] { triggerType });
        var getItem = dictType.GetMethod("get_Item", new[] { triggerType });
        var setItem = dictType.GetMethod("set_Item", new[] { triggerType, listType });
        if (containsKey == null || getItem == null || setItem == null)
        {
            _log?.Warning("  Conversation clone injection: dict ContainsKey/get_Item/set_Item not found.");
            return false;
        }

        object list;
        var hasKey = (bool)containsKey.Invoke(dict, new[] { triggerKey });
        if (hasKey)
        {
            list = getItem.Invoke(dict, new[] { triggerKey });
        }
        else
        {
            var listCtor = listType.GetConstructor(Type.EmptyTypes);
            if (listCtor == null)
            {
                _log?.Warning("  Conversation clone injection: list parameterless ctor not found.");
                return false;
            }
            list = listCtor.Invoke(null);
            setItem.Invoke(dict, new[] { triggerKey, list });
        }

        // Append clone to the Il2Cpp list.
        var listAdd = listType.GetMethod("Add");
        if (listAdd == null)
        {
            _log?.Warning("  Conversation clone injection: list Add method not found.");
            return false;
        }
        listAdd.Invoke(list, new object[] { clone });
        return true;
    }

    private static int? SampleManagerConversationType(Il2CppObjectBase manager, Type managerType)
    {
        var prop = managerType.GetProperty("m_ConversationTemplates",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanRead) return null;

        var array = prop.GetValue(manager);
        if (array == null) return null;

        var arrayType = array.GetType();
        var lengthProp = arrayType.GetProperty("Length",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var indexer = FindIndexer(arrayType);
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
        => ReadEnumIntPropertyOnType(obj, obj.GetType(), propertyName);

    private static int? ReadEnumIntPropertyOnType(Il2CppObjectBase obj, Type wrapperType, string propertyName)
    {
        // Cast through the specific wrapper Type when the caller statically
        // sees the object as a base class (e.g. UnityEngine.Object) — that
        // class doesn't carry the typed property we want to read.
        var typed = AsTypedWrapper(obj, wrapperType);
        if (typed == null) return null;
        var prop = wrapperType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return null;
        var raw = prop.GetValue(typed);
        if (raw == null) return null;
        return Convert.ToInt32(raw);
    }

    private static List<int> ReadEnumIntListOnType(Il2CppObjectBase obj, Type wrapperType, string propertyName)
    {
        var typed = AsTypedWrapper(obj, wrapperType);
        if (typed == null) return null;
        var prop = wrapperType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null) return null;
        var list = prop.GetValue(typed);
        if (list == null) return null;

        var listType = list.GetType();
        var countProp = listType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        var indexer = listType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        if (countProp == null || indexer == null) return null;

        var count = (int)countProp.GetValue(list);
        var result = new List<int>(count);
        var args = new object[1];
        for (var i = 0; i < count; i++)
        {
            args[0] = i;
            result.Add(Convert.ToInt32(indexer.GetValue(list, args)));
        }
        return result;
    }

    /// <summary>
    /// Replace each Role in the clone's Roles list with a fresh Role that
    /// carries field-level copies of the source Role's values. After this,
    /// modder-side patches that descend into Roles[i] mutate the clone's
    /// Role, not the source's. The m_SerializedRequirements list is also
    /// reallocated to a fresh List&lt;string&gt; with the same string entries
    /// so per-index replacements stay on the clone.
    /// </summary>
    private static void DeepCopyRoles(object typedClone, Type cloneType)
    {
        try
        {
            var rolesProp = cloneType.GetProperty("Roles", BindingFlags.Public | BindingFlags.Instance);
            if (rolesProp == null || !rolesProp.CanRead) return;
            var roles = rolesProp.GetValue(typedClone);
            if (roles == null) return;

            var rolesType = roles.GetType();
            var countProp = rolesType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var indexer = rolesType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            var setItem = indexer != null
                ? indexer.GetSetMethod()
                : null;
            if (countProp == null || indexer == null || setItem == null) return;

            var count = (int)countProp.GetValue(roles);
            if (count == 0) return;

            // Element type via the list's generic arg.
            var elementType = rolesType.IsGenericType
                ? rolesType.GenericTypeArguments[0]
                : null;
            if (elementType == null) return;

            var readArgs = new object[1];
            for (var i = 0; i < count; i++)
            {
                readArgs[0] = i;
                var sourceRole = indexer.GetValue(roles, readArgs);
                if (sourceRole == null) continue;
                var freshRole = TemplateCloneApplier.CloneValueObjectByFieldReflection(
                    sourceRole, elementType, "Conversation clone Role copy", _log);
                if (freshRole == null) continue;
                setItem.Invoke(roles, new[] { i, freshRole });
            }
        }
        catch (Exception ex)
        {
            _log?.Warning($"  Conversation clone DeepCopyRoles threw: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void RefreshConversationTemplateTypedState(object typedClone, Type cloneType)
    {
        try
        {
            // ConversationNodeContainer.OnAfterDeserialize(ConversationTemplate)
            // and Role.OnAfterDeserialize(ConversationTemplate) rebuild
            // typed m_Nodes / Requirements from their string list backings.
            var nodesProp = cloneType.GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance);
            var nodes = nodesProp?.GetValue(typedClone);
            if (nodes != null)
            {
                var nodesType = nodes.GetType();
                var deserialise = nodesType.GetMethod("OnAfterDeserialize",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null, types: new[] { cloneType }, modifiers: null);
                deserialise?.Invoke(nodes, new[] { typedClone });
            }

            var rolesProp = cloneType.GetProperty("Roles", BindingFlags.Public | BindingFlags.Instance);
            var roles = rolesProp?.GetValue(typedClone);
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

    private static object AsTypedWrapper(Il2CppObjectBase obj, Type wrapperType)
        => Il2CppReflectiveCast.CastOrNull(obj, wrapperType);

    private static PropertyInfo FindIndexer(Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 1) return p;
        }
        return null;
    }
}
