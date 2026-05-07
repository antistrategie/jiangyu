using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// jq-like navigator over a <see cref="TemplateTypeCatalog"/>. Accepts a path
/// of the shape <c>TypeName[.member[.member[[N]...]]]</c> — where the type
/// prefix can be short or fully-qualified, and the remainder is a
/// <see cref="TemplatePatchPathValidator"/>-compatible field path. Collection
/// members are auto-unwrapped to their element type so <c>Skills</c> and
/// <c>Skills[0]</c> resolve identically for type discovery, while still
/// letting modders copy the <c>[0]</c> form straight into a patch.
/// </summary>
public static class TemplateMemberQuery
{
    /// <summary>
    /// Resolve <paramref name="query"/> against <paramref name="catalog"/>.
    /// </summary>
    /// <param name="subtypeHints">
    /// Per-segment concrete-subtype overrides used to descend through
    /// polymorphic reference arrays. Keyed by segment index.
    /// </param>
    /// <param name="namespaceHint">
    /// Optional CLR namespace of the source type, forwarded to
    /// <see cref="TemplateTypeCatalog.ResolveType"/> to disambiguate
    /// short-name collisions. See that method's docs for the prefix-stripping
    /// rules.
    /// </param>
    /// <param name="elementTypeContext">
    /// Optional parent collection element-type. When the caller knows the
    /// query's root type is a polymorphic subtype within a known family
    /// (e.g. the visual editor's "Attack" picked from a list of
    /// <c>SkillEventHandlerTemplate</c> descendants), passing the family
    /// here restricts short-name resolution to the subtype set, picking
    /// <c>Effects.Attack</c> over the unrelated <c>AI.Behaviors.Attack</c>
    /// without needing a namespace hint. Ignored when the type prefix
    /// already resolves unambiguously.
    /// </param>
    public static QueryResult Run(
        TemplateTypeCatalog catalog,
        string query,
        IReadOnlyDictionary<int, string>? subtypeHints = null,
        string? namespaceHint = null,
        string? elementTypeContext = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryResult.FromError("query is empty.");

        var trimmed = query.Trim();
        if (trimmed.StartsWith('.') || trimmed.EndsWith('.'))
            return QueryResult.FromError("query cannot start or end with '.'.");

        var segments = trimmed.Split('.');
        if (segments.Any(string.IsNullOrWhiteSpace))
            return QueryResult.FromError("query contains empty segment.");

        // Find the longest prefix of dot-joined identifier segments (i.e.
        // segments without '[') that resolves to a known type. Anything after
        // becomes the field path.
        var typeCutoff = FindBestTypePrefix(catalog, segments, namespaceHint, out var resolvedType, out var resolutionError);

        // When the bare type lookup is ambiguous AND the caller supplied an
        // element-type context, retry the resolution restricted to subtypes
        // of that family. Picks Effects.Attack over AI.Behaviors.Attack
        // without making the caller produce an FQN or namespace hint.
        if (resolvedType == null
            && !string.IsNullOrWhiteSpace(elementTypeContext)
            && segments.Length > 0)
        {
            var firstSegment = segments[0];
            // Only retry when the first segment was the type-prefix attempt;
            // ambiguity past the first segment is a different shape (a
            // member off a resolved type, etc.) and not what this hint is for.
            var elementContextType = catalog.ResolveType(elementTypeContext, out _, out _);
            if (elementContextType != null)
            {
                var subtypeMatch = catalog.ResolveSubtypeHint(elementContextType, firstSegment, out _);
                if (subtypeMatch != null)
                {
                    resolvedType = subtypeMatch;
                    typeCutoff = 1;
                    resolutionError = null;
                }
            }
        }

        if (resolvedType == null)
            return QueryResult.FromError(resolutionError ?? $"no type prefix in '{trimmed}' matched a known type.");

        var typeName = string.Join('.', segments.Take(typeCutoff));
        var fieldSegments = segments.Skip(typeCutoff).ToArray();

        if (fieldSegments.Length == 0)
            return TypeNodeFor(catalog, resolvedType, typeName);

        var fieldPath = string.Join('.', fieldSegments);
        if (!TemplatePatchPathValidator.IsSupportedFieldPath(fieldPath))
            return QueryResult.FromError($"field path '{fieldPath}' is not a supported patch path.");

        return NavigateFieldPath(catalog, resolvedType, typeName, fieldSegments, subtypeHints);
    }

