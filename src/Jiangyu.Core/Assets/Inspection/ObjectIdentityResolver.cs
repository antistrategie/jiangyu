using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public static class ObjectIdentityResolver
{
    public static ObjectResolutionResult Resolve(AssetIndex? index, string name, string? className)
    {
        if (index?.Assets is null)
        {
            return new ObjectResolutionResult { Status = ObjectResolutionStatus.IndexUnavailable };
        }

        var candidates = index.Assets
            .Where(asset =>
                string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)
                && (className is null || string.Equals(asset.ClassName, className, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(asset => asset.Collection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.PathId)
            .ThenBy(asset => asset.ClassName, StringComparer.OrdinalIgnoreCase)
            .Select(asset => new ResolvedObjectCandidate
            {
                Name = asset.Name ?? "(unnamed)",
                ClassName = asset.ClassName ?? "Unknown",
                Collection = asset.Collection ?? "",
                PathId = asset.PathId,
            })
            .ToList();

        return candidates.Count switch
        {
            0 => new ObjectResolutionResult { Status = ObjectResolutionStatus.NotFound },
            1 => new ObjectResolutionResult
            {
                Status = ObjectResolutionStatus.Success,
                Resolved = candidates[0],
                Candidates = candidates,
            },
            _ => new ObjectResolutionResult
            {
                Status = ObjectResolutionStatus.Ambiguous,
                Candidates = candidates,
            },
        };
    }
}
