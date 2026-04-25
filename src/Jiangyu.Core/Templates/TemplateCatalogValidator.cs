using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Catalog-aware semantic validator for compiled template patches and clones.
/// Complements <see cref="TemplatePatchPathValidator"/> (which only checks
/// path syntax) by confirming that each <c>TemplateType</c> resolves to a
/// known type and each <c>FieldPath</c> names a real member of that type.
/// Composite values are recursed into.
///
/// Writes a diagnostic per failure through the provided <see cref="ILogSink"/>
/// and returns the total error count so callers (compile, tests) can gate on
/// a zero-error result.
/// </summary>
public static class TemplateCatalogValidator
{
    public static int Validate(
        IEnumerable<CompiledTemplatePatch>? patches,
        IEnumerable<CompiledTemplateClone>? clones,
        TemplateTypeCatalog catalog,
        ILogSink log)
    {
        var errors = 0;

        if (patches != null)
        {
            foreach (var patch in patches)
                errors += ValidatePatch(patch, catalog, log);
        }

        if (clones != null)
        {
            foreach (var clone in clones)
                errors += ValidateClone(clone, catalog, log);
        }

        return errors;
    }

    /// <summary>
    /// Applies the same catalog-aware semantic rules used at compile time to an
    /// editor document in memory. Appends validation errors to
    /// <see cref="KdlEditorDocument.Errors"/> and normalises directive value
    /// payloads in-place (numeric kind coercion, implicit concrete references).
    /// </summary>
    public static void ValidateEditorDocument(
        KdlEditorDocument document,
        TemplateTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var node in document.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.TemplateType))
                continue;

            var resolvedType = catalog.ResolveType(node.TemplateType, out _, out var typeError);
            if (resolvedType == null)
            {
                document.Errors.Add(new KdlEditorError
                {
                    Message = typeError ?? $"Unknown template type '{node.TemplateType}'.",
                    Line = node.Line,
                });
                continue;
            }

            foreach (var directive in node.Directives)
            {
                if (string.IsNullOrWhiteSpace(directive.FieldPath))
                    continue;

                var op = KdlTemplateParser.EditorDirectiveToCompiled(directive);
                var localErrors = new List<string>();
                var errorCount = ValidateOperation(
                    op,
                    node.TemplateType,
                    catalog,
                    message => localErrors.Add(message));

                if (errorCount > 0)
                {
                    var line = directive.Line ?? node.Line;
                    foreach (var message in localErrors)
                    {
                        document.Errors.Add(new KdlEditorError
                        {
                            Message = message,
                            Line = line,
                        });
                    }
                    continue;
                }

                // Keep the editor directive in sync with any canonicalisation
                // done by semantic validation (numeric kind coercion,
                // concrete-field string -> TemplateReference, etc.).
                var normalised = KdlTemplateParser.CompiledOpToEditorDirective(op);
                directive.Op = normalised.Op;
                directive.FieldPath = normalised.FieldPath;
                directive.Index = normalised.Index;
                directive.Value = normalised.Value;
            }
        }
    }

    private static int ValidatePatch(CompiledTemplatePatch patch, TemplateTypeCatalog catalog, ILogSink log)
    {
        var templateType = patch.TemplateType ?? "EntityTemplate";
        var label = $"{templateType}:{patch.TemplateId}";

        var type = catalog.ResolveType(templateType, out _, out var typeError);
        if (type == null)
        {
            log.Error($"Template patch '{label}' — {typeError ?? $"unknown template type '{templateType}'."}");
            return 1;
        }

        var errors = 0;
        foreach (var op in patch.Set)
        {
            errors += ValidateOperation(
                op,
                templateType,
                catalog,
                message => log.Error($"Template patch '{label}.{op.FieldPath}' — {message}"));
        }
        return errors;
    }

    private static int ValidateClone(CompiledTemplateClone clone, TemplateTypeCatalog catalog, ILogSink log)
    {
        var templateType = clone.TemplateType ?? "";
        if (string.IsNullOrWhiteSpace(templateType)) return 0;

        var label = $"{templateType}:{clone.CloneId}";
        var type = catalog.ResolveType(templateType, out _, out var typeError);
        if (type == null)
        {
            log.Error($"Template clone '{label}' — {typeError ?? $"unknown template type '{templateType}'."}");
            return 1;
        }
        return 0;
    }

    private static int ValidateOperation(
        CompiledTemplateSetOperation op,
        string templateType,
        TemplateTypeCatalog catalog,
        Action<string> reportError)
    {
        if (string.IsNullOrWhiteSpace(op.FieldPath)) return 0;

        var queryPath = $"{templateType}.{op.FieldPath}";
        var result = TemplateMemberQuery.Run(catalog, queryPath);
        if (result.Kind == QueryResultKind.Error)
        {
            reportError($"'{op.FieldPath}' is not a field of {templateType}.");
            return 1;
        }

        if (op.Value is not null
            && result.PatchScalarKind is { } scalarKind
            && (scalarKind == CompiledTemplateValueKind.Byte
                || scalarKind == CompiledTemplateValueKind.Int32
                || scalarKind == CompiledTemplateValueKind.Single))
        {
            if (!TemplateValueCoercion.TryCoerceNumericKind(op.Value, scalarKind, out var coercionError))
            {
                reportError(coercionError ?? $"cannot coerce value kind {op.Value.Kind} to {scalarKind}.");
                return 1;
            }
        }

        // NamedArray fields ([NamedArray(typeof(T))] byte[]) are fixed-size
        // lookups keyed by the enum — one slot per enum member. Append/Insert/
        // Remove would shift the slot-to-enum correspondence and break the
        // invariant, so only Set with an in-range index makes sense.
        if (result.NamedArrayEnumTypeName is not null
            && (op.Op == CompiledTemplateOp.Append
                || op.Op == CompiledTemplateOp.InsertAt
                || op.Op == CompiledTemplateOp.Remove))
        {
            reportError(
                $"op={op.Op} is not supported on the "
                + $"fixed-size {result.NamedArrayEnumTypeName}-indexed array '{op.FieldPath}'; "
                + "use 'set' with index= to change one slot.");
            return 1;
        }

        // `set` on a whole collection would replace the list wholesale — use
        // Append/Insert/Remove for collection edits, or `set "<Field>" index=N`
        // to write one element. The query auto-unwraps collections, so
        // UnwrappedFrom is non-null whenever the terminal landed on a
        // collection. Without an explicit index, that's a whole-collection
        // set; with one, it's an element set (handled by the applier).
        if (op.Op == CompiledTemplateOp.Set && result.UnwrappedFrom is not null && !op.Index.HasValue)
        {
            reportError(
                "op=Set on a collection requires an 'index' field; "
                + $"use 'set \"{op.FieldPath}\" index=N <value>' to write one element, "
                + "or Append/Insert/Remove for list-level edits.");
            return 1;
        }

        // Reverse guard: index= on a scalar member is nonsensical and likely
        // a copy-paste mistake.
        if (op.Op == CompiledTemplateOp.Set && result.UnwrappedFrom is null && op.Index.HasValue)
        {
            reportError(
                $"op=Set carries index={op.Index.Value} "
                + "but the target is not a collection; remove the index field.");
            return 1;
        }

        // Reference handling. The catalog is the single source of truth for
        // the lookup type — modders don't have to repeat ref="…" when the
        // declared field type is concrete (e.g. PerkTemplate). Two paths:
        //   - value.Kind == String on a reference-target field → coerce to a
        //     TemplateReference value with TemplateType=null. Loader derives
        //     the lookup type from the field at apply time.
        //   - value.Kind == TemplateReference: validate ref= matches the
        //     declared type when present; require ref= when the declared type
        //     is abstract (polymorphic).
        var declaredType = result.CurrentType;
        var fieldIsReferenceTarget = declaredType is not null
            && TemplateTypeCatalog.IsTemplateReferenceTarget(declaredType);

        if (op.Value is not null)
        {
            if (fieldIsReferenceTarget && op.Value.Kind == CompiledTemplateValueKind.String)
            {
                if (declaredType!.IsAbstract)
                {
                    reportError(
                        $"field type {catalog.FriendlyName(declaredType)} "
                        + "is polymorphic; specify ref=\"<TemplateType>\" to disambiguate.");
                    return 1;
                }
                op.Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.TemplateReference,
                    Reference = new CompiledTemplateReference
                    {
                        TemplateType = null,
                        TemplateId = op.Value.String ?? string.Empty,
                    },
                };
            }
            else if (op.Value.Kind == CompiledTemplateValueKind.TemplateReference)
            {
                if (!fieldIsReferenceTarget)
                {
                    reportError(
                        "ref= on a non-reference field "
                        + $"(declared {catalog.FriendlyName(declaredType ?? typeof(object))}).");
                    return 1;
                }
                var refPayload = op.Value.Reference;
                if (refPayload?.TemplateType is { } explicitType)
                {
                    var resolved = catalog.ResolveType(explicitType, out _, out _);
                    if (resolved is null || !declaredType!.IsAssignableFrom(resolved))
                    {
                        reportError(
                            $"ref=\"{explicitType}\" is not assignable to "
                            + $"{catalog.FriendlyName(declaredType!)}.");
                        return 1;
                    }
                }
                else if (declaredType!.IsAbstract)
                {
                    reportError(
                        $"field type {catalog.FriendlyName(declaredType)} "
                        + "is polymorphic; specify ref=\"<TemplateType>\".");
                    return 1;
                }
            }
        }

        if (op.Value?.Kind == CompiledTemplateValueKind.Composite && op.Value.Composite != null)
            return ValidateCompositeValue(op.Value.Composite, op.FieldPath, catalog, reportError);

        return 0;
    }

    private static int ValidateCompositeValue(
        CompiledTemplateComposite composite,
        string contextPath,
        TemplateTypeCatalog catalog,
        Action<string> reportError)
    {
        if (string.IsNullOrWhiteSpace(composite.TypeName)) return 0;

        var type = catalog.ResolveType(composite.TypeName, out _, out var typeError);
        if (type == null)
        {
            reportError(typeError ?? $"unknown composite type '{composite.TypeName}'.");
            return 1;
        }

        var errors = 0;
        foreach (var (fieldName, value) in composite.Fields)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) continue;

            var result = TemplateMemberQuery.Run(catalog, $"{composite.TypeName}.{fieldName}");
            if (result.Kind == QueryResultKind.Error)
            {
                reportError($"'{fieldName}' is not a field of {composite.TypeName}.");
                errors++;
                continue;
            }

            if (value.Kind == CompiledTemplateValueKind.Composite && value.Composite != null)
                errors += ValidateCompositeValue(value.Composite, $"{contextPath}.{fieldName}", catalog, reportError);
        }
        return errors;
    }
}