    private static int FindBestTypePrefix(
        TemplateTypeCatalog catalog,
        string[] segments,
        string? namespaceHint,
        out Type? resolvedType,
        out string? error)
    {
        resolvedType = null;
        error = null;

        int firstBracketed = Array.FindIndex(segments, s => s.Contains('['));
        int maxPrefixLen = firstBracketed == -1 ? segments.Length : firstBracketed;

        int bestLen = 0;
        Type? bestType = null;
        string? lastError = null;
        IReadOnlyList<Type>? ambiguousCandidates = null;

        for (var len = 1; len <= maxPrefixLen; len++)
        {
            var candidate = string.Join('.', segments.Take(len));
            var match = catalog.ResolveType(candidate, out var candidates, out var resolveError, namespaceHint);
            if (match != null)
            {
                bestLen = len;
                bestType = match;
                lastError = null;
                ambiguousCandidates = null;
            }
            else if (candidates.Count > 1)
            {
                lastError = resolveError;
                ambiguousCandidates = candidates;
            }
        }

        resolvedType = bestType;
        if (bestType == null && ambiguousCandidates != null)
        {
            error = lastError + " candidates: " + string.Join(", ", ambiguousCandidates.Select(t => t.FullName));
        }
        return bestLen;
    }

    private static QueryResult TypeNodeFor(TemplateTypeCatalog catalog, Type type, string resolvedPath)
    {
        return new QueryResult
        {
            Kind = QueryResultKind.TypeNode,
            ResolvedPath = resolvedPath,
            CurrentType = type,
            Members = catalog.EnrichMembers(type, TemplateTypeCatalog.GetMembers(type, includeReadOnly: true)),
        };
    }

