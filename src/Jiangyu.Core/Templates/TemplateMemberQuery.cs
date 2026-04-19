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
            Members = TemplateTypeCatalog.GetMembers(type),
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

        for (var i = 0; i < fieldSegments.Length; i++)
        {
            var segment = fieldSegments[i];
            var bracketIndex = segment.IndexOf('[');
            var memberName = bracketIndex == -1 ? segment : segment[..bracketIndex];
            var hasIndexer = bracketIndex != -1;

            var members = TemplateTypeCatalog.GetMembers(currentType, includeReadOnly: true);
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
            };
        }

        return new QueryResult
        {
            Kind = QueryResultKind.TypeNode,
            ResolvedPath = resolvedPath,
            CurrentType = currentType,
            Members = TemplateTypeCatalog.GetMembers(currentType),
            IsWritable = lastWritable,
            UnwrappedFrom = unwrappedFrom,
        };
    }

    private static CompiledTemplateScalarValueKind? MapScalarKind(Type type)
    {
        if (type.IsEnum)
            return CompiledTemplateScalarValueKind.Enum;

        return type.FullName switch
        {
            "System.Boolean" => CompiledTemplateScalarValueKind.Boolean,
            "System.Int32" => CompiledTemplateScalarValueKind.Int32,
            "System.Single" => CompiledTemplateScalarValueKind.Single,
            "System.String" => CompiledTemplateScalarValueKind.String,
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
    public CompiledTemplateScalarValueKind? PatchScalarKind { get; init; }
    public Type? UnwrappedFrom { get; init; }
    public string? ErrorMessage { get; init; }

    public static QueryResult FromError(string message) => new()
    {
        Kind = QueryResultKind.Error,
        ErrorMessage = message,
    };
}
