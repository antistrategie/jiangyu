using System.Globalization;
using System.Text;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Serialises a <see cref="KdlEditorDocument"/> back to KDL text.
/// Inverse of <see cref="KdlTemplateParser.ParseText"/>.
/// </summary>
public static class KdlTemplateSerialiser
{
    public static string Serialise(KdlEditorDocument document)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var node in document.Nodes)
        {
            if (!first) sb.AppendLine();
            first = false;

            switch (node.Kind)
            {
                case KdlEditorNodeKind.Patch:
                    WritePatch(sb, node);
                    break;
                case KdlEditorNodeKind.Clone:
                    WriteClone(sb, node);
                    break;
            }
        }

        // Ensure trailing newline
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.AppendLine();

        return sb.ToString();
    }

    private static void WritePatch(StringBuilder sb, KdlEditorNode node)
    {
        sb.Append($"patch \"{Esc(node.TemplateType)}\" \"{Esc(node.TemplateId ?? "")}\"");

        if (node.Directives.Count == 0)
        {
            sb.AppendLine(" {}");
            return;
        }

        sb.AppendLine(" {");
        foreach (var d in node.Directives)
        {
            sb.Append("    ");
            WriteDirective(sb, d);
            sb.AppendLine();
        }
        sb.AppendLine("}");
    }

    private static void WriteClone(StringBuilder sb, KdlEditorNode node)
    {
        sb.Append($"clone \"{Esc(node.TemplateType)}\" from=\"{Esc(node.SourceId ?? "")}\" id=\"{Esc(node.CloneId ?? "")}\"");

        if (node.Directives.Count == 0)
        {
            sb.AppendLine();
            return;
        }

        sb.AppendLine(" {");
        foreach (var d in node.Directives)
        {
            sb.Append("    ");
            WriteDirective(sb, d);
            sb.AppendLine();
        }
        sb.AppendLine("}");
    }

    private static void WriteDirective(StringBuilder sb, KdlEditorDirective d)
    {
        var op = d.Op switch
        {
            KdlEditorOp.Set => "set",
            KdlEditorOp.Append => "append",
            KdlEditorOp.Insert => "insert",
            KdlEditorOp.Remove => "remove",
            KdlEditorOp.Clear => "clear",
            _ => "set",
        };

        sb.Append($"{op} \"{Esc(d.FieldPath)}\"");

        if ((d.Op == KdlEditorOp.Insert || d.Op == KdlEditorOp.Set) && d.Index != null)
            sb.Append(CultureInfo.InvariantCulture, $" index={d.Index.Value}");

        if (d.Op == KdlEditorOp.Remove)
        {
            if (d.Index != null)
                sb.Append(CultureInfo.InvariantCulture, $" index={d.Index.Value}");
            return;
        }

        // Clear has neither index nor value — the fieldPath alone is the
        // whole directive. Bail before the value branch.
        if (d.Op == KdlEditorOp.Clear)
            return;

        if (d.Value != null)
        {
            sb.Append(' ');
            WriteValue(sb, d.Value);
        }
    }

    private static void WriteValue(StringBuilder sb, KdlEditorValue v)
    {
        switch (v.Kind)
        {
            case KdlEditorValueKind.Boolean:
                sb.Append(v.Boolean == true ? "#true" : "#false");
                break;

            case KdlEditorValueKind.Byte:
            case KdlEditorValueKind.Int32:
                sb.Append((v.Int32 ?? 0).ToString(CultureInfo.InvariantCulture));
                break;

            case KdlEditorValueKind.Single:
                // KDL is locale-independent: invariant culture keeps '.' as the
                // decimal separator regardless of the host's regional settings.
                // Ensure a decimal point so the parser recognises it as float.
                var f = v.Single ?? 0f;
                var s = f.ToString("G", CultureInfo.InvariantCulture);
                sb.Append(s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0");
                break;

            case KdlEditorValueKind.String:
                sb.Append($"\"{Esc(v.String ?? "")}\"");
                break;

            case KdlEditorValueKind.Enum:
                sb.Append($"enum=\"{Esc(v.EnumType ?? "")}\" \"{Esc(v.EnumValue ?? "")}\"");
                break;

            case KdlEditorValueKind.TemplateReference:
                // The reference type is implicit on concrete fields — only
                // emit `ref="…"` when the modder explicitly chose a type
                // (polymorphic destination). Loader and validator derive the
                // type from the declared field otherwise.
                if (!string.IsNullOrEmpty(v.ReferenceType))
                    sb.Append($"ref=\"{Esc(v.ReferenceType)}\" \"{Esc(v.ReferenceId ?? "")}\"");
                else
                    sb.Append($"\"{Esc(v.ReferenceId ?? "")}\"");
                break;

            case KdlEditorValueKind.Composite:
                WriteComposite(sb, v);
                break;
        }
    }

    private static void WriteComposite(StringBuilder sb, KdlEditorValue v)
    {
        sb.Append($"composite=\"{Esc(v.CompositeType ?? "")}\"");

        if (v.CompositeFields == null || v.CompositeFields.Count == 0)
        {
            sb.Append(" {}");
            return;
        }

        sb.Append(" {");
        foreach (var (fieldName, fieldValue) in v.CompositeFields)
        {
            sb.AppendLine();
            sb.Append($"        set \"{Esc(fieldName)}\" ");
            WriteValue(sb, fieldValue);
        }
        sb.AppendLine();
        sb.Append("    }");
    }

    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