    private static QueryResult NavigateFieldPath(
        TemplateTypeCatalog catalog,
        Type rootType,
        string typeName,
        string[] fieldSegments,
        IReadOnlyDictionary<int, string>? subtypeHints)
    {
        var resolvedPathParts = new List<string> { typeName };
        var currentType = rootType;
        Type? lastMemberType = null;
        Type? unwrappedFrom = null;
        bool lastWritable = true;
        bool lastOdinOnly = false;
        bool lastSegmentHadIndexer = false;
        string? lastNamedArrayEnum = null;

        for (var i = 0; i < fieldSegments.Length; i++)
        {
            var segment = fieldSegments[i];
            var bracketIndex = segment.IndexOf('[');
            var memberName = bracketIndex == -1 ? segment : segment[..bracketIndex];
            var hasIndexer = bracketIndex != -1;
            lastSegmentHadIndexer = hasIndexer;

            var members = catalog.EnrichMembers(currentType, TemplateTypeCatalog.GetMembers(currentType, includeReadOnly: true));
            var member = members.FirstOrDefault(m => m.Name == memberName);
            if (member == null)
            {
                var attempted = string.Join('.', resolvedPathParts.Concat([segment]));
                return QueryResult.FromError(
                    $"member '{memberName}' not found on '{catalog.FriendlyName(currentType)}' (while resolving '{attempted}').");
            }

            resolvedPathParts.Add(segment);
            lastMemberType = member.MemberType;
            lastWritable = member.IsWritable;
            lastOdinOnly = member.IsLikelyOdinOnly;
            // Only carry the NamedArray enum forward when the terminal step
            // actually lands on the array itself — a path like
            // `InitialAttributes.Foo` that navigates past the array element
            // is no longer the NamedArray itself.
            lastNamedArrayEnum = (i == fieldSegments.Length - 1 && !hasIndexer)
                ? member.NamedArrayEnumTypeName
                : null;
            unwrappedFrom = null;

            var memberType = member.MemberType;
            var elementType = TemplateTypeCatalog.GetElementType(memberType);

            if (hasIndexer && elementType == null)
            {
                var attempted = string.Join('.', resolvedPathParts);
                return QueryResult.FromError(
                    $"indexer applied to non-collection member '{member.Name}' of type '{catalog.FriendlyName(memberType)}' (at '{attempted}').");
            }

            if (elementType != null)
            {
                // Auto-unwrap collections regardless of bracket syntax. jq-like
                // navigation treats Skills and Skills[0] identically for type
                // discovery — matching the patch path syntax in both directions.
                currentType = elementType;
                lastMemberType = elementType;
                unwrappedFrom = memberType;

                // Polymorphic descent: when the unwrapped element type has
                // strict reference subtypes (e.g. SkillEventHandlerTemplate
                // → Attack/AddSkill/...), the catalogue can't see the inner
                // members of the concrete instance from the abstract base
                // alone. The modder declares the concrete subtype via
                // type="X" on the descent block; the parser threads it
                // through as SubtypeHints[i]. Switch the navigator to that
                // type before continuing.
                if (hasIndexer
                    && i < fieldSegments.Length - 1
                    && catalog.HasReferenceSubtype(elementType))
                {
                    var hint = subtypeHints != null && subtypeHints.TryGetValue(i, out var hinted) ? hinted : null;
                    if (hint == null)
                    {
                        var attempted = string.Join('.', resolvedPathParts);
                        return QueryResult.FromError(
                            $"polymorphic descent through '{member.Name}[…]' "
                            + $"(element type '{catalog.FriendlyName(elementType)}' has subclasses) "
                            + "requires a type=\"<ConcreteType>\" hint on the descent block "
                            + $"(at '{attempted}').");
                    }

                    // Resolve the hint within the element type's concrete-
                    // subtype set, not against the whole assembly. This both
                    // disambiguates (e.g. "Attack" picks Effects.Attack
                    // unambiguously over AI.Behaviors.Attack, which is not a
                    // SkillEventHandlerTemplate subtype) and tightens the
                    // error message when the hint is genuinely off-tree.
                    var resolved = catalog.ResolveSubtypeHint(elementType, hint, out var matchedFullNames);
                    if (resolved == null)
                    {
                        var note = matchedFullNames.Count > 0
                            ? " candidates: " + string.Join(", ", matchedFullNames)
                            : string.Empty;
                        return QueryResult.FromError(
                            $"type=\"{hint}\" on descent into '{member.Name}[…]' "
                            + $"is not a subtype of '{catalog.FriendlyName(elementType)}'.{note}");
                    }

                    if (!elementType.IsAssignableFrom(resolved))
                    {
                        return QueryResult.FromError(
                            $"type=\"{hint}\" is not a subtype of '{catalog.FriendlyName(elementType)}' "
                            + $"required by '{member.Name}[…]'.");
                    }

                    currentType = resolved;
                    lastMemberType = resolved;
                }
            }
            else
            {
                currentType = memberType;

                // Scalar polymorphic descent: a non-collection field whose
                // declared type is a polymorphic base (e.g. Odin-routed
                // Attack.DamageFilterCondition: ITacticalCondition). The
                // modder supplies type="<Subtype>" on the outer descent
                // block; the parser threads it through subtypeHints. Switch
                // currentType so subsequent segments resolve subclass
                // members. We only perform this when the next segment is
                // about to navigate past this one (i < length-1) and there
                // is something to switch to.
                if (!hasIndexer
                    && i < fieldSegments.Length - 1
                    && subtypeHints != null
                    && subtypeHints.TryGetValue(i, out var scalarHint)
                    && !string.IsNullOrEmpty(scalarHint))
                {
                    var resolvedScalar = catalog.ResolveSubtypeHint(memberType, scalarHint, out var matchedFullNames);
                    if (resolvedScalar == null)
                    {
                        var note = matchedFullNames.Count > 0
                            ? " candidates: " + string.Join(", ", matchedFullNames)
                            : string.Empty;
                        return QueryResult.FromError(
                            $"type=\"{scalarHint}\" on descent into '{member.Name}' "
                            + $"is not a subtype of '{catalog.FriendlyName(memberType)}'.{note}");
                    }
                    currentType = resolvedScalar;
                    lastMemberType = resolvedScalar;
                }
            }
        }

        var resolvedPath = string.Join('.', resolvedPathParts);

        if (lastMemberType != null && TemplateTypeCatalog.IsScalar(lastMemberType))
        {
            return new QueryResult
            {
                Kind = QueryResultKind.Leaf,
                ResolvedPath = resolvedPath,
                CurrentType = lastMemberType,
                IsWritable = lastWritable,
                PatchScalarKind = MapScalarKind(lastMemberType),
                EnumMemberNames = TemplateTypeCatalog.GetEnumMemberNames(lastMemberType),
                IsLikelyOdinOnly = lastOdinOnly,
                UnwrappedFrom = unwrappedFrom,
                NamedArrayEnumTypeName = lastNamedArrayEnum,
            };
        }

        if (lastMemberType != null
            && TemplateTypeCatalog.IsTemplateReferenceTarget(lastMemberType)
            && lastSegmentHadIndexer)
        {
            return new QueryResult
            {
                Kind = QueryResultKind.Leaf,
                ResolvedPath = resolvedPath,
                CurrentType = lastMemberType,
                IsWritable = lastWritable,
                PatchScalarKind = CompiledTemplateValueKind.TemplateReference,
                ReferenceTargetTypeName = catalog.FriendlyName(lastMemberType),
                IsLikelyOdinOnly = lastOdinOnly,
                UnwrappedFrom = unwrappedFrom,
            };
        }

        return new QueryResult
        {
            Kind = QueryResultKind.TypeNode,
            ResolvedPath = resolvedPath,
            CurrentType = currentType,
            NamedArrayEnumTypeName = lastNamedArrayEnum,
            Members = catalog.EnrichMembers(currentType, TemplateTypeCatalog.GetMembers(currentType, includeReadOnly: true)),
            IsWritable = lastWritable,
            UnwrappedFrom = unwrappedFrom,
            PatchScalarKind = lastMemberType != null
                && TemplateTypeCatalog.IsTemplateReferenceTarget(lastMemberType)
                && !lastSegmentHadIndexer
                ? CompiledTemplateValueKind.TemplateReference
                : null,
            ReferenceTargetTypeName = lastMemberType != null
                && TemplateTypeCatalog.IsTemplateReferenceTarget(lastMemberType)
                && !lastSegmentHadIndexer
                ? catalog.FriendlyName(lastMemberType)
                : null,
            IsLikelyOdinOnly = lastOdinOnly,
        };
    }

