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
// m_TemplateArrays (the GetAll<T> enumeration backing store) is not written;
// consumers that need to see the clone look it up by ID. Contract rationale
// and verification live in docs/research/verified/template-cloning.md.

/// <summary>
/// Applies merged template clone directives at runtime by deep-copying live
/// templates and registering the copies with <c>DataTemplateLoader</c>. Clones
/// must run before <see cref="TemplatePatchApplier"/> so patches can target
/// the newly registered clone IDs.
/// </summary>
internal sealed class TemplateCloneApplier
{
    private readonly TemplateCloneCatalog _catalog;
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
            if (innerMap.ContainsKey(directive.CloneId))
            {
                continue;
            }

            if (!innerMap.TryGetValue(directive.SourceId, out var source))
            {
                log.Warning(
                    $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                    + "source template not found in DataTemplateLoader.");
                continue;
            }

            if (!TryCloneTemplate(source, directive.CloneId, log, out var clone))
                continue;

            innerMap[directive.CloneId] = clone;

            // Also extend m_TemplateArrays so DataTemplateLoader.GetAll<T>()
            // sees the clone. Without this, gameplay code that enumerates all
            // templates of a type skips clones even though TryGet<T>(id) finds
            // them. Failure here is non-fatal: patches still apply via the
            // m_TemplateMaps entry; only GetAll<T> consumers miss the clone.
            if (!TryExtendTemplateArray(resolvedType, clone, log))
            {
                log.Warning(
                    $"Template clone '{templateTypeName}:{directive.CloneId}': "
                    + "GetAll<T> enumeration won't see this clone; check earlier diagnostics for cause.");
            }

            log.Msg(
                $"Template clone registered: {templateTypeName}:{directive.SourceId} -> {directive.CloneId} "
                + $"(mod '{directive.OwnerLabel}').");
            applied++;
        }

        _appliedTypes.Add(templateTypeName);
        return applied;
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
