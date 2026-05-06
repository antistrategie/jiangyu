using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Templates;
using KdlSharp;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Parses KDL template authoring files (<c>templates/**/*.kdl</c>) into the
/// <see cref="CompiledTemplatePatch"/> and <see cref="CompiledTemplateClone"/>
/// models consumed by the existing <see cref="Compile.TemplatePatchEmitter"/>
/// pipeline.
/// </summary>
public static class KdlTemplateParser
{
    public readonly record struct ParseResult(
        List<CompiledTemplatePatch> Patches,
        List<CompiledTemplateClone> Clones,
        int ErrorCount);

    /// <summary>
    /// Parse all KDL files discovered under the given root directory (recursive).
    /// Enforces per-file (type, id) uniqueness and cross-file uniqueness within
    /// the mod.
    /// </summary>
    public static ParseResult ParseAll(string templatesDir, ILogSink log)
    {
        var patches = new List<CompiledTemplatePatch>();
        var clones = new List<CompiledTemplateClone>();
        var errorCount = 0;

        var kdlFiles = Directory.EnumerateFiles(templatesDir, "*.kdl", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (kdlFiles.Count == 0)
            return new ParseResult(patches, clones, 0);

        // Cross-file uniqueness: (type, id) → source file
        var patchSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        var cloneSeen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in kdlFiles)
        {
            var relativePath = Path.GetRelativePath(templatesDir, file);
            var fileResult = ParseFile(file, relativePath, log);
            errorCount += fileResult.ErrorCount;

            // Check cross-file uniqueness for patches
            foreach (var patch in fileResult.Patches)
            {
                var key = (patch.TemplateType ?? "EntityTemplate") + "\0" + patch.TemplateId;
                if (patchSeen.TryGetValue(key, out var existingFile))
                {
                    log.Error(
                        $"KDL: duplicate patch target '{patch.TemplateType}:{patch.TemplateId}' "
                        + $"in '{relativePath}' — already defined in '{existingFile}'.");
                    errorCount++;
                }
                else
                {
                    patchSeen[key] = relativePath;
                    patches.Add(patch);
                }
            }

            // Check cross-file uniqueness for clones
            foreach (var clone in fileResult.Clones)
            {
                var key = (clone.TemplateType ?? "") + "\0" + clone.CloneId;
                if (cloneSeen.TryGetValue(key, out var existingFile))
                {
                    log.Error(
                        $"KDL: duplicate clone id '{clone.TemplateType}:{clone.CloneId}' "
                        + $"in '{relativePath}' — already defined in '{existingFile}'.");
                    errorCount++;
                }
                else
                {
                    cloneSeen[key] = relativePath;
                    clones.Add(clone);
                }
            }
        }

        return new ParseResult(patches, clones, errorCount);
    }

    /// <summary>
    /// Parse KDL text into an editor-facing document that preserves node order
    /// and clone inline directives. Used by the Studio visual editor.
    /// </summary>
    public static KdlEditorDocument ParseText(string text)
    {
        var doc = new KdlEditorDocument();

        KdlDocument kdl;
        try
        {
            kdl = KdlDocument.Parse(text);
        }
        catch (Exception)
        {
            // KDL library throws on the first syntax error and stops.
            // Recover by splitting into top-level blocks and parsing each
            // independently so we can report errors across all blocks.
            return ParseTextWithRecovery(text);
        }

        ProcessKdlNodes(kdl, doc, lineOffset: 0);
        return doc;
    }

    /// <summary>
    /// Split raw text into top-level blocks (one per patch/clone node) and parse
    /// each independently. This lets us report syntax errors from every block
    /// instead of stopping at the first one.
    /// </summary>
    private static KdlEditorDocument ParseTextWithRecovery(string text)
    {
        var doc = new KdlEditorDocument();

        foreach (var (blockText, startLine) in SplitTopLevelBlocks(text))
        {
            var trimmed = blockText.Trim();
            if (trimmed.Length == 0) continue;

            KdlDocument kdl;
            try
            {
                kdl = KdlDocument.Parse(trimmed);
            }
            catch (Exception ex)
            {
                var localLine = ExtractLineFromMessage(ex.Message);
                var absoluteLine = localLine.HasValue ? localLine.Value + startLine - 1 : startLine;
                doc.Errors.Add(new KdlEditorError { Message = ex.Message, Line = absoluteLine });
                continue;
            }

            ProcessKdlNodes(kdl, doc, lineOffset: startLine - 1);
        }

        return doc;
    }