    /// <summary>
    /// Canonical scalar-kind mapping. Folds sibling integer widths (sbyte/
    /// short/ushort/uint/long/ulong) onto <see cref="CompiledTemplateValueKind.Int32"/>
    /// and <see cref="double"/> onto <see cref="CompiledTemplateValueKind.Single"/>;
    /// the loader applier range-checks at apply time. Public so RPC layers
    /// (member listings) can consult the same table — keeps the editor's
    /// "Repetitions" UInt16 → Int32 control choice in sync with leaf queries.
    /// </summary>
    public static CompiledTemplateValueKind? MapScalarKind(Type type)
    {
        if (type.IsEnum)
            return CompiledTemplateValueKind.Enum;

        return type.FullName switch
        {
            "System.Boolean" => CompiledTemplateValueKind.Boolean,
            "System.Byte" => CompiledTemplateValueKind.Byte,
            "System.Int32" => CompiledTemplateValueKind.Int32,
            // Sibling integer widths share the Int32 patch kind: editor input
            // is the same and the loader range-checks on apply.
            "System.SByte" => CompiledTemplateValueKind.Int32,
            "System.Int16" => CompiledTemplateValueKind.Int32,
            "System.UInt16" => CompiledTemplateValueKind.Int32,
            "System.UInt32" => CompiledTemplateValueKind.Int32,
            "System.Int64" => CompiledTemplateValueKind.Int32,
            "System.UInt64" => CompiledTemplateValueKind.Int32,
            "System.Single" => CompiledTemplateValueKind.Single,
            "System.Double" => CompiledTemplateValueKind.Single,
            "System.String" => CompiledTemplateValueKind.String,
            _ => null,
        };
    }
}

public enum QueryResultKind
{
    TypeNode,
    Leaf,
    Error,
}

public sealed class QueryResult
{
    public QueryResultKind Kind { get; init; }
    public string? ResolvedPath { get; init; }
    public Type? CurrentType { get; init; }
    public IReadOnlyList<MemberShape>? Members { get; init; }
    public bool IsWritable { get; init; } = true;
    public CompiledTemplateValueKind? PatchScalarKind { get; init; }
    public Type? UnwrappedFrom { get; init; }
    public IReadOnlyList<string> EnumMemberNames { get; init; } = [];
    public string? ReferenceTargetTypeName { get; init; }
    public bool IsLikelyOdinOnly { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>
    /// When the terminal member is a <c>[NamedArray(typeof(T))]</c> primitive
    /// array, the short name of the paired enum. Non-null implies the array
    /// is fixed-size and enum-indexed — append/insert/remove are semantically
    /// invalid on these fields.
    /// </summary>
    public string? NamedArrayEnumTypeName { get; init; }

    public static QueryResult FromError(string message) => new()
    {
        Kind = QueryResultKind.Error,
        ErrorMessage = message,
    };
}
