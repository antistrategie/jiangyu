using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jiangyu.Shared.Bundles;

public sealed class ModLoadPlan(IReadOnlyList<DiscoveredMod> loadableMods, IReadOnlyList<BlockedMod> blockedMods)
{
    public static ModLoadPlan Empty { get; } = new([], []);

    public IReadOnlyList<DiscoveredMod> LoadableMods { get; } = loadableMods;
    public IReadOnlyList<BlockedMod> BlockedMods { get; } = blockedMods;
}

public sealed class DiscoveredMod(
    string name,
    string version,
    string directoryPath,
    string relativeDirectoryPath,
    string manifestPath,
    IReadOnlyList<string> bundlePaths,
    IReadOnlyList<ManifestDependency> dependencies,
    IReadOnlyList<ManifestDependency> conflicts)
{
    public string Name { get; } = name;
    public string Version { get; } = version;
    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string ManifestPath { get; } = manifestPath;
    public IReadOnlyList<string> BundlePaths { get; } = bundlePaths;
    public IReadOnlyList<ManifestDependency> Dependencies { get; } = dependencies;
    public IReadOnlyList<ManifestDependency> Conflicts { get; } = conflicts;
}

public sealed class BlockedMod(string name, string directoryPath, string relativeDirectoryPath, string reason)
{
    public string Name { get; } = name;
    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string Reason { get; } = reason;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? RelativeDirectoryPath : Name;
}

public sealed class ManifestDependency(string raw, string name, string? @operator, string? constraint)
{
    public string Raw { get; } = raw;
    public string Name { get; } = name;

    /// <summary>The comparison operator (<c>&gt;=</c>, <c>&lt;</c>, ...), or null for a
    /// presence-only entry with no version constraint.</summary>
    public string? Operator { get; } = @operator;

    /// <summary>The version the <see cref="Operator"/> compares against, or null when there
    /// is no constraint.</summary>
    public string? Constraint { get; } = constraint;
}

public static class ModLoadPlanBuilder
{
    private const string LoaderDependencyName = "Jiangyu";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex DependencyPattern = new(
        @"^\s*(?<name>.+?)(?:\s*(?<operator>>=|<=|==|!=|>|<|=)\s*(?<constraint>.+))?\s*$",
        RegexOptions.Compiled);

    /// <summary>Discover, validate, and dependency-gate the mods under <paramref name="modsDir"/>.
    /// <paramref name="loaderVersion"/> is the running Jiangyu version that satisfies a
    /// <c>Jiangyu</c> dependency or conflict constraint; pass null offline to fall back to a
    /// presence-only check for the loader entry.</summary>
    public static ModLoadPlan Build(string modsDir, string? loaderVersion = null)
    {
        if (!Directory.Exists(modsDir))
            return ModLoadPlan.Empty;

        var discovered = new List<DiscoveredMod>();
        var blocked = new List<BlockedMod>();
        var manifestPaths = Directory.GetFiles(modsDir, "jiangyu.json", SearchOption.AllDirectories)
            .OrderBy(path => GetRelativeDirectoryPath(modsDir, path), StringComparer.Ordinal)
            .ToArray();

        foreach (var manifestPath in manifestPaths)
        {
            if (TryDiscoverMod(modsDir, manifestPath, out var mod, out var blockedMod))
                discovered.Add(mod!);
            else if (blockedMod is not null)
                blocked.Add(blockedMod);
        }

        var duplicateNameGroups = discovered
            .GroupBy(mod => mod.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();

        if (duplicateNameGroups.Length > 0)
        {
            var duplicateNames = new HashSet<string>(duplicateNameGroups.Select(group => group.Key), StringComparer.Ordinal);
            foreach (var group in duplicateNameGroups)
            {
                var duplicateLocations = string.Join(", ", group
                    .OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal)
                    .Select(mod => mod.RelativeDirectoryPath));

                foreach (var mod in group)
                {
                    blocked.Add(new BlockedMod(
                        mod.Name,
                        mod.DirectoryPath,
                        mod.RelativeDirectoryPath,
                        $"Duplicate manifest name '{mod.Name}' also appears in: {duplicateLocations}."));
                }
            }

            discovered.RemoveAll(mod => duplicateNames.Contains(mod.Name));
        }

        // `depends`/`conflicts` resolve against manifest `name`. This is provisional until
        // Jiangyu defines a stable machine-readable mod identifier separate from display name.
        // The loader itself is present as `Jiangyu` at the running version; offline (no version
        // supplied) it is present with an empty version, so its constraints fall back to a
        // presence-only check.
        var availableVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in discovered)
            availableVersions[mod.Name] = mod.Version;
        availableVersions[LoaderDependencyName] = loaderVersion ?? string.Empty;

