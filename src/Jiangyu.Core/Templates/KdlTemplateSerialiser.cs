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
    /// Emit a list of directives, grouping consecutive directives whose
    /// outermost <see cref="TemplateDescentStep"/> matches under a single
    /// <c>set "Field" index=N type="X" { ... }</c> block. Order is preserved —
    /// non-descent directives and breaks in the descent run interleave at
    /// their original positions; later runs sharing the same outer step still
    /// emit as separate blocks because reordering would change modder intent.
    /// </summary>
    private static void WriteDirectiveBlock(StringBuilder sb, IList<KdlEditorDirective> directives, int indent)
    {
        var i = 0;
        while (i < directives.Count)
        {
            var d = directives[i];
            var outerStep = d.Descent is { Count: > 0 } steps ? steps[0] : null;

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
                if (outerStep.Index.HasValue)
                    sb.Append($" index={outerStep.Index.Value.ToString(CultureInfo.InvariantCulture)}");
                if (!string.IsNullOrEmpty(outerStep.Subtype))
                    sb.Append($" type=\"{Esc(outerStep.Subtype)}\"");
                sb.AppendLine(" {");
                WriteDirectiveBlock(sb, group, indent + 1);
                WriteIndent(sb, indent);
                sb.AppendLine("}");
            }
            else
            {
                WriteIndent(sb, indent);
                WriteFlatDirective(sb, d, indent);
                sb.AppendLine();
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
        => a.Field == b.Field && a.Index == b.Index && a.Subtype == b.Subtype;

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
                sb.Append(CultureInfo.InvariantCulture, $" index={d.Index.Value}");
            else if (d.Value != null)
            {
                sb.Append(' ');
                WriteValue(sb, d.Value, indent);
            }
            return;
        }

        if (d.Op == KdlEditorOp.Clear)
            return;

        if (d.Value != null)
        {
            sb.Append(' ');
            WriteValue(sb, d.Value, indent);
        }
    }

    private static void WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++)
            sb.Append("    ");
    }

    private static void WriteValue(StringBuilder sb, KdlEditorValue v, int indent)
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
                WriteFieldBag(sb, "composite", v, indent);
                break;

            case KdlEditorValueKind.HandlerConstruction:
                WriteFieldBag(sb, "handler", v, indent);
                break;

            case KdlEditorValueKind.AssetReference:
                sb.Append($"asset=\"{Esc(v.AssetName ?? "")}\"");
                break;

            case KdlEditorValueKind.Null:
                sb.Append("#null");
                break;
        }
    }

    /// <summary>
    /// Emit a composite or handler value bag. <paramref name="parentIndent"/>
    /// is the indent of the surrounding directive; inner ops emit at
    /// <c>parentIndent + 1</c> and the closing brace lines up with the
    /// surrounding directive. Threaded through so nested composites
    /// (Sound inside SoundBank, SoundVariation inside Sound) keep their
    /// indentation in lockstep with depth.
    /// </summary>
    private static void WriteFieldBag(StringBuilder sb, string keyword, KdlEditorValue v, int parentIndent)
    {
        // Omit composite=/handler= when the type is empty so inference applies
        // on re-parse. The validator clears the type for monomorphic
        // destinations in editor-doc mode; compile-path docs always carry a
        // concrete type.
        var hasType = !string.IsNullOrWhiteSpace(v.CompositeType);
        var hasFrom = !string.IsNullOrWhiteSpace(v.CompositeFrom);
        if (hasType)
            sb.Append($"{keyword}=\"{Esc(v.CompositeType!)}\"");
        if (hasFrom)
            sb.Append((hasType ? " " : "") + $"from=\"{Esc(v.CompositeFrom!)}\"");

        if (v.CompositeDirectives == null || v.CompositeDirectives.Count == 0)
        {
            sb.Append(" {}");
            return;
        }

        sb.AppendLine(" {");
        WriteDirectiveBlock(sb, v.CompositeDirectives, parentIndent + 1);
        WriteIndent(sb, parentIndent);
        sb.Append('}');
    }

    private static string Esc(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
