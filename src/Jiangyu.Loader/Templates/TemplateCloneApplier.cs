using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;

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

        var singleton = DataTemplateLoader.GetSingleton();
        var innerMap = singleton.m_TemplateMaps[Il2CppType.From(resolvedType)];

        var applied = 0;
        foreach (var directive in directives.Values)
        {
            if (!TemplateRuntimeAccess.TryGetTemplateById(
                    templateTypeName, directive.SourceId, out var source, out _, out var sourceError))
            {
                var reason = string.IsNullOrEmpty(sourceError) ? "not found" : sourceError;
                log.Warning(
                    $"Template clone '{templateTypeName}:{directive.SourceId} -> {directive.CloneId}': "
                    + $"source template {reason}.");
                continue;
            }

            if (!TryCloneTemplate(source, directive.CloneId, log, out var clone))
                continue;

            innerMap[directive.CloneId] = clone;
            log.Msg(
                $"Template clone registered: {templateTypeName}:{directive.SourceId} -> {directive.CloneId} "
                + $"(mod '{directive.OwnerLabel}').");
            applied++;
        }

        _appliedTypes.Add(templateTypeName);
        return applied;
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

}
