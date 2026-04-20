using System.Text;
using System.Text.Json;
using Jiangyu.Core.Models;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

public sealed class TemplateInspectionTextContext
{
    public required string TemplateType { get; init; }
    public required string TemplateId { get; init; }
    public TemplateIndex? TemplateIndex { get; init; }
    public IReadOnlyList<string> OdinOnlyFields { get; init; } = [];
    public string? PreviewManifestPath { get; init; }
}

public static class TemplateInspectionTextRenderer
{
    public static string Render(ObjectInspectionResult result, TemplateInspectionTextContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder();
        builder.AppendLine($"{context.TemplateType} {context.TemplateId}");
        builder.AppendLine($"  collection: {result.Object.Collection}");
        builder.AppendLine($"  pathId: {result.Object.PathId}");
        if (!string.IsNullOrWhiteSpace(context.PreviewManifestPath))
        {
            builder.AppendLine($"  preview: {context.PreviewManifestPath}");
        }

        if (context.OdinOnlyFields.Count > 0)
        {
            builder.AppendLine($"  odin-only fields: {string.Join(", ", context.OdinOnlyFields)}");
        }

        InspectedFieldNode? structure = result.Fields.FirstOrDefault(field => string.Equals(field.Name, "m_Structure", StringComparison.Ordinal));
        List<InspectedFieldNode> fields = structure?.Fields ?? result.Fields;

        builder.AppendLine("{");
        foreach (InspectedFieldNode field in fields)
        {
            AppendField(builder, field, context, 1, field.FieldTypeName);
        }
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendField(
        StringBuilder builder,
        InspectedFieldNode node,
        TemplateInspectionTextContext context,
        int indentLevel,
        string? expectedTypeName)
    {
        string indent = new(' ', indentLevel * 2);
        string label = node.Name ?? "<value>";

        if (string.Equals(node.Kind, "array", StringComparison.Ordinal))
        {
            IReadOnlyList<string> namedElements = TemplateFieldPathSugar.GetNamedArrayElementNames(context.TemplateType, label);
            if (namedElements.Count > 0)
            {
                builder.AppendLine($"{indent}{label}: {{");

                List<InspectedFieldNode> elements = node.Elements ?? [];
                for (int i = 0; i < Math.Min(namedElements.Count, elements.Count); i++)
                {
                    builder.AppendLine($"{indent}  {namedElements[i]}: {FormatInline(elements[i], context, GetElementTypeName(node.FieldTypeName))}");
                }

                if (node.Truncated == true)
                {
                    builder.AppendLine($"{indent}  <truncated count={node.Count}>");
                }

                builder.AppendLine($"{indent}}}");
                return;
            }

            builder.AppendLine($"{indent}{label}: [");
            foreach (InspectedFieldNode element in node.Elements ?? [])
            {
                if (string.Equals(element.Kind, "object", StringComparison.Ordinal) && element.Fields is { Count: > 0 })
                {
                    builder.AppendLine($"{indent}  {{");
                    foreach (InspectedFieldNode field in element.Fields)
                    {
                        AppendField(builder, field, context, indentLevel + 2, field.FieldTypeName);
                    }
                    builder.AppendLine($"{indent}  }}");
                }
                else
                {
                    builder.AppendLine($"{indent}  {FormatInline(element, context, GetElementTypeName(node.FieldTypeName))}");
                }
            }

            if (node.Truncated == true)
            {
                builder.AppendLine($"{indent}  <truncated count={node.Count}>");
            }

            builder.AppendLine($"{indent}]");
            return;
        }

        if (string.Equals(node.Kind, "object", StringComparison.Ordinal) && node.Fields is { Count: > 0 })
        {
            builder.AppendLine($"{indent}{label}: {{");
            foreach (InspectedFieldNode field in node.Fields)
            {
                AppendField(builder, field, context, indentLevel + 1, field.FieldTypeName);
            }
            builder.AppendLine($"{indent}}}");
            return;
        }

        builder.AppendLine($"{indent}{label}: {FormatInline(node, context, expectedTypeName)}");
    }

    private static string FormatInline(
        InspectedFieldNode node,
        TemplateInspectionTextContext context,
        string? expectedTypeName)
    {
        if (node.Null == true)
        {
            return "null";
        }

        if (string.Equals(node.Kind, "reference", StringComparison.Ordinal))
        {
            return FormatReference(node.Reference, expectedTypeName, context.TemplateIndex);
        }

        if (string.Equals(node.Kind, "string", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(node.Value?.ToString() ?? string.Empty);
        }

        if (string.Equals(node.Kind, "object", StringComparison.Ordinal) && node.Fields is { Count: > 0 })
        {
            return "{ … }";
        }

        if (string.Equals(node.Kind, "array", StringComparison.Ordinal))
        {
            return $"[{node.Count ?? node.Elements?.Count ?? 0}]";
        }

        if (node.Value is null)
        {
            return "null";
        }

        return node.Value switch
        {
            bool boolean => boolean ? "true" : "false",
            _ => node.Value.ToString() ?? "null",
        };
    }

    private static string FormatReference(
        InspectedReference? reference,
        string? expectedTypeName,
        TemplateIndex? index)
    {
        if (reference is null || (reference.FileId == 0 && reference.PathId == 0 && string.IsNullOrWhiteSpace(reference.Name)))
        {
            return "null";
        }

        string? displayType = null;
        if (reference.PathId is long pathId && index is not null)
        {
            TemplateInstanceEntry? entry = index.Instances.FirstOrDefault(instance => instance.Identity.PathId == pathId);
            displayType = entry?.ClassName;
        }

        displayType ??= reference.ClassName is not null
            && !string.Equals(reference.ClassName, "MonoBehaviour", StringComparison.Ordinal)
            && !string.Equals(reference.ClassName, "IObject", StringComparison.Ordinal)
            ? reference.ClassName
            : ShortTypeName(expectedTypeName);

        if (!string.IsNullOrWhiteSpace(reference.Name))
        {
            return $"{displayType}:{reference.Name}";
        }

        if (reference.PathId is long value)
        {
            return $"{displayType}[pathId={value}]";
        }

        return displayType ?? "reference";
    }

    private static string? ShortTypeName(string? fieldTypeName)
    {
        if (string.IsNullOrWhiteSpace(fieldTypeName))
        {
            return null;
        }

        string typeName = fieldTypeName.EndsWith("[]", StringComparison.Ordinal)
            ? fieldTypeName[..^2]
            : fieldTypeName;

        int lt = typeName.IndexOf('<');
        int gt = typeName.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            typeName = typeName[(lt + 1)..gt];
        }

        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
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
}
