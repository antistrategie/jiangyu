using System.Reflection;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

// Collection-shape detection and per-shape binders for the patch
// applier. Set ops on collection elements, Append/Insert via the
// shape-specific binder, and the Remove/Clear primitives all live
// here; HashSet Append and multi-dim cell writes are handled in the
// per-op partial because their semantics differ enough that lumping
// them under a single binder obscured the contract.
internal sealed partial class TemplatePatchApplier
{
    private enum CollectionShape { List, HashSet, ReferenceArray, StructArray }

    // Resolves the terminal collection for a mutation op (Remove / Clear)
    // and runs the four sanity checks both ops share: member reads,
    // not null, has a recognised collection shape, and the parent
    // exposes a writable surface for write-back. On any failure logs
    // the warning and writes the appropriate outcome; on success
    // populates all five outs. Removes ~50 lines of identical opener
    // boilerplate from the two op handlers that carry it.
    private static bool TryResolveTerminalCollectionForMutation(
        object current, PathSegment terminal,
        string templateTypeName, string templateId, LoadedPatchOperation op,
        string opLabel, MelonLogger.Instance log,
        out object collection, out Type collectionType,
        out CollectionShape shape, out Type elementType,
        out Action<object> fieldSetter,
        out ApplyOutcome outcome)
    {
        collection = null;
        collectionType = null;
        shape = default;
        elementType = null;
        fieldSetter = null;
        outcome = ApplyOutcome.MemberMissing;

        if (!TryReadMember(current, terminal.Name, out collection, out collectionType, out var readError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"cannot read terminal collection '{terminal.Name}': {readError}");
            return false;
        }

        if (collection == null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"terminal collection '{terminal.Name}' ({collectionType.FullName}) is null; nothing to {opLabel}.");
            return false;
        }

        if (!TryGetCollectionShape(collectionType, out shape, out elementType))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"collection type {collectionType.FullName} is not supported for {opLabel}.");
            return false;
        }

        if (!TryGetWritableMember(current, terminal.Name, out _, out fieldSetter, out _))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"parent {current.GetType().FullName} has no writable '{terminal.Name}' for {opLabel}.");
            return false;
        }

        return true;
    }

    private static bool TryGetCollectionShape(Type collectionType, out CollectionShape shape, out Type elementType)
    {
        // HashSet checked before the generic Add-method probe because
        // HashSet<T>.Add also satisfies that probe; without this branch
        // the Remove-by-value path can't tell a HashSet from a List.
        if (IsHashSetType(collectionType))
        {
            shape = CollectionShape.HashSet;
            elementType = collectionType.GetGenericArguments()[0];
            return true;
        }

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

    private static bool IsHashSetType(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            var def = current.GetGenericTypeDefinition().FullName;
            if (def == "System.Collections.Generic.HashSet`1"
                || def == "Il2CppSystem.Collections.Generic.HashSet`1")
                return true;
        }
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

    // Terminal indexer writes target an array/list element: scalar types
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

            case CollectionShape.HashSet:
                // HashSet Append is dispatched directly from
                // TryApplyOperation (TryApplyHashSetAdd) and never
                // reaches here. InsertAt is rejected by the validator.
                error = $"internal: HashSet<{elementType.Name}> reached "
                    + "TryBindCollectionMutation; should have been short-circuited.";
                return false;

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
}
