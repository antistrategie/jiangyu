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
        WriteDirectiveBlock(sb, node.Directives, indent: 1);
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
        WriteDirectiveBlock(sb, node.Directives, indent: 1);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Emit a list of directives, grouping consecutive same-prefix descent
    /// ops under a single outer <c>set "Field" index=N type="X" { ... }</c>
    /// block. Order is preserved — non-descent and differently-prefixed
    /// descent directives interleave at the original positions.
    /// </summary>
    private static void WriteDirectiveBlock(StringBuilder sb, IList<KdlEditorDirective> directives, int indent)
    {
        var i = 0;
        while (i < directives.Count)
        {
            var d = directives[i];
            if (TryPeelDescentSegment(d, out var prefix, out var index, out var hint, out var residual))
            {
                // Gather a run of consecutive directives that share the same
                // outer (prefix, index, hint) so they all fold into one block.
                // A break in the run flushes the current group; later runs
                // with the same outer key still emit as separate blocks (we
                // never reorder — modder ordering is intentional).
                var group = new List<KdlEditorDirective> { residual };
                while (i + 1 < directives.Count
                       && TryPeelDescentSegment(directives[i + 1], out var p2, out var i2, out var h2, out var r2)
                       && p2 == prefix
                       && i2 == index
                       && h2 == hint)
                {
                    i++;
                    group.Add(r2);
                }

                WriteIndent(sb, indent);
                sb.Append($"set \"{Esc(prefix)}\" index={index!.Value.ToString(CultureInfo.InvariantCulture)}");
                if (!string.IsNullOrEmpty(hint))
                    sb.Append($" type=\"{Esc(hint)}\"");
                sb.AppendLine(" {");
                WriteDirectiveBlock(sb, group, indent + 1);
                WriteIndent(sb, indent);
                sb.AppendLine("}");
            }
            else if (TryPeelTerminalIndexer(d, out var terminalField, out var terminalIndex))
            {
                // Path ends in [N] with nothing after — equivalent to a
                // top-level set with index= property. Rewrite to the
                // canonical KDL form so emission and parser stay in lockstep.
                WriteIndent(sb, indent);
                sb.Append($"set \"{Esc(terminalField)}\" index={terminalIndex.ToString(CultureInfo.InvariantCulture)}");
                if (d.Value != null)
                {
                    sb.Append(' ');
                    WriteValue(sb, d.Value);
                }
                sb.AppendLine();
            }
            else
            {
                WriteIndent(sb, indent);
                WriteFlatDirective(sb, d);
                sb.AppendLine();
            }

            i++;
        }
    }

    /// <summary>
    /// True when the directive's path begins with <c>Field[N].rest</c> — i.e.
    /// has a leading indexer with at least one further segment. Returns the
    /// peeled <paramref name="prefix"/> and <paramref name="index"/>, the
    /// outer <paramref name="hint"/> from <c>SubtypeHints[0]</c> if any, and
    /// a <paramref name="residual"/> directive whose <c>FieldPath</c> is the
    /// remainder, with <c>SubtypeHints</c> shifted down by one segment.
    /// </summary>
    private static bool TryPeelDescentSegment(
        KdlEditorDirective d,
        out string prefix,
        out int? index,
        out string? hint,
        out KdlEditorDirective residual)
    {
        prefix = string.Empty;
        index = null;
        hint = null;
        residual = null!;

        var path = d.FieldPath;
        var bracketOpen = path.IndexOf('[');
        if (bracketOpen <= 0) return false;
        var bracketClose = path.IndexOf(']', bracketOpen + 1);
        if (bracketClose <= bracketOpen) return false;

        // Need a continuation segment after the indexer; bare Field[N] is the
        // terminal-indexer case and is handled separately so we can rewrite
        // it as the canonical index= property form.
        if (bracketClose == path.Length - 1) return false;
        if (path[bracketClose + 1] != '.') return false;

        var indexText = path[(bracketOpen + 1)..bracketClose];
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
            return false;

        prefix = path[..bracketOpen];
        index = parsedIndex;

        if (d.SubtypeHints != null && d.SubtypeHints.TryGetValue(0, out var outerHint))
            hint = outerHint;

        var residualPath = path[(bracketClose + 2)..];
        Dictionary<int, string>? residualHints = null;
        if (d.SubtypeHints != null)
        {
            residualHints = new Dictionary<int, string>();
            foreach (var (k, v) in d.SubtypeHints)
            {
                if (k > 0) residualHints[k - 1] = v;
            }
            if (residualHints.Count == 0) residualHints = null;
        }

        residual = new KdlEditorDirective
        {
            Op = d.Op,
            FieldPath = residualPath,
            Index = d.Index,
            SubtypeHints = residualHints,
            Value = d.Value,
            Line = d.Line,
        };
        return true;
    }

    /// <summary>
    /// True when the directive's path is a bare <c>Field[N]</c> with no
    /// descent — needs to be rewritten as <c>set "Field" index=N value</c>.
    /// </summary>
    private static bool TryPeelTerminalIndexer(
        KdlEditorDirective d,
        out string field,
        out int index)
    {
        field = string.Empty;
        index = 0;

        // Only Set ops can carry a terminal-indexer rewrite — append/insert/
        // remove/clear all use the index= property at parse time and never
        // produce paths with [N] at the end.
        if (d.Op != KdlEditorOp.Set) return false;

        var path = d.FieldPath;
        var bracketOpen = path.IndexOf('[');
        if (bracketOpen <= 0) return false;
        var bracketClose = path.IndexOf(']', bracketOpen + 1);
        if (bracketClose != path.Length - 1) return false;

        var indexText = path[(bracketOpen + 1)..bracketClose];
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedIndex))
            return false;

        field = path[..bracketOpen];
        index = parsedIndex;
        return true;
    }

    /// <summary>
    /// Emit a directive whose <c>FieldPath</c> contains no bracket indexers.
    /// This is the simple case: scalar set, append, insert, remove, clear.
    /// </summary>
    private static void WriteFlatDirective(StringBuilder sb, KdlEditorDirective d)
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

        if (d.Op == KdlEditorOp.Clear)
            return;

        if (d.Value != null)
        {
            sb.Append(' ');
            WriteValue(sb, d.Value);
        }
    }

    private static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++)
            sb.Append("    ");
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
                WriteFieldBag(sb, "composite", v);
                break;

            case KdlEditorValueKind.HandlerConstruction:
                WriteFieldBag(sb, "handler", v);
                break;
        }
    }

    private static void WriteFieldBag(StringBuilder sb, string keyword, KdlEditorValue v)
    {
        sb.Append($"{keyword}=\"{Esc(v.CompositeType ?? "")}\"");

        if (v.CompositeDirectives == null || v.CompositeDirectives.Count == 0)
        {
            sb.Append(" {}");
            return;
        }

        sb.AppendLine(" {");
        // Reuse the outer directive emission so inner ops render the same
        // way as their top-level counterparts. Indent two levels deeper
        // than the surrounding directive's indent — the parent directive
        // already sits at indent 1 (inside a patch/clone), and its child
        // block opens at indent 2.
        WriteDirectiveBlock(sb, v.CompositeDirectives, indent: 2);
        sb.Append("    }");
    }

    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
