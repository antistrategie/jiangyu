using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

// JIANGYU-CONTRACT: Live template identity for patching is the serialised
// m_ID string, not (collection, pathId) or native pointer. Scope was derived
// from the 2026-04-19 MissionPreparation EntityTemplate dump (templateCount=
// 260, all 260 expose m_ID, live IDs unique, no live Collection or PathId
// surface). Each DataTemplate subtype is assumed to follow the same m_ID
// convention; the applier logs clearly when a subtype's live templates lack
// a readable m_ID so divergent cases are surfaced rather than silently
// ignored. Field paths are walked as dotted segments with optional [N]
// indexers; intermediate segments may be reference types or value types (the
// latter are read-boxed-and-written-back after the terminal set).

/// <summary>
/// One-shot applier that takes the merged catalogue of compiled template
/// patches and writes each scalar set operation into the matching live
/// template once the game has materialised the DataTemplate cache for that
/// type. Callers are expected to invoke <see cref="TryApply"/> repeatedly;
/// it no-ops until templates are present, then applies once per type and
/// latches for that type.
/// </summary>
internal sealed class TemplatePatchApplier
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

    private static ApplyOutcome TryApplyOperation(
        object template, string templateTypeName, string templateId, LoadedPatchOperation op,
        ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        if (!TryParseInnerSegments(op.FieldPath, out var innerSegments, out var parseError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + parseError);
            return ApplyOutcome.MemberMissing;
        }

        var descentCount = op.Descent?.Count ?? 0;
        var chain = new List<ChainEntry>(descentCount + innerSegments.Length);
        object current = template;

        // Walk descent steps first: each navigates into element [Index] of
        // collection [Field], optionally casting to the [Subtype] wrapper.
        if (op.Descent != null)
        {
            for (var i = 0; i < op.Descent.Count; i++)
            {
                var step = op.Descent[i];
                if (!TryReadMember(current, step.Field, out var value, out var memberType, out var readError))
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"cannot navigate descent step '{step.Field}': {readError}");
                    return ApplyOutcome.MemberMissing;
                }

                if (value == null)
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"descent step '{step.Field}' ({memberType.FullName}) is null on this template.");
                    return ApplyOutcome.MemberMissing;
                }

                var entry = new ChainEntry
                {
                    Parent = current,
                    Name = step.Field,
                    ValueIsStruct = memberType.IsValueType,
                };

                if (!TryIndexInto(value, step.Index, out var element, out var elementType, out var indexError))
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"cannot index descent step '{step.Field}' index={step.Index}: {indexError}");
                    return ApplyOutcome.MemberMissing;
                }

                if (element == null)
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"element {step.Index} of '{step.Field}' is null.");
                    return ApplyOutcome.MemberMissing;
                }

                // We don't support writing mutated struct-elements back into
                // collections on this slice. Reject early so modders know.
                if (elementType.IsValueType)
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"element {step.Index} of '{step.Field}' is a value-type "
                        + $"({elementType.FullName}); in-collection struct mutation is not supported on this slice.");
                    return ApplyOutcome.MemberMissing;
                }

                // Polymorphic descent: the Il2CppInterop wrapper for a
                // List<AbstractBase> element returns the base-type wrapper,
                // so reflection on it sees only the base's own members
                // (typically zero). Cast to the concrete subtype wrapper
                // when the modder declared one via type="X" on the descent
                // block so subclass fields are visible.
                if (!string.IsNullOrEmpty(step.Subtype))
                {
                    if (!TryCastToSubtype(element, step.Subtype, out var castElement, out var castError))
                    {
                        log.Warning(FormatPrefix(templateTypeName, templateId, op)
                            + $"cannot cast '{step.Field}[{step.Index}]' to subtype "
                            + $"'{step.Subtype}': {castError}");
                        return ApplyOutcome.MemberMissing;
                    }
                    element = castElement;
                }

                current = element;
                chain.Add(entry);
            }
        }

        // Walk inner path segments (dotted name only, no brackets).
        // Each non-terminal segment is a plain member read; the terminal
        // segment carries the actual write below.
        for (var i = 0; i < innerSegments.Length - 1; i++)
        {
            var segment = innerSegments[i];
            if (!TryReadMember(current, segment.Name, out var value, out var memberType, out var readError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot navigate segment '{segment.Name}': {readError}");
                return ApplyOutcome.MemberMissing;
            }

            if (value == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"intermediate segment '{segment.Name}' ({memberType.FullName}) is null on this template.");
                return ApplyOutcome.MemberMissing;
            }

            chain.Add(new ChainEntry
            {
                Parent = current,
                Name = segment.Name,
                ValueIsStruct = memberType.IsValueType,
            });
            current = value;
        }

        var terminal = innerSegments[^1];
        Action<object> setter;
        Func<object> getter;
        Type terminalType;

        if (op.Op == CompiledTemplateOp.Remove)
            return TryApplyRemove(current, terminal, templateTypeName, templateId, op, log);

        if (op.Op == CompiledTemplateOp.Clear)
            return TryApplyClear(current, terminal, templateTypeName, templateId, op, log);

        if (op.Op == CompiledTemplateOp.Append || op.Op == CompiledTemplateOp.InsertAt)
        {
            if (!TryReadMember(current, terminal.Name, out var collection, out var collectionType, out var readError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot read terminal collection '{terminal.Name}': {readError}");
                return ApplyOutcome.MemberMissing;
            }

            int? insertIndex = op.Op == CompiledTemplateOp.InsertAt ? op.Index : null;
            if (!TryBindCollectionMutation(
                    current, terminal.Name, collection, collectionType, insertIndex,
                    out terminalType, out setter, out getter, out var bindError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot bind {op.Op} on '{terminal.Name}': {bindError}");
                return ApplyOutcome.MemberMissing;
            }
        }
        else if (op.Op == CompiledTemplateOp.Set && op.Index.HasValue)
        {
            // Set one collection element via `set "Field" index=N <value>`.
            // Terminal is non-indexed (validator enforces); resolve the
            // collection then bind the element at op.Index.
            if (!TryReadMember(current, terminal.Name, out var arrayValue, out var arrayType, out var readError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot read terminal collection '{terminal.Name}': {readError}");
                return ApplyOutcome.MemberMissing;
            }

            if (arrayValue == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"terminal collection '{terminal.Name}' ({arrayType.FullName}) is null on this template.");
                return ApplyOutcome.MemberMissing;
            }

            if (!TryBindArrayElement(arrayValue, op.Index.Value, out terminalType, out setter, out getter, out var elementError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot bind '{terminal.Name}' index={op.Index.Value}: {elementError}");
                return ApplyOutcome.MemberMissing;
            }
        }
        else if (terminal.Index.HasValue)
        {
            if (!TryReadMember(current, terminal.Name, out var arrayValue, out var arrayType, out var readError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot read terminal array member '{terminal.Name}': {readError}");
                return ApplyOutcome.MemberMissing;
            }

            if (arrayValue == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"terminal array '{terminal.Name}' ({arrayType.FullName}) is null on this template.");
                return ApplyOutcome.MemberMissing;
            }

            if (!TryBindArrayElement(arrayValue, terminal.Index.Value, out terminalType, out setter, out getter, out var elementError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot bind '{terminal.Name}[{terminal.Index.Value}]': {elementError}");
                return ApplyOutcome.MemberMissing;
            }
        }
        else
        {
            if (!TryGetWritableMember(current, terminal.Name, out terminalType, out setter, out getter))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"no writable field or property '{terminal.Name}' found on {current.GetType().FullName}.");
                return ApplyOutcome.MemberMissing;
            }
        }

        var outcome = ApplyAndVerify(templateTypeName, templateId, op, terminalType, setter, getter, assetResolver, log);
        if (outcome != ApplyOutcome.Applied)
            return outcome;

        // Write-back for value-type intermediates: propagate the mutated
        // descendant up the chain through each parent's setter.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var entry = chain[i];
            if (!entry.ValueIsStruct)
                continue;

            if (!TryGetWritableMember(entry.Parent, entry.Name, out _, out var parentSetter, out _))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"write-back after terminal set failed - parent segment '{entry.Name}' on "
                    + $"{entry.Parent.GetType().FullName} has no writable surface, so the struct mutation may not persist.");
                return ApplyOutcome.Applied;
            }

            try
            {
                parentSetter(current);
            }
            catch (Exception ex)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"write-back after terminal set threw at segment '{entry.Name}': {ex.Message}");
                return ApplyOutcome.Applied;
            }

            current = entry.Parent;
        }

        return outcome;
    }

    private static ApplyOutcome ApplyAndVerify(
        string templateTypeName, string templateId, LoadedPatchOperation op, Type memberType,
        Action<object> setter, Func<object> getter, ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        if (!TryConvertScalar(op.Value, memberType, assetResolver, log, out var converted, out var conversionError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + conversionError);
            return ApplyOutcome.ConversionFailed;
        }

        try
        {
            setter(converted);
        }
        catch (Exception ex)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + $"threw on set: {ex.Message}");
            return ApplyOutcome.ConversionFailed;
        }

        if (getter != null)
        {
            object readback;
            try
            {
                readback = getter();
            }
            catch (Exception ex)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"readback after set threw: {ex.Message}");
                return ApplyOutcome.Applied;
            }

            var matches = ReadbackMatches(converted, readback);
            var verb = op.Op switch
            {
                CompiledTemplateOp.Append => "appended",
                CompiledTemplateOp.InsertAt => $"inserted at [{op.Index}]",
                _ => "set to",
            };
            if (!matches)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"wrote {converted}, read back {readback ?? "null"} - the write did not propagate to the live template.");
            }
            else
            {
                log.Msg(FormatPrefix(templateTypeName, templateId, op)
                    + $"{verb} {FormatValue(converted)}, readback matches.");
            }
        }

        return ApplyOutcome.Applied;
    }

    private static string FormatPrefix(string templateTypeName, string templateId, LoadedPatchOperation op)
        => $"Template patch '{templateTypeName}:{templateId}.{op.FieldPath}' (mod '{op.OwnerLabel}'): ";

    // For Il2Cpp wrappers, identity is the native object pointer, not
    // Il2CppObjectBase.Equals (which compares per-wrapper GC handles and so
    // returns false for two wrappers pooled over the same native object).
    // Everything else — scalar boxed values, strings, enums — goes through
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

    private sealed class ChainEntry
    {
        public object Parent { get; set; }
        public string Name { get; set; }
        public bool ValueIsStruct { get; set; }
    }

    // The inner FieldPath is a dotted member path with no bracket indexers
    // (descent and per-op element index live on Descent / Index fields, not
    // in the path string). Empty segments would mean the path was malformed
    // by something upstream; surface as an error rather than a silent skip.
    private static bool TryParseInnerSegments(string fieldPath, out PathSegment[] segments, out string error)
    {
        var raw = fieldPath.Split('.');
        segments = new PathSegment[raw.Length];
        error = null;

        for (var i = 0; i < raw.Length; i++)
        {
            var segment = raw[i];
            if (string.IsNullOrEmpty(segment))
            {
                segments = null;
                error = $"empty segment in fieldPath '{fieldPath}'.";
                return false;
            }
            if (segment.Contains('['))
            {
                segments = null;
                error = $"unexpected bracket indexer in inner fieldPath '{segment}'; descent uses Descent steps, element index uses op.Index.";
                return false;
            }
            segments[i] = new PathSegment(segment, null);
        }

        return true;
    }

    private static bool TryReadMember(
        object instance, string name, out object value, out Type memberType, out string error)
    {
        value = null;
        memberType = null;
        error = null;

        if (instance == null)
        {
            error = "receiver is null.";
            return false;
        }

        var type = instance.GetType();
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                memberType = property.PropertyType;
                try
                {
                    value = property.GetValue(instance);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"read of property '{name}' on {type.FullName} threw: {ex.Message}";
                    return false;
                }
            }

            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                memberType = field.FieldType;
                try
                {
                    value = field.GetValue(instance);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"read of field '{name}' on {type.FullName} threw: {ex.Message}";
                    return false;
                }
            }
        }

        error = $"no field or property '{name}' found on {type.FullName}.";
        return false;
    }

    private static bool TryGetWritableMember(
        object instance, string name, out Type memberType, out Action<object> setter, out Func<object> getter)
    {
        memberType = null;
        setter = null;
        getter = null;

        if (instance == null)
            return false;

        var type = instance.GetType();
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
            {
                memberType = property.PropertyType;
                var local = property;
                setter = value => local.SetValue(instance, value);
                if (local.CanRead)
                    getter = () => local.GetValue(instance);
                return true;
            }

            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && !field.IsInitOnly)
            {
                memberType = field.FieldType;
                var local = field;
                setter = value => local.SetValue(instance, value);
                getter = () => local.GetValue(instance);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cast an Il2CppInterop wrapper to a concrete subtype named by the
    /// modder via <c>type="<i>X</i>"</c> on a descent block. The wrapper
    /// returned by indexing a <c>List&lt;AbstractBase&gt;</c> reports the
    /// base type and exposes only the base's own members, so reflection
    /// can't see subclass fields like <c>AddSkill.ShowHUDText</c>. The cast
    /// goes through <see cref="Il2CppObjectBase.Cast{T}"/> reflectively
    /// because <c>T</c> is only known at runtime.
    /// </summary>
    private static bool TryCastToSubtype(object element, string subtypeShortName, out object cast, out string error)
    {
        cast = null!;
        error = null!;

        if (element is not Il2CppObjectBase il2cpp)
        {
            error = $"element type {element.GetType().FullName} is not an Il2CppObjectBase.";
            return false;
        }

        var concreteType = ResolveIl2CppSubtype(element.GetType(), subtypeShortName);
        if (concreteType == null)
        {
            error = $"no Il2Cpp wrapper type named '{subtypeShortName}' in the wrapper assembly.";
            return false;
        }

        if (!element.GetType().IsAssignableFrom(concreteType))
        {
            error = $"'{subtypeShortName}' (full name '{concreteType.FullName}') "
                + $"does not derive from '{element.GetType().FullName}'.";
            return false;
        }

        try
        {
            var castMethod = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!
                .MakeGenericMethod(concreteType);
            cast = castMethod.Invoke(il2cpp, null)!;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Cast<{concreteType.FullName}> threw: {(ex.InnerException ?? ex).Message}";
            return false;
        }
    }

    private static readonly Dictionary<string, Type> SubtypeResolutionCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Find the Il2CppInterop wrapper type for <paramref name="shortName"/>,
    /// preferring the same namespace as <paramref name="elementType"/>. The
    /// wrapper assembly is what the path-walked element already lives in,
    /// so almost every game subtype is in the same namespace as its base.
    /// Falls back to a global short-name search if the same-namespace lookup
    /// finds nothing — covers cross-namespace subclassing cases. Returns
    /// null when no candidate matches.
    /// </summary>
    internal static Type ResolveIl2CppSubtype(Type elementType, string shortName)
    {
        var ns = elementType.Namespace ?? string.Empty;
        var cacheKey = ns + "::" + shortName;
        if (SubtypeResolutionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Same-namespace lookup first. Wrap in try/catch so a partial-load
        // assembly doesn't bypass the global fallback. Match by name AND
        // assignability — otherwise an unrelated same-namespace type
        // (e.g. SkillGroup in the same namespace as SkillEventHandlerTemplate)
        // wins the fast path and the caller sees a misleading "type X
        // does not derive from base" error downstream.
        Type same = null;
        try
        {
            same = elementType.Assembly
                .GetTypes()
                .FirstOrDefault(t => t.Name == shortName
                    && t.Namespace == ns
                    && elementType.IsAssignableFrom(t));
        }
        catch { /* fall through */ }

        if (same != null)
        {
            SubtypeResolutionCache[cacheKey] = same;
            return same;
        }

        // Fall back to all loaded assemblies — match short name + assignable
        // to the element type. Slower but covers types declared elsewhere.
        Type anywhere = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var candidate in types)
            {
                if (candidate.Name != shortName) continue;
                if (!elementType.IsAssignableFrom(candidate)) continue;
                anywhere = candidate;
                break;
            }
            if (anywhere != null) break;
        }

        SubtypeResolutionCache[cacheKey] = anywhere;
        return anywhere;
    }

    // Read-only element access against Il2Cpp-side collections. Supports the
    // main collection shapes generated by Il2CppInterop: ReferenceArray<T>,
    // StructArray, Il2CppSystem.Collections.Generic.List<T>, and anything
    // that exposes a single-parameter Item indexer by reflection. Bounds
    // are enforced via the collection's Length/Count before indexing.
    private static bool TryIndexInto(object collection, int index, out object element, out Type elementType, out string error)
    {
        element = null;
        elementType = null;
        error = null;

        if (collection == null)
        {
            error = "collection is null.";
            return false;
        }

        if (index < 0)
        {
            error = $"negative index {index}.";
            return false;
        }

        var collectionType = collection.GetType();

        // Fast path: Il2CppInterop's own array wrappers expose an int indexer.
        var indexer = FindIndexer(collectionType);
        if (indexer != null)
        {
            elementType = indexer.PropertyType;

            if (!WithinCollectionBounds(collection, collectionType, index, out var boundsError))
            {
                error = boundsError;
                return false;
            }

            try
            {
                element = indexer.GetValue(collection, new object[] { index });
                return true;
            }
            catch (Exception ex)
            {
                error = $"indexer threw: {ex.Message}";
                return false;
            }
        }

        error = $"collection type {collectionType.FullName} exposes no int indexer.";
        return false;
    }

    // Terminal indexer writes target an array/list element — scalar types
    // (byte, int, float, bool, enum, string) or DataTemplate references
    // resolved via CompiledTemplateValueKind.TemplateReference. The
    // setter/getter closures carry the collection + index so ApplyAndVerify
    // can do its read-write-readback loop without caring whether it's writing
    // a member or an element.
    private static bool TryBindArrayElement(
        object collection, int index, out Type elementType, out Action<object> setter, out Func<object> getter, out string error)
    {
        elementType = null;
        setter = null;
        getter = null;
        error = null;

        if (collection is Array managedArray)
        {
            if (index < 0 || index >= managedArray.Length)
            {
                error = $"index {index} out of bounds (length={managedArray.Length}).";
                return false;
            }

            elementType = managedArray.GetType().GetElementType();
            var arrayLocal = managedArray;
            var indexLocal = index;
            setter = value => arrayLocal.SetValue(value, indexLocal);
            getter = () => arrayLocal.GetValue(indexLocal);
            return true;
        }

        var collectionType = collection.GetType();
        var indexer = FindWritableIndexer(collectionType);
        if (indexer == null)
        {
            error = $"collection type {collectionType.FullName} exposes no writable int indexer.";
            return false;
        }

        if (!WithinCollectionBounds(collection, collectionType, index, out var boundsError))
        {
            error = boundsError;
            return false;
        }

        elementType = indexer.PropertyType;
        var collectionLocal = collection;
        var indexerLocal = indexer;
        var indexArg = new object[] { index };
        setter = value => indexerLocal.SetValue(collectionLocal, value, indexArg);
        getter = () => indexerLocal.GetValue(collectionLocal, indexArg);
        return true;
    }

    // Collection-mutation binder for Append and InsertAt. Dispatches on shape:
    //   - List-like (has instance Add(T)): mutate live collection in place via
    //     Add / Insert, unless the field is null in which case we construct
    //     via the parameterless ctor and writeback.
    //   - Il2CppReferenceArray<T> / Il2CppStructArray<T>: rebuild a fresh
    //     native array of length+1 and writeback; null field yields a
    //     1-element array. Writing the whole new array through the generated
    //     property setter uses Il2CppInterop's GC write barrier.
    //
    // insertIndex=null means Append; non-null means InsertAt at that position.
    private static bool TryBindCollectionMutation(
        object parent, string fieldName, object collection, Type collectionType,
        int? insertIndex,
        out Type elementType, out Action<object> setter, out Func<object> getter, out string error)
    {
        setter = null;
        getter = null;
        error = null;

        if (collection != null)
            collectionType = collection.GetType();

        if (!TryGetCollectionShape(collectionType, out var shape, out elementType))
        {
            error = $"collection type {collectionType.FullName} is not a supported shape "
                + "(List<T>, Il2CppReferenceArray<T>, or Il2CppStructArray<T>).";
            return false;
        }

        if (!TryGetWritableMember(parent, fieldName, out _, out var fieldSetter, out _))
        {
            error = $"parent {parent.GetType().FullName} has no writable '{fieldName}' for collection write-back.";
            return false;
        }

        switch (shape)
        {
            case CollectionShape.List:
                return BindListMutation(
                    parent, fieldName, collection, collectionType, elementType, fieldSetter, insertIndex,
                    out setter, out getter, out error);

            case CollectionShape.ReferenceArray:
            case CollectionShape.StructArray:
                return BindArrayMutation(
                    parent, fieldName, collection, collectionType, elementType, fieldSetter, insertIndex,
                    out setter, out getter, out error);

            default:
                error = "internal: unhandled collection shape.";
                return false;
        }
    }

    private enum CollectionShape { List, ReferenceArray, StructArray }

    private static bool TryGetCollectionShape(Type collectionType, out CollectionShape shape, out Type elementType)
    {
        var addMethod = FindInstanceAddMethod(collectionType);
        if (addMethod != null)
        {
            shape = CollectionShape.List;
            elementType = addMethod.GetParameters()[0].ParameterType;
            return true;
        }

        if (IsIl2CppArrayOf(collectionType, "Il2CppReferenceArray"))
        {
            shape = CollectionShape.ReferenceArray;
            elementType = collectionType.GetGenericArguments()[0];
            return true;
        }

        if (IsIl2CppArrayOf(collectionType, "Il2CppStructArray"))
        {
            shape = CollectionShape.StructArray;
            elementType = collectionType.GetGenericArguments()[0];
            return true;
        }

        shape = default;
        elementType = null;
        return false;
    }

    private static MethodInfo FindInstanceAddMethod(Type collectionType)
    {
        for (var current = collectionType; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name == "Add" && method.GetParameters().Length == 1)
                    return method;
            }
        }

        return null;
    }

    private static bool IsIl2CppArrayOf(Type type, string simpleName)
    {
        if (!type.IsGenericType)
            return false;
        var fullName = type.GetGenericTypeDefinition().FullName;
        return string.Equals(
            fullName,
            "Il2CppInterop.Runtime.InteropTypes.Arrays." + simpleName + "`1",
            StringComparison.Ordinal);
    }

    private static bool BindListMutation(
        object parent, string fieldName, object _liveCollection, Type collectionType, Type elementType,
        Action<object> fieldSetter, int? insertIndex,
        out Action<object> setter, out Func<object> getter, out string error)
    {
        setter = null;
        getter = null;
        error = null;

        var addMethod = FindInstanceAddMethod(collectionType);
        var insertMethod = insertIndex.HasValue
            ? collectionType.GetMethod("Insert",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int), elementType },
                modifiers: null)
            : null;

        if (insertIndex.HasValue && insertMethod == null)
        {
            error = $"{collectionType.FullName} exposes no Insert(int, {elementType.Name}) method.";
            return false;
        }

        var listCtor = collectionType.GetConstructor(Type.EmptyTypes);
        var countProperty = collectionType.GetProperty(
            "Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var indexer = FindIndexer(collectionType);
        var addArgs = new object[1];
        var insertArgs = insertIndex.HasValue ? new object[2] : null;

        setter = value =>
        {
            var live = TryReadField(parent, fieldName);
            if (live == null)
            {
                if (listCtor == null)
                    throw new InvalidOperationException(
                        $"cannot construct {collectionType.FullName}: no parameterless ctor.");
                if (insertIndex.HasValue && insertIndex.Value > 0)
                    throw new InvalidOperationException(
                        $"InsertAt index {insertIndex.Value} out of range for empty collection.");

                live = listCtor.Invoke(null);
                fieldSetter(live);
            }

            if (insertIndex.HasValue)
            {
                insertArgs![0] = insertIndex.Value;
                insertArgs[1] = value;
                insertMethod!.Invoke(live, insertArgs);
            }
            else
            {
                addArgs[0] = value;
                addMethod!.Invoke(live, addArgs);
            }
        };

        if (countProperty != null && indexer != null)
        {
            var getterArgs = new object[1];
            getter = () =>
            {
                var live = TryReadField(parent, fieldName);
                if (live == null || countProperty.GetValue(live) is not int count || count <= 0)
                    return null;
                var readIndex = insertIndex ?? count - 1;
                if (readIndex < 0 || readIndex >= count)
                    return null;
                getterArgs[0] = readIndex;
                return indexer.GetValue(live, getterArgs);
            };
        }

        return true;
    }

    private static bool BindArrayMutation(
        object parent, string fieldName, object _liveCollection, Type collectionType, Type elementType,
        Action<object> fieldSetter, int? insertIndex,
        out Action<object> setter, out Func<object> getter, out string error)
    {
        setter = null;
        getter = null;
        error = null;

        var lengthProperty = collectionType.GetProperty(
            "Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var indexer = FindIndexer(collectionType);
        var managedArrayType = elementType.MakeArrayType();
        var arrayCtor = collectionType.GetConstructor(new[] { managedArrayType });
        if (lengthProperty == null || indexer == null || arrayCtor == null)
        {
            error = $"{collectionType.FullName} missing Length/indexer/managed-array ctor.";
            return false;
        }

        var readArgs = new object[1];

        setter = value =>
        {
            var live = TryReadField(parent, fieldName);
            int oldLength;
            object[] old;
            if (live == null)
            {
                if (insertIndex.HasValue && insertIndex.Value > 0)
                    throw new InvalidOperationException(
                        $"InsertAt index {insertIndex.Value} out of range for empty array.");
                oldLength = 0;
                old = Array.Empty<object>();
            }
            else
            {
                oldLength = (int)lengthProperty.GetValue(live)!;
                old = new object[oldLength];
                for (var i = 0; i < oldLength; i++)
                {
                    readArgs[0] = i;
                    old[i] = indexer.GetValue(live, readArgs);
                }
            }

            var newLength = oldLength + 1;
            var targetIndex = insertIndex ?? oldLength;
            if (targetIndex < 0 || targetIndex > oldLength)
                throw new InvalidOperationException(
                    $"InsertAt index {targetIndex} out of range 0..{oldLength} (inclusive).");

            var managed = Array.CreateInstance(elementType, newLength);
            for (var i = 0; i < targetIndex; i++)
                managed.SetValue(old[i], i);
            managed.SetValue(value, targetIndex);
            for (var i = targetIndex; i < oldLength; i++)
                managed.SetValue(old[i], i + 1);

            var rebuilt = arrayCtor.Invoke(new[] { managed });
            fieldSetter(rebuilt);
        };

        getter = () =>
        {
            var live = TryReadField(parent, fieldName);
            if (live == null)
                return null;
            var length = (int)lengthProperty.GetValue(live)!;
            if (length <= 0)
                return null;
            var readIndex = insertIndex ?? length - 1;
            if (readIndex < 0 || readIndex >= length)
                return null;
            readArgs[0] = readIndex;
            return indexer.GetValue(live, readArgs);
        };

        return true;
    }

    // Remove dispatch is separate from Set/Append/InsertAt because it takes
    // no value — no conversion, no readback-compare. The terminal is the
    // indexed element to delete.
    private static ApplyOutcome TryApplyRemove(
        object current, PathSegment terminal, string templateTypeName, string templateId,
        LoadedPatchOperation op, MelonLogger.Instance log)
    {
        if (!TryReadMember(current, terminal.Name, out var collection, out var collectionType, out var readError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"cannot read terminal collection '{terminal.Name}': {readError}");
            return ApplyOutcome.MemberMissing;
        }

        if (collection == null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"terminal collection '{terminal.Name}' ({collectionType.FullName}) is null; nothing to remove.");
            return ApplyOutcome.MemberMissing;
        }

        if (!TryGetCollectionShape(collectionType, out var shape, out var elementType))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"collection type {collectionType.FullName} is not supported for Remove.");
            return ApplyOutcome.MemberMissing;
        }

        if (!TryGetWritableMember(current, terminal.Name, out _, out var fieldSetter, out _))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"parent {current.GetType().FullName} has no writable '{terminal.Name}' for remove.");
            return ApplyOutcome.MemberMissing;
        }

        var removeIndex = op.Index
            ?? throw new InvalidOperationException(
                $"Remove operation on '{terminal.Name}' has no Index; the compiled patch is malformed.");

        try
        {
            switch (shape)
            {
                case CollectionShape.List:
                    RemoveFromList(collection, collectionType, removeIndex);
                    break;

                case CollectionShape.ReferenceArray:
                case CollectionShape.StructArray:
                    var rebuilt = RemoveFromArray(collection, collectionType, elementType, removeIndex);
                    fieldSetter(rebuilt);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"remove threw: {ex.Message}");
            return ApplyOutcome.ConversionFailed;
        }

        log.Msg(FormatPrefix(templateTypeName, templateId, op)
            + $"removed element at {removeIndex} from {collectionType.Name}.");
        return ApplyOutcome.Applied;
    }

    // Clear empties the terminal collection in place. List<T> uses the
    // built-in Clear(); native IL2CPP arrays are immutable, so we rebuild a
    // zero-length array of the same element type and write it back through
    // the parent's setter. A null collection is treated as missing — the
    // loader's missing-field path warns the modder rather than silently
    // materialising an empty list.
    private static ApplyOutcome TryApplyClear(
        object current, PathSegment terminal, string templateTypeName, string templateId,
        LoadedPatchOperation op, MelonLogger.Instance log)
    {
        if (!TryReadMember(current, terminal.Name, out var collection, out var collectionType, out var readError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"cannot read terminal collection '{terminal.Name}': {readError}");
            return ApplyOutcome.MemberMissing;
        }

        if (collection == null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"terminal collection '{terminal.Name}' ({collectionType.FullName}) is null; nothing to clear.");
            return ApplyOutcome.MemberMissing;
        }

        if (!TryGetCollectionShape(collectionType, out var shape, out var elementType))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"collection type {collectionType.FullName} is not supported for Clear.");
            return ApplyOutcome.MemberMissing;
        }

        if (!TryGetWritableMember(current, terminal.Name, out _, out var fieldSetter, out _))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"parent {current.GetType().FullName} has no writable '{terminal.Name}' for clear.");
            return ApplyOutcome.MemberMissing;
        }

        try
        {
            switch (shape)
            {
                case CollectionShape.List:
                    ClearList(collection, collectionType);
                    break;

                case CollectionShape.ReferenceArray:
                case CollectionShape.StructArray:
                    var emptied = BuildEmptyArray(collectionType, elementType);
                    fieldSetter(emptied);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"clear threw: {ex.Message}");
            return ApplyOutcome.ConversionFailed;
        }

        log.Msg(FormatPrefix(templateTypeName, templateId, op)
            + $"cleared {collectionType.Name}.");
        return ApplyOutcome.Applied;
    }

    private static void ClearList(object list, Type listType)
    {
        var clear = listType.GetMethod(
            "Clear",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException(
                $"{listType.FullName} has no Clear() method.");
        clear.Invoke(list, null);
    }

    private static object BuildEmptyArray(Type arrayType, Type elementType)
    {
        var ctor = arrayType.GetConstructor(new[] { elementType.MakeArrayType() })
            ?? throw new InvalidOperationException(
                $"{arrayType.FullName} missing managed-array ctor for clear.");
        var managed = Array.CreateInstance(elementType, 0);
        return ctor.Invoke(new[] { managed });
    }

    private static void RemoveFromList(object list, Type listType, int index)
    {
        var removeAt = listType.GetMethod(
            "RemoveAt",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, types: new[] { typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException(
                $"{listType.FullName} has no RemoveAt(int) method.");
        removeAt.Invoke(list, new object[] { index });
    }

    private static object RemoveFromArray(object array, Type arrayType, Type elementType, int index)
    {
        var lengthProperty = arrayType.GetProperty(
            "Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var indexer = FindIndexer(arrayType);
        var ctor = arrayType.GetConstructor(new[] { elementType.MakeArrayType() });
        if (lengthProperty == null || indexer == null || ctor == null)
            throw new InvalidOperationException(
                $"{arrayType.FullName} missing Length/indexer/managed-array ctor for remove.");

        var oldLength = (int)lengthProperty.GetValue(array)!;
        if (index < 0 || index >= oldLength)
            throw new IndexOutOfRangeException(
                $"Remove index {index} out of range 0..{oldLength - 1}.");

        var managed = Array.CreateInstance(elementType, oldLength - 1);
        var readArgs = new object[1];
        for (var i = 0; i < index; i++)
        {
            readArgs[0] = i;
            managed.SetValue(indexer.GetValue(array, readArgs), i);
        }
        for (var i = index + 1; i < oldLength; i++)
        {
            readArgs[0] = i;
            managed.SetValue(indexer.GetValue(array, readArgs), i - 1);
        }

        return ctor.Invoke(new[] { managed });
    }

    private static object TryReadField(object parent, string fieldName)
        => TryReadMember(parent, fieldName, out var value, out _, out _) ? value : null;

    // Soft bounds check via reflection on Length/Count. Returns true when the
    // index is known-in-range OR when we couldn't read a length (in which case
    // the indexer itself will throw). False only on a confirmed overflow.
    private static bool WithinCollectionBounds(object collection, Type collectionType, int index, out string error)
    {
        error = null;

        var lengthMember = collectionType.GetProperty(
            "Length",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? collectionType.GetProperty(
                "Count",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (lengthMember == null || lengthMember.GetIndexParameters().Length != 0)
            return true;

        try
        {
            if (lengthMember.GetValue(collection) is int length && (index < 0 || index >= length))
            {
                error = $"index {index} out of bounds (length={length}).";
                return false;
            }
        }
        catch
        {
            // Proceed without a pre-check; the indexer will throw.
        }

        return true;
    }

    private static PropertyInfo FindWritableIndexer(Type collectionType)
    {
        for (var current = collectionType; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int) && property.CanWrite && property.CanRead)
                    return property;
            }
        }

        return null;
    }

    private static PropertyInfo FindIndexer(Type collectionType)
    {
        for (var current = collectionType; current != null; current = current.BaseType)
        {
            foreach (var property in current.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int) && property.CanRead)
                    return property;
            }
        }

        return null;
    }

    private static bool TryConvertScalar(
        CompiledTemplateValue value, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        if (value == null)
        {
            converted = null;
            error = "value payload is null.";
            return false;
        }

        // Numeric normalisation shared with compile/editor validation:
        // allow canonical narrowing/widening between Byte/Int32/Single before
        // strict target-type checks below. Integer-family destinations (short,
        // ushort, uint, long, ulong, sbyte) all normalise to the Int32 patch
        // kind here; the kind=Int32 branch below range-checks and widens to
        // the actual member type. Double normalises through Single.
        if (targetType == typeof(byte))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Byte, out error))
            {
                converted = null;
                return false;
            }
        }
        else if (TemplateValueCoercion.IsIntegerFamilyTarget(targetType))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Int32, out error))
            {
                converted = null;
                return false;
            }
        }
        else if (targetType == typeof(float) || targetType == typeof(double))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(value, CompiledTemplateValueKind.Single, out error))
            {
                converted = null;
                return false;
            }
        }

        converted = null;
        error = null;

        switch (value.Kind)
        {
            case CompiledTemplateValueKind.Boolean:
                if (targetType != typeof(bool))
                {
                    error = $"value kind Boolean but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Boolean.Value;
                return true;

            case CompiledTemplateValueKind.Byte:
                if (targetType != typeof(byte))
                {
                    error = $"value kind Byte but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Byte.Value;
                return true;

            case CompiledTemplateValueKind.Int32:
                return TemplateValueCoercion.TryWidenInt32(value.Int32.Value, targetType, out converted, out error);

            case CompiledTemplateValueKind.Single:
                if (targetType == typeof(float))
                {
                    converted = value.Single.Value;
                    return true;
                }
                if (targetType == typeof(double))
                {
                    converted = (double)value.Single.Value;
                    return true;
                }
                error = $"value kind Single but member type is {targetType.FullName}.";
                return false;

            case CompiledTemplateValueKind.String:
                if (targetType != typeof(string))
                {
                    error = $"value kind String but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.String;
                return true;

            case CompiledTemplateValueKind.Enum:
                if (!targetType.IsEnum)
                {
                    error = $"value kind Enum but member type {targetType.FullName} is not an enum.";
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(value.EnumType) &&
                    !string.Equals(value.EnumType, targetType.Name, StringComparison.Ordinal))
                {
                    error = $"value EnumType '{value.EnumType}' does not match member enum type {targetType.Name}.";
                    return false;
                }
                try
                {
                    var parsed = Enum.Parse(targetType, value.EnumValue, ignoreCase: false);
                    // Strict membership, mirroring the compile-time validator.
                    // Enum.Parse accepts any numeric form in the underlying
                    // type's range (e.g. "99" → an undefined ItemSlot value);
                    // reject those so a hand-edited compiled manifest can't
                    // sneak an undefined value past the loader.
                    if (!Enum.IsDefined(targetType, parsed))
                    {
                        error = $"enum value '{value.EnumValue}' is not a defined member of {targetType.Name}.";
                        return false;
                    }
                    converted = parsed;
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"failed to parse enum value '{value.EnumValue}' for {targetType.Name}: {ex.Message}";
                    return false;
                }

            case CompiledTemplateValueKind.TemplateReference:
                return TryResolveTemplateReference(value.Reference, targetType, out converted, out error);

            case CompiledTemplateValueKind.AssetReference:
                return TryResolveAssetReference(value.Asset, targetType, assetResolver, out converted, out error);

            case CompiledTemplateValueKind.Composite:
                return TryConstructComposite(value.Composite, targetType, assetResolver, log, out converted, out error);

            case CompiledTemplateValueKind.HandlerConstruction:
                return TryConstructHandler(value.HandlerConstruction, targetType, assetResolver, log, out converted, out error);

            default:
                error = $"unknown value kind {value.Kind}.";
                return false;
        }
    }

    /// <summary>
    /// Constructs a fresh ScriptableObject of the named subtype (for the
    /// modder's <c>handler="X"</c> authoring shape on append/insert/set
    /// against a polymorphic-reference array). Routes through the existing
    /// composite construction path then sets
    /// <c>HideFlags.DontUnloadUnusedAsset</c> so scene-change GC doesn't
    /// sweep the freshly-allocated handler. When the payload's type name is
    /// empty, the array's element type is used (monomorphic case).
    /// </summary>
    private static bool TryConstructHandler(
        CompiledTemplateComposite handler, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        converted = null;

        if (handler == null)
        {
            error = "value kind HandlerConstruction but payload is null.";
            return false;
        }

        // If the modder omitted handler="X" because the destination is
        // monomorphic (validator already confirmed), the empty TypeName
        // resolves to the array's element type. Synthesise the payload so
        // TryConstructComposite has something to look up.
        var effectivePayload = handler;
        if (string.IsNullOrWhiteSpace(handler.TypeName))
        {
            effectivePayload = new CompiledTemplateComposite
            {
                TypeName = targetType.Name,
                Operations = handler.Operations ?? new List<CompiledTemplateSetOperation>(),
            };
        }

        if (!TryConstructComposite(effectivePayload, targetType, assetResolver, log, out converted, out error))
            return false;

        if (converted is UnityEngine.Object asUnity)
        {
            // hideFlags keeps scene-change GC from sweeping the freshly-
            // allocated handler. name matches the vanilla convention (each
            // game-shipped handler is named after its concrete subtype:
            // "AddSkill", "ChangeProperty", etc.) so inspector dumps show
            // "SkillEventHandlerTemplate:AddSkill" instead of an unnamed
            // entry. ScriptableObject.CreateInstance leaves name empty
            // by default.
            try { asUnity.hideFlags = UnityEngine.HideFlags.DontUnloadUnusedAsset; }
            catch { }
            try { asUnity.name = effectivePayload.TypeName; }
            catch { }
        }
        return true;
    }

    // Constructs a fresh instance of the composite's typeName and recursively
    // writes each authored field via the same TryConvertScalar conversion.
    // Dispatches construction by base class:
    //  - ScriptableObject subtypes: ScriptableObject.CreateInstance (runs
    //    Unity's OnEnable etc.).
    //  - Il2CppObjectBase subtypes (e.g. LocalizedLine, plain Il2CppSystem.*
    //    support types): allocate via il2cpp_object_new on the IL2CPP class
    //    pointer, then wrap with the generated (IntPtr) ctor. Skips running
    //    any IL2CPP-side ctor — fields are written individually below.
    //  - Pure managed types: Activator.CreateInstance (parameterless ctor).
    private static bool TryConstructComposite(
        CompiledTemplateComposite composite, Type targetType, ModAssetResolver assetResolver, MelonLogger.Instance log,
        out object converted, out string error)
    {
        converted = null;

        if (composite == null || string.IsNullOrWhiteSpace(composite.TypeName))
        {
            error = "value kind Composite but payload is missing typeName.";
            return false;
        }

        var resolvedType = TemplateRuntimeAccess.ResolveTemplateType(composite.TypeName, out var resolveError);
        if (resolvedType == null)
        {
            error = $"Composite: cannot resolve typeName '{composite.TypeName}' — {resolveError}";
            return false;
        }

        if (!targetType.IsAssignableFrom(resolvedType))
        {
            error = $"Composite typeName '{composite.TypeName}' ({resolvedType.FullName}) is not assignable to member type {targetType.FullName}.";
            return false;
        }

        object instance;
        try
        {
            if (typeof(UnityEngine.ScriptableObject).IsAssignableFrom(resolvedType))
            {
                instance = UnityEngine.ScriptableObject.CreateInstance(Il2CppInterop.Runtime.Il2CppType.From(resolvedType));
            }
            else if (typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(resolvedType))
            {
                if (!TryAllocateIl2CppInstance(resolvedType, out instance, out var il2cppError))
                {
                    error = $"Composite: construction of {resolvedType.FullName} failed: {il2cppError}";
                    return false;
                }
            }
            else
            {
                instance = Activator.CreateInstance(resolvedType)!;
            }
        }
        catch (Exception ex)
        {
            error = $"Composite: construction of {resolvedType.FullName} threw: {ex.Message}";
            return false;
        }

        // Il2CppInterop polymorphic factories (e.g. ScriptableObject.CreateInstance(Il2CppType))
        // return a base-typed wrapper. Reflection on that wrapper sees only
        // base-class members, so subtype fields like AddSkill.Event would
        // be missed. Cast the wrapper to resolvedType so GetType() reports
        // the concrete type and TryGetWritableMember can find subclass
        // fields. Same Cast<T>-via-MakeGenericMethod pattern used by the
        // path-walk applier and the clone deep-copy.
        if (instance is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase il2cppInstance
            && instance.GetType() != resolvedType)
        {
            try
            {
                var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                    .GetMethod(nameof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase.Cast))!
                    .MakeGenericMethod(resolvedType);
                var cast = castMethod.Invoke(il2cppInstance, null);
                if (cast != null) instance = cast;
            }
            catch (Exception ex)
            {
                error = $"Composite: Cast<{resolvedType.FullName}> after construction threw: {(ex.InnerException ?? ex).Message}";
                return false;
            }
        }

        // Apply each authored operation against the freshly-constructed
        // instance using the same path-walk-and-apply machinery the outer
        // applier uses on live templates. Set ops on top-level fields are
        // the common case; collection ops (Append/Insert/Remove/Clear) on
        // the constructed instance's collection members work too — e.g.
        // appending a fresh PropertyChange to a ChangeProperty handler's
        // Properties list during construction.
        if (composite.Operations != null)
        {
            foreach (var innerOp in composite.Operations)
            {
                var loadedOp = new LoadedPatchOperation(
                    innerOp.Op,
                    innerOp.FieldPath,
                    innerOp.Index,
                    (IReadOnlyList<TemplateDescentStep>?)innerOp.Descent ?? Array.Empty<TemplateDescentStep>(),
                    innerOp.Value,
                    $"composite:{composite.TypeName}");

                var outcome = TryApplyOperation(
                    instance,
                    composite.TypeName,
                    "<construction>",
                    loadedOp,
                    assetResolver,
                    log);
                if (outcome != ApplyOutcome.Applied)
                {
                    error = $"Composite {resolvedType.Name}: inner op {innerOp.Op} '{innerOp.FieldPath}' failed (outcome={outcome}).";
                    return false;
                }
            }
        }

        converted = instance;
        error = null;
        return true;
    }

    // Cached per-type lookups for IL2CPP allocation. Filled lazily on first
    // composite construction of each wrapper type. Plain Dictionary is fine:
    // template apply runs from JiangyuMod's scene-load coroutine on the Unity
    // main thread, so reads and writes are serialised. A null entry in
    // Il2CppIntPtrCtorCache is the cached "no (IntPtr) ctor" sentinel; reuse
    // it on subsequent lookups instead of re-resolving.
    private static readonly Dictionary<Type, IntPtr> Il2CppClassPtrCache = new();
    private static readonly Dictionary<Type, ConstructorInfo> Il2CppIntPtrCtorCache = new();
    private static readonly Type[] IntPtrCtorSignature = new[] { typeof(IntPtr) };

    private static IntPtr ResolveIl2CppClassPtr(Type t)
    {
        try
        {
            var storeType = typeof(Il2CppClassPointerStore<>).MakeGenericType(t);
            var field = storeType.GetField(
                "NativeClassPtr",
                BindingFlags.Public | BindingFlags.Static);
            if (field == null)
                return IntPtr.Zero;
            var raw = field.GetValue(null);
            return raw == null ? IntPtr.Zero : (IntPtr)raw;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static ConstructorInfo ResolveIntPtrCtor(Type t)
    {
        return t.GetConstructor(IntPtrCtorSignature);
    }

    // Allocates an IL2CPP object for an Il2CppInterop wrapper type
    // (descendant of Il2CppObjectBase) and runs the type's IL2CPP-side
    // parameterless ctor so default-state invariants are established before
    // composite field writes overwrite individual members.
    //
    // Steps:
    //  1. il2cpp_object_new on the resolved native class pointer — allocates
    //     and zero-fills the IL2CPP object, sets up the vtable.
    //  2. il2cpp_runtime_object_init — invokes the type's parameterless
    //     instance ctor on the IL2CPP runtime so any defaults (backing
    //     lists, default flags) are populated. Best-effort: if no
    //     parameterless ctor exists or the ctor throws, we proceed against
    //     the zero-initialised object — fields the modder authored will be
    //     overwritten next, and unauthored fields keep their zero defaults.
    //  3. Wrap with the generated managed (IntPtr) ctor so the result is a
    //     usable Il2CppObjectBase wrapper.
    private static bool TryAllocateIl2CppInstance(Type wrapperType, out object instance, out string error)
    {
        instance = null;
        error = null;

        if (!Il2CppClassPtrCache.TryGetValue(wrapperType, out var classPtr))
        {
            classPtr = ResolveIl2CppClassPtr(wrapperType);
            Il2CppClassPtrCache[wrapperType] = classPtr;
        }

        if (classPtr == IntPtr.Zero)
        {
            error = $"Il2CppClassPointerStore<{wrapperType.FullName}>.NativeClassPtr not found.";
            return false;
        }

        if (!Il2CppIntPtrCtorCache.TryGetValue(wrapperType, out var ctor))
        {
            ctor = ResolveIntPtrCtor(wrapperType);
            Il2CppIntPtrCtorCache[wrapperType] = ctor;
        }

        if (ctor == null)
        {
            error = $"{wrapperType.FullName} has no (IntPtr) constructor; cannot wrap a fresh IL2CPP allocation.";
            return false;
        }

        var instancePtr = IL2CPP.il2cpp_object_new(classPtr);
        if (instancePtr == IntPtr.Zero)
        {
            error = $"il2cpp_object_new returned null for {wrapperType.FullName}.";
            return false;
        }

        // Best-effort IL2CPP-side ctor. Some wrapper types (pure data shells
        // with no parameterless .ctor on the native side) will throw here;
        // that's acceptable because authored field writes follow. Swallowing
        // matches the previous "skip ctor" behaviour as a fallback while
        // preserving correct init for the common case.
        try
        {
            IL2CPP.il2cpp_runtime_object_init(instancePtr);
        }
        catch
        {
            // Intentionally ignored; see comment above.
        }

        try
        {
            instance = ctor.Invoke(new object[] { instancePtr });
            return true;
        }
        catch (Exception ex)
        {
            error = $"(IntPtr) ctor invocation threw: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    // Resolves a modder-authored asset reference (a single name string) to a
    // live Unity Object. The lookup category is the destination field's
    // declared Unity type; the resolver walks the mod-bundle catalog first
    // and falls back to the live game-asset registry. See
    // ModAssetResolver for the JIANGYU-CONTRACT detail on resolution order.
    private static bool TryResolveAssetReference(
        Jiangyu.Shared.Templates.CompiledAssetReference reference, Type targetType,
        ModAssetResolver assetResolver, out object converted, out string error)
    {
        converted = null;

        if (reference == null || string.IsNullOrEmpty(reference.Name))
        {
            error = "value kind AssetReference but reference name is missing.";
            return false;
        }

        if (assetResolver == null)
        {
            error = $"AssetReference '{reference.Name}': no asset resolver wired into the applier.";
            return false;
        }

        if (!Jiangyu.Shared.Replacements.AssetCategory.IsSupported(targetType.Name))
        {
            error = $"AssetReference '{reference.Name}' targets {targetType.FullName}, "
                + "which is not a supported asset category. "
                + "Sprite, Texture2D, AudioClip, and Material are supported today; "
                + "Mesh and GameObject wait on the prefab-construction layer "
                + "(see PREFAB_CLONING_TODO.md).";
            return false;
        }

        var resolved = assetResolver.TryFind(targetType, reference.Name);
        if (resolved == null)
        {
            error = $"AssetReference '{reference.Name}': no asset of type {targetType.Name} "
                + "found in the mod bundle catalog or the live game-asset registry.";
            return false;
        }

        converted = resolved;
        error = null;
        return true;
    }

    // Resolves a modder-authored (templateType, templateId) pair to the live
    // Il2Cpp wrapper. TryGetTemplateById dispatches by base class:
    // DataTemplate subtypes resolve via DataTemplateLoader.TryGet<T>(m_ID);
    // other ScriptableObject subtypes (e.g. PerkTreeTemplate) resolve via
    // Resources.FindObjectsOfTypeAll by Object.name.
    private static bool TryResolveTemplateReference(
        CompiledTemplateReference reference, Type targetType, out object converted, out string error)
    {
        converted = null;

        if (reference == null)
        {
            error = "value kind TemplateReference but reference is null.";
            return false;
        }

        // TemplateType is optional in the canonical schema — when omitted the
        // destination field's declared type IS the lookup type. Fall back to
        // targetType.Name so the rest of the resolution path stays unchanged.
        var lookupTypeName = string.IsNullOrWhiteSpace(reference.TemplateType)
            ? targetType.Name
            : reference.TemplateType;

        if (!TemplateRuntimeAccess.TryGetTemplateById(
                lookupTypeName, reference.TemplateId,
                out var resolvedTemplate, out var resolvedType, out var resolveError))
        {
            error = resolvedType == null
                ? $"TemplateReference '{lookupTypeName}:{reference.TemplateId}' — {resolveError}"
                : $"TemplateReference: no live {lookupTypeName} with id '{reference.TemplateId}' found.";
            return false;
        }

        if (!targetType.IsAssignableFrom(resolvedType))
        {
            error = $"TemplateReference targets {resolvedType.FullName} but member expects {targetType.FullName}.";
            return false;
        }

        converted = resolvedTemplate;
        error = null;
        return true;
    }

    private enum ApplyOutcome
    {
        Applied,
        MemberMissing,
        ConversionFailed,
    }
}