    /// <summary>
    /// Process parsed KDL nodes into editor nodes/errors, adjusting line numbers
    /// by <paramref name="lineOffset"/> for block-recovery mode.
    /// </summary>
    private static void ProcessKdlNodes(KdlDocument kdl, KdlEditorDocument doc, int lineOffset)
    {
        var errors = new List<string>();
        var log = new ListLogSink(errors);

        foreach (var node in kdl.Nodes)
        {
            var pos = FormatPos("text", node.SourcePosition);
            var localLine = node.SourcePosition?.Line;
            var line = localLine.HasValue ? localLine.Value + lineOffset : (int?)null;

            switch (node.Name)
            {
                case "patch":
                    if (TryParsePatchNode(node, pos, log, out var patch))
                    {
                        var patchNode = CompiledPatchToEditor(patch);
                        patchNode.Line = line;
                        StampDirectiveLines(patchNode.Directives, node.Children, lineOffset);
                        doc.Nodes.Add(patchNode);
                    }
                    else
                    {
                        FlushErrors(doc, errors, line);
                    }
                    break;

                case "clone":
                    if (TryParseCloneNode(node, pos, log, out var clone, out var clonePatches))
                    {
                        var editorNode = new KdlEditorNode
                        {
                            Kind = KdlEditorNodeKind.Clone,
                            TemplateType = clone.TemplateType ?? string.Empty,
                            SourceId = clone.SourceId,
                            CloneId = clone.CloneId,
                            Line = line,
                        };
                        if (clonePatches != null)
                        {
                            foreach (var op in clonePatches.Set)
                                editorNode.Directives.Add(CompiledOpToEditor(op));
                            StampDirectiveLines(editorNode.Directives, node.Children, lineOffset);
                        }
                        doc.Nodes.Add(editorNode);
                    }
                    else
                    {
                        FlushErrors(doc, errors, line);
                    }
                    break;

                default:
                    doc.Errors.Add(new KdlEditorError
                    {
                        Message = $"Unknown top-level node '{node.Name}'. Expected 'patch' or 'clone'.",
                        Line = line,
                    });
                    break;
            }

            FlushErrors(doc, errors, line);
        }
    }

    /// <summary>
    /// Split raw KDL text into top-level blocks by tracking brace depth.
    /// Returns each block's text and its 1-based starting line number.
    /// </summary>
    private static List<(string Text, int StartLine)> SplitTopLevelBlocks(string text)
    {
        var blocks = new List<(string Text, int StartLine)>();
        var lines = text.Split('\n');
        var blockStart = -1;
        var depth = 0;
        var maxDepth = 0;
        var inString = false;
        var escape = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track brace depth, skipping quoted strings
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (escape) { escape = false; continue; }
                if (ch == '\\' && inString) { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;

                // Line comment — rest of line is ignored
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                    break;

                if (ch == '{') { depth++; maxDepth = Math.Max(maxDepth, depth); continue; }
                if (ch == '}') { depth--; continue; }
            }

            var nonBlank = line.TrimStart().Length > 0;

            // Detect start of a block (non-blank line at depth 0)
            if (blockStart < 0 && nonBlank)
                blockStart = i;

            // End block when we return to depth 0 after entering braces
            if (blockStart >= 0 && depth <= 0 && maxDepth > 0 && nonBlank)
            {
                blocks.Add((string.Join('\n', lines[blockStart..(i + 1)]), blockStart + 1));
                blockStart = -1;
                depth = 0;
                maxDepth = 0;
                inString = false;
            }
        }

        // Capture any trailing block (unclosed brace or braceless node)
        if (blockStart >= 0)
            blocks.Add((string.Join('\n', lines[blockStart..]), blockStart + 1));

