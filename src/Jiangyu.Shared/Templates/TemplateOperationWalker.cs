namespace Jiangyu.Shared.Templates;

/// <summary>
/// Walker-input view over a single compiled template op. Both the in-memory
/// preview applier (which receives <see cref="CompiledTemplateSetOperation"/>
/// directly) and the runtime loader (which carries the same shape with an
/// extra owner tag on <c>LoadedPatchOperation</c>) project into this
/// uniform handle before calling the walker.
/// </summary>
public readonly struct TemplateOperationView
{
    public TemplateOperationView(
        CompiledTemplateOp op,
        string fieldPath,
        int? index,
        IReadOnlyList<int>? indexPath,
        IReadOnlyList<TemplateDescentStep>? descent,
        CompiledTemplateValue? value)
    {
        Op = op;
        FieldPath = fieldPath ?? string.Empty;
        Index = index;
        IndexPath = indexPath;
        Descent = descent;
        Value = value;
    }

    public CompiledTemplateOp Op { get; }
    public string FieldPath { get; }
    public int? Index { get; }
    public IReadOnlyList<int>? IndexPath { get; }
    public IReadOnlyList<TemplateDescentStep>? Descent { get; }
    public CompiledTemplateValue? Value { get; }

    public static TemplateOperationView FromCompiled(CompiledTemplateSetOperation op)
    {
        if (op is null) throw new ArgumentNullException(nameof(op));
        return new TemplateOperationView(op.Op, op.FieldPath, op.Index, op.IndexPath, op.Descent, op.Value);
    }
}

/// <summary>
/// Drives a single template op against an <see cref="IFieldVisitor{TNode}"/>
/// adapter. Owns the path parser, the descent + inner-segment walk, and the
/// terminal-op dispatch shapes (scalar set, element set, MD-array cell set,
/// append, insert-at, remove, clear). Each adapter supplies the read/write
/// primitives for its node universe; the walker is the single place where
/// the op grammar lives.
/// </summary>
public static class TemplateOperationWalker
{
    /// <summary>
    /// Resolves the target location for <paramref name="op"/> by walking
    /// <see cref="TemplateOperationView.Descent"/> then the inner segments
    /// parsed from <see cref="TemplateOperationView.FieldPath"/>, then
    /// dispatches the terminal op via <paramref name="visitor"/>.
    /// <paramref name="error"/> carries the visitor-supplied or walker-
    /// supplied diagnostic on a non-Applied result.
    /// </summary>
    public static OperationResult Execute<TNode>(
        IFieldVisitor<TNode> visitor,
        TNode root,
        in TemplateOperationView op,
        out string? error)
    {
        if (visitor is null) throw new ArgumentNullException(nameof(visitor));

        OperationResult result;

        if (!TryParseInnerSegments(op.FieldPath, out var innerSegments, out var parseError))
        {
            error = parseError;
            result = OperationResult.MemberMissing;
            visitor.OnCompleted(result);
            return result;
        }

        if (innerSegments.Count == 0)
        {
            error = $"Template operation path '{op.FieldPath}' is empty.";
            result = OperationResult.MemberMissing;
            visitor.OnCompleted(result);
            return result;
        }

        TNode current = root;

        if (op.Descent is { Count: > 0 } descent)
        {
            for (int i = 0; i < descent.Count; i++)
            {
                TemplateDescentStep step = descent[i];
                visitor.OnEnterSegment(current, step.Field);

                OperationResult stepResult = visitor.TryDescendElement(
                    current, step.Field, step.Index, out TNode next, out string? stepError);

                if (stepResult != OperationResult.Applied)
                {
                    error = stepError;
                    visitor.OnCompleted(stepResult);
                    return stepResult;
                }

                current = next;
            }
        }

        for (int i = 0; i < innerSegments.Count - 1; i++)
        {
            PathSegment seg = innerSegments[i];
            visitor.OnEnterSegment(current, seg.Name);
            OperationResult segResult = visitor.TryReadField(current, seg.Name, out TNode next, out string? readError);
            if (segResult != OperationResult.Applied)
            {
                error = readError;
                visitor.OnCompleted(segResult);
                return segResult;
            }
            current = next;
        }

        PathSegment terminal = innerSegments[innerSegments.Count - 1];
        result = DispatchTerminal(visitor, current, terminal, op, out error);
        visitor.OnCompleted(result);
        return result;
    }

