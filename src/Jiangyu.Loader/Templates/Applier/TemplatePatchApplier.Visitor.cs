using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

// IL2CPP-side adapter for TemplateOperationWalker. Owns the navigation
// read primitives (TryReadMember-wrapped, plus the Il2CppObjectBase.Cast
// branch for polymorphic descent), the per-op-shape dispatchers (scalar
// set + readback, element bind, MD-cell write, HashSet Add, list/array
// insert/append, remove, clear), and the struct write-back chain that
// fires after a successful terminal write so a mutated value-type
// intermediate persists up to its reference-type host. One instance per
// op call (cheap), state is reset implicitly by construction.
internal sealed partial class TemplatePatchApplier
{
    private sealed class RuntimeFieldVisitor : IFieldVisitor<object>
    {
        private readonly string _templateTypeName;
        private readonly string _templateId;
        private readonly LoadedPatchOperation _op;
        private readonly ModAssetResolver _assetResolver;
        private readonly MelonLogger.Instance _log;
        private readonly List<ChainEntry> _chain = new();
        private object _latestCurrent;

        public RuntimeFieldVisitor(
            string templateTypeName, string templateId, LoadedPatchOperation op,
            ModAssetResolver assetResolver, MelonLogger.Instance log)
        {
            _templateTypeName = templateTypeName;
            _templateId = templateId;
            _op = op;
            _assetResolver = assetResolver;
            _log = log;
        }

