using System.Reflection;
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
    private readonly HashSet<string> _appliedTypes = new(StringComparer.Ordinal);

    public TemplatePatchApplier(TemplatePatchCatalog catalog)
    {
        _catalog = catalog;
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
                switch (TryApplyOperation(template, templateTypeName, templateEntry.Key, op, log))
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
        object template, string templateTypeName, string templateId, LoadedPatchOperation op, MelonLogger.Instance log)
    {
        if (!TryParsePath(op.FieldPath, out var segments, out var parseError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + parseError);
            return ApplyOutcome.MemberMissing;
        }

        var chain = new List<ChainEntry>(segments.Length);
        object current = template;

        // Walk intermediate segments, tracking each step so we can write
        // value-types back after the terminal set.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
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

            var entry = new ChainEntry
            {
                Parent = current,
                Name = segment.Name,
                ValueIsStruct = memberType.IsValueType,
            };

            if (segment.Index.HasValue)
            {
                if (!TryIndexInto(value, segment.Index.Value, out var element, out var elementType, out var indexError))
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"cannot index segment '{segment.Name}[{segment.Index.Value}]': {indexError}");
                    return ApplyOutcome.MemberMissing;
                }

                if (element == null)
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"element {segment.Index.Value} of '{segment.Name}' is null.");
                    return ApplyOutcome.MemberMissing;
                }

                // We don't support writing mutated struct-elements back into
                // collections on this slice. Reject early so modders know.
                if (elementType.IsValueType)
                {
                    log.Warning(FormatPrefix(templateTypeName, templateId, op)
                        + $"element {segment.Index.Value} of '{segment.Name}' is a value-type "
                        + $"({elementType.FullName}); in-collection struct mutation is not supported on this slice.");
                    return ApplyOutcome.MemberMissing;
                }

                // Indexed elements can't be written back into the collection on
                // this slice, but mutations on a reference-type element
                // propagate naturally, so we continue without a chain entry.
                current = element;
                chain.Add(entry);
                continue;
            }

            current = value;
            chain.Add(entry);
        }

        var terminal = segments[^1];
        Action<object> setter;
        Func<object> getter;
        Type terminalType;

        if (op.Op == CompiledTemplateOp.Remove)
            return TryApplyRemove(current, terminal, templateTypeName, templateId, op, log);

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

        var outcome = ApplyAndVerify(templateTypeName, templateId, op, terminalType, setter, getter, log);
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
        Action<object> setter, Func<object> getter, MelonLogger.Instance log)
    {
        if (!TryConvertScalar(op.Value, memberType, out var converted, out var conversionError))
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
        return object.Equals(written, readback);
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

    // The validator at load time rejects non-digit indexers and Int32
    // overflows, so in practice we never reach a failing TryParse here. The
    // defensive branch is kept so a bypassed validation path fails loudly
    // rather than crashing the entire apply cycle.
    private static bool TryParsePath(string fieldPath, out PathSegment[] segments, out string error)
    {
        var raw = fieldPath.Split('.');
        segments = new PathSegment[raw.Length];
        error = null;

        for (var i = 0; i < raw.Length; i++)
        {
            var segment = raw[i];
            var bracket = segment.IndexOf('[');
            if (bracket < 0)
            {
                segments[i] = new PathSegment(segment, null);
                continue;
            }

            var name = segment[..bracket];
            var indexText = segment.Substring(bracket + 1, segment.Length - bracket - 2);
            if (!int.TryParse(indexText, out var index))
            {
                segments = null;
                error = $"unparseable indexer '[{indexText}]' in segment '{segment}'.";
                return false;
            }

            segments[i] = new PathSegment(name, index);
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

    // Read-only element access against Il2Cpp-side collections. Supports the
    // main collection shapes generated by Il2CppInterop: ReferenceArray<T>,
    // StructArray, Il2CppSystem.Collections.Generic.List<T>, and anything that
    // exposes a single-parameter Item indexer by reflection. Bounds are
    // enforced via the collection's Length/Count before indexing.
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
        elementType = null;
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
        object parent, string fieldName, object liveCollection, Type collectionType, Type elementType,
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
        object parent, string fieldName, object liveCollection, Type collectionType, Type elementType,
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

        var removeIndex = terminal.Index!.Value;

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
        CompiledTemplateValue value, Type targetType, out object converted, out string error)
    {
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
                if (targetType != typeof(int))
                {
                    error = $"value kind Int32 but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Int32.Value;
                return true;

            case CompiledTemplateValueKind.Single:
                if (targetType != typeof(float))
                {
                    error = $"value kind Single but member type is {targetType.FullName}.";
                    return false;
                }
                converted = value.Single.Value;
                return true;

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
                    converted = Enum.Parse(targetType, value.EnumValue, ignoreCase: false);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"failed to parse enum value '{value.EnumValue}' for {targetType.Name}: {ex.Message}";
                    return false;
                }

            case CompiledTemplateValueKind.TemplateReference:
                return TryResolveTemplateReference(value.Reference, targetType, out converted, out error);

            case CompiledTemplateValueKind.Composite:
                return TryConstructComposite(value.Composite, targetType, out converted, out error);

            default:
                error = $"unknown value kind {value.Kind}.";
                return false;
        }
    }

    // Constructs a fresh instance of the composite's typeName and recursively
    // writes each authored field via the same TryConvertScalar conversion.
    // Dispatches construction by base class: ScriptableObject subtypes use
    // ScriptableObject.CreateInstance (runs Unity's OnEnable etc.); plain
    // support types use the wrapper's parameterless ctor via Activator.
    private static bool TryConstructComposite(
        CompiledTemplateComposite composite, Type targetType, out object converted, out string error)
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
            instance = typeof(UnityEngine.ScriptableObject).IsAssignableFrom(resolvedType)
                ? UnityEngine.ScriptableObject.CreateInstance(Il2CppInterop.Runtime.Il2CppType.From(resolvedType))
                : Activator.CreateInstance(resolvedType)!;
        }
        catch (Exception ex)
        {
            error = $"Composite: construction of {resolvedType.FullName} threw: {ex.Message}";
            return false;
        }

        if (composite.Fields != null)
        {
            foreach (var (fieldName, fieldValue) in composite.Fields)
            {
                if (!TryGetWritableMember(instance, fieldName, out var memberType, out var setter, out _))
                {
                    error = $"Composite {resolvedType.Name}: no writable member '{fieldName}'.";
                    return false;
                }

                if (!TryConvertScalar(fieldValue, memberType, out var fieldConverted, out var fieldError))
                {
                    error = $"Composite {resolvedType.Name}.{fieldName}: {fieldError}";
                    return false;
                }

                try
                {
                    setter(fieldConverted);
                }
                catch (Exception ex)
                {
                    error = $"Composite {resolvedType.Name}.{fieldName} setter threw: {ex.Message}";
                    return false;
                }
            }
        }

        converted = instance;
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

        if (!TemplateRuntimeAccess.TryGetTemplateById(
                reference.TemplateType, reference.TemplateId,
                out var resolvedTemplate, out var resolvedType, out var resolveError))
        {
            error = resolvedType == null
                ? $"TemplateReference '{reference.TemplateType}:{reference.TemplateId}' — {resolveError}"
                : $"TemplateReference: no live {reference.TemplateType} with id '{reference.TemplateId}' found.";
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
