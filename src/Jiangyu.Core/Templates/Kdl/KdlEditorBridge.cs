using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Round-trip bridge between <see cref="KdlEditorDocument"/> (the
/// authoring-side model the Studio editor mutates) and the compiled
/// <see cref="CompiledTemplatePatch"/> family (the validator's input).
/// Split out from <c>KdlTemplateParser</c>: the parser handles text →
/// editor document, this bridge handles editor ↔ compiled, and the
/// serialiser handles editor document → text.
/// </summary>
public static class KdlEditorBridge
{
    public static CompiledTemplateSetOperation EditorDirectiveToCompiled(KdlEditorDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        return new CompiledTemplateSetOperation
        {
            Op = directive.Op switch
            {
                KdlEditorOp.Set => CompiledTemplateOp.Set,
                KdlEditorOp.Append => CompiledTemplateOp.Append,
                KdlEditorOp.Insert => CompiledTemplateOp.InsertAt,
                KdlEditorOp.Remove => CompiledTemplateOp.Remove,
                KdlEditorOp.Clear => CompiledTemplateOp.Clear,
                _ => CompiledTemplateOp.Set,
            },
            FieldPath = directive.FieldPath,
            Index = directive.Index,
            IndexPath = directive.IndexPath != null ? new List<int>(directive.IndexPath) : null,
            Descent = directive.Descent != null ? CloneDescent(directive.Descent) : null,
            Value = directive.Value != null ? EditorValueToCompiled(directive.Value) : null,
        };
    }

    public static KdlEditorDirective CompiledOpToEditorDirective(CompiledTemplateSetOperation op)
        => CompiledOpToEditor(op);

    private static List<TemplateDescentStep> CloneDescent(List<TemplateDescentStep> source)
    {
        var copy = new List<TemplateDescentStep>(source.Count);
        foreach (var step in source)
            copy.Add(new TemplateDescentStep { Field = step.Field, Index = step.Index, Subtype = step.Subtype });
        return copy;
    }

    internal static KdlEditorNode CompiledPatchToEditor(CompiledTemplatePatch patch)
    {
        var node = new KdlEditorNode
        {
            Kind = KdlEditorNodeKind.Patch,
            TemplateType = patch.TemplateType ?? string.Empty,
            TemplateId = patch.TemplateId,
        };
        foreach (var op in patch.Set)
            node.Directives.Add(CompiledOpToEditor(op));
        return node;
    }

    private static KdlEditorDirective CompiledOpToEditor(CompiledTemplateSetOperation op)
    {
        return new KdlEditorDirective
        {
            Op = op.Op switch
            {
                CompiledTemplateOp.Set => KdlEditorOp.Set,
                CompiledTemplateOp.Append => KdlEditorOp.Append,
                CompiledTemplateOp.InsertAt => KdlEditorOp.Insert,
                CompiledTemplateOp.Remove => KdlEditorOp.Remove,
                CompiledTemplateOp.Clear => KdlEditorOp.Clear,
                _ => KdlEditorOp.Set,
            },
            FieldPath = op.FieldPath,
            Index = op.Index,
            IndexPath = op.IndexPath != null ? new List<int>(op.IndexPath) : null,
            Descent = op.Descent != null ? CloneDescent(op.Descent) : null,
            Value = op.Value != null ? CompiledValueToEditor(op.Value) : null,
            // SourceLine is the parser-stamped source position; the comment
            // attribution pass uses this on every directive including those
            // nested inside composite/handler bodies. Null on directives
            // produced outside the parser (compile-side or hand-built tests).
            Line = op.SourceLine,
        };
    }

    private static CompiledTemplateValue EditorValueToCompiled(KdlEditorValue value)
    {
        return value.Kind switch
        {
            KdlEditorValueKind.Boolean => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Boolean,
                Boolean = value.Boolean,
            },
            KdlEditorValueKind.Byte => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Byte,
                Int32 = value.Int32,
                Byte = value.Int32 is >= byte.MinValue and <= byte.MaxValue
                    ? (byte)value.Int32.Value
                    : null,
            },
            KdlEditorValueKind.Int32 => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = value.Int32,
            },
            KdlEditorValueKind.Single => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Single,
                Single = value.Single,
            },
            KdlEditorValueKind.String => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.String,
                String = value.String,
            },
            KdlEditorValueKind.Enum => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Enum,
                EnumType = value.EnumType,
                EnumValue = value.EnumValue,
            },
            KdlEditorValueKind.TemplateReference => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.TemplateReference,
                Reference = new CompiledTemplateReference
                {
                    TemplateType = value.ReferenceType,
                    TemplateId = value.ReferenceId ?? string.Empty,
                },
            },
            KdlEditorValueKind.Composite => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Composite,
                Composite = new CompiledTemplateComposite
                {
                    TypeName = value.CompositeType ?? string.Empty,
                    Operations = value.CompositeDirectives?
                        .Select(EditorDirectiveToCompiled).ToList() ?? [],
                    From = string.IsNullOrWhiteSpace(value.CompositeFrom) ? null : value.CompositeFrom,
                },
            },
            KdlEditorValueKind.HandlerConstruction => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.HandlerConstruction,
                HandlerConstruction = new CompiledTemplateComposite
                {
                    TypeName = value.CompositeType ?? string.Empty,
                    Operations = value.CompositeDirectives?
                        .Select(EditorDirectiveToCompiled).ToList() ?? [],
                },
            },
            KdlEditorValueKind.AssetReference => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.AssetReference,
                Asset = new CompiledAssetReference
                {
                    Name = value.AssetName ?? string.Empty,
                },
            },
            KdlEditorValueKind.Null => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Null,
            },
            _ => new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.String,
                String = string.Empty,
            },
        };
    }

    private static KdlEditorValue CompiledValueToEditor(CompiledTemplateValue v)
    {
        return v.Kind switch
        {
            CompiledTemplateValueKind.Boolean => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Boolean,
                Boolean = v.Boolean,
            },
            CompiledTemplateValueKind.Byte => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Byte,
                Int32 = v.Byte,
            },
            CompiledTemplateValueKind.Int32 => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Int32,
                Int32 = v.Int32,
            },
            CompiledTemplateValueKind.Single => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Single,
                Single = v.Single,
            },
            CompiledTemplateValueKind.String => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.String,
                String = v.String,
            },
            CompiledTemplateValueKind.Enum => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Enum,
                EnumType = v.EnumType,
                EnumValue = v.EnumValue,
            },
            CompiledTemplateValueKind.TemplateReference => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.TemplateReference,
                ReferenceType = v.Reference?.TemplateType,
                ReferenceId = v.Reference?.TemplateId,
            },
            CompiledTemplateValueKind.Composite => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Composite,
                // For tagged-string composites the validator rewrote TypeName
                // to the resolved CLR full name and stashed the modder's
                // original discriminator on TaggedDiscriminator. Round-trip
                // emits the discriminator (what the modder wrote) so the
                // round-tripped KDL matches the input. Non-tagged composites
                // fall back to TypeName as before.
                CompositeType = v.Composite is { TaggedDiscriminator: { Length: > 0 } d }
                    ? d
                    : v.Composite?.TypeName,
                CompositeFrom = v.Composite?.From,
                CompositeDirectives = v.Composite?.Operations
                    .Select(CompiledOpToEditor).ToList(),
            },
            CompiledTemplateValueKind.HandlerConstruction => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.HandlerConstruction,
                CompositeType = v.HandlerConstruction?.TypeName,
                CompositeDirectives = v.HandlerConstruction?.Operations
                    .Select(CompiledOpToEditor).ToList(),
            },
            CompiledTemplateValueKind.AssetReference => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.AssetReference,
                AssetName = v.Asset?.Name,
            },
            CompiledTemplateValueKind.Null => new KdlEditorValue
            {
                Kind = KdlEditorValueKind.Null,
            },
            _ => new KdlEditorValue { Kind = KdlEditorValueKind.String, String = "" },
        };
    }
}
