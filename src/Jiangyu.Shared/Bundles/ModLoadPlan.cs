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
    string directoryPath,
    string relativeDirectoryPath,
    string manifestPath,
    IReadOnlyList<string> bundlePaths,
    IReadOnlyList<ManifestDependency> dependencies)
{
    public string Name { get; } = name;
    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string ManifestPath { get; } = manifestPath;
    public IReadOnlyList<string> BundlePaths { get; } = bundlePaths;
    public IReadOnlyList<ManifestDependency> Dependencies { get; } = dependencies;
}

public sealed class BlockedMod(string name, string directoryPath, string relativeDirectoryPath, string reason)
{
    public string Name { get; } = name;
    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string Reason { get; } = reason;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? RelativeDirectoryPath : Name;
}

public sealed class ManifestDependency(string raw, string name, string? constraint)
{
    public string Raw { get; } = raw;
    public string Name { get; } = name;
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

    public static ModLoadPlan Build(string modsDir)
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

        // `depends` currently resolves against manifest `name`. This is provisional until
        // Jiangyu defines a stable machine-readable mod identifier separate from display name.
        var availableNames = new HashSet<string>(discovered.Select(mod => mod.Name), StringComparer.Ordinal)
        {
            LoaderDependencyName,
        };

        var loadable = new List<DiscoveredMod>();
        foreach (var mod in discovered.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal))
        {
            var missingDependencies = mod.Dependencies
                .Where(dep => !availableNames.Contains(dep.Name))
                .Select(dep => dep.Raw)
                .ToArray();

            if (missingDependencies.Length > 0)
            {
                blocked.Add(new BlockedMod(
                    mod.Name,
                    mod.DirectoryPath,
                    mod.RelativeDirectoryPath,
                    $"Missing required mod(s): {string.Join(", ", missingDependencies)}."));
                continue;
            }

            loadable.Add(mod);
        }

        return new ModLoadPlan(
            loadable,
            [.. blocked.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal)]);
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

            if (!TryParseDependencies(manifest.Depends, out var dependencies, out var dependencyError))
            {
                blockedMod = new BlockedMod(
                    manifest.Name,
                    modDir,
                    relativeDirectoryPath,
                    dependencyError!);
                return false;
            }

            var bundlePaths = Directory.GetFiles(modDir, "*.bundle")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            mod = new DiscoveredMod(
                manifest.Name.Trim(),
                modDir,
                relativeDirectoryPath,
                manifestPath,
                bundlePaths,
                dependencies);
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

    private static bool TryParseDependencies(
        IReadOnlyList<string>? rawDependencies,
        out IReadOnlyList<ManifestDependency> dependencies,
        out string? error)
    {
        dependencies = [];
        error = null;

        if (rawDependencies == null || rawDependencies.Count == 0)
            return true;

        var result = new List<ManifestDependency>(rawDependencies.Count);
        foreach (var rawDependency in rawDependencies)
        {
            if (string.IsNullOrWhiteSpace(rawDependency))
            {
                error = "Manifest contains an empty dependency entry.";
                return false;
            }

            var match = DependencyPattern.Match(rawDependency);
            if (!match.Success)
            {
                error = $"Dependency entry '{rawDependency}' is invalid.";
                return false;
            }

            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"Dependency entry '{rawDependency}' does not name a required mod.";
                return false;
            }

            var constraint = match.Groups["constraint"].Success
                ? match.Groups["constraint"].Value.Trim()
                : null;

            result.Add(new ManifestDependency(rawDependency, name, string.IsNullOrWhiteSpace(constraint) ? null : constraint));
        }

        dependencies = result;
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

    private sealed class LoaderManifest
    {
        public string? Name { get; set; }
        public List<string>? Depends { get; set; }
    }
}
