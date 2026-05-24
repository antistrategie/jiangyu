namespace Jiangyu.Shared.Templates;

/// <summary>
/// Outcome of a single template-op walk. The walker maps a parser-stage
/// fault (malformed path, missing member) to <see cref="MemberMissing"/>,
/// a value-conversion fault (incompatible kind, asset lookup failure) to
/// <see cref="ConversionFailed"/>, and a visitor opting out of a shape it
/// cannot represent (e.g. preview vs MD-array cell writes) to
/// <see cref="NotSupported"/>. Visitors classify their own failures by
/// returning the matching variant from each primitive.
/// </summary>
public enum OperationResult
{
    Applied,
    MemberMissing,
    ConversionFailed,
    NotSupported,
}

/// <summary>
/// Adapter contract for <see cref="TemplateOperationWalker"/>. The walker
/// owns parsing <c>FieldPath</c>, sequencing <c>Descent</c>, and dispatching
/// the terminal op kind. The visitor owns reading and writing against its
/// own node universe: <c>InspectedFieldNode</c> trees for the preview
/// applier, live IL2CPP wrappers for the runtime applier.
///
/// <para>Implementations are stateful: they hold the bindings each op needs
/// (asset resolver, log sink, reference lookup). The runtime adapter also
/// uses <see cref="OnEnterSegment"/> to record the parent stack so a
/// struct-typed intermediate can be written back up the chain after a
/// successful terminal op. Adapters with reference-type node universes
/// leave the default no-op.</para>
/// </summary>
/// <typeparam name="TNode">
/// The visitor's location handle: whatever the adapter uses to point at
/// "I'm here, this is the parent that holds the next member". For preview
/// it's <c>InspectedFieldNode</c>; for runtime it's the live wrapper object.
/// </typeparam>
public interface IFieldVisitor<TNode>
{
    /// <summary>
    /// Resolve <paramref name="parent"/>'s <paramref name="fieldName"/>
    /// member and bind its value into <paramref name="child"/>. Used for
    /// non-terminal segment walks and for the read half of scalar
    /// polymorphic descent.
    /// </summary>
    OperationResult TryReadField(TNode parent, string fieldName, out TNode child, out string? error);

    /// <summary>
    /// Cast or otherwise switch the descended node's visible type to
    /// <paramref name="subtype"/>. The IL2CPP adapter routes this through
    /// <c>Il2CppObjectBase.Cast&lt;T&gt;</c>; the preview adapter updates
    /// the node's <c>FieldTypeName</c> tag. When <paramref name="subtype"/>
    /// is null the descent is a pass-through.
    /// </summary>
    OperationResult TryDescendScalar(TNode current, string? subtype, out TNode descended, out string? error);

    /// <summary>
    /// Read element <paramref name="index"/> of the collection at
    /// <paramref name="parent"/>'s <paramref name="fieldName"/> member,
    /// optionally casting it to <paramref name="subtype"/>. Used for
    /// collection-element descent (the <c>set "Field" index=N type="X"</c>
    /// KDL form).
    /// </summary>
    OperationResult TryDescendElement(TNode parent, string fieldName, int index, string? subtype, out TNode descended, out string? error);

    /// <summary>
    /// Write <paramref name="value"/> to <paramref name="parent"/>'s
    /// <paramref name="fieldName"/> scalar (non-collection) member. The
    /// runtime adapter routes through the same readback-verified path
    /// used today; the preview adapter rebuilds the field node.
    /// </summary>
    OperationResult TrySetScalar(TNode parent, string fieldName, CompiledTemplateValue value, out string? error);

    /// <summary>
    /// Write <paramref name="value"/> to element <paramref name="index"/>
    /// of the collection at <paramref name="parent"/>'s
    /// <paramref name="fieldName"/> member. Covers both the
    /// <c>set "Field" index=N</c> form and the legacy bracket-terminal
    /// path the runtime applier already accepts.
    /// </summary>
    OperationResult TrySetElement(TNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error);

    /// <summary>
    /// Write <paramref name="value"/> to multi-dimensional cell
    /// <paramref name="indexPath"/> of the array at
    /// <paramref name="parent"/>'s <paramref name="fieldName"/> member.
    /// The runtime adapter routes through <c>Il2CppMdArrayAccessor</c>;
    /// adapters that don't model multi-dim arrays (e.g. the preview)
    /// return <see cref="OperationResult.NotSupported"/>.
    /// </summary>
    OperationResult TrySetCell(TNode parent, string fieldName, IReadOnlyList<int> indexPath, CompiledTemplateValue value, out string? error);

    /// <summary>
    /// Append <paramref name="value"/> to the collection at
    /// <paramref name="parent"/>'s <paramref name="fieldName"/> member.
    /// The runtime adapter sniffs the collection kind (List, array,
    /// HashSet) and dispatches accordingly.
    /// </summary>
    OperationResult TryAppend(TNode parent, string fieldName, CompiledTemplateValue value, out string? error);

    /// <summary>
    /// Insert <paramref name="value"/> at position <paramref name="index"/>
    /// in the collection at <paramref name="parent"/>'s
    /// <paramref name="fieldName"/> member.
    /// </summary>
    OperationResult TryInsertAt(TNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error);

    /// <summary>
    /// Remove an entry from the collection at <paramref name="parent"/>'s
    /// <paramref name="fieldName"/> member. List/array removes carry an
    /// <paramref name="index"/>; HashSet removes carry a
    /// <paramref name="value"/> (the validator gates which shape applies).
    /// </summary>
    OperationResult TryRemove(TNode parent, string fieldName, int? index, CompiledTemplateValue? value, out string? error);

    /// <summary>
    /// Empty the collection at <paramref name="parent"/>'s
    /// <paramref name="fieldName"/> member. Composes with subsequent
    /// <see cref="TryAppend"/> ops for the "replace the whole list" idiom.
    /// </summary>
    OperationResult TryClear(TNode parent, string fieldName, out string? error);

    /// <summary>
    /// Hook fired once per navigation step (descent + inner) before the
    /// walker reads the child. The runtime adapter records the parent
    /// stack here so a struct write-back can propagate up after a
    /// successful terminal op; reference-tree adapters leave the default
    /// no-op.
    /// </summary>
    void OnEnterSegment(TNode parent, string segmentName) { }

    /// <summary>
    /// Hook fired exactly once at the end of every walker call, after the
    /// terminal op has dispatched. The runtime adapter uses this to drive
    /// the struct chain unwind; reference-tree adapters leave the default
    /// no-op.
    /// </summary>
    void OnCompleted(OperationResult result) { }
}
