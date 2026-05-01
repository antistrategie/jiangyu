using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
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

    public int TryApply(MelonLogger.Instance log)
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

    private int TryApplyType(
        string templateTypeName,
        Dictionary<string, LoadedCloneDirective> directives,
        MelonLogger.Instance log)
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

        if (!TryGetTemplateMap(resolvedType, out var innerMap))
            return 0;

        var applied = 0;
        foreach (var directive in directives.Values)
        {
            if (!innerMap.TryGetValue(directive.CloneId, out var clone))
            {
                if (!innerMap.TryGetValue(directive.SourceId, out var source))
                {
                    log.Warning(
                        $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                        + "source template not found in DataTemplateLoader.");
                    continue;
                }

                if (!TryCloneTemplate(source, directive.CloneId, log, out clone))
                    continue;

                // Object.Instantiate shallow-copies PPtr lists, so the clone's
                // owned-element collections (EventHandlers and any future
                // abstract-polymorphic ScriptableObject-element list) point to
                // the same handler assets as the source. Patches through the
                // clone would mutate the source's handlers. Deep-copy each
                // owned element so the clone has its own. resolvedType is the
                // concrete managed wrapper type (e.g. PerkTemplate); the
                // clone variable is DataTemplate-typed, so reflection on it
                // sees only base-class members. We re-cast inside the helper.
                DeepCopyOwnedReferences(clone, resolvedType, directive.CloneId, log);

                RegisterCloneIntoSlot(resolvedType, innerMap, resolvedType, directive.CloneId, clone, log);

                log.Msg(
                    $"Template clone registered: {templateTypeName}:{directive.SourceId} -> {directive.CloneId} "
                    + $"(mod '{directive.OwnerLabel}').");
                applied++;
            }

            MirrorCloneToAncestors(resolvedType, directive.CloneId, clone, log);
        }

        _appliedTypes.Add(templateTypeName);
        return applied;
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
        MelonLogger.Instance log)
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
    /// <c>m_TemplateMaps</c>/<c>m_TemplateArrays</c> slot the game has already
    /// materialised, walking <paramref name="resolvedType"/>.<c>BaseType</c>
    /// upward to <c>DataTemplate</c>. Caller must have registered the clone
    /// into the most-derived slot first; this method handles ancestors only.
    /// Idempotent: each ancestor is independently gated on
    /// <c>ContainsKey(cloneId)</c>, so re-registration ticks fill in slots
    /// the game materialises later than the first apply pass. The walk only
    /// writes to slots already in the outer dict; it never creates new
    /// ancestor-keyed slots, so its visible destinations are bounded by what
    /// the game itself populates.
    /// </summary>
    private static void MirrorCloneToAncestors(
        Type resolvedType, string cloneId, DataTemplate clone, MelonLogger.Instance log)
    {
        var dataTemplateType = typeof(DataTemplate);
        var current = resolvedType.BaseType;
        while (current != null && dataTemplateType.IsAssignableFrom(current))
        {
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
    private static void DeepCopyOwnedReferences(DataTemplate clone, Type concreteType, string cloneId, MelonLogger.Instance log)
    {
        if (clone == null)
        {
            log.Msg($"Template clone '{cloneId}': deep-copy skipped, clone is null.");
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

            var elementType = GetIl2CppListElementType(prop.PropertyType);
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

            var elementType = GetIl2CppListElementType(field.FieldType);
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
            log.Msg(
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
        MelonLogger.Instance log,
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
    {
        if (OwnedElementTypeCache.TryGetValue(elementType, out var cached))
            return cached;

        bool decision;
        try
        {
            if (typeof(DataTemplate).IsAssignableFrom(elementType))
                decision = false;
            else if (!typeof(UnityEngine.ScriptableObject).IsAssignableFrom(elementType))
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

    internal static Type GetIl2CppListElementType(Type collectionType)
    {
        if (collectionType == null) return null;
        if (!collectionType.IsGenericType) return null;
        var def = collectionType.GetGenericTypeDefinition().FullName ?? string.Empty;
        if (def != "Il2CppSystem.Collections.Generic.List`1"
            && def != "System.Collections.Generic.List`1")
            return null;
        return collectionType.GenericTypeArguments.FirstOrDefault();
    }

    private static bool TryCloneTemplate(
        Il2CppObjectBase source,
        string cloneId,
        MelonLogger.Instance log,
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

    // Walks the class hierarchy to find m_ID (inherited from DataTemplate base),
    // reads its IL2CPP offset by name, and writes the new Il2Cpp string pointer.
    private static bool TryWriteTemplateId(Il2CppObjectBase clone, string cloneId, MelonLogger.Instance log)
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
            var idField = FindFieldInHierarchy(klass, "m_ID");
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

    private static IntPtr FindFieldInHierarchy(IntPtr klass, string fieldName)
    {
        var current = klass;
        while (current != IntPtr.Zero)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(current, fieldName);
            if (field != IntPtr.Zero)
                return field;

            current = IL2CPP.il2cpp_class_get_parent(current);
        }

        return IntPtr.Zero;
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
        Type resolvedType, Il2CppObjectBase clone, MelonLogger.Instance log)
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

        var dictIndexer = FindIndexerWithKey(arraysType, il2CppType.GetType());
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

    private static PropertyInfo FindIndexerWithKey(Type type, Type keyType)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == keyType)
                return property;
        }
        return null;
    }

}
