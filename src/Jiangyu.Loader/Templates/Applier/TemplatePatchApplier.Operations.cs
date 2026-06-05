using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Templates;

// Per-op entry point + the IL2CPP-specific dispatch helpers the visitor
// reaches into. TryApplyOperation projects the loaded op into the
// walker view and drives a fresh RuntimeFieldVisitor; descent +
// inner-segment navigation and op-kind switching live in the shared
// walker. ApplyAndVerify is the read-write-readback contract the
// scalar/element/append/insert paths share; collection-shape ops
// (HashSet Add, MD-array cell, Remove, Clear) bypass it because their
// semantics don't fit the "value-with-readback" loop.
internal sealed partial class TemplatePatchApplier
{
    private static ApplyOutcome TryApplyOperation(
        object template, string templateTypeName, string templateId, LoadedPatchOperation op,
        ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        var visitor = new RuntimeFieldVisitor(templateTypeName, templateId, op, assetResolver, log);
        var view = new TemplateOperationView(
            op.Op, op.FieldPath, op.Index, op.IndexPath, op.Descent, op.Value);

        var result = TemplateOperationWalker.Execute(visitor, template, view, out _);
        return result switch
        {
            OperationResult.Applied => ApplyOutcome.Applied,
            OperationResult.MemberMissing => ApplyOutcome.MemberMissing,
            // NotSupported only fires when the walker hands a shape the
            // visitor can't represent. Surface it as a conversion failure
            // so the patch summary's `conversion` counter ticks rather
            // than silently swallowing the op.
            _ => ApplyOutcome.ConversionFailed,
        };
    }

