using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

public sealed class TemplatePreviewResolvedReference
{
    public required string TemplateType { get; init; }
    public required string TemplateId { get; init; }
    public string? Collection { get; init; }
    public long? PathId { get; init; }
}

public static class TemplateInspectionPreviewApplier
{
    public static ObjectInspectionResult CloneForTarget(ObjectInspectionResult source, TemplatePreviewKey target)
    {
        ArgumentNullException.ThrowIfNull(source);

        ObjectInspectionResult cloned = DeepClone(source);
        SetRootName(cloned.Fields, target.TemplateId);

        return new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = target.TemplateId,
                ClassName = source.Object.ClassName,
                Collection = source.Object.Collection,
                PathId = source.Object.PathId,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = source.Options.MaxDepth,
                MaxArraySampleLength = source.Options.MaxArraySampleLength,
                Truncated = source.Options.Truncated,
            },
            Fields = cloned.Fields,
        };
    }

    public static void Apply(
        ObjectInspectionResult result,
        IReadOnlyList<CompiledTemplatePatch> patches,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(resolveReference);

        InspectedFieldNode structure = result.Fields.FirstOrDefault(field => string.Equals(field.Name, "m_Structure", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Template preview requires an m_Structure field.");

        PreviewVisitor visitor = new(resolveReference);

        foreach (CompiledTemplatePatch patch in patches)
        {
            foreach (CompiledTemplateSetOperation op in patch.Set)
            {
                OperationResult outcome = TemplateOperationWalker.Execute(
                    visitor,
                    structure,
                    TemplateOperationView.FromCompiled(op),
                    out string? error);

                if (outcome != OperationResult.Applied)
                {
                    throw new InvalidOperationException(
                        error ?? $"Template preview op '{op.Op}' on '{op.FieldPath}' failed ({outcome}).");
                }
            }
        }
    }

    public static ObjectInspectionResult DeepClone(ObjectInspectionResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ObjectInspectionResult
        {
            Object = new InspectedObjectIdentity
            {
                Name = source.Object.Name,
                ClassName = source.Object.ClassName,
                Collection = source.Object.Collection,
                PathId = source.Object.PathId,
            },
            Options = new ObjectInspectionOptions
            {
                MaxDepth = source.Options.MaxDepth,
                MaxArraySampleLength = source.Options.MaxArraySampleLength,
                Truncated = source.Options.Truncated,
            },
            Fields = [.. source.Fields.Select(CloneNode)],
        };
    }

    private static void SetRootName(List<InspectedFieldNode> fields, string templateId)
    {
        InspectedFieldNode? nameField = fields.FirstOrDefault(field => string.Equals(field.Name, "m_Name", StringComparison.Ordinal));
        if (nameField is not null)
        {
            nameField.Kind = "string";
            nameField.FieldTypeName ??= "String";
            nameField.Null = null;
            nameField.Value = templateId;
        }
    }

    private static InspectedFieldNode CloneNode(InspectedFieldNode source)
    {
        return new InspectedFieldNode
        {
            Name = source.Name,
            Kind = source.Kind,
            FieldTypeName = source.FieldTypeName,
            Null = source.Null,
            Truncated = source.Truncated,
            Value = source.Value,
            Count = source.Count,
            Reference = source.Reference is null
                ? null
                : new InspectedReference
                {
                    FileId = source.Reference.FileId,
                    PathId = source.Reference.PathId,
                    Name = source.Reference.Name,
                    ClassName = source.Reference.ClassName,
                },
            Elements = source.Elements?.Select(CloneNode).ToList(),
            Fields = source.Fields?.Select(CloneNode).ToList(),
            Reason = source.Reason,
        };
    }

    /// <summary>
    /// Adapter that lets <see cref="TemplateOperationWalker"/> mutate an
    /// <c>InspectedFieldNode</c> tree. Node identity is the tree node itself;
    /// reads walk <see cref="InspectedFieldNode.Fields"/>, writes rebuild the
    /// affected child node so kind/type tags round-trip through the same
    /// shape the inspector produces. Multi-dim cell writes are
    /// <see cref="OperationResult.NotSupported"/> because the preview tree
    /// doesn't model multi-dim arrays at the inspector level.
    /// </summary>
    private sealed class PreviewVisitor : IFieldVisitor<InspectedFieldNode>
    {
        private readonly Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> _resolveReference;

        public PreviewVisitor(Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
        {
            _resolveReference = resolveReference;
        }

        public OperationResult TryReadField(InspectedFieldNode parent, string fieldName, out InspectedFieldNode child, out string? error)
        {
            if (!string.Equals(parent.Kind, "object", StringComparison.Ordinal))
            {
                child = null!;
                error = $"Template preview expected an object while resolving '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            if (parent.Fields is null)
            {
                child = null!;
                error = $"Template preview cannot traverse truncated object '{parent.Name ?? parent.FieldTypeName ?? "object"}'. Increase --max-depth.";
                return OperationResult.MemberMissing;
            }

            InspectedFieldNode? found = parent.Fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
            if (found is null)
            {
                child = null!;
                error = $"Template preview field '{fieldName}' was not found.";
                return OperationResult.MemberMissing;
            }

            child = found;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryDescendElement(InspectedFieldNode parent, string fieldName, int index, out InspectedFieldNode descended, out string? error)
        {
            OperationResult readResult = TryReadField(parent, fieldName, out InspectedFieldNode field, out error);
            if (readResult != OperationResult.Applied)
            {
                descended = null!;
                return readResult;
            }

            if (!string.Equals(field.Kind, "array", StringComparison.Ordinal))
            {
                descended = null!;
                error = $"Template preview descent into '{fieldName}' expected an array.";
                return OperationResult.MemberMissing;
            }

            if (field.Elements is null)
            {
                descended = null!;
                error = $"Template preview cannot descend into truncated array '{fieldName}'. Increase --max-array-sample.";
                return OperationResult.MemberMissing;
            }

            if (index < 0 || index >= field.Elements.Count)
            {
                descended = null!;
                error = $"Template preview descent index {index} is out of range for '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            // Edit descent: navigate into the existing element. Inner ops apply
            // to its fields; the element keeps whatever concrete subtype the
            // inspector captured.
            descended = field.Elements[index];
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetScalar(InspectedFieldNode parent, string fieldName, CompiledTemplateValue value, out string? error)
        {
            if (parent.Fields is null)
            {
                error = "Template preview hit a truncated object.";
                return OperationResult.MemberMissing;
            }

            int idx = parent.Fields.FindIndex(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
            if (idx < 0)
            {
                error = $"Template preview field '{fieldName}' was not found.";
                return OperationResult.MemberMissing;
            }

            InspectedFieldNode existing = parent.Fields[idx];
            parent.Fields[idx] = BuildNode(existing.Name, existing.FieldTypeName, existing.Kind, value);
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetElement(InspectedFieldNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error)
        {
            OperationResult readResult = TryReadField(parent, fieldName, out InspectedFieldNode arrayField, out error);
            if (readResult != OperationResult.Applied)
                return readResult;

            if (arrayField.Elements is null)
            {
                error = $"Template preview cannot write into truncated array '{fieldName}'. Increase --max-array-sample.";
                return OperationResult.MemberMissing;
            }

            if (index < 0 || index >= arrayField.Elements.Count)
            {
                error = $"Template preview index {index} is out of range for '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            InspectedFieldNode existing = arrayField.Elements[index];
            arrayField.Elements[index] = BuildNode(
                existing.Name,
                existing.FieldTypeName ?? GetElementTypeName(arrayField.FieldTypeName),
                existing.Kind,
                value);
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetCell(InspectedFieldNode parent, string fieldName, IReadOnlyList<int> indexPath, CompiledTemplateValue value, out string? error)
        {
            error = $"Template preview does not render multi-dim cell writes on '{fieldName}'.";
            return OperationResult.NotSupported;
        }

        public OperationResult TryAppend(InspectedFieldNode parent, string fieldName, CompiledTemplateValue value, out string? error)
            => InsertIntoArray(parent, fieldName, index: null, value, out error);

        public OperationResult TryInsertAt(InspectedFieldNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error)
            => InsertIntoArray(parent, fieldName, index, value, out error);

        public OperationResult TryRemove(InspectedFieldNode parent, string fieldName, int? index, CompiledTemplateValue? value, out string? error)
        {
            if (!index.HasValue)
            {
                error = $"Template preview remove op for '{fieldName}' must target an indexed element.";
                return OperationResult.ConversionFailed;
            }

            OperationResult readResult = TryReadField(parent, fieldName, out InspectedFieldNode arrayField, out error);
            if (readResult != OperationResult.Applied)
                return readResult;

            if (arrayField.Elements is null)
            {
                error = $"Template preview cannot remove from truncated array '{fieldName}'. Increase --max-array-sample.";
                return OperationResult.MemberMissing;
            }

            if (index.Value < 0 || index.Value >= arrayField.Elements.Count)
            {
                error = $"Template preview remove index {index.Value} is out of range for '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            arrayField.Elements.RemoveAt(index.Value);
            arrayField.Count = arrayField.Elements.Count;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryClear(InspectedFieldNode parent, string fieldName, out string? error)
        {
            OperationResult readResult = TryReadField(parent, fieldName, out InspectedFieldNode arrayField, out error);
            if (readResult != OperationResult.Applied)
                return readResult;

            if (!string.Equals(arrayField.Kind, "array", StringComparison.Ordinal))
            {
                error = $"Template preview clear op for '{fieldName}' targets a non-array field.";
                return OperationResult.MemberMissing;
            }

            // Mirror the runtime applier's contract: Clear empties the
            // collection in place. The preview tree just drops every element
            // and zeroes Count.
            arrayField.Elements = [];
            arrayField.Count = 0;
            error = null;
            return OperationResult.Applied;
        }

        private OperationResult InsertIntoArray(InspectedFieldNode parent, string fieldName, int? index, CompiledTemplateValue value, out string? error)
        {
            OperationResult readResult = TryReadField(parent, fieldName, out InspectedFieldNode arrayField, out error);
            if (readResult != OperationResult.Applied)
                return readResult;

            if (!string.Equals(arrayField.Kind, "array", StringComparison.Ordinal))
            {
                error = $"Template preview field '{fieldName}' is not an array.";
                return OperationResult.MemberMissing;
            }

            arrayField.Elements ??= [];
            if (arrayField.Count.HasValue && arrayField.Count.Value > arrayField.Elements.Count)
            {
                error = $"Template preview cannot modify truncated array '{fieldName}'. Increase --max-array-sample.";
                return OperationResult.MemberMissing;
            }

            int insertIndex = index ?? arrayField.Elements.Count;
            if (insertIndex < 0 || insertIndex > arrayField.Elements.Count)
            {
                error = $"Template preview insert index {insertIndex} is out of range for '{fieldName}'.";
                return OperationResult.MemberMissing;
            }

            string? elementType = GetElementTypeName(arrayField.FieldTypeName);
            arrayField.Elements.Insert(
                insertIndex,
                BuildNode(null, elementType, InferKindFromFieldType(elementType), value));
            arrayField.Count = arrayField.Elements.Count;
            error = null;
            return OperationResult.Applied;
        }

        private InspectedFieldNode BuildNode(
            string? name,
            string? fieldTypeName,
            string? existingKind,
            CompiledTemplateValue value)
        {
            return value.Kind switch
            {
                CompiledTemplateValueKind.Boolean => new InspectedFieldNode
                {
                    Name = name,
                    Kind = existingKind ?? "bool",
                    FieldTypeName = fieldTypeName ?? "Boolean",
                    Value = value.Boolean ?? false,
                },
                CompiledTemplateValueKind.Byte => new InspectedFieldNode
                {
                    Name = name,
                    Kind = existingKind ?? "int",
                    FieldTypeName = fieldTypeName ?? "Byte",
                    Value = value.Byte ?? 0,
                },
                CompiledTemplateValueKind.Int32 => new InspectedFieldNode
                {
                    Name = name,
                    Kind = existingKind ?? "int",
                    FieldTypeName = fieldTypeName ?? "Int32",
                    Value = value.Int32 ?? 0,
                },
                CompiledTemplateValueKind.Single => new InspectedFieldNode
                {
                    Name = name,
                    Kind = existingKind ?? "float",
                    FieldTypeName = fieldTypeName ?? "Single",
                    Value = value.Single ?? 0f,
                },
                CompiledTemplateValueKind.String => new InspectedFieldNode
                {
                    Name = name,
                    Kind = existingKind ?? "string",
                    FieldTypeName = fieldTypeName ?? "String",
                    Null = value.String is null ? true : null,
                    Value = value.String,
                },
                CompiledTemplateValueKind.Enum => new InspectedFieldNode
                {
                    Name = name,
                    Kind = "enum",
                    FieldTypeName = fieldTypeName,
                    Value = value.EnumValue ?? throw new InvalidOperationException("Template preview enum value is missing."),
                },
                CompiledTemplateValueKind.TemplateReference => BuildReferenceNode(name, fieldTypeName, value.Reference),
                CompiledTemplateValueKind.Composite => BuildCompositeNode(name, fieldTypeName, value.Composite),
                // TypeConstruction shares Composite's field-bag shape but
                // names a freshly constructed ScriptableObject. Render it as
                // the constructed subtype so an indexed overwrite
                // (set "Field" index=N type="X") shows the new handler
                // rather than the element it replaced.
                CompiledTemplateValueKind.TypeConstruction => BuildCompositeNode(name, value.TypeConstruction?.TypeName ?? fieldTypeName, value.TypeConstruction),
                _ => throw new InvalidOperationException($"Unsupported template preview value kind '{value.Kind}'."),
            };
        }

        private InspectedFieldNode BuildReferenceNode(
            string? name,
            string? fieldTypeName,
            CompiledTemplateReference? reference)
        {
            if (reference is null)
            {
                throw new InvalidOperationException("Template preview reference value is missing.");
            }

            TemplatePreviewResolvedReference resolved = _resolveReference(reference)
                ?? throw new InvalidOperationException(
                    $"Template preview reference '{reference.TemplateType}:{reference.TemplateId}' was not found.");

            return new InspectedFieldNode
            {
                Name = name,
                Kind = "reference",
                FieldTypeName = fieldTypeName ?? resolved.TemplateType,
                Reference = new InspectedReference
                {
                    FileId = resolved.Collection is null ? null : 0,
                    PathId = resolved.PathId,
                    Name = resolved.TemplateId,
                    ClassName = resolved.TemplateType,
                },
            };
        }

        private InspectedFieldNode BuildCompositeNode(
            string? name,
            string? fieldTypeName,
            CompiledTemplateComposite? composite)
        {
            if (composite is null)
            {
                throw new InvalidOperationException("Template preview composite value is missing.");
            }

            // Preview only renders simple Set ops (one value per top-level field
            // of the constructed instance). Append/Insert/Remove/Clear ops on the
            // constructed instance's collection members can't be approximated
            // without a real list to mutate; the preview shows nothing for those
            // and the runtime applier handles them faithfully.
            var setOps = composite.Operations
                .Where(op => op.Op == CompiledTemplateOp.Set
                    && op.Value is not null
                    && !string.IsNullOrEmpty(op.FieldPath)
                    && !op.FieldPath.Contains('[')
                    && !op.FieldPath.Contains('.'));

            return new InspectedFieldNode
            {
                Name = name,
                Kind = "object",
                FieldTypeName = fieldTypeName ?? composite.TypeName,
                Fields =
                [
                    .. setOps.Select(op => BuildNode(op.FieldPath, null, null, op.Value!)),
                ],
            };
        }

        private static string? GetElementTypeName(string? fieldTypeName)
        {
            if (string.IsNullOrWhiteSpace(fieldTypeName))
            {
                return null;
            }

            if (fieldTypeName.EndsWith("[]", StringComparison.Ordinal))
            {
                return fieldTypeName[..^2];
            }

            int lt = fieldTypeName.IndexOf('<');
            int gt = fieldTypeName.LastIndexOf('>');
            if (lt >= 0 && gt > lt)
            {
                return fieldTypeName[(lt + 1)..gt];
            }

            return null;
        }

        private static string InferKindFromFieldType(string? fieldTypeName)
        {
            return fieldTypeName switch
            {
                "Boolean" or "System.Boolean" => "bool",
                "Single" or "System.Single" => "float",
                "String" or "System.String" => "string",
                _ => "reference",
            };
        }
    }
}