    private static OperationResult DispatchTerminal<TNode>(
        IFieldVisitor<TNode> visitor,
        TNode parent,
        PathSegment terminal,
        in TemplateOperationView op,
        out string? error)
    {
        switch (op.Op)
        {
            case CompiledTemplateOp.Set:
                return DispatchSet(visitor, parent, terminal, op, out error);

            case CompiledTemplateOp.Append:
                if (op.Value is null)
                {
                    error = $"Append on '{terminal.Name}' has no value.";
                    return OperationResult.ConversionFailed;
                }
                if (terminal.Index.HasValue)
                {
                    error = $"Append on '{terminal.Name}' targets the collection field, not an indexed element.";
                    return OperationResult.ConversionFailed;
                }
                return visitor.TryAppend(parent, terminal.Name, op.Value, out error);

            case CompiledTemplateOp.InsertAt:
                if (op.Value is null)
                {
                    error = $"InsertAt on '{terminal.Name}' has no value.";
                    return OperationResult.ConversionFailed;
                }
                if (!op.Index.HasValue)
                {
                    error = $"InsertAt on '{terminal.Name}' has no index.";
                    return OperationResult.ConversionFailed;
                }
                if (terminal.Index.HasValue)
                {
                    error = $"InsertAt on '{terminal.Name}' targets the collection field, not an indexed element.";
                    return OperationResult.ConversionFailed;
                }
                return visitor.TryInsertAt(parent, terminal.Name, op.Index.Value, op.Value, out error);

            case CompiledTemplateOp.Remove:
                // Index-based (List/Array) and value-based (HashSet) removes
                // share the same visitor surface; the adapter inspects the
                // collection shape to pick the right path.
                return visitor.TryRemove(parent, terminal.Name, op.Index, op.Value, out error);

            case CompiledTemplateOp.Clear:
                if (terminal.Index.HasValue)
                {
                    error = $"Clear on '{terminal.Name}' targets the collection field, not an indexed element.";
                    return OperationResult.ConversionFailed;
                }
                return visitor.TryClear(parent, terminal.Name, out error);

            default:
                error = $"Unsupported template op '{op.Op}' on '{terminal.Name}'.";
                return OperationResult.NotSupported;
        }
    }

    private static OperationResult DispatchSet<TNode>(
        IFieldVisitor<TNode> visitor,
        TNode parent,
        PathSegment terminal,
        in TemplateOperationView op,
        out string? error)
    {
        if (op.Value is null)
        {
            error = $"Set on '{terminal.Name}' has no value.";
            return OperationResult.ConversionFailed;
        }

        // Cell write (multi-dim array): IndexPath carries the per-axis
        // coordinates. Mutually exclusive with Index/terminal.Index per
        // validator.
        if (op.IndexPath is { Count: > 0 } indexPath)
        {
            if (op.Index.HasValue || terminal.Index.HasValue)
            {
                error = $"Set with cell address on '{terminal.Name}' cannot also carry a scalar index.";
                return OperationResult.ConversionFailed;
            }
            return visitor.TrySetCell(parent, terminal.Name, indexPath, op.Value, out error);
        }

        // Element write: index comes from either op.Index (KDL
        // `set "Field" index=N`) or terminal.Index (bracket form).
        if (op.Index.HasValue || terminal.Index.HasValue)
        {
            int idx = op.Index ?? terminal.Index!.Value;
            return visitor.TrySetElement(parent, terminal.Name, idx, op.Value, out error);
        }

        return visitor.TrySetScalar(parent, terminal.Name, op.Value, out error);
    }

    private static bool TryParseInnerSegments(string fieldPath, out IReadOnlyList<PathSegment> segments, out string? error)
    {
        if (string.IsNullOrEmpty(fieldPath))
        {
            segments = Array.Empty<PathSegment>();
            error = null;
            return true;
        }

        string[] raw = fieldPath.Split('.');
        PathSegment[] result = new PathSegment[raw.Length];

        for (int i = 0; i < raw.Length; i++)
        {
            string seg = raw[i];
            if (seg.Length == 0)
            {
                segments = Array.Empty<PathSegment>();
                error = $"Template operation path '{fieldPath}' has an empty segment.";
                return false;
            }

            int bracket = seg.IndexOf('[');
            if (bracket < 0)
            {
                result[i] = new PathSegment(seg, null);
                continue;
            }

            if (i != raw.Length - 1)
            {
                segments = Array.Empty<PathSegment>();
                error = $"Template operation path '{fieldPath}' has a bracket indexer on non-terminal segment '{seg}'.";
                return false;
            }

            if (!seg.EndsWith("]", StringComparison.Ordinal))
            {
                segments = Array.Empty<PathSegment>();
                error = $"Template operation path '{fieldPath}' has a malformed indexer in '{seg}'.";
                return false;
            }

            string name = seg.Substring(0, bracket);
            string indexText = seg.Substring(bracket + 1, seg.Length - bracket - 2);
            if (name.Length == 0)
            {
                segments = Array.Empty<PathSegment>();
                error = $"Template operation path '{fieldPath}' has an empty name before indexer in '{seg}'.";
                return false;
            }
            if (!int.TryParse(indexText, out int index) || index < 0)
            {
                segments = Array.Empty<PathSegment>();
                error = $"Template operation path '{fieldPath}' has a non-numeric or negative indexer in '{seg}'.";
                return false;
            }

            result[i] = new PathSegment(name, index);
        }

        segments = result;
        error = null;
        return true;
    }

    /// <summary>
    /// Parsed segment of a compiled <c>FieldPath</c>. <see cref="Index"/> is
    /// populated only when the modder authored bracket notation on the
    /// terminal segment; non-terminal brackets are rejected by the parser.
    /// </summary>
    public readonly struct PathSegment
    {
        public PathSegment(string name, int? index)
        {
            Name = name;
            Index = index;
        }

        public string Name { get; }
        public int? Index { get; }
    }
}
