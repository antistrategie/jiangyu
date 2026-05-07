using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;

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
/// latches for that type.
/// </summary>
internal sealed partial class TemplatePatchApplier
{
    private readonly TemplatePatchCatalog _catalog;
    private readonly ModAssetResolver _assetResolver;
    private readonly HashSet<string> _appliedTypes = new(StringComparer.Ordinal);

    public TemplatePatchApplier(TemplatePatchCatalog catalog, ModAssetResolver assetResolver = null)
    {
        _catalog = catalog;
        _assetResolver = assetResolver;
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

    private int TryApplyType(
        string templateTypeName,
        Dictionary<string, List<LoadedPatchOperation>> patchesForType,
        MelonLogger.Instance log)
    {
        // Use GetAllTemplates once only to trigger materialisation and detect
        // "templates not ready yet" (return 0, retry next scene) vs terminal
        // type-resolution failure. Per-template lookups below go through
        // DataTemplateLoader.TryGet<T> directly, which reads m_TemplateMaps and
        // therefore sees both game-native templates and Jiangyu clones.
        var liveTemplates = TemplateRuntimeAccess.GetAllTemplates(templateTypeName, out var resolvedType, out var resolveError);

        if (resolvedType == null)
        {
            var expectedOps = patchesForType.Values.Sum(inner => inner.Count);
            log.Warning(
                $"Template patch: cannot resolve type '{templateTypeName}' ({resolveError}); "
                + $"skipping {expectedOps} op(s).");
            _appliedTypes.Add(templateTypeName);
            return 0;
        }

        if (liveTemplates.Count == 0)
            return 0;

        var applied = 0;
        var missingTemplate = 0;
        var missingMember = 0;
        var conversionFailed = 0;

        foreach (var templateEntry in patchesForType)
        {
            if (!TemplateRuntimeAccess.TryGetTemplateById(
                    templateTypeName, templateEntry.Key,
                    out var template, out _, out var lookupError))
            {
                missingTemplate += templateEntry.Value.Count;
                var reason = string.IsNullOrEmpty(lookupError) ? "not found" : lookupError;
                log.Warning(
                    $"Template patch: no live {templateTypeName} with m_ID '{templateEntry.Key}' ({reason}); "
                    + $"skipping {templateEntry.Value.Count} op(s).");
                continue;
            }

            foreach (var op in templateEntry.Value)
            {
                switch (TryApplyOperation(template, templateTypeName, templateEntry.Key, op, _assetResolver, log))
                {
                    case ApplyOutcome.Applied:
                        applied++;
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

        _appliedTypes.Add(templateTypeName);
        log.Msg(
            $"Applied {applied} {templateTypeName} patch op(s). "
            + $"[skipped: missingTemplate={missingTemplate} missingMember={missingMember} "
            + $"conversion={conversionFailed}]");
        return applied;
    }

    private static string FormatPrefix(string templateTypeName, string templateId, LoadedPatchOperation op)
        => $"Template patch '{templateTypeName}:{templateId}.{op.FieldPath}' (mod '{op.OwnerLabel}'): ";

    // For Il2Cpp wrappers, identity is the native object pointer, not
    // Il2CppObjectBase.Equals (which compares per-wrapper GC handles and so
    // returns false for two wrappers pooled over the same native object).
    // Everything else (scalar boxed values, strings, enums) goes through
    // object.Equals as normal.
    private static bool ReadbackMatches(object written, object readback)
    {
        if (written is Il2CppObjectBase writtenObj && readback is Il2CppObjectBase readbackObj)
            return writtenObj.Pointer == readbackObj.Pointer;
        return Equals(written, readback);
    }

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

        return string.IsNullOrWhiteSpace(id) ? typeName : $"{typeName} '{id}'";
    }

    // One segment of a parsed dotted field path. Index is non-null when
    // the segment carried a bracketed [N] indexer; the operations partial
    // dispatches on it to either bind the array element or fall through
    // to a plain member set.
    private readonly struct PathSegment
    {
        public PathSegment(string name, int? index)
        {
            Name = name;
            Index = index;
        }

        public string Name { get; }
        public int? Index { get; }
    }

    private enum ApplyOutcome
    {
        Applied,
        MemberMissing,
        ConversionFailed,
    }
}
