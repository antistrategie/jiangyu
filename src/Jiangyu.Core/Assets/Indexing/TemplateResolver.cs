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

        // The index files classes by short name with the namespace beside it. A compiled
        // reference may carry the full name (the compiler writes one where the short name
        // is shared): the segment after the last '.' or '+' is the class the index is asked
        // for, and the namespace before it, with the interop's Il2Cpp prefix dropped,
        // picks between twins when the index knows their namespaces. A nested name (with
        // a '+') is matched by class alone, since the index files nested classes by their
        // own name. A mod's ns:Name is not a dotted class name at all.
        var cut = className.Contains(':') ? -1 : Math.Max(className.LastIndexOf('.'), className.LastIndexOf('+'));
        var shortClassName = cut >= 0 ? className[(cut + 1)..] : className;
        var namespaceName = cut >= 0 && className.LastIndexOf('.') is var dot && dot > 0 && !className.Contains('+')
            ? className[..dot]
            : null;
        if (namespaceName != null && namespaceName.StartsWith("Il2Cpp", StringComparison.Ordinal))
            namespaceName = namespaceName["Il2Cpp".Length..];

        var candidates = index.Instances
            .Where(instance =>
                string.Equals(instance.ClassName, shortClassName, StringComparison.OrdinalIgnoreCase)
                && (namespaceName is null
                    || instance.NamespaceName is null
                    || string.Equals(instance.NamespaceName, namespaceName, StringComparison.OrdinalIgnoreCase))
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
