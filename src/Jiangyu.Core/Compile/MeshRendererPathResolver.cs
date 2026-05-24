using Jiangyu.Core.Assets;
using SharpGLTF.Schema2;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Match a modder's glTF replacement file against the expected
/// SkinnedMeshRenderer paths the target prefab exposes. Two stages:
///
/// <list type="number">
///   <item><b>Discovery</b>: walk every node with a mesh in the replacement
///     glTF and emit one or more <see cref="ReplacementMeshPathCandidate"/>
///     per node (raw node path, mesh name, and node name aliases).</item>
///   <item><b>Resolution</b>: for each expected target path, pick the
///     candidate whose normalised path matches. Normalisation strips
///     <c>.001</c>-style Blender suffixes and <c>_container</c> suffixes
///     so a hand-edited rip lines up with the vanilla naming.</item>
/// </list>
///
/// <para>Plus two bundle-name helpers used by the compile pipeline once a
/// target has been resolved (<see cref="BuildBundleMeshName"/> stamps a
/// stable <c>mesh__hash</c> name onto the staged bundle), and the mesh-name
/// reconciliation pair used when the modder's mesh name carries a Blender
/// suffix the target doesn't.</para>
/// </summary>
internal static class MeshRendererPathResolver
{
    public const string DiagnosticsEnvVar = "JIANGYU_MESH_DISCOVERY_DIAGNOSTICS";

    public static IReadOnlyList<ReplacementMeshPathCandidate> DiscoverReplacementMeshPathCandidates(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var candidates = model.LogicalNodes
            .Where(node => node.Mesh != null)
            .SelectMany(node => BuildPathCandidates(node, filePath))
            .Distinct()
            .OrderBy(candidate => candidate.PathSelector, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No mesh nodes were found in replacement file '{filePath}'.");
        }

        return candidates;
    }

    public static IReadOnlyList<ResolvedReplacementRendererTarget> ResolveReplacementRendererTargets(
        IReadOnlyList<SkinnedMeshTarget> expectedTargets,
        IReadOnlyList<ReplacementMeshPathCandidate> providedCandidates,
        out string[] missingTargetPaths)
    {
        var byNormalisedPath = providedCandidates
            .GroupBy(candidate => candidate.MatchPath, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(candidate => candidate.PathSelector)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var usedSourceSelectors = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<ResolvedReplacementRendererTarget>();
        var missing = new List<string>();

        foreach (var target in expectedTargets.OrderBy(target => target.RendererPath, StringComparer.Ordinal))
        {
            var normalisedTargetPath = NormaliseRendererPath(target.RendererPath);
            if (!byNormalisedPath.TryGetValue(normalisedTargetPath, out var selectors))
            {
                missing.Add(target.RendererPath);
                continue;
            }

            var sourceSelector = selectors.FirstOrDefault(selector => !usedSourceSelectors.Contains(selector));
            if (string.IsNullOrWhiteSpace(sourceSelector))
            {
                missing.Add(target.RendererPath);
                continue;
            }

            usedSourceSelectors.Add(sourceSelector);
            resolved.Add(new ResolvedReplacementRendererTarget(target.RendererPath, target.MeshName, sourceSelector, target.TargetMeshMaxHalfExtent));
        }

        missingTargetPaths = [.. missing.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal)];
        return resolved;
    }

    public static IReadOnlyDictionary<string, int> DiscoverReplacementMeshPrimitiveCounts(string filePath)
    {
        var model = ModelRoot.Load(filePath);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in model.LogicalNodes.Where(node => node.Mesh != null))
        {
            var primitiveCount = node.Mesh!.Primitives.Count;
            foreach (var alias in GetReplacementMeshAliases(node, filePath))
            {
                if (result.TryGetValue(alias, out var existingCount) && existingCount != primitiveCount)
                {
                    throw new InvalidOperationException(
                        $"Replacement file '{filePath}' maps alias '{alias}' to multiple mesh primitive counts ({existingCount} and {primitiveCount}).");
                }

                result[alias] = primitiveCount;
            }
        }