        var loadable = new List<DiscoveredMod>();
        foreach (var mod in discovered.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal))
        {
            var problems = new List<string>();

            foreach (var dependency in mod.Dependencies)
            {
                var unmet = DescribeUnmetDependency(dependency, availableVersions);
                if (unmet is not null)
                    problems.Add(unmet);
            }

            foreach (var conflict in mod.Conflicts)
            {
                var triggered = DescribeTriggeredConflict(conflict, availableVersions);
                if (triggered is not null)
                    problems.Add(triggered);
            }

            if (problems.Count > 0)
            {
                blocked.Add(new BlockedMod(
                    mod.Name,
                    mod.DirectoryPath,
                    mod.RelativeDirectoryPath,
                    $"Cannot load '{mod.Name}': {string.Join("; ", problems)}."));
                continue;
            }

            loadable.Add(mod);
        }

        return new ModLoadPlan(
            loadable,
            [.. blocked.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal)]);
    }

    // A dependency is unmet when its mod is absent, or present but failing the version
    // constraint. A constraint that can't be evaluated (no operator, or either side
    // unparseable as a version) degrades to a presence-only check. Returns the block
    // reason, or null when satisfied.
    private static string? DescribeUnmetDependency(ManifestDependency dependency, IReadOnlyDictionary<string, string> availableVersions)
    {
        if (!availableVersions.TryGetValue(dependency.Name, out var presentVersion))
            return $"requires {dependency.Raw}";

        if (dependency.Operator is null || dependency.Constraint is null)
            return null;

        if (!SemVer.TryParse(presentVersion, out var present) || !SemVer.TryParse(dependency.Constraint, out var required))
            return null;

        return SemVer.Satisfies(present, dependency.Operator, required)
            ? null
            : $"requires {dependency.Raw} but found {dependency.Name} {presentVersion}";
    }

    // A conflict triggers when the named mod is present and (for a constrained entry) its
    // version satisfies the conflict range. A constrained entry whose present version can't
    // be evaluated does not trigger: we won't block on an unconfirmable range. Returns the
    // block reason, or null when there is no conflict.
    private static string? DescribeTriggeredConflict(ManifestDependency conflict, IReadOnlyDictionary<string, string> availableVersions)
    {
        if (!availableVersions.TryGetValue(conflict.Name, out var presentVersion))
            return null;

        if (conflict.Operator is null || conflict.Constraint is null)
            return $"conflicts with {conflict.Name}, which is installed";

        if (!SemVer.TryParse(presentVersion, out var present) || !SemVer.TryParse(conflict.Constraint, out var range))
            return null;

        return SemVer.Satisfies(present, conflict.Operator, range)
            ? $"conflicts with {conflict.Raw} (found {conflict.Name} {presentVersion})"
            : null;
    }

    private static bool TryDiscoverMod(string modsDir, string manifestPath, out DiscoveredMod? mod, out BlockedMod? blockedMod)
    {
        mod = null;
        blockedMod = null;

        var modDir = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(modDir))
            return false;

        var relativeDirectoryPath = GetRelativeDirectoryPath(modsDir, manifestPath);

        try
        {
            var manifest = JsonSerializer.Deserialize<LoaderManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);

            if (manifest == null)
            {
                blockedMod = new BlockedMod(
                    string.Empty,
                    modDir,
                    relativeDirectoryPath,
                    "Manifest is unreadable.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                blockedMod = new BlockedMod(
                    string.Empty,
                    modDir,
                    relativeDirectoryPath,
                    "Manifest is missing a non-empty 'name'.");
                return false;
            }

            if (!TryParseConstraints(manifest.Depends, "Dependency", out var dependencies, out var dependencyError))
            {
                blockedMod = new BlockedMod(manifest.Name, modDir, relativeDirectoryPath, dependencyError!);
                return false;
            }

            if (!TryParseConstraints(manifest.Conflicts, "Conflict", out var conflicts, out var conflictError))
            {
                blockedMod = new BlockedMod(manifest.Name, modDir, relativeDirectoryPath, conflictError!);
                return false;
            }

            // Compiled bundles live under the mod's bundles/ subfolder, beside code/.
            // A mod with no bundles (template- or code-only) simply has none here.
            var bundlesDir = Path.Combine(modDir, CompiledLayout.BundlesDirName);
            var bundlePaths = Directory.Exists(bundlesDir)
                ? Directory.GetFiles(bundlesDir, "*.bundle")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();

            mod = new DiscoveredMod(
                manifest.Name.Trim(),
                manifest.Version ?? string.Empty,
                modDir,
                relativeDirectoryPath,
                manifestPath,
                bundlePaths,
                dependencies,
                conflicts);
            return true;
        }
        catch (Exception ex)
        {
            blockedMod = new BlockedMod(
                string.Empty,
                modDir,
                relativeDirectoryPath,
                $"Failed to read manifest: {ex.Message}");
            return false;
        }
    }

    // Parses a `<name>` or `<name> <op> <constraint>` list, shared by `depends` and
    // `conflicts`. <paramref name="kind"/> labels the entry in error text ("Dependency"
    // / "Conflict").
    private static bool TryParseConstraints(
        IReadOnlyList<string>? rawEntries,
        string kind,
        out IReadOnlyList<ManifestDependency> entries,
        out string? error)
    {
        entries = [];
        error = null;

        if (rawEntries == null || rawEntries.Count == 0)
            return true;

        var result = new List<ManifestDependency>(rawEntries.Count);
        foreach (var rawEntry in rawEntries)
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                error = $"Manifest contains an empty {kind.ToLowerInvariant()} entry.";
                return false;
            }

            var match = DependencyPattern.Match(rawEntry);
            if (!match.Success)
            {
                error = $"{kind} entry '{rawEntry}' is invalid.";
                return false;
            }

            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"{kind} entry '{rawEntry}' does not name a mod.";
                return false;
            }

            var @operator = match.Groups["operator"].Success ? match.Groups["operator"].Value.Trim() : null;
            var constraint = match.Groups["constraint"].Success ? match.Groups["constraint"].Value.Trim() : null;
            var hasConstraint = !string.IsNullOrWhiteSpace(constraint);

            // A typo'd version (e.g. "Base >= 1.O.O") would otherwise parse here but silently
            // fail to evaluate later, disabling the requirement with no signal. Reject it as a
            // manifest error up front so the modder sees the cause.
            if (hasConstraint && !SemVer.TryParse(constraint, out _))
            {
                error = $"{kind} entry '{rawEntry}' has an invalid version '{constraint}'.";
                return false;
            }

            result.Add(new ManifestDependency(
                rawEntry,
                name,
                hasConstraint ? @operator : null,
                hasConstraint ? constraint : null));
        }

        entries = result;
        return true;
    }

    private static string GetRelativeDirectoryPath(string modsDir, string manifestPath)
    {
        var modDir = Path.GetDirectoryName(manifestPath) ?? modsDir;
        var relativePath = Path.GetRelativePath(modsDir, modDir);
        return string.Equals(relativePath, ".", StringComparison.Ordinal)
            ? Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : relativePath;
    }

}