    private static ApplyOutcome ApplyAndVerify(
        string templateTypeName, string templateId, LoadedPatchOperation op, Type memberType,
        Action<object> setter, Func<object> getter, ModAssetResolver assetResolver, MelonLogger.Instance log,
        object appendDestination = null)
    {
        if (!TryConvertScalar(op.Value, memberType, assetResolver, log, out var converted, out var conversionError, appendDestination))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + conversionError);
            return ApplyOutcome.ConversionFailed;
        }

        // C# reflection's PropertyInfo.SetValue does a managed-type
        // assignability check that fails for IL2CPP-wrapped interface
        // assignments (e.g. assigning MoraleStateCondition to an
        // ITacticalCondition-typed field). The two wrappers are sibling
        // classes in CIL because Il2CppInterop strips interface impls.
        // Cast the wrapper to memberType via Il2CppObjectBase.Cast<T> when
        // necessary; the cast is a wrapper-only conversion (same native
        // pointer underneath) so semantics are preserved.
        if (converted is Il2CppObjectBase il2cppObj
            && memberType != null
            && !memberType.IsInstanceOfType(converted)
            && typeof(Il2CppObjectBase).IsAssignableFrom(memberType))
        {
            if (!Il2CppReflectiveCast.TryCast(il2cppObj, memberType, out var cast, out var castError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"could not cast value to member type {memberType.FullName}: {castError}");
                return ApplyOutcome.ConversionFailed;
            }
            converted = cast;
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

            if (!ReadbackMatches(converted, readback))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"wrote {converted}, read back {readback ?? "null"}: the write did not propagate to the live template.");
            }
        }

        return ApplyOutcome.Applied;
    }

    // Multi-dim cell write via raw IL2CPP memory access. Il2CppInterop
    // doesn't expose a wrapper class for multi-dim arrays
    // (BepInEx/Il2CppInterop#218: planned for v2.0.0 but stalled since
    // 2024-09), so the generated property getter throws NRE for fields
    // declared bool[,] / byte[,] / etc. We sidestep the wrapper layer
    // by reading the field's IntPtr directly out of the instance and
    // walking the IL2CPP array header (klass*, monitor*, bounds*,
    // max_length, then element data at offset 0x20). Element size is
    // inferred from the value kind: Boolean and Byte are 1 byte, Int32
    // is 4 bytes. Reads back the cell after writing to verify the write
    // landed.
    private static ApplyOutcome TryApplyMdCellWrite(
        object current, string terminalName,
        string templateTypeName, string templateId, LoadedPatchOperation op,
        MelonLogger.Instance log)
    {
        if (op.Value is null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"multi-dim Set on '{terminalName}' has no value; the compiled patch is malformed.");
            return ApplyOutcome.ConversionFailed;
        }

        if (!Il2CppMdArrayAccessor.TryGetFieldArrayPointer(
                current, terminalName, out var arrayPtr, out var lookupError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"native MD-array lookup for '{terminalName}' failed: {lookupError}");
            return ApplyOutcome.MemberMissing;
        }

        var rank = op.IndexPath.Count;
        if (!Il2CppMdArrayAccessor.TryReadDimensions(
                arrayPtr, rank, out var dims, out var dimError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"native MD-array bounds read for '{terminalName}' failed: {dimError}");
            return ApplyOutcome.MemberMissing;
        }

        if (!Il2CppMdArrayAccessor.TryComputeRowMajorIndex(
                op.IndexPath, dims, out var flatIndex, out var indexError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"cell {string.Join(",", op.IndexPath)} on '{terminalName}': {indexError}");
            return ApplyOutcome.MemberMissing;
        }

        // Element size dispatch. For modder-authored cell writes the
        // value kind reflects what they wrote in KDL. The IL2CPP-side
        // element size has to match: the validator already ensures
        // value-kind/destination-type compatibility for ordinary
        // collections, but multi-dim arrays' element type is hidden
        // behind Il2CppObjectBase so we trust the value kind here and
        // log a readback mismatch if the write doesn't stick.
        var value = op.Value;
        switch (value.Kind)
        {
            case CompiledTemplateValueKind.Boolean:
                {
                    var newByte = value.Boolean == true ? (byte)1 : (byte)0;
                    Il2CppMdArrayAccessor.WriteByteCell(arrayPtr, flatIndex, newByte);
                    var readback = Il2CppMdArrayAccessor.ReadByteCell(arrayPtr, flatIndex);
                    if (readback != newByte)
                    {
                        log.Warning(FormatPrefix(templateTypeName, templateId, op)
                            + $"wrote {newByte} to cell {string.Join(",", op.IndexPath)} of "
                            + $"'{terminalName}', read back {readback}: element size may be wrong.");
                        return ApplyOutcome.Applied;
                    }
                    log.Debug(FormatPrefix(templateTypeName, templateId, op)
                        + $"set {string.Join(",", op.IndexPath)}={value.Boolean == true} on "
                        + $"'{terminalName}' (native MD-array, dims={string.Join("x", dims)}).");
                    return ApplyOutcome.Applied;
                }

            case CompiledTemplateValueKind.Byte:
                {
                    var newByte = (byte)(value.Int32 ?? 0);
                    Il2CppMdArrayAccessor.WriteByteCell(arrayPtr, flatIndex, newByte);
                    var readback = Il2CppMdArrayAccessor.ReadByteCell(arrayPtr, flatIndex);
                    if (readback != newByte)
                    {
                        log.Warning(FormatPrefix(templateTypeName, templateId, op)
                            + $"wrote {newByte} to cell {string.Join(",", op.IndexPath)} of "
                            + $"'{terminalName}', read back {readback}.");
                        return ApplyOutcome.Applied;
                    }
                    log.Debug(FormatPrefix(templateTypeName, templateId, op)
                        + $"set {string.Join(",", op.IndexPath)}=0x{newByte:X2} on '{terminalName}' "
                        + $"(native MD-array, dims={string.Join("x", dims)}).");
                    return ApplyOutcome.Applied;
                }

            case CompiledTemplateValueKind.Int32:
                {
                    var newInt = value.Int32 ?? 0;
                    Il2CppMdArrayAccessor.WriteInt32Cell(arrayPtr, flatIndex, newInt);
                    var readback = Il2CppMdArrayAccessor.ReadInt32Cell(arrayPtr, flatIndex);
                    if (readback != newInt)
                    {
                        log.Warning(FormatPrefix(templateTypeName, templateId, op)
                            + $"wrote {newInt} to cell {string.Join(",", op.IndexPath)} of "
                            + $"'{terminalName}', read back {readback}: element size may be wrong "
                            + "(byte-backed enum stored in 1 byte? Use kind=Byte instead).");
                        return ApplyOutcome.Applied;
                    }
                    log.Debug(FormatPrefix(templateTypeName, templateId, op)
                        + $"set {string.Join(",", op.IndexPath)}={newInt} on '{terminalName}' "
                        + $"(native MD-array, dims={string.Join("x", dims)}).");
                    return ApplyOutcome.Applied;
                }

            default:
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"native MD-array write doesn't yet handle value kind {value.Kind}; "
                    + $"on '{terminalName}', cell {string.Join(",", op.IndexPath)} not applied.");
                return ApplyOutcome.ConversionFailed;
        }
    }

    // Append on HashSet<T>: convert the value, invoke Add(T) on the live
    // set (or a fresh one when the field is null), log idempotently. Add
    // returns bool: true if newly added, false if already present. Both
    // outcomes satisfy "ensure present", so neither is a failure.
    // Bypasses ApplyAndVerify because HashSet has no positional
    // indexer, so the standard "readback last element" check doesn't
    // apply.
    private static ApplyOutcome TryApplyHashSetAdd(
        object current, string terminalName, object collection, Type collectionType,
        string templateTypeName, string templateId, LoadedPatchOperation op,
        ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        var elementType = collectionType.GetGenericArguments()[0];

        if (op.Value == null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"Append on HashSet '{terminalName}' has no value; the compiled patch is malformed.");
            return ApplyOutcome.ConversionFailed;
        }

        if (!TryConvertScalar(op.Value, elementType, assetResolver, log, out var converted, out var convertError))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op) + convertError);
            return ApplyOutcome.ConversionFailed;
        }

        if (!TryGetWritableMember(current, terminalName, out _, out var fieldSetter, out _))
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"parent {current.GetType().FullName} has no writable '{terminalName}' for Append.");
            return ApplyOutcome.MemberMissing;
        }

        var live = collection;
        if (live == null)
        {
            var setCtor = collectionType.GetConstructor(Type.EmptyTypes);
            if (setCtor == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"cannot materialise null {collectionType.FullName}: no parameterless ctor.");
                return ApplyOutcome.ConversionFailed;
            }
            live = setCtor.Invoke(null);
            fieldSetter(live);
        }

        var addMethod = FindInstanceAddMethod(collectionType);
        if (addMethod == null)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"{collectionType.FullName} has no instance Add({elementType.Name}) method.");
            return ApplyOutcome.MemberMissing;
        }

        try
        {
            var added = addMethod.Invoke(live, new[] { converted }) as bool?;
            log.Debug(FormatPrefix(templateTypeName, templateId, op)
                + (added == false
                    ? $"value {FormatValue(converted)} already present in {collectionType.Name}; no-op."
                    : $"appended {FormatValue(converted)} to {collectionType.Name}."));
        }
        catch (Exception ex)
        {
            log.Warning(FormatPrefix(templateTypeName, templateId, op)
                + $"add threw: {(ex.InnerException ?? ex).Message}");
            return ApplyOutcome.ConversionFailed;
        }

        return ApplyOutcome.Applied;
    }

    // Remove takes either an index= (List/Array, positional) or a value
    // (HashSet, by-value). The shape gates which path runs: List/Array
    // dispatch through RemoveFromList/RemoveFromArray and don't go
    // through ApplyAndVerify because there's no value to read back
    // against; HashSet uses the type's own bool-returning Remove(T) and
    // logs whether the entry was actually present.
    private static ApplyOutcome TryApplyRemove(
        object current, string terminalName, string templateTypeName, string templateId,
        LoadedPatchOperation op, ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        if (!TryResolveTerminalCollectionForMutation(
                current, terminalName, templateTypeName, templateId, op, "remove", log,
                out var collection, out var collectionType,
                out var shape, out var elementType, out var fieldSetter,
                out var outcome))
            return outcome;

        // HashSet<T>: by-value removal via instance Remove(T). The
        // validator already rejected index-based Remove on HashSet, so a
        // populated op.Value is the contract here.
        if (shape == CollectionShape.HashSet)
        {
            if (op.Value == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"Remove on HashSet '{terminalName}' has no value; the compiled patch is malformed.");
                return ApplyOutcome.ConversionFailed;
            }

            if (!TryConvertScalar(op.Value, elementType, assetResolver, log, out var converted, out var convertError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op) + convertError);
                return ApplyOutcome.ConversionFailed;
            }

            var removeMethod = collectionType.GetMethod(
                "Remove",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { elementType },
                modifiers: null);
            if (removeMethod == null)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"{collectionType.FullName} has no instance Remove({elementType.Name}) method.");
                return ApplyOutcome.MemberMissing;
            }

            try
            {
                var removed = (bool?)removeMethod.Invoke(collection, new[] { converted });
                log.Debug(FormatPrefix(templateTypeName, templateId, op)
                    + (removed == true
                        ? $"removed {FormatValue(converted)} from {collectionType.Name}."
                        : $"value {FormatValue(converted)} not present in {collectionType.Name}; no-op."));
            }
            catch (Exception ex)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"remove threw: {(ex.InnerException ?? ex).Message}");
                return ApplyOutcome.ConversionFailed;
            }

            return ApplyOutcome.Applied;
        }

        var removeIndex = op.Index
            ?? throw new InvalidOperationException(
                $"Remove operation on '{terminalName}' has no Index; the compiled patch is malformed.");

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

        log.Debug(FormatPrefix(templateTypeName, templateId, op)
            + $"removed element at {removeIndex} from {collectionType.Name}.");
        return ApplyOutcome.Applied;
    }

    // Clear empties the terminal collection in place. List<T> and
    // HashSet<T> use the built-in Clear(); native IL2CPP arrays are
    // immutable, so we rebuild a zero-length array of the same element
    // type and write it back through the parent's setter. A null
    // collection is treated as missing: the loader's missing-field path
    // warns the modder rather than silently materialising an empty
    // collection.
    private static ApplyOutcome TryApplyClear(
        object current, string terminalName, string templateTypeName, string templateId,
        LoadedPatchOperation op, ModAssetResolver assetResolver, MelonLogger.Instance log)
    {
        if (TryResolveTerminalCollectionForMutation(
                current, terminalName, templateTypeName, templateId, op, "clear", log,
                out var collection, out var collectionType,
                out var shape, out var elementType, out var fieldSetter,
                out var outcome,
                warnOnNonCollection: false,
                warnOnNull: false))
        {
            try
            {
                switch (shape)
                {
                    case CollectionShape.List:
                    case CollectionShape.HashSet:
                        // Both BCL and Il2Cpp List/HashSet expose a parameterless
                        // Clear(); the helper just invokes that.
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

            log.Debug(FormatPrefix(templateTypeName, templateId, op)
                + $"cleared {collectionType.Name}.");
            return ApplyOutcome.Applied;
        }

        // Resolve failed. Distinguish three cases on a writable member:
        //  - a null value (collection or inline object) is already empty, so
        //    clear is a successful no-op — matching the offline preview;
        //  - a writable non-collection member with a live value is an inline
        //    object/struct to reset to a fresh default;
        //  - a genuinely missing member keeps the resolver's diagnostic.
        if (TryGetWritableMember(current, terminalName, out var memberType, out var objectSetter, out _))
        {
            if (collection == null || TryGetCollectionShape(memberType, out _, out _))
            {
                log.Debug(FormatPrefix(templateTypeName, templateId, op)
                    + $"clear on '{terminalName}' ({memberType.FullName}) is a no-op: already empty.");
                return ApplyOutcome.Applied;
            }

            var fresh = new CompiledTemplateComposite
            {
                TypeName = memberType.FullName ?? memberType.Name,
            };
            if (!TryConstructComposite(fresh, memberType, assetResolver, log, out var reset, out var constructError))
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"clear could not reset '{terminalName}' ({memberType.FullName}) to a default: {constructError}");
                return ApplyOutcome.ConversionFailed;
            }
            try
            {
                objectSetter(reset);
            }
            catch (Exception ex)
            {
                log.Warning(FormatPrefix(templateTypeName, templateId, op)
                    + $"clear write-back of '{terminalName}' threw: {ex.Message}");
                return ApplyOutcome.ConversionFailed;
            }
            log.Debug(FormatPrefix(templateTypeName, templateId, op)
                + $"reset {memberType.Name} to a default.");
            return ApplyOutcome.Applied;
        }

        return outcome;
    }
}
