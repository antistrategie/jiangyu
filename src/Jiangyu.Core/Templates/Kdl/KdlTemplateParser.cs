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
            var recovered = ParseTextWithRecovery(text);
            AttachLineComments(recovered, text);
            AttachBlankLines(recovered, text);
            return recovered;
        }

        ProcessKdlNodes(kdl, doc, lineOffset: 0);
        AttachLineComments(doc, text);
        AttachBlankLines(doc, text);
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
                        var patchNode = KdlEditorBridge.CompiledPatchToEditor(patch);
                        patchNode.Line = line;
                        ApplyLineOffsetToDirectives(patchNode.Directives, lineOffset);
                        doc.Nodes.Add(patchNode);
                    }
                    else
                    {
                        FlushErrors(doc, errors, line);
                    }
                    break;

                case "clone":
                case "create":
                    var isCreate = node.Name == "create";
                    if (TryParseCloneNode(node, pos, log, isCreate, out var clone, out var clonePatches))
                    {
                        var editorNode = new KdlEditorNode
                        {
                            Kind = isCreate ? KdlEditorNodeKind.Create : KdlEditorNodeKind.Clone,
                            TemplateType = clone.TemplateType ?? string.Empty,
                            SourceId = clone.SourceId,
                            CloneId = clone.CloneId,
                            Line = line,
                        };
                        if (clonePatches != null)
                        {
                            foreach (var op in clonePatches.Set)
                                editorNode.Directives.Add(KdlEditorBridge.CompiledOpToEditorDirective(op));
                            ApplyLineOffsetToDirectives(editorNode.Directives, lineOffset);
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
                        Message = $"Unknown top-level node '{node.Name}'. Expected 'patch', 'clone' or 'create'.",
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
    /// <summary>
    /// Apply <paramref name="lineOffset"/> to every <see cref="KdlEditorDirective.Line"/>
    /// in the tree. The bridge stamps the local (per-block) line via
    /// <see cref="CompiledTemplateSetOperation.SourceLine"/>; ParseTextWithRecovery
    /// parses each top-level block independently with its own offset, so we
    /// have to adjust after the fact.
    /// </summary>
    /// <summary>
    /// Scan the raw text for blank lines (lines containing only whitespace)
    /// and mark the directive immediately following each blank run with
    /// <see cref="KdlEditorDirective.BlankLineBefore"/>. Multiple consecutive
    /// blanks collapse to one — the serialiser only emits a single blank
    /// line regardless. Blanks are only attributed within sibling
    /// directive lists (same node body or same composite body), so an
    /// inter-node blank like the separator between two top-level clones
    /// doesn't bleed onto the next clone's first directive (the serialiser
    /// already emits inter-node blanks unconditionally).
    /// </summary>
    private static void AttachBlankLines(KdlEditorDocument doc, string text)
    {
        var normalised = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');
        var blanks = new SortedSet<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 || string.IsNullOrWhiteSpace(lines[i]))
                blanks.Add(i + 1);
        }
        if (blanks.Count == 0) return;

        foreach (var node in doc.Nodes)
            AttachBlanksAmongSiblings(node.Directives, blanks);
    }

    private static void AttachBlanksAmongSiblings(
        List<KdlEditorDirective> siblings,
        SortedSet<int> blanks)
    {
        KdlEditorDirective? prev = null;
        foreach (var d in siblings)
        {
            if (prev?.Line is { } prevLine && d.Line is { } currLine && prevLine < currLine)
            {
                foreach (var blank in blanks)
                {
                    if (blank <= prevLine) continue;
                    if (blank >= currLine) break;
                    d.BlankLineBefore = true;
                    break;
                }
            }
            prev = d;
            if (d.Value?.CompositeDirectives is { } inner)
                AttachBlanksAmongSiblings(inner, blanks);
        }
    }

    /// <summary>
    /// Recursively collect every directive's leading / trailing closure for
    /// the comment-attribution pass, descending into composite and handler
    /// bodies. Directives with no <see cref="KdlEditorDirective.Line"/> are
    /// silently skipped — they don't have a source position so we can't
    /// reason about which comment is "above" them.
    /// </summary>
    private static void CollectDirectiveEntities(
        IEnumerable<KdlEditorDirective> directives,
        List<(int Line, Action<string> AppendLeading, Action<string> SetTrailing)> entities)
    {
        foreach (var directive in directives)
        {
            if (directive.Line is { } directiveLine)
            {
                entities.Add((
                    directiveLine,
                    t => (directive.LeadingComments ??= []).Add(t),
                    t => directive.TrailingComment = t));
            }
            if (directive.Value?.CompositeDirectives is { } inner)
                CollectDirectiveEntities(inner, entities);
        }
    }

    private static void ApplyLineOffsetToDirectives(IEnumerable<KdlEditorDirective> directives, int lineOffset)
    {
        if (lineOffset == 0) return;
        foreach (var directive in directives)
        {
            if (directive.Line.HasValue)
                directive.Line += lineOffset;
            if (directive.Value?.CompositeDirectives is { } inner)
                ApplyLineOffsetToDirectives(inner, lineOffset);
        }
    }

    /// <summary>
    /// Tokenise <c>//</c> line comments out of the raw source text and
    /// attach each to the closest following stamped entity (node or top-
    /// level directive). Comments trailing the last entity land on
    /// <see cref="KdlEditorDocument.TrailingComments"/>. KdlSharp drops
    /// comments during parse, so we run a separate string-aware pass.
    /// </summary>
    private static void AttachLineComments(KdlEditorDocument doc, string text)
    {
        var comments = ExtractLineComments(text);
        if (comments.Count == 0) return;

        // Each entity contributes two closures: one for "comment on a line
        // strictly before this entity" (leading), one for "comment on the
        // same line as this entity's opening" (inline trailing). KDL only
        // allows one comment per line so a single TrailingComment slot is
        // enough.
        var entities = new List<(int Line, Action<string> AppendLeading, Action<string> SetTrailing)>();
        foreach (var node in doc.Nodes)
        {
            if (node.Line is { } nodeLine)
            {
                entities.Add((
                    nodeLine,
                    t => (node.LeadingComments ??= []).Add(t),
                    t => node.TrailingComment = t));
            }
            CollectDirectiveEntities(node.Directives, entities);
        }
        entities.Sort((a, b) => a.Line.CompareTo(b.Line));

        var idx = 0;
        foreach (var (line, commentText) in comments)
        {
            while (idx < entities.Count && entities[idx].Line < line) idx++;
            if (idx < entities.Count && entities[idx].Line == line)
            {
                entities[idx].SetTrailing(commentText);
            }
            else if (idx < entities.Count)
            {
                entities[idx].AppendLeading(commentText);
            }
            else
            {
                (doc.TrailingComments ??= []).Add(commentText);
            }
        }
    }

    /// <summary>
    /// Scan <paramref name="text"/> for <c>//</c> line comments. Returns
    /// each as (1-based line, normalised text). String state (including
    /// KDL v2 triple-quoted multi-line literals) is tracked so a <c>//</c>
    /// inside a quoted string isn't treated as a comment. The leading
    /// <c>// </c> prefix is stripped (one optional space after the slashes
    /// is consumed) so callers can re-emit with a canonical prefix.
    /// </summary>
    private static List<(int Line, string Text)> ExtractLineComments(string text)
    {
        var result = new List<(int Line, string Text)>();
        var normalised = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');
        var inString = false;
        var inTripleQuote = false;
        var escape = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var c = 0;
            while (c < line.Length)
            {
                if (escape) { escape = false; c++; continue; }
                var ch = line[c];

                // Triple-quoted bodies span multiple lines and disable
                // single-quote string state until the closing """.
                if (!inString && c + 2 < line.Length && ch == '"' && line[c + 1] == '"' && line[c + 2] == '"')
                {
                    inTripleQuote = !inTripleQuote;
                    c += 3;
                    continue;
                }
                if (inTripleQuote) { c++; continue; }

                if (ch == '\\' && inString) { escape = true; c++; continue; }
                if (ch == '"') { inString = !inString; c++; continue; }
                if (inString) { c++; continue; }

                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                {
                    var rest = line[(c + 2)..];
                    if (rest.Length > 0 && rest[0] == ' ') rest = rest[1..];
                    result.Add((i + 1, rest));
                    break;
                }
                c++;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts an editor directive into the compiled patch operation model.
    /// Used by semantic validation on the editor AST so Host and compile share
    /// one operation/value shape.
    /// </summary>
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
                case "create":
                    if (!TryParseCloneNode(node, pos, log, isCreate: node.Name == "create", out var clone, out var clonePatches))
                    {
                        errorCount++;
                        break;
                    }

                    clones.Add(clone);
                    if (clonePatches != null)
                        patches.Add(clonePatches);
                    break;

                default:
                    log.Error($"{pos}: unknown top-level node '{node.Name}'. Expected 'patch', 'clone' or 'create'.");
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
    // clone "TemplateType" from="sourceId" id="cloneId" { ... }  (copies a source)
    // create "TemplateType" id="newId" { ... }                   (fresh template)
    private static bool TryParseCloneNode(
        KdlNode node, string pos, ILogSink log, bool isCreate,
        out CompiledTemplateClone clone, out CompiledTemplatePatch? inlinePatches)
    {
        clone = null!;
        inlinePatches = null;
        var keyword = isCreate ? "create" : "clone";

        if (node.Arguments.Count < 1)
        {
            log.Error($"{pos}: '{keyword}' requires at least one argument: templateType.");
            return false;
        }

        var templateType = node.Arguments[0].AsString();
        if (string.IsNullOrWhiteSpace(templateType))
        {
            log.Error($"{pos}: '{keyword}' templateType must be a non-empty string.");
            return false;
        }

        var from = GetProperty(node, "from");
        var id = GetProperty(node, "id");

        if (isCreate)
        {
            if (!string.IsNullOrWhiteSpace(from))
            {
                log.Error($"{pos}: 'create' makes a fresh template and takes no from= property (use 'clone' to copy a source).");
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(from))
        {
            log.Error($"{pos}: 'clone' requires from=\"sourceId\" property.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            log.Error($"{pos}: '{keyword}' requires id=\"{(isCreate ? "newId" : "cloneId")}\" property.");
            return false;
        }

        clone = new CompiledTemplateClone
        {
            TemplateType = templateType,
            SourceId = isCreate ? null : from!,
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
    // allowObjectDescent gates the no-index object-field descent
    // (set "F" { ... }). It is true at the patch/clone top level and inside a
    // descent block (editing existing structure), and false inside a
    // construct block (type="X" { ... }), where a bare child block builds the
    // sub-object as a composite so its fields stay together for validation.
    private static bool TryParseOperation(
        KdlNode node, string pos, ILogSink log,
        List<CompiledTemplateSetOperation> ops,
        bool allowObjectDescent = true)
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

        // type= is the single construction keyword. It names the type and the
        // compiler picks the storage mechanism from the destination field: an
        // inline value for a concrete struct, a constructed ScriptableObject for
        // a reference element, a tagged string for a tagged-string field. The
        // error points composite=/handler= authors at type=.
        if (GetProperty(node, "composite") != null || GetProperty(node, "handler") != null)
        {
            var keyword = GetProperty(node, "composite") != null ? "composite=" : "handler=";
            log.Error(
                $"{pos}: '{keyword}' is not a valid keyword. Use type=\"<TypeName>\". "
                + "type= names the type and the compiler picks how to store it from the destination field.");
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
                SourceLine = node.SourcePosition?.Line,
            });
            return true;
        }

        // Remove takes either index=N (for List<T>: positional removal) or
        // a value (for HashSet<T>: by-value removal). The two forms are
        // mutually exclusive — having both means the modder confused
        // collection shapes. The validator does the final compatibility
        // check against the destination's declared type.
        if (opKind == CompiledTemplateOp.Remove)
        {
            if (node.HasChildren)
            {
                log.Error($"{pos}: 'remove' does not take a child block.");
                return false;
            }

            var removeIndexProp = GetPropertyValue(node, "index");
            var hasIndex = removeIndexProp != null && removeIndexProp.AsInt32() != null;
            var hasValue =
                node.Arguments.Count >= 2
                || GetProperty(node, "enum") != null
                || GetProperty(node, "ref") != null
                || GetProperty(node, "asset") != null;

            if (hasIndex && hasValue)
            {
                log.Error(
                    $"{pos}: 'remove' takes either index=N (for List<T>) or a value (for HashSet<T>), not both.");
                return false;
            }
            if (!hasIndex && !hasValue)
            {
                log.Error(
                    $"{pos}: 'remove' requires either an index=N property (for List<T>) "
                    + "or a value argument (for HashSet<T>).");
                return false;
            }

            if (hasIndex)
            {
                ops.Add(new CompiledTemplateSetOperation
                {
                    Op = opKind,
                    FieldPath = fieldPath,
                    Index = removeIndexProp!.AsInt32(),
                    SourceLine = node.SourcePosition?.Line,
                });
                return true;
            }

            if (!TryParseValue(node, pos, log, out var removeValue))
                return false;

            ops.Add(new CompiledTemplateSetOperation
            {
                Op = opKind,
                FieldPath = fieldPath,
                Value = removeValue,
                SourceLine = node.SourcePosition?.Line,
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

            // Edit-in-place descent (no type=, no from=, no cell=):
            //   set "Field" index=N { ... }  edits element N of a collection
            //   set "Field" { ... }          edits the object/struct at Field
            // Each inner directive becomes its own compiled op with a
            // TemplateDescentStep (Field, index) prepended onto its Descent
            // list; a null index marks object-field descent. type= and from=
            // construct a fresh value instead and fall through to TryParseValue.
            // Object-field descent (null index) is suppressed inside a
            // construct block: there the bare child block builds the sub-object
            // as a composite (TryParseValue) so its fields stay together.
            if (node.HasChildren
                && GetProperty(node, "type") == null
                && GetProperty(node, "from") == null
                && GetProperty(node, "cell") == null
                && (allowObjectDescent || parsedIndex is int))
            {
                return TryParseDescentBlock(node, pos, log, ops, fieldPath, parsedIndex);
            }
        }

        // Multi-dimensional cell address: set "Field" cell="r,c" <value>.
        // Mutually exclusive with index= because index= drives 1D collection
        // writes while cell= drives N-dim arrays. Parsing happens on every
        // op kind so a misuse (e.g. cell= on append) gets a specific error.
        List<int>? parsedIndexPath = null;
        if (GetProperty(node, "cell") is { Length: > 0 } cellRaw)
        {
            if (opKind != CompiledTemplateOp.Set)
            {
                log.Error($"{pos}: cell= is only valid on 'set'; multi-dim arrays are written one cell at a time.");
                return false;
            }
            if (parsedIndex.HasValue)
            {
                log.Error($"{pos}: 'set' cannot carry both index= and cell=; use one or the other.");
                return false;
            }

            var coords = cellRaw.Split(',');
            parsedIndexPath = new List<int>(coords.Length);
            foreach (var raw in coords)
            {
                if (!int.TryParse(raw.Trim(), out var coord) || coord < 0)
                {
                    log.Error($"{pos}: 'set' cell= must be a comma-separated list of non-negative integers (e.g. cell=\"3,4\").");
                    return false;
                }
                parsedIndexPath.Add(coord);
            }
        }
        else if (opKind == CompiledTemplateOp.Append)
        {
            if (GetPropertyValue(node, "index") != null)
            {
                log.Error($"{pos}: 'append' does not take an index= property; use 'insert' for positional writes.");
                return false;
            }
        }

        // Parse the value
        if (!TryParseValue(node, pos, log, out var value))
            return false;

        ops.Add(new CompiledTemplateSetOperation
        {
            Op = opKind,
            FieldPath = fieldPath,
            Index = parsedIndex,
            IndexPath = parsedIndexPath,
            Value = value,
            SourceLine = node.SourcePosition?.Line,
        });
        return true;
    }

    /// <summary>
    /// Walk an edit-descent block (<c>set "Field" index=N { ... }</c> for a
    /// collection element, or <c>set "Field" { ... }</c> for an object field)
    /// and produce one compiled op per inner directive. Each inner op gets a
    /// <see cref="TemplateDescentStep"/> prepended onto its
    /// <see cref="CompiledTemplateSetOperation.Descent"/> list, recording the
    /// (field, index) navigation introduced at this level; a null index marks
    /// object-field descent. Nested descent ends up with multiple steps in
    /// outer-to-inner order.
    /// </summary>
    private static bool TryParseDescentBlock(
        KdlNode node, string pos, ILogSink log,
        List<CompiledTemplateSetOperation> ops,
        string outerField, int? outerIndex)
    {
        // set "Field" index=N { ... } edits collection element N;
        // set "Field" { ... } (null index) edits the object/struct at Field.
        // The descent step carries no subtype; the concrete type is inferred
        // from the live value at apply time and from the source at validate.
        if (node.Arguments.Count > 1)
        {
            log.Error(
                $"{pos}: 'set' with a child block must not carry a value. "
                + "Move the value into one of the inner 'set' directives.");
            return false;
        }

        if (GetProperty(node, "ref") != null
            || GetProperty(node, "enum") != null
            || GetProperty(node, "asset") != null)
        {
            log.Error(
                $"{pos}: 'set' with a child block must not carry ref=, enum=, or asset= properties; "
                + "those belong on the inner 'set' that produces the value.");
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
            // Any op (set/append/insert/remove/clear) is valid inside an edit
            // block and mutates the descended value in place.
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
            Index = outerIndex,
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
    /// - <c>type="TypeName"</c> with children (TypeConstruction — the compiler
    ///   later picks ScriptableObject construction, inline value, or tagged
    ///   string from the destination field)
    /// - <c>ref="TemplateType" "id"</c> (TemplateReference)
    /// - <c>enum="EnumType" "value"</c> (Enum)
    /// - <c>asset="name"</c> (Asset)
    /// - A child block with no value-type property (inferred inline value)
    /// - A second positional argument (scalar or string)
    /// </summary>
    private static bool TryParseValue(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        // Check for type= construction. On append/insert this builds a fresh
        // polymorphic element (e.g. an EventHandlers entry); on set, type= is
        // consumed earlier as a descent block, so reaching here with type=
        // means either an append/insert construct or a set that named type=
        // without the required child block (the construct path reports the
        // missing block).
        var typeConstruct = GetProperty(node, "type");
        if (typeConstruct != null)
            return TryParseTypeConstructionValue(node, typeConstruct, pos, log, out value);

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

        // Inferred inline value: a child block with no value-type property and
        // no positional value. The element type is determined from the
        // destination at validation time, which lets monomorphic destinations
        // (List<Sound>, List<SoundVariation>, List<ID>) omit the redundant
        // type="X" declaration. Polymorphic destinations still need explicit
        // type=; the validator rejects an inferred value there with a candidate
        // list. Authoring sentinel: TypeName="" tells the validator to infer
        // rather than resolve.
        if (node.HasChildren && node.Arguments.Count < 2)
            return TryParseCompositeValue(node, string.Empty, pos, log, out value);

        // Must have a second argument with the literal value
        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: '{node.Name}' requires a value (second argument, ref=, enum=, asset=, or type=).");
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
                // Explicit null literal: emit a Null-kind compiled value. The
                // applier rejects this for value-typed fields (it can only
                // assign to reference-typed scalars), so the type check is
                // deferred to apply time when the destination field is known.
                value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Null,
                };
                return true;

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

    // type="TypeName" { set "field" <value> ... } constructs a fresh element.
    //
    // It names the concrete type to build. The compiler picks the storage
    // mechanism from the destination field. A polymorphic-reference element
    // (SkillTemplate.EventHandlers) constructs a freshly-allocated
    // ScriptableObject. A concrete struct field (EntityTemplate.AIRole) builds
    // an inline value. A tagged-string field packs a tagged string. On append
    // or insert the element is pushed or inserted. On an indexed set it
    // replaces the element at that slot, whereas an indexed set with no type=
    // edits in place via a descent block. The inner directives configure the
    // new instance.
    private static bool TryParseTypeConstructionValue(
        KdlNode node, string subtypeName, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (string.IsNullOrWhiteSpace(subtypeName))
        {
            log.Error($"{pos}: type= must be a non-empty subtype name.");
            return false;
        }

        if (GetProperty(node, "ref") != null
            || GetProperty(node, "enum") != null
            || GetProperty(node, "asset") != null)
        {
            log.Error(
                $"{pos}: 'type=' is exclusive with ref=, enum=, and asset=. "
                + "It names a new element to construct; the others are different value shapes.");
            return false;
        }

        if (node.Arguments.Count > 1)
        {
            log.Error($"{pos}: 'type=' construction must not carry a positional value. Configure fields via inner directives only.");
            return false;
        }

        // An empty body (type="X" {}) is allowed: it constructs the element
        // with its default field values, which is the intent for marker nodes
        // such as a tagged-string EMPTY conversation node.
        if (!TryParseInnerOperations(node, pos, log, out var ops))
            return false;

        // Optional from="..." prototype-source, mirroring the inferred-value
        // path: deep-copy an existing element in the destination collection
        // (matched by name) before applying the inner ops.
        var from = GetProperty(node, "from");

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.TypeConstruction,
            TypeConstruction = new CompiledTemplateComposite
            {
                TypeName = subtypeName,
                Operations = ops,
                From = string.IsNullOrWhiteSpace(from) ? null : from,
            },
        };
        return true;
    }

    // Inferred inline value: a child block with no type= (TypeName=""). The
    // destination's element type resolves it at validate time. Same operations as the
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

        // Optional from="..." prototype-source. The applier looks up an
        // existing element in the destination collection by this key
        // (matched against the element's name property), deep-copies it,
        // and applies the inner ops on the copy. Lets the modder inherit
        // editor-baked defaults instead of enumerating every field.
        var from = GetProperty(node, "from");

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = typeName,
                Operations = ops,
                From = string.IsNullOrWhiteSpace(from) ? null : from,
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
            // Construct block: a bare child block builds a sub-object as a
            // composite, not an object-field descent.
            if (!TryParseOperation(child, childPos, log, ops, allowObjectDescent: false))
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
