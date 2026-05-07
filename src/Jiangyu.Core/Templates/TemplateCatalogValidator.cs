using Jiangyu.Core.Abstractions;
using Jiangyu.Shared.Replacements;
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
        ILogSink log,
        IAssetAdditionsCatalog? additions = null)
    {
        var errors = 0;

        if (patches != null)
        {
            foreach (var patch in patches)
                errors += ValidatePatch(patch, catalog, additions, log);
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
                    additions: null,
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

    private static int ValidatePatch(
        CompiledTemplatePatch patch,
        TemplateTypeCatalog catalog,
        IAssetAdditionsCatalog? additions,
        ILogSink log)
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
                additions,
                message => log.Error($"Template patch '{label}.{FormatFullPath(op)}' — {message}"));
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
        IAssetAdditionsCatalog? additions,
        Action<string> reportError)
    {
        if (string.IsNullOrWhiteSpace(op.FieldPath)) return 0;

        // TemplateMemberQuery still walks a flat dotted-with-bracket path,
        // so build one from the structural descent at the boundary. The
        // segment-keyed hints map mirrors the descent step subtypes.
        var (queryPath, hints) = BuildLegacyQueryPath(templateType, op);
        var result = TemplateMemberQuery.Run(catalog, queryPath, hints);
        if (result.Kind == QueryResultKind.Error)
        {
            // Surface the navigator's specific message (polymorphism hint
            // missing, subtype not assignable, indexer on a non-collection,
            // etc.) so modders see what to fix. Falling back to the generic
            // "not a field of X" eats useful context.
            reportError(result.ErrorMessage ?? $"'{FormatFullPath(op)}' is not a field of {templateType}.");
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
        // Remove/Clear would shift the slot-to-enum correspondence and break
        // the invariant, so only Set with an in-range index makes sense.
        if (result.NamedArrayEnumTypeName is not null
            && (op.Op == CompiledTemplateOp.Append
                || op.Op == CompiledTemplateOp.InsertAt
                || op.Op == CompiledTemplateOp.Remove
                || op.Op == CompiledTemplateOp.Clear))
        {
            reportError(
                $"op={op.Op} is not supported on the "
                + $"fixed-size {result.NamedArrayEnumTypeName}-indexed array '{FormatFullPath(op)}'; "
                + "use 'set' with index= to change one slot.");
            return 1;
        }

        // Clear is collection-only. UnwrappedFrom is non-null exactly when the
        // resolved terminal is a collection, so the absence of unwrapping
        // means the modder pointed Clear at a scalar field — likely a typo.
        if (op.Op == CompiledTemplateOp.Clear && result.UnwrappedFrom is null)
        {
            reportError(
                $"op=Clear targets a non-collection field "
                + $"(declared {catalog.FriendlyName(result.CurrentType ?? typeof(object))}); "
                + "Clear empties a list/array, so the destination must be a collection.");
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

        // HashSet<T> destinations have no positional order, so order-based
        // ops are nonsensical even though the syntax parses. Conversely,
        // List<T> Remove still needs index= — by-value removal is HashSet
        // territory only. Lists, arrays, and other ordered collections
        // skip all four guards.
        if (result.UnwrappedFrom is not null
            && TemplateTypeCatalog.IsHashSetCollection(result.UnwrappedFrom))
        {
            if (op.Op == CompiledTemplateOp.InsertAt)
            {
                reportError(
                    $"op=InsertAt is not supported on HashSet field '{op.FieldPath}' — "
                    + "HashSet has no positional order. Use 'append' (Add semantics) instead.");
                return 1;
            }
            if (op.Op == CompiledTemplateOp.Set && op.Index.HasValue)
            {
                reportError(
                    $"op=Set with index= is not supported on HashSet field '{op.FieldPath}' — "
                    + "HashSet has no positional addressing. Use 'append' to add or "
                    + "'remove' with a value to drop an entry.");
                return 1;
            }
            if (op.Op == CompiledTemplateOp.Remove && op.Index.HasValue)
            {
                reportError(
                    $"op=Remove on HashSet field '{op.FieldPath}' uses a value, not index= — "
                    + "remove the index property and supply the entry to drop "
                    + "(e.g. 'remove \"" + op.FieldPath + "\" enum=\"…\" \"…\"').");
                return 1;
            }
        }
        else if (op.Op == CompiledTemplateOp.Remove
            && result.UnwrappedFrom is not null
            && !op.Index.HasValue
            && op.Value is not null)
        {
            reportError(
                $"op=Remove on List-shaped field '{op.FieldPath}' requires index=N — "
                + "by-value removal is only supported on HashSet collections.");
            return 1;
        }

        // Multi-dim cell write (Phase 2d): catalog sees Odin-routed
        // multi-dim arrays as Il2CppObjectBase wrappers, so we cannot
        // structurally verify rank or element type at compile time. The
        // shape-level path validator already rejected the malformed cases
        // (negative coords, mixing index= with cell=). Runtime applier
        // does the rank/element check against the live array. Reject
        // cell= on collection destinations (lists/arrays use index=).
        if (op.Op == CompiledTemplateOp.Set && op.IndexPath is { Count: > 0 } && result.UnwrappedFrom is not null)
        {
            reportError(
                $"op=Set carries cell={string.Join(",", op.IndexPath)} but the target is a collection; "
                + "use index=N for collection writes — cell= is reserved for multi-dim arrays.");
            return 1;
        }

        // Reference / enum shorthand. The catalog is the single source of
        // truth for the destination type — modders don't have to repeat
        // ref="…" or enum="…" when the declared field type already pins
        // it down. Three coercions:
        //   - value.Kind == String on a reference-target field → coerce to
        //     a TemplateReference with TemplateType=null. Loader derives
        //     the lookup type from the field at apply time.
        //   - value.Kind == String on an enum field → coerce to an Enum
        //     with EnumType = declared type's short name. Validates the
        //     member name against the declared enum's known members.
        //   - value.Kind == TemplateReference: validate ref= matches the
        //     declared type when present; require ref= when the declared
        //     type is abstract (polymorphic).
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
            else if (op.Value.Kind == CompiledTemplateValueKind.String
                && declaredType is { IsEnum: true })
            {
                // String shorthand for enum-typed fields: derive the
                // EnumType from the declared field type so the modder
                // can write `set "MoraleState" "Fleeing"` instead of the
                // redundant `set "MoraleState" enum="MoraleState" "Fleeing"`.
                var memberName = op.Value.String ?? string.Empty;
                if (string.IsNullOrEmpty(memberName))
                {
                    reportError(
                        $"empty value on enum field '{op.FieldPath}' "
                        + $"(declared {declaredType.Name}).");
                    return 1;
                }
                if (!result.EnumMemberNames.Contains(memberName, StringComparer.Ordinal))
                {
                    reportError(
                        $"'{memberName}' is not a member of enum {declaredType.Name} "
                        + $"(known: {string.Join(", ", result.EnumMemberNames)}).");
                    return 1;
                }
                op.Value = new CompiledTemplateValue
                {
                    Kind = CompiledTemplateValueKind.Enum,
                    EnumType = declaredType.Name,
                    EnumValue = memberName,
                };
            }
            else if (op.Value.Kind == CompiledTemplateValueKind.Enum)
            {
                if (ValidateEnumValue(op.Value, declaredType, result.EnumMemberNames, reportError))
                    return 1;
            }
            else if (op.Value.Kind == CompiledTemplateValueKind.AssetReference)
            {
                if (ValidateAssetReference(op.Value, declaredType, catalog, additions, reportError))
                    return 1;
            }
        }

        if (op.Value?.Kind == CompiledTemplateValueKind.Composite && op.Value.Composite != null)
            return ValidateCompositeValue(
                op.Value.Composite,
                FormatFullPath(op),
                result.CurrentType,
                catalog,
                additions,
                reportError);

        if (op.Value?.Kind == CompiledTemplateValueKind.HandlerConstruction && op.Value.HandlerConstruction != null)
            return ValidateHandlerConstruction(op.Value.HandlerConstruction, op, result, catalog, additions, reportError);

        return 0;
    }

    /// <summary>
    /// Cross-checks an authored <see cref="CompiledTemplateValueKind.Enum"/>
    /// value against the destination field's declared enum type. Catches the
    /// common authoring mistakes at compile time so they don't reach the
    /// loader: wrong enum type name (typo from a similar-shaped class),
    /// undefined member, or enum= on a non-enum destination.
    ///
    /// Returns <c>true</c> when an error was reported. The caller should
    /// short-circuit on <c>true</c>.
    /// </summary>
    private static bool ValidateEnumValue(
        CompiledTemplateValue value,
        Type? declaredType,
        IReadOnlyList<string> declaredMembers,
        Action<string> reportError)
    {
        if (declaredType is null || !declaredType.IsEnum)
        {
            reportError(
                $"enum= value on a non-enum destination "
                + $"(declared {declaredType?.FullName ?? "<unknown>"}).");
            return true;
        }

        var declaredName = declaredType.Name;

        if (!string.IsNullOrWhiteSpace(value.EnumType)
            && !string.Equals(value.EnumType, declaredName, StringComparison.Ordinal))
        {
            reportError(
                $"enum=\"{value.EnumType}\" does not match the declared enum type "
                + $"'{declaredName}' (known members: {string.Join(", ", declaredMembers)}).");
            return true;
        }

        var enumValue = value.EnumValue;
        if (string.IsNullOrWhiteSpace(enumValue))
        {
            reportError($"enum value is empty for {declaredName}.");
            return true;
        }

        if (declaredMembers.Contains(enumValue, StringComparer.Ordinal))
            return false;

        // Numeric form: Enum.Parse accepts "8" → ItemSlot.ModularVehicleLight,
        // matching the loader's behaviour. Confirm the value is a defined
        // member rather than just any integer in range — modders shouldn't
        // be able to author a numerically-valid-but-undefined value. Reads
        // member constants via reflection so this works on types loaded via
        // MetadataLoadContext (Enum.TryParse rejects those).
        if (long.TryParse(enumValue, out var numeric))
        {
            foreach (var name in declaredMembers)
            {
                var field = declaredType.GetField(name);
                if (field?.GetRawConstantValue() is { } raw
                    && Convert.ToInt64(raw) == numeric)
                {
                    return false;
                }
            }
        }

        reportError(
            $"'{enumValue}' is not a defined member of enum '{declaredName}' "
            + $"(known members: {string.Join(", ", declaredMembers)}).");
        return true;
    }

    /// <summary>
    /// Cross-checks an authored
    /// <see cref="CompiledTemplateValueKind.AssetReference"/> value against
    /// the destination field's declared Unity type. The category folder
    /// (sprites, textures, audio, materials) is derived from that type via
    /// <see cref="AssetCategory.IsSupported"/>; this validator catches the
    /// authoring mistakes that would otherwise reach the loader: asset= on a
    /// non-asset field, an empty name, and references against deferred Unity
    /// types (Mesh / GameObject) so the modder learns about the
    /// prefab-construction dependency at compile time rather than at apply
    /// time.
    ///
    /// Returns <c>true</c> when an error was reported. The caller should
    /// short-circuit on <c>true</c>.
    /// </summary>
    private static bool ValidateAssetReference(
        CompiledTemplateValue value,
        Type? declaredType,
        TemplateTypeCatalog catalog,
        IAssetAdditionsCatalog? additions,
        Action<string> reportError)
    {
        if (declaredType is null)
        {
            reportError("asset= value on a field whose declared type could not be resolved.");
            return true;
        }

        var className = declaredType.Name;

        if (!AssetCategory.IsSupported(className))
        {
            // Surface the deferred-kinds case with the same pointer the
            // shared dispatcher would throw, so a modder reading the compile
            // error and a future implementer reading the dispatcher's stack
            // trace land on the same design doc.
            if (className is "Mesh" or "GameObject" or "PrefabHierarchyObject")
            {
                reportError(
                    $"asset= for field type {catalog.FriendlyName(declaredType)} "
                    + "is not yet supported. Mesh and prefab additions wait on "
                    + "the prefab-construction layer (see PREFAB_CLONING_TODO.md).");
            }
            else
            {
                reportError(
                    "asset= on a non-asset field type "
                    + $"(declared {catalog.FriendlyName(declaredType)}). "
                    + "Asset references resolve to Unity assets like Sprite, "
                    + "Texture2D, AudioClip, or Material.");
            }
            return true;
        }

        if (string.IsNullOrWhiteSpace(value.Asset?.Name))
        {
            reportError("asset= value has an empty name.");
            return true;
        }

        // File-existence check against assets/additions/<category>/. Only
        // runs when the compiler hands us a catalog; the editor-validation
        // path passes null because it doesn't have project-root context and
        // the actual missing-asset diagnostic surfaces at compile time.
        if (additions != null)
        {
            var category = AssetCategory.ForClassName(className);
            if (category != null && !additions.Contains(category, value.Asset!.Name))
            {
                reportError(
                    $"asset=\"{value.Asset.Name}\" was not found at "
                    + $"assets/additions/{category}/{value.Asset.Name}.<ext>. "
                    + "Add the asset file or correct the reference name.");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validate a HandlerConstruction value: the named subtype must exist,
    /// must descend from the destination collection's element type, and
    /// every inner field must resolve on the subtype with a compatible
    /// scalar kind. Optional handler= for monomorphic destinations:
    /// when the element type has no strict subtypes, an empty TypeName is
    /// allowed and the catalogue uses the element type itself.
    /// </summary>
    private static int ValidateHandlerConstruction(
        CompiledTemplateComposite construction,
        CompiledTemplateSetOperation op,
        QueryResult destination,
        TemplateTypeCatalog catalog,
        IAssetAdditionsCatalog? additions,
        Action<string> reportError)
    {
        // Two valid destinations:
        //  - Collection element-type, auto-unwrapped from the array. The
        //    canonical Append/Insert pattern: handler= constructs an owned
        //    ScriptableObject element to push into the list.
        //  - Polymorphic scalar field, where the declared type itself has
        //    concrete subtypes the modder must pick from. Phase 2b: lets
        //    Odin-routed interface/abstract scalar fields like
        //    Attack.DamageFilterCondition (ITacticalCondition) be set to a
        //    fresh concrete instance.
        //
        // CurrentType is the resolved destination type in either case; only
        // the rejection rule differs.
        var elementType = destination.CurrentType;
        if (elementType is null)
        {
            reportError($"handler= construction on '{FormatFullPath(op)}': cannot resolve destination type.");
            return 1;
        }

        var isCollectionDestination = destination.UnwrappedFrom is not null;
        // For collection-element handlers, the existing ScriptableObject-only
        // semantics apply (HasReferenceSubtype). For scalar polymorphic
        // destinations the subtypes may be plain managed classes (Odin-routed
        // interface impls, etc.), so we use the broader concrete-descendant
        // check via EnumerateConcreteSubtypes.
        var isPolymorphicScalar = !isCollectionDestination
            && catalog.EnumerateConcreteSubtypes(elementType).Count > 0;

        if (!isCollectionDestination && !isPolymorphicScalar)
        {
            reportError(
                "handler= construction targets a collection element or a "
                + "polymorphic scalar field, but field "
                + $"'{FormatFullPath(op)}' is concrete and non-polymorphic. "
                + "For a concrete inline value, use composite=\"<TypeName>\".");
            return 1;
        }

        // Resolve the named subtype. Allow empty/null when the destination
        // is monomorphic (no strict subtypes), in which case we use the
        // element type itself. For both gates we want a broad
        // concrete-descendant view: HasReferenceSubtype counts only
        // ScriptableObject descendants, while EnumerateConcreteSubtypes
        // also picks up plain managed classes via the metadata supplement
        // (Odin-routed interfaces with managed impls). The polymorphic-
        // scalar destination always needs a TypeName because we already
        // confirmed it has subtypes above.
        Type subtype;
        var typeName = construction.TypeName;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            if (isPolymorphicScalar
                || catalog.HasReferenceSubtype(elementType)
                || catalog.EnumerateConcreteSubtypes(elementType).Count > 0)
            {
                reportError(
                    $"handler= on '{FormatFullPath(op)}' must name a concrete subtype: "
                    + $"destination type {catalog.FriendlyName(elementType)} has subclasses.");
                return 1;
            }
            subtype = elementType;
        }
        else
        {
            // Restrict resolution to subtypes of the destination element
            // type. Mirrors the polymorphic-descent navigator: short-name
            // collisions with unrelated classes (production case:
            // Effects.Attack vs AI.Behaviors.Attack) are filtered out
            // structurally, so the modder's handler= picks the correct one.
            var resolved = catalog.ResolveSubtypeHint(elementType, typeName, out var ambiguous);
            if (resolved is null)
            {
                var ambiguityNote = ambiguous.Count > 0
                    ? " candidates: " + string.Join(", ", ambiguous)
                    : string.Empty;
                reportError(
                    $"handler=\"{typeName}\" is not a subtype of "
                    + $"{catalog.FriendlyName(elementType)} required by '{FormatFullPath(op)}'.{ambiguityNote}");
                return 1;
            }
            subtype = resolved;
        }

        // Inner directives validate against the resolved subtype using the
        // same op semantics as the outer level. Use FullName so ambiguous
        // short names (test fixtures with two FixtureSkillTemplate classes
        // in different namespaces, etc.) still resolve uniquely; the user-
        // facing context label keeps the friendly short name.
        return ValidateInnerOperations(
            construction.Operations,
            subtype.FullName ?? subtype.Name,
            $"handler= construction for '{FormatFullPath(op)}'",
            catalog,
            additions,
            reportError);
    }

    private static int ValidateCompositeValue(
        CompiledTemplateComposite composite,
        string contextPath,
        Type? destinationType,
        TemplateTypeCatalog catalog,
        IAssetAdditionsCatalog? additions,
        Action<string> reportError)
    {
        if (string.IsNullOrWhiteSpace(composite.TypeName)) return 0;

        // Restrict to subtypes of the destination type when one is known and
        // is itself polymorphic. This filters out short-name twins outside
        // the subtype family (the production case is Effects.Attack vs
        // AI.Behaviors.Attack: only the former assigns to a
        // SkillEventHandlerTemplate-typed destination).
        Type? type;
        if (destinationType != null && catalog.HasReferenceSubtype(destinationType))
        {
            type = catalog.ResolveSubtypeHint(destinationType, composite.TypeName, out var ambiguous);
            if (type == null)
            {
                var note = ambiguous.Count > 0
                    ? " candidates: " + string.Join(", ", ambiguous)
                    : string.Empty;
                reportError(
                    $"composite type=\"{composite.TypeName}\" is not a subtype of "
                    + $"{catalog.FriendlyName(destinationType)} required by composite '{contextPath}'.{note}");
                return 1;
            }
        }
        else
        {
            type = catalog.ResolveType(composite.TypeName, out _, out var typeError);
            if (type == null)
            {
                reportError(typeError ?? $"unknown composite type '{composite.TypeName}'.");
                return 1;
            }
        }

        return ValidateInnerOperations(
            composite.Operations,
            type.FullName ?? composite.TypeName,
            $"composite '{contextPath}'",
            catalog,
            additions,
            reportError);
    }

    private static int ValidateInnerOperations(
        List<CompiledTemplateSetOperation> operations,
        string typeName,
        string contextLabel,
        TemplateTypeCatalog catalog,
        IAssetAdditionsCatalog? additions,
        Action<string> reportError)
    {
        var errors = 0;
        foreach (var inner in operations)
        {
            void InnerReport(string message) => reportError($"{contextLabel}: {message}");
            errors += ValidateOperation(inner, typeName, catalog, additions, InnerReport);
        }
        return errors;
    }

    /// <summary>
    /// Build the legacy bracketed dotted path that <see cref="TemplateMemberQuery"/>
    /// expects, plus a segment-keyed subtype-hints map, from the structural
    /// descent on <paramref name="op"/>. The hints map keys descent-step
    /// indexes (zero-based) where the step carries a non-empty subtype.
    /// </summary>
    private static (string queryPath, Dictionary<int, string>? hints) BuildLegacyQueryPath(
        string templateType,
        CompiledTemplateSetOperation op)
    {
        if (op.Descent is not { Count: > 0 } descent)
            return ($"{templateType}.{op.FieldPath}", null);

        var sb = new System.Text.StringBuilder();
        sb.Append(templateType);
        Dictionary<int, string>? hints = null;
        for (var i = 0; i < descent.Count; i++)
        {
            var step = descent[i];
            sb.Append('.').Append(step.Field);
            // Bracketed indexer marks a collection descent for the navigator;
            // a bare member name is a scalar polymorphic descent. The
            // navigator distinguishes the two via the segment shape.
            if (step.Index.HasValue)
                sb.Append('[').Append(step.Index.Value).Append(']');
            if (!string.IsNullOrEmpty(step.Subtype))
                (hints ??= [])[i] = step.Subtype;
        }
        sb.Append('.').Append(op.FieldPath);
        return (sb.ToString(), hints);
    }

    /// <summary>
    /// Format the modder-facing full path for an op's destination, including
    /// any descent prefix in canonical bracketed form. Used in error messages
    /// so the modder sees where the failing write lives.
    /// </summary>
    private static string FormatFullPath(CompiledTemplateSetOperation op)
    {
        if (op.Descent is not { Count: > 0 } descent)
            return op.FieldPath;
        var sb = new System.Text.StringBuilder();
        foreach (var step in descent)
        {
            sb.Append(step.Field);
            if (step.Index.HasValue)
                sb.Append('[').Append(step.Index.Value).Append(']');
            sb.Append('.');
        }
        sb.Append(op.FieldPath);
        return sb.ToString();
    }
}
