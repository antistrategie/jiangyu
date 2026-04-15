using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jiangyu.Loader.Bundles;

internal sealed class ModLoadPlan
{
    public static ModLoadPlan Empty { get; } = new(Array.Empty<DiscoveredMod>(), Array.Empty<BlockedMod>());

    public IReadOnlyList<DiscoveredMod> LoadableMods { get; }
    public IReadOnlyList<BlockedMod> BlockedMods { get; }

    public ModLoadPlan(IReadOnlyList<DiscoveredMod> loadableMods, IReadOnlyList<BlockedMod> blockedMods)
    {
        LoadableMods = loadableMods;
        BlockedMods = blockedMods;
    }
}

internal sealed class DiscoveredMod
{
    public string Name { get; }
    public string DirectoryPath { get; }
    public string RelativeDirectoryPath { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<string> BundlePaths { get; }
    public IReadOnlyList<ManifestDependency> Dependencies { get; }

    public DiscoveredMod(
        string name,
        string directoryPath,
        string relativeDirectoryPath,
        string manifestPath,
        IReadOnlyList<string> bundlePaths,
        IReadOnlyList<ManifestDependency> dependencies)
    {
        Name = name;
        DirectoryPath = directoryPath;
        RelativeDirectoryPath = relativeDirectoryPath;
        ManifestPath = manifestPath;
        BundlePaths = bundlePaths;
        Dependencies = dependencies;
    }
}

internal sealed class BlockedMod
{
    public string? Name { get; }
    public string DirectoryPath { get; }
    public string RelativeDirectoryPath { get; }
    public string Reason { get; }

    public BlockedMod(string? name, string directoryPath, string relativeDirectoryPath, string reason)
    {
        Name = name;
        DirectoryPath = directoryPath;
        RelativeDirectoryPath = relativeDirectoryPath;
        Reason = reason;
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? RelativeDirectoryPath : Name;
}

internal sealed class ManifestDependency
{
    public string Raw { get; }
    public string Name { get; }
    public string? Constraint { get; }

    public ManifestDependency(string raw, string name, string? constraint)
    {
        Raw = raw;
        Name = name;
        Constraint = constraint;
    }
}

internal static class ModLoadPlanBuilder
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
            else if (blockedMod != null)
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
            blocked.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal).ToArray());
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
                    null,
                    modDir,
                    relativeDirectoryPath,
                    "Manifest is unreadable.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                blockedMod = new BlockedMod(
                    null,
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
                dependencies!);
            return true;
        }
        catch (Exception ex)
        {
            blockedMod = new BlockedMod(
                null,
                modDir,
                relativeDirectoryPath,
                $"Failed to read manifest: {ex.Message}");
            return false;
        }
    }

    private static bool TryParseDependencies(
        IReadOnlyList<string>? rawDependencies,
        out IReadOnlyList<ManifestDependency>? dependencies,
        out string? error)
    {
        dependencies = Array.Empty<ManifestDependency>();
        error = null;

        if (rawDependencies == null || rawDependencies.Count == 0)
            return true;

        var result = new List<ManifestDependency>(rawDependencies.Count);
        foreach (var rawDependency in rawDependencies)
        {
            if (string.IsNullOrWhiteSpace(rawDependency))
            {
                error = "Manifest contains an empty dependency entry.";
                dependencies = null;
                return false;
            }

            var match = DependencyPattern.Match(rawDependency);
            if (!match.Success)
            {
                error = $"Dependency entry '{rawDependency}' is invalid.";
                dependencies = null;
                return false;
            }

            var name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"Dependency entry '{rawDependency}' does not name a required mod.";
                dependencies = null;
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