        return blocks;
    }

    private static void FlushErrors(KdlEditorDocument doc, List<string> errors, int? fallbackLine)
    {
        foreach (var msg in errors)
            doc.Errors.Add(new KdlEditorError { Message = msg, Line = ExtractLineFromMessage(msg) ?? fallbackLine });
        errors.Clear();
    }

    /// <summary>
    /// Extract line number from error messages. Handles two formats:
    /// <list type="bullet">
    /// <item>"text:5: ..." or "path/file.kdl:12: ..." (Jiangyu position prefix)</item>
    /// <item>"Parse error at line 3, column 2: ..." (KDL library exception)</item>
    /// </list>
    /// </summary>
    private static int? ExtractLineFromMessage(string message)
    {
        // Try "at line N" (KDL library format)
        const string atLine = "at line ";
        var atIdx = message.IndexOf(atLine, StringComparison.OrdinalIgnoreCase);
        if (atIdx >= 0)
        {
            var start = atIdx + atLine.Length;
            var end = start;
            while (end < message.Length && char.IsDigit(message[end])) end++;
            if (end > start && int.TryParse(message.AsSpan()[start..end], out var line))
                return line;
        }

        // Try "prefix:N:" (Jiangyu position format)
        var firstColon = message.IndexOf(':');
        if (firstColon < 0) return null;
        var secondColon = message.IndexOf(':', firstColon + 1);
        if (secondColon < 0) return null;
        var lineSpan = message.AsSpan()[(firstColon + 1)..secondColon];
        return int.TryParse(lineSpan, out var line2) ? line2 : null;
    }

    /// <summary>
    /// Pair each editor directive with its source child node so the line
    /// number survives through to validation-time diagnostics. Relies on the
    /// 1:1 ordering between parsed directives and source children; only called
    /// after TryParsePatchNode/TryParseCloneNode have already succeeded.
    /// </summary>
    private static void StampDirectiveLines(
        List<KdlEditorDirective> directives,
        IList<KdlNode> sourceChildren,
        int lineOffset)
    {
        var pairs = Math.Min(directives.Count, sourceChildren.Count);
        for (var i = 0; i < pairs; i++)
        {
            var local = sourceChildren[i].SourcePosition?.Line;
            directives[i].Line = local.HasValue ? local.Value + lineOffset : null;
        }
    }

    /// <summary>
    /// Converts an editor directive into the compiled patch operation model.
    /// Used by semantic validation on the editor AST so Host and compile share
    /// one operation/value shape.
    /// </summary>
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
            Descent = directive.Descent != null ? CloneDescent(directive.Descent) : null,
            Value = directive.Value != null ? EditorValueToCompiled(directive.Value) : null,
        };
    }

    private static List<TemplateDescentStep> CloneDescent(List<TemplateDescentStep> source)
    {
        var copy = new List<TemplateDescentStep>(source.Count);
        foreach (var step in source)
            copy.Add(new TemplateDescentStep { Field = step.Field, Index = step.Index, Subtype = step.Subtype });
        return copy;
    }

    /// <summary>
    /// Converts a compiled patch operation into editor directive form.
    /// </summary>
    public static KdlEditorDirective CompiledOpToEditorDirective(CompiledTemplateSetOperation op)
        => CompiledOpToEditor(op);

    private static KdlEditorNode CompiledPatchToEditor(CompiledTemplatePatch patch)
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
            Descent = op.Descent != null ? CloneDescent(op.Descent) : null,
            Value = op.Value != null ? CompiledValueToEditor(op.Value) : null,
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
                CompositeType = v.Composite?.TypeName,
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
            _ => new KdlEditorValue { Kind = KdlEditorValueKind.String, String = "" },
        };
    }

    /// <summary>Captures log messages as strings for the editor RPC path.</summary>
    private sealed class ListLogSink(List<string> target) : ILogSink
    {
        public void Info(string _) { }
        public void Warning(string message) => target.Add(message);
        public void Error(string message) => target.Add(message);
    }

    private readonly record struct FileResult(
        List<CompiledTemplatePatch> Patches,
        List<CompiledTemplateClone> Clones,
        int ErrorCount);

    private static FileResult ParseFile(string filePath, string relativePath, ILogSink log)
    {
        var patches = new List<CompiledTemplatePatch>();
        var clones = new List<CompiledTemplateClone>();
        var errorCount = 0;

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            log.Error($"KDL: cannot read '{relativePath}': {ex.Message}");
            return new FileResult(patches, clones, 1);
        }

        KdlDocument doc;
        try
        {
            doc = KdlDocument.Parse(text);
        }
        catch (Exception ex)
        {
            log.Error($"KDL: parse error in '{relativePath}': {ex.Message}");
            return new FileResult(patches, clones, 1);
        }

        // Per-file uniqueness
        var filePatchSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in doc.Nodes)
        {
            var pos = FormatPos(relativePath, node.SourcePosition);

            switch (node.Name)
            {
                case "patch":
                    if (!TryParsePatchNode(node, pos, log, out var patch))
                    {
                        errorCount++;
                        break;
                    }

                    var patchKey = (patch.TemplateType ?? "EntityTemplate") + "\0" + patch.TemplateId;
                    if (!filePatchSeen.Add(patchKey))
                    {
                        log.Error(
                            $"{pos}: duplicate patch target '{patch.TemplateType}:{patch.TemplateId}' within this file.");
                        errorCount++;
                        break;
                    }

                    patches.Add(patch);
                    break;

                case "clone":
                    if (!TryParseCloneNode(node, pos, log, out var clone, out var clonePatches))
                    {
                        errorCount++;
                        break;
                    }

                    clones.Add(clone);
                    if (clonePatches != null)
                        patches.Add(clonePatches);
                    break;

                default:
                    log.Error($"{pos}: unknown top-level node '{node.Name}'. Expected 'patch' or 'clone'.");
                    errorCount++;
                    break;
            }
        }

        return new FileResult(patches, clones, errorCount);
    }

    // patch "TemplateType" "templateId" { ... }
    private static bool TryParsePatchNode(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplatePatch patch)
    {
        patch = null!;

        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: 'patch' requires two arguments: templateType and templateId.");
            return false;
        }

        var templateType = node.Arguments[0].AsString();
        var templateId = node.Arguments[1].AsString();

        if (string.IsNullOrWhiteSpace(templateType) || string.IsNullOrWhiteSpace(templateId))
        {
            log.Error($"{pos}: 'patch' templateType and templateId must be non-empty strings.");
            return false;
        }

        var ops = new List<CompiledTemplateSetOperation>();
        var hasError = false;
        foreach (var child in node.Children)
        {
            var childPos = FormatPos(pos, child.SourcePosition);
            if (!TryParseOperation(child, childPos, log, ops))
            {
                hasError = true;
            }
        }

        if (hasError)
            return false;

        patch = new CompiledTemplatePatch
        {
            TemplateType = templateType,
            TemplateId = templateId,
            Set = ops,
        };
        return true;
    }

    // clone "TemplateType" from="sourceId" id="cloneId" { ... }
    private static bool TryParseCloneNode(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplateClone clone, out CompiledTemplatePatch? inlinePatches)
    {
        clone = null!;
        inlinePatches = null;

        if (node.Arguments.Count < 1)
        {
            log.Error($"{pos}: 'clone' requires at least one argument: templateType.");
            return false;
        }

        var templateType = node.Arguments[0].AsString();
        if (string.IsNullOrWhiteSpace(templateType))
        {
            log.Error($"{pos}: 'clone' templateType must be a non-empty string.");
            return false;
        }

        var from = GetProperty(node, "from");
        var id = GetProperty(node, "id");

        if (string.IsNullOrWhiteSpace(from))
        {
            log.Error($"{pos}: 'clone' requires from=\"sourceId\" property.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            log.Error($"{pos}: 'clone' requires id=\"cloneId\" property.");
            return false;
        }

        clone = new CompiledTemplateClone
        {
            TemplateType = templateType,
            SourceId = from!,
            CloneId = id!,
        };

        // Optional inline patches on the clone
        if (node.HasChildren)
        {
            var ops = new List<CompiledTemplateSetOperation>();
            var hasError = false;
            foreach (var child in node.Children)
            {
                var childPos = FormatPos(pos, child.SourcePosition);
                if (!TryParseOperation(child, childPos, log, ops))
                {
                    hasError = true;
                }
            }

            if (hasError)
                return false;

            if (ops.Count > 0)
            {
                inlinePatches = new CompiledTemplatePatch
                {
                    TemplateType = templateType,
                    TemplateId = id!,
                    Set = ops,
                };
            }
        }

        return true;
    }

    // set "fieldPath" <value>              — scalar or whole-member set
    // set "fieldPath" index=N <value>      — element set on a collection
    // set "fieldPath" index=N type="X" {   — descend into element N (subtype X)
    //     set "subField" <value>
    //     ...
    // }
    // append "fieldPath" <value>
    // insert "fieldPath" index=N <value>
    // remove "fieldPath" index=N
    //
    // Appends parsed ops to <paramref name="ops"/>. Returns false on error.
    // A single source node usually produces one op; a descent block produces
    // one flattened op per inner directive.
    private static bool TryParseOperation(
        KdlNode node, string pos, ILogSink log,
        List<CompiledTemplateSetOperation> ops)
    {
        var opName = node.Name;
        CompiledTemplateOp opKind;
        switch (opName)
        {
            case "set": opKind = CompiledTemplateOp.Set; break;
            case "append": opKind = CompiledTemplateOp.Append; break;
            case "insert": opKind = CompiledTemplateOp.InsertAt; break;
            case "remove": opKind = CompiledTemplateOp.Remove; break;
            case "clear": opKind = CompiledTemplateOp.Clear; break;
            default:
                log.Error($"{pos}: unknown operation '{opName}'. Expected 'set', 'append', 'insert', 'remove', or 'clear'.");
                return false;
        }

        if (node.Arguments.Count < 1)
        {
            log.Error($"{pos}: '{opName}' requires at least one argument (fieldPath).");
            return false;
        }

        var fieldPath = node.Arguments[0].AsString();
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            log.Error($"{pos}: '{opName}' fieldPath must be a non-empty string.");
            return false;
        }

        // Bracket indexers in modder-authored fieldPath strings are not
        // accepted. Descent uses child blocks; element-set uses index= property.
        if (fieldPath.Contains('['))
        {
            log.Error(
                $"{pos}: bracket indexer '[' in fieldPath '{fieldPath}' is no longer supported. "
                + "Use child blocks for descent (set \"Field\" index=N { set \"SubField\" <value> }) "
                + "or index= property for element writes (set \"Field\" index=N <value>).");
            return false;
        }

        // Clear takes neither index nor value — empties the whole collection.
        if (opKind == CompiledTemplateOp.Clear)
        {
            if (GetPropertyValue(node, "index") != null)
            {
                log.Error($"{pos}: 'clear' does not take an index= property; clear empties the whole collection.");
                return false;
            }
            if (node.Arguments.Count > 1)
            {
                log.Error($"{pos}: 'clear' takes no value; only the fieldPath argument is allowed.");
                return false;
            }
            if (node.HasChildren)
            {
                log.Error($"{pos}: 'clear' does not take a child block.");
                return false;
            }

            ops.Add(new CompiledTemplateSetOperation
            {
                Op = opKind,
                FieldPath = fieldPath,
            });
            return true;
        }

        // Remove needs index= property, no value
        if (opKind == CompiledTemplateOp.Remove)
        {
            var removeIndexProp = GetPropertyValue(node, "index");
            if (removeIndexProp == null || removeIndexProp.AsInt32() == null)
            {
                log.Error($"{pos}: 'remove' requires an index=N property.");
                return false;
            }
            if (node.HasChildren)
            {
                log.Error($"{pos}: 'remove' does not take a child block.");
                return false;
            }

            ops.Add(new CompiledTemplateSetOperation
            {
                Op = opKind,
                FieldPath = fieldPath,
                Index = removeIndexProp.AsInt32(),
            });
            return true;
        }

        // InsertAt requires index=, Set accepts an optional index= for
        // element-set on a collection. Append takes no index.
        int? parsedIndex = null;
        if (opKind == CompiledTemplateOp.InsertAt)
        {
            var indexProp = GetPropertyValue(node, "index");
            if (indexProp == null || indexProp.AsInt32() == null)
            {
                log.Error($"{pos}: 'insert' requires an index=N property.");
                return false;
            }
            if (node.HasChildren
                && GetProperty(node, "composite") == null
                && GetProperty(node, "handler") == null)
            {
                log.Error(
                    $"{pos}: 'insert' with a child block requires composite=\"TypeName\" (inline value) or "
                    + "handler=\"SubtypeName\" (ScriptableObject construction) to declare the element type.");
                return false;
            }

            parsedIndex = indexProp.AsInt32();
        }
        else if (opKind == CompiledTemplateOp.Set)
        {
            var indexProp = GetPropertyValue(node, "index");
            if (indexProp != null)
            {
                var parsed = indexProp.AsInt32();
                if (parsed == null)
                {
                    log.Error($"{pos}: 'set' index= must be a non-negative integer.");
                    return false;
                }
                parsedIndex = parsed;
            }

            // Descent block: set "Field" index=N type="X" { set "Sub" ... }.
            // The outer 'set' is purely a navigation marker — it carries no
            // value and no scalar result. Each inner directive becomes its
            // own compiled op with a TemplateDescentStep prepended onto its
            // Descent list, recording the outer (Field, index, type) navigation.
            //
            // Composite construction (set "Field" composite="X" { ... }) and
            // handler construction (set "Field" handler="X" { ... }) also use
            // a child block but produce a value rather than navigate. Fall
            // through to TryParseValue.
            if (node.HasChildren
                && GetProperty(node, "composite") == null
                && GetProperty(node, "handler") == null)
            {
                return TryParseDescentBlock(node, pos, log, ops, fieldPath, parsedIndex);
            }
        }
        else if (opKind == CompiledTemplateOp.Append)
        {
            if (GetPropertyValue(node, "index") != null)
            {
                log.Error($"{pos}: 'append' does not take an index= property; use 'insert' for positional writes.");
                return false;
            }
            if (node.HasChildren
                && GetProperty(node, "composite") == null
                && GetProperty(node, "handler") == null)
            {
                log.Error(
                    $"{pos}: 'append' with a child block requires composite=\"TypeName\" (inline value) or "
                    + "handler=\"SubtypeName\" (ScriptableObject construction) to declare the element type. "
                    + "Plain descent blocks are 'set'-only.");
                return false;
            }
        }

        // type= is only meaningful on a descent block (set with index= and a
        // child block, no composite=). On any other shape it's either a typo
        // or a misunderstanding — surface it loudly rather than silently
        // dropping the hint and letting the modder think they're getting
        // polymorphism validation. The descent path consumes type= before
        // reaching this check; getting here means it isn't descent.
        if (GetProperty(node, "type") != null)
        {
            log.Error(
                $"{pos}: 'type=' is only valid on 'set' with a descent child block "
                + $"(set \"Field\" index=N type=\"X\" {{ ... }}); it has no meaning on '{opName}' "
                + (GetProperty(node, "composite") != null
                    ? "with composite= (which constructs a value), "
                    : "without descent, ")
                + "and is rejected to surface the typo.");
            return false;
        }

        // Parse the value
        if (!TryParseValue(node, pos, log, out var value))
            return false;

        ops.Add(new CompiledTemplateSetOperation
        {
            Op = opKind,
            FieldPath = fieldPath,
            Index = parsedIndex,
            Value = value,
        });
        return true;
    }

    /// <summary>
    /// Walk a descent block (<c>set "Field" index=N type="X" { ... }</c>) and
    /// produce one compiled op per inner directive. Each inner op gets a
    /// <see cref="TemplateDescentStep"/> prepended onto its
    /// <see cref="CompiledTemplateSetOperation.Descent"/> list, recording the
    /// (field, index, subtype) navigation introduced at this level. Nested
    /// descent ends up with multiple steps in outer-to-inner order.
    /// </summary>
    private static bool TryParseDescentBlock(
        KdlNode node, string pos, ILogSink log,
        List<CompiledTemplateSetOperation> ops,
        string outerField, int? outerIndex)
    {
        if (outerIndex == null)
        {
            log.Error(
                $"{pos}: 'set' with a child block requires an index= property. "
                + "Descent blocks navigate into element N of the named collection.");
            return false;
        }

        if (node.Arguments.Count > 1)
        {
            log.Error(
                $"{pos}: 'set' with a child block must not carry a value. "
                + "Move the value into one of the inner 'set' directives.");
            return false;
        }

        if (GetProperty(node, "ref") != null
            || GetProperty(node, "enum") != null
            || GetProperty(node, "composite") != null
            || GetProperty(node, "asset") != null)
        {
            log.Error(
                $"{pos}: 'set' with a child block must not carry ref=, enum=, composite=, or asset= properties; "
                + "those belong on the inner 'set' that produces the value.");
            return false;
        }

        var subtypeHint = GetProperty(node, "type");
        if (subtypeHint != null && string.IsNullOrWhiteSpace(subtypeHint))
        {
            log.Error($"{pos}: type= must be a non-empty string.");
            return false;
        }

        var children = node.Children.ToList();
        if (children.Count == 0)
        {
            log.Error($"{pos}: 'set' with a child block must contain at least one inner directive.");
            return false;
        }

        var hasError = false;
        var staging = new List<CompiledTemplateSetOperation>();
        foreach (var child in children)
        {
            var childPos = FormatPos(pos, child.SourcePosition);
            if (child.Name != "set")
            {
                log.Error(
                    $"{childPos}: only 'set' is allowed inside a descent block, not '{child.Name}'. "
                    + "Append/insert/remove/clear stay at the top level.");
                hasError = true;
                continue;
            }

            if (!TryParseOperation(child, childPos, log, staging))
            {
                hasError = true;
            }
        }

        if (hasError)
            return false;

        var newStep = new TemplateDescentStep
        {
            Field = outerField,
            Index = outerIndex.Value,
            Subtype = subtypeHint,
        };

        // Prepend the new step onto each inner op's descent list. Nested
        // descent ops already carry their own inner steps; the outer step
        // lands at position 0 so the final list reads outer-to-inner.
        foreach (var inner in staging)
        {
            var combined = new List<TemplateDescentStep> { newStep };
            if (inner.Descent != null)
                combined.AddRange(inner.Descent);
            inner.Descent = combined;
            ops.Add(inner);
        }

        return true;
    }

    /// <summary>
    /// Parse the value from an operation node. The value can come from:
    /// - <c>handler="Subtype"</c> with children (HandlerConstruction —
    ///   ScriptableObject construction for a polymorphic-reference array)
    /// - <c>composite="TypeName"</c> with children (Composite — inline value)
    /// - <c>ref="TemplateType" "id"</c> (TemplateReference)
    /// - <c>enum="EnumType" "value"</c> (Enum)
    /// - A second positional argument (scalar or string)
    /// </summary>
    private static bool TryParseValue(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        // Check for handler= (ScriptableObject construction for a
        // polymorphic-reference array element, e.g. EventHandlers).
        var handlerType = GetProperty(node, "handler");
        if (handlerType != null)
            return TryParseHandlerConstructionValue(node, handlerType, pos, log, out value);

        // Check for composite=
        var compositeType = GetProperty(node, "composite");
        if (compositeType != null)
            return TryParseCompositeValue(node, compositeType, pos, log, out value);

        // Check for ref=
        var refType = GetProperty(node, "ref");
        if (refType != null)
            return TryParseRefValue(node, refType, pos, log, out value);

        // Check for asset=
        var assetName = GetProperty(node, "asset");
        if (assetName != null)
            return TryParseAssetValue(node, assetName, pos, log, out value);

        // Check for enum=
        var enumType = GetProperty(node, "enum");
        if (enumType != null)
            return TryParseEnumValue(node, enumType, pos, log, out value);

        // Must have a second argument with the literal value
        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: '{node.Name}' requires a value (second argument, ref=, enum=, composite=, or handler=).");
            return false;
        }

        var arg = node.Arguments[1];
        return TryParseLiteralValue(arg, pos, log, out value);
    }

    private static bool TryParseLiteralValue(
        KdlValue arg, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        switch (arg.ValueType)
        {
            case KdlValueType.Boolean:
                value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Boolean,
                    Boolean = arg.AsBoolean(),
                };
                return true;

            case KdlValueType.String:
                value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.String,
                    String = arg.AsString(),
                };
                return true;

            case KdlValueType.Number:
                return TryParseNumberValue(arg, pos, log, out value);

            case KdlValueType.Null:
                log.Error($"{pos}: null values are not supported in template patches.");
                return false;

            default:
                log.Error($"{pos}: unsupported KDL value type '{arg.ValueType}'.");
                return false;
        }
    }

    private static bool TryParseNumberValue(
        KdlValue arg, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        // Distinguish float from integer via the textual representation
        var text = arg.ToKdlString();
        var isFloat = text.Contains('.') || text.Contains('e') || text.Contains('E');

        if (isFloat)
        {
            var d = arg.AsDouble();
            if (d == null)
            {
                log.Error($"{pos}: cannot parse number '{text}' as a float.");
                return false;
            }

            value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Single,
                Single = (float)d.Value,
            };
            return true;
        }

        // Integer — always emit as Int32. The runtime applier coerces to
        // Byte when the target field is byte-typed (see TryConvertValue).
        var i = arg.AsInt32();
        if (i == null)
        {
            // Try Int64 for large values
            var l = arg.AsInt64();
            if (l == null)
            {
                log.Error($"{pos}: cannot parse number '{text}' as an integer.");
                return false;
            }

            // Check if it fits in Int32
            if (l.Value is < int.MinValue or > int.MaxValue)
            {
                log.Error($"{pos}: integer '{text}' is out of Int32 range.");
                return false;
            }

            i = (int)l.Value;
        }

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Int32,
            Int32 = i.Value,
        };

        return true;
    }

    // ref="TemplateType" "templateId"
    private static bool TryParseRefValue(
        KdlNode node, string refType, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        // The template ID is the second positional argument (after fieldPath)
        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: ref= requires a template ID as the second argument.");
            return false;
        }

        var templateId = node.Arguments[1].AsString();
        if (string.IsNullOrWhiteSpace(templateId))
        {
            log.Error($"{pos}: ref= template ID must be a non-empty string.");
            return false;
        }

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.TemplateReference,
            Reference = new CompiledTemplateReference
            {
                TemplateType = refType,
                TemplateId = templateId,
            },
        };
        return true;
    }

    // asset="path/to/name"
    //
    // Reference to a Unity asset shipped by the mod under
    // assets/additions/<category>/<name>.<ext>, or an indexed game asset of
    // the same name. The category is derived from the destination field's
    // declared Unity type at compile and apply time, so the modder writes
    // the name only. The name preserves directory separators as `/`.
    private static bool TryParseAssetValue(
        KdlNode node, string assetName, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (string.IsNullOrWhiteSpace(assetName))
        {
            log.Error($"{pos}: asset= must be a non-empty asset name.");
            return false;
        }

        // KDL asset references are portable text the modder hand-writes;
        // require a single canonical separator so the same KDL compiles
        // identically on Linux and Windows. The filesystem walk at compile
        // time normalises native separators when computing logical names,
        // but the authored form must stay forward-slash.
        if (assetName.Contains('\\'))
        {
            log.Error(
                $"{pos}: asset=\"{assetName}\" uses '\\' as a path separator; "
                + "use '/' instead. KDL asset names are portable text and the "
                + "filesystem layout under assets/additions/ is normalised at compile time.");
            return false;
        }

        // The field path is the only positional argument; asset references
        // carry their payload in the property value, not a second positional.
        if (node.Arguments.Count > 1)
        {
            log.Error(
                $"{pos}: asset= must not carry a positional value beyond the field name. "
                + "The asset name lives in the property value (asset=\"name\").");
            return false;
        }

        if (node.HasChildren)
        {
            log.Error($"{pos}: asset= must not carry a child block; it is a leaf reference.");
            return false;
        }

        if (GetProperty(node, "type") != null)
        {
            log.Error(
                $"{pos}: asset= is exclusive with type=. The asset's Unity type "
                + "is derived from the destination field, not declared in source.");
            return false;
        }

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.AssetReference,
            Asset = new CompiledAssetReference
            {
                Name = assetName,
            },
        };
        return true;
    }

    // enum="EnumType" "EnumValue"
    private static bool TryParseEnumValue(
        KdlNode node, string enumType, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: enum= requires an enum value as the second argument.");
            return false;
        }

        var enumValue = node.Arguments[1].AsString();
        if (string.IsNullOrWhiteSpace(enumValue))
        {
            log.Error($"{pos}: enum= value must be a non-empty string.");
            return false;
        }

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Enum,
            EnumType = enumType,
            EnumValue = enumValue,
        };
        return true;
    }

    // handler="SubtypeName" { set "field" <value> ... }
    //
    // Constructs a freshly-allocated ScriptableObject of the named subtype
    // at apply time and pushes/inserts/replaces it in a polymorphic-
    // reference array such as SkillTemplate.EventHandlers. Distinct from
    // composite= which builds an inline value-typed struct. The inner
    // directives must be flat 'set' ops naming top-level fields on the
    // constructed type; descent and indexed writes inside the construction
    // are not supported in this slice (use a separate descent patch on
    // the resulting element after append/insert if you need them).
    private static bool TryParseHandlerConstructionValue(
        KdlNode node, string subtypeName, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (string.IsNullOrWhiteSpace(subtypeName))
        {
            log.Error($"{pos}: handler= must be a non-empty subtype name.");
            return false;
        }

        if (GetProperty(node, "ref") != null
            || GetProperty(node, "enum") != null
            || GetProperty(node, "composite") != null
            || GetProperty(node, "asset") != null
            || GetProperty(node, "type") != null)
        {
            log.Error(
                $"{pos}: 'handler=' is exclusive with ref=, enum=, composite=, asset=, and type=. "
                + "It signals construction of a new ScriptableObject; the others are different value shapes.");
            return false;
        }

        if (node.Arguments.Count > 1)
        {
            log.Error($"{pos}: 'handler=' construction must not carry a positional value; configure fields via inner directives only.");
            return false;
        }

        if (!node.HasChildren)
        {
            log.Error(
                $"{pos}: 'handler=\"{subtypeName}\"' requires a child block with at least one directive "
                + "(otherwise the constructed handler would carry only its default field values, "
                + "which is rarely intentional — use ref= or refer to an existing handler instead).");
            return false;
        }

        if (!TryParseInnerOperations(node, pos, log, out var ops))
            return false;

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.HandlerConstruction,
            HandlerConstruction = new CompiledTemplateComposite
            {
                TypeName = subtypeName,
                Operations = ops,
            },
        };
        return true;
    }

    // composite="TypeName" { <directives> }. Same set of operations as the
    // outer patch block — set/append/insert/remove/clear — applied to the
    // freshly-constructed instance. Allowing all five ops is what lets
    // modders author "append a PropertyChange to the constructed handler's
    // Properties list" inline rather than splitting into a separate descent
    // patch on the resulting list element.
    private static bool TryParseCompositeValue(
        KdlNode node, string typeName, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (!TryParseInnerOperations(node, pos, log, out var ops))
            return false;

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = typeName,
                Operations = ops,
            },
        };
        return true;
    }

    /// <summary>
    /// Parse the directive list inside a composite/handler construction block.
    /// Reuses <see cref="TryParseOperation"/> so the inner grammar exactly
    /// mirrors the outer patch block: set/append/insert/remove/clear with
    /// optional descent and inline composite/handler nesting.
    /// </summary>
    private static bool TryParseInnerOperations(
        KdlNode node, string pos, ILogSink log, out List<CompiledTemplateSetOperation> ops)
    {
        ops = [];
        var ok = true;
        foreach (var child in node.Children)
        {
            var childPos = FormatPos(pos, child.SourcePosition);
            if (!TryParseOperation(child, childPos, log, ops))
                ok = false;
        }
        return ok;
    }

    private static string? GetProperty(KdlNode node, string key)
    {
        foreach (var prop in node.Properties)
        {
            if (string.Equals(prop.Key, key, StringComparison.Ordinal))
                return prop.Value.AsString();
        }

        return null;
    }

    private static KdlValue? GetPropertyValue(KdlNode node, string key)
    {
        foreach (var prop in node.Properties)
        {
            if (string.Equals(prop.Key, key, StringComparison.Ordinal))
                return prop.Value;
        }

        return null;
    }

    private static string FormatPos(string fileContext, SourcePosition? pos)
    {
        if (pos is null) return fileContext;

        // Strip any existing line suffix and replace with the new line number
        var colonIdx = fileContext.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(fileContext[(colonIdx + 1)..], out _))
        {
            var filePrefix = fileContext[..colonIdx];
            return $"{filePrefix}:{pos.Line}";
        }

        return $"{fileContext}:{pos.Line}";
    }
}
