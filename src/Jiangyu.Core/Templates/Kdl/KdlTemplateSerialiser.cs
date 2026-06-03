using System.Globalization;
using System.Text;
using Jiangyu.Shared.Templates;

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

            WriteLeadingComments(sb, node.LeadingComments, indent: 0);

            switch (node.Kind)
            {
                case KdlEditorNodeKind.Patch:
                    WritePatch(sb, node);
                    break;
                case KdlEditorNodeKind.Clone:
                case KdlEditorNodeKind.Create:
                    WriteClone(sb, node);
                    break;
            }
        }

        if (document.TrailingComments is { Count: > 0 })
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            sb.AppendLine();
            WriteLeadingComments(sb, document.TrailingComments, indent: 0);
        }

        // Ensure trailing newline
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Emit each stored comment as <c>// {text}</c> at the given indent.
    /// Empty entries serialise to a bare <c>//</c>. No-op for null or empty.
    /// </summary>
    private static void WriteLeadingComments(StringBuilder sb, List<string>? comments, int indent)
    {
        if (comments is null || comments.Count == 0) return;
        foreach (var comment in comments)
        {
            WriteIndent(sb, indent);
            sb.Append("//");
            if (comment.Length > 0)
            {
                sb.Append(' ');
                sb.Append(comment);
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// End the current line with an optional inline comment. <c>trailing</c>
    /// of null or empty just emits the newline. Used for same-line comments
    /// after node-opening braces and flat directive bodies.
    /// </summary>
    private static void EndLine(StringBuilder sb, string? trailing)
    {
        if (!string.IsNullOrEmpty(trailing))
        {
            sb.Append("  // ");
            sb.Append(trailing);
        }
        sb.AppendLine();
    }

    private static void WritePatch(StringBuilder sb, KdlEditorNode node)
    {
        sb.Append($"patch \"{Esc(node.TemplateType)}\" \"{Esc(node.TemplateId ?? "")}\"");

        if (node.Directives.Count == 0)
        {
            sb.Append(" {}");
            EndLine(sb, node.TrailingComment);
            return;
        }

        sb.Append(" {");
        EndLine(sb, node.TrailingComment);
        WriteDirectiveBlock(sb, node.Directives, indent: 1);
        sb.AppendLine("}");
    }

    private static void WriteClone(StringBuilder sb, KdlEditorNode node)
    {
        sb.Append(node.Kind == KdlEditorNodeKind.Create
            ? $"create \"{Esc(node.TemplateType)}\" id=\"{Esc(node.CloneId ?? "")}\""
            : $"clone \"{Esc(node.TemplateType)}\" from=\"{Esc(node.SourceId ?? "")}\" id=\"{Esc(node.CloneId ?? "")}\"");

        if (node.Directives.Count == 0)
        {
            EndLine(sb, node.TrailingComment);
            return;
        }

        sb.Append(" {");
        EndLine(sb, node.TrailingComment);
        WriteDirectiveBlock(sb, node.Directives, indent: 1);
        sb.AppendLine("}");
    }

    /// <summary>
    /// Emit a list of directives, grouping consecutive directives whose
    /// outermost <see cref="TemplateDescentStep"/> matches under a single
    /// <c>set "Field" index=N { ... }</c> block. A descent is an edit, so it
    /// carries no type=, and the concrete subtype is inferred at apply time.
    /// Order is preserved: non-descent directives and breaks in the descent run
    /// interleave at their original positions, and later runs sharing the same
    /// outer step still emit as separate blocks because reordering would change
    /// modder intent.
    /// </summary>
    private static void WriteDirectiveBlock(StringBuilder sb, IList<KdlEditorDirective> directives, int indent)
    {
        var i = 0;
        while (i < directives.Count)
        {
            var d = directives[i];
            var outerStep = d.Descent is { Count: > 0 } steps ? steps[0] : null;

            // Blank-line preservation: one blank before this directive if
            // source authored one (or more, collapsed to one). Sits above
            // any leading comments so the visual grouping reads the same.
            if (d.BlankLineBefore) sb.AppendLine();

            // Leading comments are attached to the first directive in a
            // descent run (the one that emits the wrapping `set "Field"
            // {...}`), since visually the comment was above that block.
            WriteLeadingComments(sb, d.LeadingComments, indent);

            if (outerStep != null)
            {
                // Gather a contiguous run that shares the same outer step.
                var group = new List<KdlEditorDirective> { PeelOuterStep(d) };
                while (i + 1 < directives.Count
                       && directives[i + 1].Descent is { Count: > 0 } nextSteps
                       && DescentStepsEqual(nextSteps[0], outerStep))
                {
                    i++;
                    group.Add(PeelOuterStep(directives[i]));
                }

                WriteIndent(sb, indent);
                sb.Append($"set \"{Esc(outerStep.Field)}\"");
                // Object-field descent (null index) emits a bare block;
                // collection-element descent carries index=N.
                if (outerStep.Index is int outerIndex)
                    sb.Append($" index={outerIndex.ToString(CultureInfo.InvariantCulture)}");
                sb.Append(" {");
                EndLine(sb, d.TrailingComment);
                WriteDirectiveBlock(sb, group, indent + 1);
                WriteIndent(sb, indent);
                sb.AppendLine("}");
            }
            else
            {
                WriteIndent(sb, indent);
                WriteFlatDirective(sb, d, indent);
            }

            i++;
        }
    }

    /// <summary>
    /// Return a clone of <paramref name="d"/> with the first
    /// <see cref="TemplateDescentStep"/> removed from its
    /// <see cref="KdlEditorDirective.Descent"/> list. Used while the
    /// serialiser walks an outer block and hands the inner residual back
    /// to the recursive emitter.
    /// </summary>
    private static KdlEditorDirective PeelOuterStep(KdlEditorDirective d)
    {
        List<TemplateDescentStep>? remaining = null;
        if (d.Descent is { Count: > 1 } steps)
        {
            remaining = new List<TemplateDescentStep>(steps.Count - 1);
            for (var i = 1; i < steps.Count; i++)
                remaining.Add(steps[i]);
        }
        return new KdlEditorDirective
        {
            Op = d.Op,
            FieldPath = d.FieldPath,
            Index = d.Index,
            IndexPath = d.IndexPath,
            Descent = remaining,
            Value = d.Value,
            Line = d.Line,
        };
    }

    private static bool DescentStepsEqual(TemplateDescentStep a, TemplateDescentStep b)
        => a.Field == b.Field && a.Index == b.Index;

    /// <summary>
    /// Emit a non-descent directive (Descent null/empty). Covers scalar set,
    /// append, insert, remove, clear, and the indexed-set form for collection
    /// elements (the index lives on <see cref="KdlEditorDirective.Index"/>).
    /// <paramref name="indent"/> is the directive's own indent level so any
    /// nested composite value emitted via <see cref="WriteValue"/> can indent
    /// its inner block relative to the current depth.
    /// </summary>
    private static void WriteFlatDirective(StringBuilder sb, KdlEditorDirective d, int indent)
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

        // Multi-dim cell address: set "Field" cell="r,c" <value>. Mutually
        // exclusive with index= at parse time; if both somehow appear, the
        // serialiser still emits both and the path validator rejects it.
        if (d.Op == KdlEditorOp.Set && d.IndexPath is { Count: > 0 } cellPath)
            sb.Append($" cell=\"{string.Join(",", cellPath)}\"");

        if (d.Op == KdlEditorOp.Remove)
        {
            // Remove takes either index= (List<T>) or a value (HashSet<T>).
            // Mutually exclusive at parse time; emit whichever the
            // directive carries.
            if (d.Index != null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" index={d.Index.Value}");
                EndLine(sb, d.TrailingComment);
            }
            else if (d.Value != null)
            {
                sb.Append(' ');
                WriteValue(sb, d.Value, indent, d.TrailingComment);
            }
            else
            {
                EndLine(sb, d.TrailingComment);
            }
            return;
        }

        if (d.Op == KdlEditorOp.Clear)
        {
            EndLine(sb, d.TrailingComment);
            return;
        }

        if (d.Value != null)
        {
            sb.Append(' ');
            WriteValue(sb, d.Value, indent, d.TrailingComment);
        }
        else
        {
            EndLine(sb, d.TrailingComment);
        }
    }

    private static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++)
            sb.Append("    ");
    }

    /// <summary>
    /// Emit a value and terminate its line. <paramref name="inlineComment"/>
    /// is placed on the value's "first line" — at end of line for scalars,
    /// after the opening <c>{</c> for composite/handler bags.
    /// </summary>
    private static void WriteValue(StringBuilder sb, KdlEditorValue v, int indent, string? inlineComment)
    {
        switch (v.Kind)
        {
            case KdlEditorValueKind.Boolean:
                sb.Append(v.Boolean == true ? "#true" : "#false");
                EndLine(sb, inlineComment);
                break;

            case KdlEditorValueKind.Byte:
            case KdlEditorValueKind.Int32:
                sb.Append((v.Int32 ?? 0).ToString(CultureInfo.InvariantCulture));
                EndLine(sb, inlineComment);
                break;

            case KdlEditorValueKind.Single:
                // KDL is locale-independent: invariant culture keeps '.' as the
                // decimal separator regardless of the host's regional settings.
                // Ensure a decimal point so the parser recognises it as float.
                var f = v.Single ?? 0f;
                var s = f.ToString("G", CultureInfo.InvariantCulture);
                sb.Append(s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0");
                EndLine(sb, inlineComment);
                break;

            case KdlEditorValueKind.String:
                WriteStringValue(sb, v.String ?? "", indent);
                EndLine(sb, inlineComment);
                break;

            case KdlEditorValueKind.Enum:
                sb.Append($"enum=\"{Esc(v.EnumType ?? "")}\" \"{Esc(v.EnumValue ?? "")}\"");
                EndLine(sb, inlineComment);
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
                EndLine(sb, inlineComment);
                break;

            // Both kinds serialise with type=, the single authoring keyword. A
            // named type emits type="X". An empty type (inferred inline value
            // for a monomorphic destination) emits a bare { } block.
            case KdlEditorValueKind.Composite:
            case KdlEditorValueKind.TypeConstruction:
                WriteFieldBag(sb, "type", v, indent, inlineComment);
                break;

            case KdlEditorValueKind.AssetReference:
                sb.Append($"asset=\"{Esc(v.AssetName ?? "")}\"");
                EndLine(sb, inlineComment);
                break;

            case KdlEditorValueKind.Null:
                sb.Append("#null");
                EndLine(sb, inlineComment);
                break;
        }
    }

    /// <summary>
    /// Emit a type= construction value bag. <paramref name="parentIndent"/>
    /// is the indent of the surrounding directive. Inner ops emit at
    /// <c>parentIndent + 1</c> and the closing brace lines up with the
    /// surrounding directive. Threaded through so nested constructions
    /// (Sound inside SoundBank, SoundVariation inside Sound) keep their
    /// indentation in lockstep with depth.
    /// </summary>
    private static void WriteFieldBag(StringBuilder sb, string keyword, KdlEditorValue v, int parentIndent, string? inlineComment)
    {
        // Omit type= when the type is empty so inference applies on re-parse.
        // The validator clears the type for monomorphic destinations in
        // editor-doc mode; compile-path docs always carry a concrete type.
        var hasType = !string.IsNullOrWhiteSpace(v.CompositeType);
        var hasFrom = !string.IsNullOrWhiteSpace(v.CompositeFrom);
        if (hasType)
            sb.Append($"{keyword}=\"{Esc(v.CompositeType!)}\"");
        if (hasFrom)
            sb.Append((hasType ? " " : "") + $"from=\"{Esc(v.CompositeFrom!)}\"");

        // Spacing before `{`: the caller (WriteFlatDirective) already
        // emitted a single space before WriteValue. When type or from
        // attributes preceded `{` here, the brace below adds the separating
        // space between attribute and brace. When the inferred-composite
        // path emitted nothing, the caller's space is the only one —
        // adding another would produce `set "F"  {` with a double space.
        var leadingSpace = hasType || hasFrom ? " " : string.Empty;

        if (v.CompositeDirectives == null || v.CompositeDirectives.Count == 0)
        {
            sb.Append($"{leadingSpace}{{}}");
            EndLine(sb, inlineComment);
            return;
        }

        sb.Append($"{leadingSpace}{{");
        EndLine(sb, inlineComment);
        WriteDirectiveBlock(sb, v.CompositeDirectives, parentIndent + 1);
        WriteIndent(sb, parentIndent);
        sb.AppendLine("}");
    }

    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// <summary>
    /// Emit a string value. Single-line strings use the standard
    /// <c>"text"</c> form with escapes; strings containing newlines are
    /// emitted as KDL v2 triple-quoted multi-line literals so the
    /// newlines survive a round-trip without being escaped into
    /// <c>\n</c> sequences (which KDL accepts but is unreadable for
    /// paragraph-length descriptions). The closing <c>"""</c>'s
    /// whitespace prefix sets the common indent stripped from every line
    /// on re-parse; we use <c>parentIndent + 1</c> levels of four-space
    /// indent so the literal lines up with the surrounding directive
    /// block.
    /// </summary>
    private static void WriteStringValue(StringBuilder sb, string value, int parentIndent)
    {
        // KDL v2 triple-quoted bodies cannot themselves contain a literal
        // run of three double-quotes. The fallback for that edge case is
        // the single-line escape form, which preserves newlines as \n.
        var hasNewline = value.Contains('\n') || value.Contains('\r');
        if (!hasNewline || value.Contains("\"\"\""))
        {
            sb.Append('"').Append(Esc(value)).Append('"');
            return;
        }

        var bodyIndent = new string(' ', (parentIndent + 1) * 4);
        sb.Append("\"\"\"");
        // Normalise line endings so the emitted file is LF-only; the
        // parser accepts both but mixed endings inside a literal are
        // brittle to round-trip.
        var normalised = value.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');
        foreach (var line in lines)
        {
            sb.AppendLine();
            // Empty lines emit just the prefix (or nothing — KDL accepts
            // either, and a bare empty line is less visually noisy).
            if (line.Length == 0)
                continue;
            // KDL v2 triple-quoted strings still interpret backslash
            // escapes, so a literal backslash in the body would round-
            // trip as its escape interpretation. Escape it to keep the
            // body verbatim.
            sb.Append(bodyIndent).Append(line.Replace("\\", "\\\\"));
        }
        sb.AppendLine();
        sb.Append(bodyIndent).Append("\"\"\"");
    }
}
