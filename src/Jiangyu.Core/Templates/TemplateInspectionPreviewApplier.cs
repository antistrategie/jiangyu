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

        foreach (CompiledTemplatePatch patch in patches)
        {
            foreach (CompiledTemplateSetOperation op in patch.Set)
            {
                ApplyOperation(structure, op, resolveReference);
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

    private static void ApplyOperation(
        InspectedFieldNode structure,
        CompiledTemplateSetOperation op,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        PathSegment[] segments = ParsePath(op.FieldPath);

        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Template preview path '{op.FieldPath}' is empty.");
        }

        Container container = TraverseToParent(structure, segments);
        PathSegment terminal = segments[^1];

        switch (op.Op)
        {
            case CompiledTemplateOp.Set:
                ApplySet(container, terminal, op.Value!, resolveReference);
                break;
            case CompiledTemplateOp.Append:
                ApplyAppend(container, terminal, op.Value!, resolveReference);
                break;
            case CompiledTemplateOp.InsertAt:
                ApplyInsertAt(container, terminal, op.Index!.Value, op.Value!, resolveReference);
                break;
            case CompiledTemplateOp.Remove:
                ApplyRemove(container, terminal);
                break;
            default:
                throw new InvalidOperationException($"Unsupported template preview op '{op.Op}'.");
        }
    }

    private static Container TraverseToParent(InspectedFieldNode structure, IReadOnlyList<PathSegment> segments)
    {
        Container current = new(structure);

        for (int i = 0; i < segments.Count - 1; i++)
        {
            PathSegment segment = segments[i];
            InspectedFieldNode field = GetObjectField(current.ObjectNode!, segment.Name);

            if (segment.Index is null)
            {
                if (!string.Equals(field.Kind, "object", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Template preview path segment '{segment.Name}' is not an object.");
                }

                current = new Container(field);
                continue;
            }

            if (!string.Equals(field.Kind, "array", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Template preview path segment '{segment.Name}' is not an array.");
            }

            List<InspectedFieldNode> elements = field.Elements
                ?? throw new InvalidOperationException(
                    $"Template preview cannot traverse truncated array '{segment.Name}'. Increase --max-array-sample.");

            if (segment.Index.Value < 0 || segment.Index.Value >= elements.Count)
            {
                throw new InvalidOperationException(
                    $"Template preview index {segment.Index.Value} is out of range for '{segment.Name}'.");
            }

            InspectedFieldNode element = elements[segment.Index.Value];
            if (!string.Equals(element.Kind, "object", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Template preview path '{segment.Name}[{segment.Index.Value}]' does not resolve to an object.");
            }

            current = new Container(element);
        }

        return current;
    }

    private static void ApplySet(
        Container container,
        PathSegment terminal,
        CompiledTemplateValue value,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        if (terminal.Index is null)
        {
            List<InspectedFieldNode> fields = container.ObjectNode!.Fields
                ?? throw new InvalidOperationException("Template preview hit a truncated object.");

            int index = fields.FindIndex(field => string.Equals(field.Name, terminal.Name, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException($"Template preview field '{terminal.Name}' was not found.");
            }

            InspectedFieldNode existing = fields[index];
            fields[index] = BuildNode(existing.Name, existing.FieldTypeName, existing.Kind, value, resolveReference);
            return;
        }

        InspectedFieldNode arrayField = GetObjectField(container.ObjectNode!, terminal.Name);
        List<InspectedFieldNode> elements = arrayField.Elements
            ?? throw new InvalidOperationException(
                $"Template preview cannot write into truncated array '{terminal.Name}'. Increase --max-array-sample.");

        if (terminal.Index.Value < 0 || terminal.Index.Value >= elements.Count)
        {
            throw new InvalidOperationException(
                $"Template preview index {terminal.Index.Value} is out of range for '{terminal.Name}'.");
        }

        InspectedFieldNode existingElement = elements[terminal.Index.Value];
        elements[terminal.Index.Value] = BuildNode(
            existingElement.Name,
            existingElement.FieldTypeName ?? GetElementTypeName(arrayField.FieldTypeName),
            existingElement.Kind,
            value,
            resolveReference);
    }

    private static void ApplyAppend(
        Container container,
        PathSegment terminal,
        CompiledTemplateValue value,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        InsertIntoArray(container, terminal, null, value, resolveReference);
    }

    private static void ApplyInsertAt(
        Container container,
        PathSegment terminal,
        int index,
        CompiledTemplateValue value,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        InsertIntoArray(container, terminal, index, value, resolveReference);
    }

    private static void InsertIntoArray(
        Container container,
        PathSegment terminal,
        int? index,
        CompiledTemplateValue value,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        if (terminal.Index is not null)
        {
            throw new InvalidOperationException(
                $"Template preview collection op '{terminal.Name}' must target the collection field, not an indexed element.");
        }

        InspectedFieldNode arrayField = GetObjectField(container.ObjectNode!, terminal.Name);
        if (!string.Equals(arrayField.Kind, "array", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Template preview field '{terminal.Name}' is not an array.");
        }

        arrayField.Elements ??= [];
        if (arrayField.Count.HasValue && arrayField.Count.Value > arrayField.Elements.Count)
        {
            throw new InvalidOperationException(
                $"Template preview cannot modify truncated array '{terminal.Name}'. Increase --max-array-sample.");
        }

        int insertIndex = index ?? arrayField.Elements.Count;
        if (insertIndex < 0 || insertIndex > arrayField.Elements.Count)
        {
            throw new InvalidOperationException(
                $"Template preview insert index {insertIndex} is out of range for '{terminal.Name}'.");
        }

        arrayField.Elements.Insert(
            insertIndex,
            BuildNode(
                null,
                GetElementTypeName(arrayField.FieldTypeName),
                InferKindFromFieldType(GetElementTypeName(arrayField.FieldTypeName)),
                value,
                resolveReference));
        arrayField.Count = arrayField.Elements.Count;
    }

    private static void ApplyRemove(Container container, PathSegment terminal)
    {
        if (terminal.Index is null)
        {
            throw new InvalidOperationException(
                $"Template preview remove op for '{terminal.Name}' must target an indexed element.");
        }

        InspectedFieldNode arrayField = GetObjectField(container.ObjectNode!, terminal.Name);
        List<InspectedFieldNode> elements = arrayField.Elements
            ?? throw new InvalidOperationException(
                $"Template preview cannot remove from truncated array '{terminal.Name}'. Increase --max-array-sample.");

        if (terminal.Index.Value < 0 || terminal.Index.Value >= elements.Count)
        {
            throw new InvalidOperationException(
                $"Template preview remove index {terminal.Index.Value} is out of range for '{terminal.Name}'.");
        }

        elements.RemoveAt(terminal.Index.Value);
        arrayField.Count = elements.Count;
    }

    private static InspectedFieldNode GetObjectField(InspectedFieldNode objectNode, string fieldName)
    {
        if (!string.Equals(objectNode.Kind, "object", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Template preview expected an object while resolving '{fieldName}'.");
        }

        List<InspectedFieldNode> fields = objectNode.Fields
            ?? throw new InvalidOperationException(
                $"Template preview cannot traverse truncated object '{objectNode.Name ?? objectNode.FieldTypeName ?? "object"}'. Increase --max-depth.");

        return fields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Template preview field '{fieldName}' was not found.");
    }

    private static InspectedFieldNode BuildNode(
        string? name,
        string? fieldTypeName,
        string? existingKind,
        CompiledTemplateValue value,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
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
                Value = value.Byte ?? (byte)0,
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
            CompiledTemplateValueKind.TemplateReference => BuildReferenceNode(name, fieldTypeName, value.Reference, resolveReference),
            CompiledTemplateValueKind.Composite => BuildCompositeNode(name, fieldTypeName, value.Composite, resolveReference),
            _ => throw new InvalidOperationException($"Unsupported template preview value kind '{value.Kind}'."),
        };
    }

    private static InspectedFieldNode BuildReferenceNode(
        string? name,
        string? fieldTypeName,
        CompiledTemplateReference? reference,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        if (reference is null)
        {
            throw new InvalidOperationException("Template preview reference value is missing.");
        }

        TemplatePreviewResolvedReference resolved = resolveReference(reference)
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

    private static InspectedFieldNode BuildCompositeNode(
        string? name,
        string? fieldTypeName,
        CompiledTemplateComposite? composite,
        Func<CompiledTemplateReference, TemplatePreviewResolvedReference?> resolveReference)
    {
        if (composite is null)
        {
            throw new InvalidOperationException("Template preview composite value is missing.");
        }

        return new InspectedFieldNode
        {
            Name = name,
            Kind = "object",
            FieldTypeName = fieldTypeName ?? composite.TypeName,
            Fields =
            [
                .. composite.Fields.Select(entry => BuildNode(entry.Key, null, null, entry.Value, resolveReference)),
            ],
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

    private static PathSegment[] ParsePath(string fieldPath)
    {
        string[] rawSegments = fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var segments = new PathSegment[rawSegments.Length];

        for (int i = 0; i < rawSegments.Length; i++)
        {
            string raw = rawSegments[i];
            int bracket = raw.IndexOf('[');
            if (bracket < 0)
            {
                segments[i] = new PathSegment(raw, null);
                continue;
            }

            string name = raw[..bracket];
            string indexText = raw.Substring(bracket + 1, raw.Length - bracket - 2);
            if (!int.TryParse(indexText, out int index))
            {
                throw new InvalidOperationException($"Template preview path '{fieldPath}' has an invalid indexer.");
            }

            segments[i] = new PathSegment(name, index);
        }

        return segments;
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

    private sealed record PathSegment(string Name, int? Index);

    private readonly record struct Container(InspectedFieldNode ObjectNode);
}
