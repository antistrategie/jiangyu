using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public static class TemplateResolver
{
    public static TemplateResolutionResult Resolve(TemplateIndex? index, string className, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        if (index?.Instances is null)
        {
            return new TemplateResolutionResult { Status = TemplateResolutionStatus.IndexUnavailable };
        }

        var candidates = index.Instances
            .Where(instance =>
                string.Equals(instance.ClassName, className, StringComparison.OrdinalIgnoreCase)
                && (name is null || string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(instance => instance.Identity.PathId)
            .Select(instance => new ResolvedTemplateCandidate
            {
                Name = instance.Name,
                ClassName = instance.ClassName,
                Identity = new TemplateIdentity
                {
                    Collection = instance.Identity.Collection,
                    PathId = instance.Identity.PathId,
                },
            })
            .ToList();

        return candidates.Count switch
        {
            0 => new TemplateResolutionResult { Status = TemplateResolutionStatus.NotFound },
            1 => new TemplateResolutionResult
            {
                Status = TemplateResolutionStatus.Success,
                Resolved = candidates[0],
                Candidates = candidates,
            },
            _ => new TemplateResolutionResult
            {
                Status = TemplateResolutionStatus.Ambiguous,
                Candidates = candidates,
            },
        };
    }
}
