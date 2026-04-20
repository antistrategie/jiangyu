using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public sealed class TemplateSearchService(TemplateIndex? index)
{
    private readonly TemplateIndex? _index = index;

    public TemplateSearchResult Search(string query, string? className = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (_index is null)
        {
            return new TemplateSearchResult
            {
                Status = TemplateSearchStatus.IndexUnavailable,
            };
        }

        string trimmedQuery = query.Trim();
        string? trimmedClassName = string.IsNullOrWhiteSpace(className)
            ? null
            : className.Trim();

        List<TemplateTypeEntry> matchingTypes = trimmedClassName is null
            ?
            [
                .. _index.TemplateTypes
                    .Where(entry => Contains(entry.ClassName, trimmedQuery))
                    .OrderBy(entry => entry.ClassName, StringComparer.OrdinalIgnoreCase),
            ]
            : [];

        List<TemplateInstanceEntry> matchingInstances =
        [
            .. _index.Instances
                .Where(instance =>
                    (trimmedClassName is null || string.Equals(instance.ClassName, trimmedClassName, StringComparison.OrdinalIgnoreCase))
                    && (Contains(instance.Name, trimmedQuery)
                        || Contains(instance.Identity.Collection, trimmedQuery)
                        || Contains(instance.ClassName, trimmedQuery)))
                .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.Identity.Collection, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.Identity.PathId),
        ];

        return new TemplateSearchResult
        {
            Status = matchingTypes.Count == 0 && matchingInstances.Count == 0
                ? TemplateSearchStatus.NotFound
                : TemplateSearchStatus.Success,
            MatchingTypes = matchingTypes,
            MatchingInstances = matchingInstances,
        };
    }

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

public enum TemplateSearchStatus
{
    Success,
    NotFound,
    IndexUnavailable,
}

public sealed class TemplateSearchResult
{
    public required TemplateSearchStatus Status { get; init; }

    public List<TemplateTypeEntry> MatchingTypes { get; init; } = [];

    public List<TemplateInstanceEntry> MatchingInstances { get; init; } = [];
}