        return result;
    }

    public static void ValidateReplacementMeshPrimitiveContract(
        string replacementFilePath,
        string referenceFilePath,
        IReadOnlyList<ResolvedReplacementRendererTarget> resolvedTargets)
    {
        var replacementPrimitiveCounts = DiscoverReplacementMeshPrimitiveCounts(replacementFilePath);
        var referencePrimitiveCounts = DiscoverReplacementMeshPrimitiveCounts(referenceFilePath);

        var mismatches = resolvedTargets
            .Where(resolved =>
                replacementPrimitiveCounts.TryGetValue(resolved.SourceSelector, out var replacementCount) &&
                referencePrimitiveCounts.TryGetValue(resolved.TargetRendererPath, out var referenceCount) &&
                replacementCount != referenceCount)
            .Select(resolved => $"{resolved.TargetRendererPath} (replacement {replacementPrimitiveCounts[resolved.SourceSelector]}, target {referencePrimitiveCounts[resolved.TargetRendererPath]})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (mismatches.Length == 0)
            return;

        throw new InvalidOperationException(
            $"Replacement model '{replacementFilePath}' does not match the target mesh primitive/material-slot contract. " +
            $"Direct mesh replacement currently requires the same primitive count per target mesh as the target export. " +
            $"Mismatches: {string.Join(", ", mismatches)}");
    }

    public static string BuildBundleMeshName(string targetRendererPath, string targetMeshName)
    {
        var safeMeshName = SanitiseBundleNameToken(targetMeshName);
        var pathHash = ComputeStableShortHash(targetRendererPath);
        return $"{safeMeshName}__{pathHash}";
    }

    public static bool IsDiagnosticsEnabled()
    {
        var raw = Environment.GetEnvironmentVariable(DiagnosticsEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Equals("1", StringComparison.Ordinal) ||
               raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatNameListPreview(IEnumerable<string> names, int maxItems)
    {
        var ordered = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length <= maxItems)
            return string.Join(", ", ordered);

        var preview = ordered.Take(maxItems);
        return $"{string.Join(", ", preview)} (+{ordered.Length - maxItems} more)";
    }

    public static bool TryResolveTargetMeshNameFromProvided(string providedName, HashSet<string> expectedSet, out string targetName)
    {
        targetName = string.Empty;

        if (expectedSet.Contains(providedName))
        {
            targetName = providedName;
            return true;
        }

        if (TryStripBlenderNumericSuffix(providedName, out var stripped, out _) &&
            expectedSet.Contains(stripped))
        {
            targetName = stripped;
            return true;
        }

        return false;
    }

    public static int GetResolvedMeshNameSortKey(string providedName, string targetName)
    {
        if (providedName.Equals(targetName, StringComparison.Ordinal))
            return 0;

        if (TryStripBlenderNumericSuffix(providedName, out var strippedTargetCandidate, out var targetSuffixValue) &&
            strippedTargetCandidate.Equals(targetName, StringComparison.Ordinal))
        {
            return 100 + targetSuffixValue;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Strip a Blender-style <c>.001</c>/<c>.002</c>/… numeric suffix from a
    /// node or mesh name. Used at three sites: the name-reconciliation
    /// helpers above (so a target mesh that lacks the suffix still matches a
    /// modder's exported name), the path normaliser (so <c>spine.001</c>
    /// matches vanilla <c>spine</c>), and an external caller in
    /// <see cref="Glb.GlbMeshBundleCompiler"/> that runs the same
    /// reconciliation during mesh extraction.
    /// </summary>
    public static bool TryStripBlenderNumericSuffix(string name, out string strippedName, out int suffixValue)
    {
        strippedName = name;
        suffixValue = -1;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        var dotIndex = name.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= name.Length - 1)
            return false;

        var suffix = name[(dotIndex + 1)..];
        if (suffix.Length < 3 || !suffix.All(char.IsDigit))
            return false;

        if (!int.TryParse(suffix, out suffixValue))
            return false;

        strippedName = name[..dotIndex];
        return !string.IsNullOrEmpty(strippedName);
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private static IEnumerable<ReplacementMeshPathCandidate> BuildPathCandidates(Node node, string filePath)
    {
        foreach (var path in BuildNodePathAliases(node))
        {
            var normalisedPath = NormaliseRendererPath(path);
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(normalisedPath))
                yield return new ReplacementMeshPathCandidate(path, normalisedPath);
        }

        foreach (var fallbackAlias in GetReplacementMeshAliases(node, filePath))
        {
            var normalisedAlias = NormaliseRendererPath(fallbackAlias);
            if (!string.IsNullOrWhiteSpace(fallbackAlias) && !string.IsNullOrWhiteSpace(normalisedAlias))
                yield return new ReplacementMeshPathCandidate(fallbackAlias, normalisedAlias);
        }
    }

    private static IEnumerable<string> BuildNodePathAliases(Node node)
    {
        var withRoot = BuildNodePath(node, includeRoot: true);
        if (!string.IsNullOrWhiteSpace(withRoot))
            yield return withRoot;

        var withoutRoot = BuildNodePath(node, includeRoot: false);
        if (!string.IsNullOrWhiteSpace(withoutRoot))
            yield return withoutRoot;
    }

    private static string BuildNodePath(Node node, bool includeRoot)
    {
        var segments = new List<string>();
        var current = node;

        while (current != null)
        {
            if (current.VisualParent == null && !includeRoot)
                break;

            if (string.IsNullOrWhiteSpace(current.Name))
                return string.Empty;

            segments.Add(current.Name);
            current = current.VisualParent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static string NormaliseRendererPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var normalised = segment;
                if (TryStripBlenderNumericSuffix(segment, out var strippedSegment, out _))
                    normalised = strippedSegment;

                if (TryStripContainerPathSuffix(normalised, out var strippedContainerSegment))
                    normalised = strippedContainerSegment;

                return normalised;
            });

        return string.Join("/", segments);
    }

    private static bool TryStripContainerPathSuffix(string segment, out string strippedSegment)
    {
        const string containerSuffix = "_container";

        strippedSegment = segment;
        if (string.IsNullOrWhiteSpace(segment) ||
            !segment.EndsWith(containerSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        strippedSegment = segment[..^containerSuffix.Length];
        return !string.IsNullOrWhiteSpace(strippedSegment);
    }

    private static string SanitiseBundleNameToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mesh";

        var chars = value.Select(c =>
        {
            if (char.IsLetterOrDigit(c))
                return c;

            return c switch
            {
                '_' => '_',
                '-' => '-',
                _ => '_',
            };
        }).ToArray();

        return new string(chars);
    }

    private static string ComputeStableShortHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
    }

    private static IReadOnlyList<string> GetReplacementMeshAliases(Node node, string filePath)
    {
        var mesh = node.Mesh ?? throw new InvalidOperationException("Replacement mesh discovery expected a node with a mesh.");
        var aliases = new List<string>();

        foreach (var pathAlias in BuildNodePathAliases(node))
        {
            if (!aliases.Contains(pathAlias, StringComparer.Ordinal))
                aliases.Add(pathAlias);
        }

        if (!string.IsNullOrWhiteSpace(mesh.Name))
            aliases.Add(mesh.Name);

        if (!string.IsNullOrWhiteSpace(node.Name) &&
            !aliases.Contains(node.Name, StringComparer.Ordinal))
        {
            aliases.Add(node.Name);
        }

        if (aliases.Count == 0)
            aliases.Add(Path.GetFileNameWithoutExtension(filePath));

        return aliases;
    }
}