        public OperationResult TryReadField(object parent, string fieldName, out object child, out string error)
        {
            _latestCurrent = parent;
            if (!TryReadMember(parent, fieldName, out child, out var memberType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot navigate segment '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            if (child == null)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"intermediate segment '{fieldName}' ({memberType.FullName}) is null on this template.");
                error = $"intermediate '{fieldName}' is null.";
                return OperationResult.MemberMissing;
            }

            _chain.Add(new ChainEntry
            {
                Parent = parent,
                Name = fieldName,
                ValueIsStruct = memberType.IsValueType,
            });
            _latestCurrent = child;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryDescendElement(object parent, string fieldName, int index, out object descended, out string error)
        {
            descended = null;
            if (!TryReadMember(parent, fieldName, out var value, out var memberType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot navigate descent step '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            if (value == null)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"descent step '{fieldName}' ({memberType.FullName}) is null on this template.");
                error = $"descent collection '{fieldName}' is null.";
                return OperationResult.MemberMissing;
            }

            _chain.Add(new ChainEntry
            {
                Parent = parent,
                Name = fieldName,
                ValueIsStruct = memberType.IsValueType,
            });

            if (!TryIndexInto(value, index, out var element, out var elementType, out var indexError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot index descent step '{fieldName}' index={index}: {indexError}");
                error = indexError;
                return OperationResult.MemberMissing;
            }

            if (element == null)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"element {index} of '{fieldName}' is null.");
                error = $"element {index} of '{fieldName}' is null.";
                return OperationResult.MemberMissing;
            }

            // In-place struct-element edit. Indexing a value-type collection
            // hands back a *copy* of the element (structs copy on read), so
            // mutating it wouldn't reach the collection on its own. Record an
            // element chain entry carrying the collection and index: after the
            // inner ops mutate this boxed copy, the write-back loop stores it
            // back through the collection indexer (collection[index] = copy).
            // The compile-time validator only admits this descent for blittable
            // structs (no reference/string members); a non-indexable or
            // multi-dim shape fails cleanly at write-back via TryBindArrayElement
            // rather than corrupting state.
            // JIANGYU-CONTRACT: storing the mutated copy back through the
            // collection indexer (Il2CppStructArray<T>.Item set, List<T>
            // indexer) round-trips a blittable struct reliably on this
            // Il2CppInterop stack. Scope: blittable value-type elements
            // admitted by the validator. Validated by the live-game edit of
            // OffmapAbilityDelayMod (ShipUpgradeTemplate DelayMods); the
            // pure-reflection binder is covered by managed List<T>/T[]
            // fixtures in Jiangyu.Loader.Tests.
            if (elementType.IsValueType)
            {
                _chain.Add(new ChainEntry
                {
                    Parent = value,
                    ElementIndex = index,
                    ValueIsStruct = true,
                });
                descended = element;
                _latestCurrent = element;
                error = null;
                return OperationResult.Applied;
            }

            // Reference-type element: edit the live object in place. Indexing a
            // List<AbstractBase> hands back a base-typed wrapper exposing only
            // base members, so detect the live element's actual concrete type
            // and cast to it; the inner ops then reach the subclass fields. If
            // the concrete type can't be resolved the base-typed wrapper stays:
            // ops touching only base members still apply, and subclass-member
            // ops surface a clear "no writable" diagnostic.
            if (TryCastToLiveConcreteType(element, value.GetType(), out var concrete, out _))
            {
                element = concrete;
            }

            descended = element;
            _latestCurrent = element;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryDescendField(object parent, string fieldName, out object descended, out string error)
        {
            descended = null;
            _latestCurrent = parent;
            if (!TryReadMember(parent, fieldName, out var value, out var memberType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot navigate object-field descent '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            if (value == null)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"object-field descent '{fieldName}' ({memberType.FullName}) is null on this template; "
                    + "clear it first to build a fresh default (monomorphic), or use type= for a polymorphic field.");
                error = $"object-field descent '{fieldName}' is null.";
                return OperationResult.MemberMissing;
            }

            _chain.Add(new ChainEntry
            {
                Parent = parent,
                Name = fieldName,
                ValueIsStruct = memberType.IsValueType,
            });

            // A polymorphic object field hands back a base-typed wrapper; cast
            // to the live concrete type. No-op for value-type and already-
            // concrete fields, which keep the base wrapper.
            if (TryCastToLiveConcreteType(value, memberType, out var concrete, out _))
            {
                value = concrete;
            }

            descended = value;
            _latestCurrent = value;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetScalar(object parent, string fieldName, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            if (!TryGetWritableMember(parent, fieldName, out var memberType, out var setter, out var getter))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"no writable field or property '{fieldName}' found on {parent.GetType().FullName}.");
                error = $"no writable '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            error = null;
            return RunVerified(parent, memberType, setter, getter, appendDestination: null);
        }

        public OperationResult TrySetElement(object parent, string fieldName, int index, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            if (!TryReadMember(parent, fieldName, out var arrayValue, out var arrayType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot read terminal collection '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            if (arrayValue == null)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"terminal collection '{fieldName}' ({arrayType.FullName}) is null on this template.");
                error = $"terminal '{fieldName}' is null.";
                return OperationResult.MemberMissing;
            }

            if (!TryBindArrayElement(arrayValue, index, out var elementType, out var setter, out var getter, out var bindError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot bind '{fieldName}' index={index}: {bindError}");
                error = bindError;
                return OperationResult.MemberMissing;
            }

            error = null;
            return RunVerified(parent, elementType, setter, getter, appendDestination: null);
        }

        public OperationResult TrySetCell(object parent, string fieldName, IReadOnlyList<int> indexPath, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            // The MD-cell write is its own self-contained primitive: it
            // bypasses the wrapper layer and writes the IL2CPP array memory
            // directly. Bouncing through the helper keeps the comment trail
            // (rationale, BepInEx tracker link) where it already lives.
            var outcome = TryApplyMdCellWrite(parent, fieldName, _templateTypeName, _templateId, _op, _log);
            error = null;
            return outcome switch
            {
                ApplyOutcome.Applied => OperationResult.Applied,
                ApplyOutcome.MemberMissing => OperationResult.MemberMissing,
                _ => OperationResult.ConversionFailed,
            };
        }

        public OperationResult TryAppend(object parent, string fieldName, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            if (!TryReadMember(parent, fieldName, out var collection, out var collectionType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot read terminal collection '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            // HashSet Append takes the dedicated path: HashSet has no
            // positional indexer so the readback contract doesn't apply.
            if (collection != null && IsHashSetType(collectionType))
            {
                var outcome = TryApplyHashSetAdd(parent, fieldName, collection, collectionType,
                    _templateTypeName, _templateId, _op, _assetResolver, _log);
                error = null;
                return TranslateOutcome(outcome);
            }

            if (!TryBindCollectionMutation(
                    parent, fieldName, collection, collectionType, insertIndex: null,
                    out var elementType, out var setter, out var getter, out var bindError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot bind Append on '{fieldName}': {bindError}");
                error = bindError;
                return OperationResult.MemberMissing;
            }

            error = null;
            return RunVerified(parent, elementType, setter, getter, appendDestination: collection);
        }

        public OperationResult TryInsertAt(object parent, string fieldName, int index, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            if (!TryReadMember(parent, fieldName, out var collection, out var collectionType, out var readError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot read terminal collection '{fieldName}': {readError}");
                error = readError;
                return OperationResult.MemberMissing;
            }

            if (!TryBindCollectionMutation(
                    parent, fieldName, collection, collectionType, insertIndex: index,
                    out var elementType, out var setter, out var getter, out var bindError))
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"cannot bind InsertAt on '{fieldName}': {bindError}");
                error = bindError;
                return OperationResult.MemberMissing;
            }

            error = null;
            return RunVerified(parent, elementType, setter, getter, appendDestination: collection);
        }

        public OperationResult TryRemove(object parent, string fieldName, int? index, CompiledTemplateValue value, out string error)
        {
            _latestCurrent = parent;
            var outcome = TryApplyRemove(parent, fieldName, _templateTypeName, _templateId, _op, _assetResolver, _log);
            error = null;
            return TranslateOutcome(outcome);
        }

        public OperationResult TryClear(object parent, string fieldName, out string error)
        {
            _latestCurrent = parent;
            var outcome = TryApplyClear(parent, fieldName, _templateTypeName, _templateId, _op, _assetResolver, _log);
            error = null;
            return TranslateOutcome(outcome);
        }

        public void OnEnterSegment(object parent, string segmentName)
        {
            // Chain bookkeeping happens in TryReadField/TryDescendElement
            // where the visitor knows the member type. Default no-op here.
        }

        public void OnCompleted(OperationResult result)
        {
            if (result != OperationResult.Applied)
                return;

            // Struct write-back: propagate the mutated descendant up the
            // chain through each parent's setter. _latestCurrent is the
            // direct parent of the terminal write (post-mutation); each
            // iteration writes a struct level back into its own parent and
            // walks one step up. current advances to entry.Parent on every
            // level, struct or reference, so a reference level (whose
            // in-place mutation already persisted) still hands the correct
            // value to the next-shallower struct level above it.
            var current = _latestCurrent;
            for (var i = _chain.Count - 1; i >= 0; i--)
            {
                var entry = _chain[i];
                if (entry.ValueIsStruct && !TryWriteBackStructLevel(entry, current))
                    return;

                current = entry.Parent;
            }
        }

        // Writes one mutated struct level back into its parent: through the
        // collection indexer for a value-type element (Parent[ElementIndex]
        // = current), through the named-member setter otherwise. A resolve
        // or write failure warns and stops the chain walk; the terminal set
        // itself already succeeded, so this stays a warning, not an error.
        private bool TryWriteBackStructLevel(ChainEntry entry, object current)
        {
            Action<object> setter;
            string target;
            if (entry.ElementIndex.HasValue)
            {
                if (!TryBindArrayElement(
                        entry.Parent, entry.ElementIndex.Value,
                        out _, out setter, out _, out var bindError))
                {
                    _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                        + $"write-back after terminal set failed: element {entry.ElementIndex.Value} of "
                        + $"{entry.Parent.GetType().FullName} has no writable indexer ({bindError}); "
                        + "the struct mutation may not persist.");
                    return false;
                }

                target = $"element {entry.ElementIndex.Value} of {entry.Parent.GetType().FullName}";
            }
            else
            {
                if (!TryGetWritableMember(entry.Parent, entry.Name, out _, out setter, out _))
                {
                    _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                        + $"write-back after terminal set failed: parent segment '{entry.Name}' on "
                        + $"{entry.Parent.GetType().FullName} has no writable surface, so the struct mutation may not persist.");
                    return false;
                }

                target = $"segment '{entry.Name}'";
            }

            try
            {
                setter(current);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning(FormatPrefix(_templateTypeName, _templateId, _op)
                    + $"write-back after terminal set threw at {target}: {ex.Message}");
                return false;
            }
        }

        // RunVerified mirrors ApplyAndVerify's read-write-readback contract.
        // The conversion + Il2Cpp interop cast live inside ApplyAndVerify;
        // the visitor delegates verbatim so the static helper stays the
        // single place the "convert, set, optionally read back" sequence
        // is defined.
        private OperationResult RunVerified(
            object parent, Type memberType, Action<object> setter, Func<object> getter,
            object appendDestination)
        {
            _latestCurrent = parent;
            var outcome = ApplyAndVerify(
                _templateTypeName, _templateId, _op, memberType, setter, getter,
                _assetResolver, _log, appendDestination);
            return TranslateOutcome(outcome);
        }

        private static OperationResult TranslateOutcome(ApplyOutcome outcome) => outcome switch
        {
            ApplyOutcome.Applied => OperationResult.Applied,
            ApplyOutcome.MemberMissing => OperationResult.MemberMissing,
            ApplyOutcome.ConversionFailed => OperationResult.ConversionFailed,
            _ => OperationResult.ConversionFailed,
        };
    }
}
