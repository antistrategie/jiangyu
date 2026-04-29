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
    public static QueryResult Run(TemplateTypeCatalog catalog, string query)
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
        var typeCutoff = FindBestTypePrefix(catalog, segments, out var resolvedType, out var resolutionError);
        if (resolvedType == null)
            return QueryResult.FromError(resolutionError ?? $"no type prefix in '{trimmed}' matched a known type.");

        var typeName = string.Join('.', segments.Take(typeCutoff));
        var fieldSegments = segments.Skip(typeCutoff).ToArray();

        if (fieldSegments.Length == 0)
            return TypeNodeFor(catalog, resolvedType, typeName);

        var fieldPath = string.Join('.', fieldSegments);
        if (!TemplatePatchPathValidator.IsSupportedFieldPath(fieldPath))
            return QueryResult.FromError($"field path '{fieldPath}' is not a supported patch path.");

        return NavigateFieldPath(catalog, resolvedType, typeName, fieldSegments);
    }

    private static int FindBestTypePrefix(
        TemplateTypeCatalog catalog,
        string[] segments,
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
            var match = catalog.ResolveType(candidate, out var candidates, out var resolveError);
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
        string[] fieldSegments)
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
            }
            else
            {
                currentType = memberType;
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
