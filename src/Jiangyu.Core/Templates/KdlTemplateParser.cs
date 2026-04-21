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

        if (!node.HasChildren)
        {
            log.Error($"{pos}: 'patch' block for '{templateType}:{templateId}' has no operations.");
            return false;
        }

        var ops = new List<CompiledTemplateSetOperation>();
        foreach (var child in node.Children)
        {
            var childPos = FormatPos(pos, child.SourcePosition);
            if (!TryParseOperation(child, childPos, log, out var op))
                return false;
            ops.Add(op);
        }

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
            foreach (var child in node.Children)
            {
                var childPos = FormatPos(pos, child.SourcePosition);
                if (!TryParseOperation(child, childPos, log, out var op))
                    return false;
                ops.Add(op);
            }

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

    // set "fieldPath" <value>
    // append "fieldPath" <value>
    // insert "fieldPath" index=N <value>
    // remove "fieldPath"
    private static bool TryParseOperation(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplateSetOperation op)
    {
        op = null!;

        var opName = node.Name;
        CompiledTemplateOp opKind;
        switch (opName)
        {
            case "set": opKind = CompiledTemplateOp.Set; break;
            case "append": opKind = CompiledTemplateOp.Append; break;
            case "insert": opKind = CompiledTemplateOp.InsertAt; break;
            case "remove": opKind = CompiledTemplateOp.Remove; break;
            default:
                log.Error($"{pos}: unknown operation '{opName}'. Expected 'set', 'append', 'insert', or 'remove'.");
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

        // Remove needs no value
        if (opKind == CompiledTemplateOp.Remove)
        {
            op = new CompiledTemplateSetOperation
            {
                Op = opKind,
                FieldPath = fieldPath,
            };
            return true;
        }

        // InsertAt needs index= property
        int? insertIndex = null;
        if (opKind == CompiledTemplateOp.InsertAt)
        {
            var indexProp = GetPropertyValue(node, "index");
            if (indexProp == null || indexProp.AsInt32() == null)
            {
                log.Error($"{pos}: 'insert' requires an index=N property.");
                return false;
            }

            insertIndex = indexProp.AsInt32();
        }

        // Parse the value
        if (!TryParseValue(node, pos, log, out var value))
            return false;

        op = new CompiledTemplateSetOperation
        {
            Op = opKind,
            FieldPath = fieldPath,
            Index = insertIndex,
            Value = value,
        };
        return true;
    }

    /// <summary>
    /// Parse the value from an operation node. The value can come from:
    /// - A second positional argument (scalar or string)
    /// - ref="Type" "id" properties/args (TemplateReference)
    /// - enum="EnumType" "value" properties/args (Enum)
    /// - composite="TypeName" with children (Composite)
    /// </summary>
    private static bool TryParseValue(
        KdlNode node, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        // Check for composite=
        var compositeType = GetProperty(node, "composite");
        if (compositeType != null)
            return TryParseCompositeValue(node, compositeType, pos, log, out value);

        // Check for ref=
        var refType = GetProperty(node, "ref");
        if (refType != null)
            return TryParseRefValue(node, refType, pos, log, out value);

        // Check for enum=
        var enumType = GetProperty(node, "enum");
        if (enumType != null)
            return TryParseEnumValue(node, enumType, pos, log, out value);

        // Must have a second argument with the literal value
        if (node.Arguments.Count < 2)
        {
            log.Error($"{pos}: '{node.Name}' requires a value (second argument, ref=, enum=, or composite=).");
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

    // composite="TypeName" { set "field" <value> ... }
    private static bool TryParseCompositeValue(
        KdlNode node, string typeName, string pos, ILogSink log,
        out CompiledTemplateValue value)
    {
        value = null!;

        if (!node.HasChildren)
        {
            log.Error($"{pos}: composite= requires child 'set' nodes for field values.");
            return false;
        }

        var fields = new Dictionary<string, CompiledTemplateValue>(StringComparer.Ordinal);
        foreach (var child in node.Children)
        {
            var childPos = FormatPos(pos, child.SourcePosition);

            if (child.Name != "set")
            {
                log.Error($"{childPos}: only 'set' is valid inside a composite block.");
                return false;
            }

            if (child.Arguments.Count < 1)
            {
                log.Error($"{childPos}: 'set' inside composite requires a field name argument.");
                return false;
            }

            var fieldName = child.Arguments[0].AsString();
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                log.Error($"{childPos}: composite field name must be a non-empty string.");
                return false;
            }

            if (!TryParseValue(child, childPos, log, out var fieldValue))
                return false;

            if (fields.ContainsKey(fieldName))
            {
                log.Error($"{childPos}: duplicate field '{fieldName}' in composite.");
                return false;
            }

            fields[fieldName] = fieldValue;
        }

        value = new CompiledTemplateValue
        {
            Kind = CompiledTemplateValueKind.Composite,
            Composite = new CompiledTemplateComposite
            {
                TypeName = typeName,
                Fields = fields,
            },
        };
        return true;
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
