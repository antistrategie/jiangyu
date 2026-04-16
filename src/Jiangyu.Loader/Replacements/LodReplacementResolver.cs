#nullable enable
namespace Jiangyu.Loader.Replacements;

internal static class LodReplacementResolver
{
    public static string? FindNearestAvailableTarget(IEnumerable<string> availableNames, string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return null;

        var available = availableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (available.Contains(requestedName, StringComparer.Ordinal))
            return requestedName;

        if (!TryParseLodName(requestedName, out var requestedBaseName, out var requestedLod))
            return null;

        var candidates = available
            .Select(name => TryParseLodName(name, out var baseName, out var lod)
                ? new LodCandidate(name, baseName, lod)
                : null)
            .Where(candidate => candidate is not null &&
                                string.Equals(candidate.BaseName, requestedBaseName, StringComparison.OrdinalIgnoreCase))
            .Cast<LodCandidate>()
            .OrderBy(candidate => Math.Abs(candidate.LodIndex - requestedLod))
            .ThenBy(candidate => candidate.LodIndex)
            .ToArray();

        return candidates.FirstOrDefault()?.Name;
    }

    internal static bool TryParseLodName(string meshName, out string baseName, out int lodIndex)
    {
        baseName = string.Empty;
        lodIndex = -1;

        if (string.IsNullOrWhiteSpace(meshName))
            return false;

        var markerIndex = meshName.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            return false;

        var suffix = meshName[(markerIndex + 4)..];
        if (!int.TryParse(suffix, out lodIndex))
            return false;

        baseName = meshName[..markerIndex];
        return !string.IsNullOrWhiteSpace(baseName);
    }

    private sealed class LodCandidate
    {
        public string Name { get; }
        public string BaseName { get; }
        public int LodIndex { get; }

        public LodCandidate(string name, string baseName, int lodIndex)
        {
            Name = name;
            BaseName = baseName;
            LodIndex = lodIndex;
        }
    }
}
