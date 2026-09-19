using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jiangyu.Shared.Bundles;

public sealed class ModLoadPlan(
    IReadOnlyList<DiscoveredMod> loadableMods,
    IReadOnlyList<BlockedMod> blockedMods,
    IReadOnlyList<string> warnings)
{
    public static ModLoadPlan Empty { get; } = new([], [], []);

    /// <summary>The mods that load, in load order: lexical folder order, with every mod
    /// placed after the mods it depends on.</summary>
    public IReadOnlyList<DiscoveredMod> LoadableMods { get; } = loadableMods;
    public IReadOnlyList<BlockedMod> BlockedMods { get; } = blockedMods;

    /// <summary>Conditions that changed how a mod loads without blocking it: an optional
    /// dependency that is installed but unusable, an entry ignored for closing a cycle,
    /// or an entry that names the mod itself. The loader logs each one.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings;
}

public sealed class DiscoveredMod(
    string name,
    string version,
    string? compiledForUnity,
    string? compiledForJiangyu,
    string directoryPath,
    string relativeDirectoryPath,
    string manifestPath,
    IReadOnlyList<string> bundlePaths,
    IReadOnlyList<ManifestDependency> dependencies,
    IReadOnlyList<ManifestDependency> optionalDependencies,
    IReadOnlyList<ManifestDependency> conflicts)
{
    public string Name { get; } = name;
    public string Version { get; } = version;

    /// <summary>The game's Unity version the mod was compiled against, and the Jiangyu
    /// toolchain that compiled it. Carried off the manifest read during discovery so the
    /// startup version gates read it here rather than parsing every jiangyu.json again.
    /// Null on a hand-written manifest the compiler never stamped.</summary>
    public string? CompiledForUnity { get; } = compiledForUnity;

    /// <inheritdoc cref="CompiledForUnity"/>
    public string? CompiledForJiangyu { get; } = compiledForJiangyu;

    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string ManifestPath { get; } = manifestPath;
    public IReadOnlyList<string> BundlePaths { get; } = bundlePaths;
    public IReadOnlyList<ManifestDependency> Dependencies { get; } = dependencies;

    /// <summary>Mods this one loads after when they are installed, and loads without when
    /// they are not.</summary>
    public IReadOnlyList<ManifestDependency> OptionalDependencies { get; } = optionalDependencies;

    public IReadOnlyList<ManifestDependency> Conflicts { get; } = conflicts;
}

public sealed class BlockedMod(string name, string directoryPath, string relativeDirectoryPath, string reason, string? version = null)
{
    public string Name { get; } = name;
    public string DirectoryPath { get; } = directoryPath;
    public string RelativeDirectoryPath { get; } = relativeDirectoryPath;
    public string Reason { get; } = reason;

    /// <summary>The manifest's version when one could be read, so a ranged conflict with this
    /// mod is still judged against it. Null when the manifest gave none.</summary>
    public string? Version { get; } = version;

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

    // Bounds on the search for the fewest optional entries that leave no cycle: subsets
    // up to this size, and only while the number of subsets stays under this many. Past
    // either, every cycle-closing entry is dropped in one pass and the restore step puts
    // back what the settled graph can honour, so a dense tangle costs one pass.
    private const int MaxDropSetSize = 4;
    private const int MaxDropSetSearch = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex DependencyPattern = new(
        @"^\s*(?<name>.+?)(?:\s*(?<operator>>=|<=|==|!=|>|<|=)\s*(?<constraint>.+))?\s*$",
        RegexOptions.Compiled);

    /// <summary>Discover, validate, dependency-gate and order the mods under
    /// <paramref name="modsDir"/>. <paramref name="loaderVersion"/> is the running Jiangyu
    /// version that satisfies a <c>Jiangyu</c> dependency or conflict constraint. Pass null
    /// offline to fall back to a presence-only check for the loader entry.</summary>
    public static ModLoadPlan Build(string modsDir, string? loaderVersion = null)
    {
        if (!Directory.Exists(modsDir))
            return ModLoadPlan.Empty;

        var discovered = new List<DiscoveredMod>();
        var blocked = new List<BlockedMod>();
        var warnings = new List<string>();
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

        // Every installed mod by name, whatever becomes of it: `conflicts` judges against
        // this, since a conflict is with what is installed. A manifest that failed
        // discovery but named itself counts, at its own version when it could be read, and
        // a name two folders share carries both versions.
        var installedVersions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var mod in discovered)
            InstalledVersionsAdd(installedVersions, mod.Name, mod.Version);
        foreach (var mod in blocked)
            if (!string.IsNullOrWhiteSpace(mod.Name))
                InstalledVersionsAdd(installedVersions, mod.Name, mod.Version ?? string.Empty);
        installedVersions[LoaderDependencyName] = [loaderVersion ?? string.Empty];

        // Two folders declaring one name block both copies, a copy that failed discovery
        // included: it is installed under that name, so the other cannot stand in for it.
        var locationsByName = discovered
            .Select(mod => (mod.Name, mod.RelativeDirectoryPath))
            .Concat(blocked.Where(mod => !string.IsNullOrWhiteSpace(mod.Name)).Select(mod => (mod.Name, mod.RelativeDirectoryPath)))
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group.Select(pair => pair.RelativeDirectoryPath).OrderBy(path => path, StringComparer.Ordinal)),
                StringComparer.Ordinal);

        foreach (var mod in discovered.Where(mod => locationsByName.ContainsKey(mod.Name)))
        {
            blocked.Add(new BlockedMod(
                mod.Name,
                mod.DirectoryPath,
                mod.RelativeDirectoryPath,
                $"Duplicate manifest name '{mod.Name}' also appears in: {locationsByName[mod.Name]}.",
                mod.Version));
        }

        discovered.RemoveAll(mod => locationsByName.ContainsKey(mod.Name));

        // `depends`/`conflicts` resolve against manifest `name`. This is provisional until
        // Jiangyu defines a stable machine-readable mod identifier separate from display name.
        // The loader itself is present as `Jiangyu` at the running version. Offline (no version
        // supplied) it is present with an empty version, so its constraints fall back to a
        // presence-only check. A mod leaves availableVersions when it is blocked, and its name
        // moves to blockedNames, so a dependent hears "blocked" rather than "absent" for a mod
        // that is installed but unusable. The two sets never overlap.
        var availableVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in discovered)
            availableVersions[mod.Name] = mod.Version;
        availableVersions[LoaderDependencyName] = loaderVersion ?? string.Empty;

        var blockedNames = new HashSet<string>(
            blocked.Select(mod => mod.Name).Where(name => !string.IsNullOrWhiteSpace(name) && !availableVersions.ContainsKey(name)),
            StringComparer.Ordinal);

        // Folder order is the base order: independent mods keep it, and a dependency only
        // ever moves ahead of its dependents.
        var remaining = new List<DiscoveredMod>();
        foreach (var mod in discovered.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal))
        {
            var problems = new List<string>();

            foreach (var dependency in mod.Dependencies)
            {
                // An entry naming the mod itself is ignored. The ordering pass warns about it.
                if (dependency.Name == mod.Name)
                    continue;
                var unmet = DescribeUnmetDependency(dependency, availableVersions, blockedNames);
                if (unmet is not null)
                    problems.Add(unmet);
            }

            foreach (var conflict in mod.Conflicts)
            {
                if (conflict.Name == mod.Name)
                    continue;
                var triggered = DescribeTriggeredConflict(conflict, installedVersions);
                if (triggered is not null)
                    problems.Add(triggered);
            }

            if (problems.Count > 0)
            {
                Block(mod, problems, blocked, blockedNames, availableVersions);
                continue;
            }

            remaining.Add(mod);
        }

        BlockDependentsOfBlockedMods(remaining, blocked, blockedNames, availableVersions);
        var loadable = OrderByDependencies(remaining, availableVersions, blocked, blockedNames, warnings);

        return new ModLoadPlan(
            loadable,
            [.. blocked.OrderBy(mod => mod.RelativeDirectoryPath, StringComparer.Ordinal)],
            warnings);
    }

    private static void InstalledVersionsAdd(Dictionary<string, List<string>> installedVersions, string name, string version)
    {
        if (!installedVersions.TryGetValue(name, out var versions))
            installedVersions[name] = versions = [];
        versions.Add(version);
    }

    private static void Block(
        DiscoveredMod mod,
        IReadOnlyList<string> problems,
        List<BlockedMod> blocked,
        HashSet<string> blockedNames,
        Dictionary<string, string> availableVersions)
    {
        blocked.Add(new BlockedMod(
            mod.Name,
            mod.DirectoryPath,
            mod.RelativeDirectoryPath,
            $"Cannot load '{mod.Name}': {string.Join("; ", problems)}.",
            mod.Version));
        blockedNames.Add(mod.Name);
        availableVersions.Remove(mod.Name);
    }

    // A mod whose required dependency is installed but blocked cannot load either: its
    // dependency's bundles never mount and its templates never apply. Runs to a fixpoint so
    // a chain of dependents falls with the first blocked mod, whatever the folder order.
    private static void BlockDependentsOfBlockedMods(
        List<DiscoveredMod> remaining,
        List<BlockedMod> blocked,
        HashSet<string> blockedNames,
        Dictionary<string, string> availableVersions)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var mod in remaining.ToArray())
            {
                var problems = mod.Dependencies
                    .Where(dependency => blockedNames.Contains(dependency.Name))
                    .Select(dependency => $"requires {dependency.Raw}, which is blocked")
                    .ToList();
                if (problems.Count == 0)
                    continue;

                Block(mod, problems, blocked, blockedNames, availableVersions);
                remaining.Remove(mod);
                changed = true;
            }
        }
    }

    // Places each mod after the mods it depends on, required and optional alike, moving
    // only what has to move: walking folder order, a mod's unplaced dependencies are
    // emitted ahead of it (each preceded by its own), so a dependency moves up to sit
    // before its first dependent and mods that are not pulled ahead keep their relative
    // order. Before that walk, repeated sweeps in folder order (place a mod once every mod
    // it must follow is placed) find the mods that wait on each other. A cycle through
    // required edges blocks its members (and, through BlockDependentsOfBlockedMods,
    // whatever requires them). Otherwise every remaining cycle closes through an optional
    // edge. Within the search bounds the fewest such edges that leave no cycle are
    // dropped, preferring edges that run against folder order so dropping them lets the
    // folder order stand, and beyond the bounds every closing edge is dropped and the
    // ones the settled graph can honour are put back. Each edge still dropped is a
    // warning. An optional dependency orders when it can and never blocks.
    //
    // Each pass resolves the optional edges and their warnings afresh against the mods
    // still loadable, and only the pass that succeeds keeps its warnings, so a mod blocked
    // by a later pass leaves no stale line and a mod blocked by an earlier pass is reported
    // as blocked to whatever optionally depends on it.
    private static List<DiscoveredMod> OrderByDependencies(
        List<DiscoveredMod> mods,
        Dictionary<string, string> availableVersions,
        List<BlockedMod> blocked,
        HashSet<string> blockedNames,
        List<string> warnings)
    {
        // Dropped optional edges, in drop order, each with the warning that reports it.
        var droppedEdges = new List<((string Mod, string Dependency) Edge, string Warning)>();

        while (true)
        {
            var passWarnings = new List<string>();
            var folderIndex = mods
                .Select((mod, index) => (mod.Name, index))
                .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);

            var requiredEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var optionalEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var optionalEntries = new Dictionary<(string Mod, string Dependency), string>();
            var dropped = droppedEdges.Select(entry => entry.Edge).ToHashSet();
            foreach (var mod in mods)
            {
                var edges = new List<string>();
                foreach (var dependency in mod.Dependencies)
                {
                    if (dependency.Name == mod.Name)
                    {
                        passWarnings.Add($"{Label(mod)}: dependency '{dependency.Raw}' names the mod itself, the entry is ignored.");
                        continue;
                    }
                    if (folderIndex.ContainsKey(dependency.Name) && !edges.Contains(dependency.Name))
                        edges.Add(dependency.Name);
                }
                foreach (var conflict in mod.Conflicts)
                {
                    if (conflict.Name == mod.Name)
                        passWarnings.Add($"{Label(mod)}: conflict '{conflict.Raw}' names the mod itself, the entry is ignored.");
                    else if (NamesLoaderInAnotherCase(conflict.Name))
                        passWarnings.Add($"{Label(mod)}: conflict '{conflict.Raw}' is not the loader's name {LoaderDependencyName}, the entry is ignored.");
                }
                requiredEdges[mod.Name] = edges;
                optionalEdges[mod.Name] = ResolveOptionalEdges(mod, folderIndex, availableVersions, blockedNames, dropped, optionalEntries, passWarnings);
            }

            var placed = Sweep(mods, requiredEdges, optionalEdges, null);

            if (placed.Count == mods.Count)
            {
                // A drop made under one pass's view can turn out unneeded once the rest
                // settle: when the dependency no longer reaches its dependent, the edge
                // closes no cycle and goes back, with its warning. Restoring only adds
                // edges that close no cycle, so the graph stays acyclic and this settles.
                var combined = mods.ToDictionary(
                    mod => mod.Name,
                    mod => requiredEdges[mod.Name].Concat(optionalEdges[mod.Name]).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
                var everyName = new HashSet<string>(combined.Keys, StringComparer.Ordinal);
                // Edges that run with folder order go back first, then the others from the
                // last folder up, so the entry the preference ranks first to drop is the
                // one still ignored when they compete.
                var restoreOrder = droppedEdges
                    .OrderBy(entry => folderIndex[entry.Edge.Mod] < folderIndex[entry.Edge.Dependency] ? 1 : 0)
                    .ThenByDescending(entry => folderIndex[entry.Edge.Mod])
                    .ThenByDescending(entry => folderIndex[entry.Edge.Dependency])
                    .ToList();
                foreach (var entry in restoreOrder)
                {
                    if (Reachable(entry.Edge.Dependency, combined, everyName).Contains(entry.Edge.Mod))
                        continue;
                    droppedEdges.Remove(entry);
                    optionalEdges[entry.Edge.Mod].Add(entry.Edge.Dependency);
                    combined[entry.Edge.Mod].Add(entry.Edge.Dependency);
                }

                // Two entries naming the mod itself would say the same thing twice.
                warnings.AddRange(passWarnings.Concat(droppedEdges.Select(entry => entry.Warning)).Distinct(StringComparer.Ordinal));
                return EmitInFolderOrder(mods, requiredEdges, optionalEdges);
            }

            var stuck = mods.Where(mod => !placed.Contains(mod.Name)).ToList();
            var stuckNames = new HashSet<string>(stuck.Select(mod => mod.Name), StringComparer.Ordinal);

            var cycleMembers = stuck
                .Where(mod => Reachable(mod.Name, requiredEdges, stuckNames).Contains(mod.Name))
                .ToList();
            if (cycleMembers.Count > 0)
            {
                foreach (var mod in cycleMembers)
                {
                    // The mod's own cycle: the members it reaches that reach it back, so two
                    // unrelated cycles in one folder are reported apart.
                    var reaches = Reachable(mod.Name, requiredEdges, stuckNames);
                    var others = cycleMembers
                        .Where(other => other.Name != mod.Name && reaches.Contains(other.Name)
                            && Reachable(other.Name, requiredEdges, stuckNames).Contains(mod.Name))
                        .Select(other => $"'{other.Name}'");
                    Block(mod, [$"dependency cycle with {string.Join(", ", others)}"], blocked, blockedNames, availableVersions);
                }
                mods.RemoveAll(cycleMembers.Contains);
                BlockDependentsOfBlockedMods(mods, blocked, blockedNames, availableVersions);
                continue;
            }

            // No required cycle, so every stuck mod waits on a cycle that closes through an
            // optional edge. An optional edge closes a cycle when its dependency reaches the
            // dependent back through the stuck set. Candidates run in preference order: an
            // edge against folder order first (dropping it lets the folder order stand),
            // then by the dependent's and the dependency's folder positions.
            var stuckEdges = stuck.ToDictionary(
                mod => mod.Name,
                mod => requiredEdges[mod.Name].Concat(optionalEdges[mod.Name]).Where(stuckNames.Contains).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
            var closing = stuck
                .SelectMany(mod => optionalEdges[mod.Name]
                    .Where(dependency => stuckNames.Contains(dependency) && Reachable(dependency, stuckEdges, stuckNames).Contains(mod.Name))
                    .Select(dependency => (Mod: mod.Name, Dependency: dependency)))
                .OrderBy(edge => folderIndex[edge.Mod] < folderIndex[edge.Dependency] ? 0 : 1)
                .ThenBy(edge => folderIndex[edge.Mod])
                .ThenBy(edge => folderIndex[edge.Dependency])
                .ToList();

            // Unreachable by construction: a stuck set with no required cycle has a cycle
            // through an optional edge. Kept so a slip here fails loudly instead of hanging.
            if (closing.Count == 0)
                throw new InvalidOperationException("Load order: the stuck set has no edge to drop.");

            // The fewest closing edges whose drop places every mod, searched by set size
            // in preference order within the bounds. Beyond them, every closing edge is
            // dropped at once and the restore step above puts back each one the settled
            // graph can honour, so a dense tangle costs one pass and the result still has
            // no entry that could be honoured on its own.
            var chosen = FewestFreeingDrops(mods, requiredEdges, optionalEdges, closing) ?? closing;
            foreach (var edge in chosen)
            {
                droppedEdges.Add((
                    edge,
                    $"'{edge.Mod}' [{mods.First(mod => mod.Name == edge.Mod).RelativeDirectoryPath}]: optional dependency '{optionalEntries[edge]}' is ignored for ordering, honouring it would form a cycle."));
            }
        }
    }

    // The first subset of <paramref name="candidates"/>, by size then by candidate order,
    // whose drop lets the sweep place every mod. Null when none is found within the bounds.
    private static List<(string Mod, string Dependency)>? FewestFreeingDrops(
        List<DiscoveredMod> mods,
        IReadOnlyDictionary<string, List<string>> requiredEdges,
        IReadOnlyDictionary<string, List<string>> optionalEdges,
        List<(string Mod, string Dependency)> candidates)
    {
        for (var size = 1; size <= Math.Min(MaxDropSetSize, candidates.Count); size++)
        {
            if (Combinations(candidates.Count, size) > MaxDropSetSearch)
                return null;
            var indices = Enumerable.Range(0, size).ToArray();
            while (true)
            {
                var subset = indices.Select(index => candidates[index]).ToList();
                if (Sweep(mods, requiredEdges, optionalEdges, subset.ToHashSet()).Count == mods.Count)
                    return subset;

                // Next combination in lexicographic order.
                var slot = size - 1;
                while (slot >= 0 && indices[slot] == candidates.Count - size + slot)
                    slot--;
                if (slot < 0)
                    break;
                indices[slot]++;
                for (var next = slot + 1; next < size; next++)
                    indices[next] = indices[next - 1] + 1;
            }
        }
        return null;
    }

    private static long Combinations(int count, int size)
    {
        long result = 1;
        for (var i = 1; i <= size; i++)
            result = result * (count - size + i) / i;
        return result;
    }

    // Repeated sweeps in folder order: place a mod once every mod it must follow is placed.
    // Returns the placed set, complete when the graph is acyclic. <paramref name="without"/>
    // leaves optional edges out, to test what dropping them would free.
    private static HashSet<string> Sweep(
        List<DiscoveredMod> mods,
        IReadOnlyDictionary<string, List<string>> requiredEdges,
        IReadOnlyDictionary<string, List<string>> optionalEdges,
        HashSet<(string Mod, string Dependency)>? without)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var progressed = true;
        while (placed.Count < mods.Count && progressed)
        {
            progressed = false;
            foreach (var mod in mods)
            {
                if (placed.Contains(mod.Name)
                    || !requiredEdges[mod.Name].All(placed.Contains)
                    || !optionalEdges[mod.Name].All(dependency => placed.Contains(dependency) || (without is not null && without.Contains((mod.Name, dependency)))))
                    continue;
                placed.Add(mod.Name);
                progressed = true;
            }
        }
        return placed;
    }

    // The final order of an acyclic graph: folder order, with each mod preceded by the
    // dependencies not yet emitted, themselves in folder order and preceded by theirs.
    private static List<DiscoveredMod> EmitInFolderOrder(
        List<DiscoveredMod> mods,
        IReadOnlyDictionary<string, List<string>> requiredEdges,
        IReadOnlyDictionary<string, List<string>> optionalEdges)
    {
        var byName = mods.ToDictionary(mod => mod.Name, StringComparer.Ordinal);
        var folderIndex = mods.Select((mod, index) => (mod.Name, index)).ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);
        var ordered = new List<DiscoveredMod>(mods.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        void Emit(DiscoveredMod mod)
        {
            if (!emitted.Add(mod.Name))
                return;
            var ahead = requiredEdges[mod.Name].Concat(optionalEdges[mod.Name])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => folderIndex[name]);
            foreach (var name in ahead)
                Emit(byName[name]);
            ordered.Add(mod);
        }

        foreach (var mod in mods)
            Emit(mod);
        return ordered;
    }

    // The optional dependencies of one mod that order it, by name: each is loadable and
    // meets its constraint. Every entry is judged, so a duplicate or a `Jiangyu` entry
    // still reports a constraint it fails, and an edge is recorded once, under the first
    // entry that earned it. An entry that names the mod itself, one that is installed but
    // blocked, one that fails its constraint, and one dropped to break a cycle are ignored,
    // the first three with a warning here and the last with the warning recorded when it
    // was dropped. An absent mod is what optional means, and passes without a word.
    private static List<string> ResolveOptionalEdges(
        DiscoveredMod mod,
        IReadOnlyDictionary<string, int> loadable,
        IReadOnlyDictionary<string, string> availableVersions,
        HashSet<string> blockedNames,
        HashSet<(string Mod, string Dependency)> droppedEdges,
        Dictionary<(string Mod, string Dependency), string> entries,
        List<string> warnings)
    {
        var edges = new List<string>();
        foreach (var dependency in mod.OptionalDependencies)
        {
            if (dependency.Name == mod.Name)
            {
                warnings.Add($"{Label(mod)}: optional dependency '{dependency.Raw}' names the mod itself, the entry is ignored.");
                continue;
            }

            if (dependency.Name == LoaderDependencyName)
            {
                var loaderMismatch = DescribeVersionMismatch(dependency, availableVersions[LoaderDependencyName]);
                if (loaderMismatch is not null)
                    warnings.Add($"{Label(mod)}: optional dependency '{dependency.Raw}' is not met ({loaderMismatch}), the entry is ignored.");
                continue;
            }

            if (loadable.ContainsKey(dependency.Name))
            {
                var mismatch = DescribeVersionMismatch(dependency, availableVersions[dependency.Name]);
                if (mismatch is not null)
                {
                    warnings.Add($"{Label(mod)}: optional dependency '{dependency.Raw}' is not met ({mismatch}), the entry is ignored.");
                    continue;
                }
                var edge = (mod.Name, dependency.Name);
                entries.TryAdd(edge, dependency.Raw);
                if (!droppedEdges.Contains(edge) && !edges.Contains(dependency.Name))
                    edges.Add(dependency.Name);
                continue;
            }

            if (blockedNames.Contains(dependency.Name))
                warnings.Add($"{Label(mod)}: optional dependency '{dependency.Raw}' is installed but blocked, the entry is ignored.");
            else if (NamesLoaderInAnotherCase(dependency.Name))
                warnings.Add($"{Label(mod)}: optional dependency '{dependency.Raw}' is not the loader's name {LoaderDependencyName}, the entry is ignored.");
        }
        return edges;
    }

    // A name that is the loader's in another letter case: no mod can carry it, so the
    // entry is a slip worth pointing out rather than an absent mod.
    private static bool NamesLoaderInAnotherCase(string name)
        => name != LoaderDependencyName && string.Equals(name, LoaderDependencyName, StringComparison.OrdinalIgnoreCase);

    private static string Label(DiscoveredMod mod) => $"'{mod.Name}' [{mod.RelativeDirectoryPath}]";

    // Every name reachable from <paramref name="name"/> along <paramref name="edges"/>
    // restricted to <paramref name="within"/>, including the name itself when a path leads
    // back to it.
    private static HashSet<string> Reachable(string name, IReadOnlyDictionary<string, List<string>> edges, HashSet<string> within)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Stack<string>(edges[name].Where(within.Contains));
        while (frontier.Count > 0)
        {
            var current = frontier.Pop();
            if (!reached.Add(current))
                continue;
            foreach (var next in edges[current].Where(within.Contains))
                frontier.Push(next);
        }
        return reached;
    }

    // A dependency is unmet when its mod is blocked, absent, or present but failing the
    // version constraint. Returns the block reason, or null when satisfied.
    private static string? DescribeUnmetDependency(
        ManifestDependency dependency,
        IReadOnlyDictionary<string, string> availableVersions,
        HashSet<string> blockedNames)
    {
        if (availableVersions.TryGetValue(dependency.Name, out var presentVersion))
        {
            var mismatch = DescribeVersionMismatch(dependency, presentVersion);
            return mismatch is null ? null : $"requires {dependency.Raw} but {mismatch}";
        }

        if (blockedNames.Contains(dependency.Name))
            return $"requires {dependency.Raw}, which is blocked";
        return NamesLoaderInAnotherCase(dependency.Name)
            ? $"requires {dependency.Raw}, and the loader's name is {LoaderDependencyName}"
            : $"requires {dependency.Raw}";
    }

    // "found <name> <version>" when a present mod fails the entry's constraint, else null.
    // A constraint that can't be evaluated (no operator, or either side unparseable as a
    // version) degrades to a presence-only check.
    private static string? DescribeVersionMismatch(ManifestDependency dependency, string presentVersion)
    {
        if (dependency.Operator is null || dependency.Constraint is null)
            return null;

        if (!SemVer.TryParse(presentVersion, out var present) || !SemVer.TryParse(dependency.Constraint, out var required))
            return null;

        return SemVer.Satisfies(present, dependency.Operator, required)
            ? null
            : $"found {dependency.Name} {presentVersion}";
    }

    // A conflict triggers when the named mod is installed, whether or not it loads, and
    // (for a constrained entry) any installed copy's version satisfies the conflict range.
    // Judging it against the mods that load instead would be circular: blocking the
    // conflicting mod could unblock this one, whose loading could block it again. A
    // constrained entry whose present version can't be evaluated does not trigger: we
    // won't block on an unconfirmable range. Returns the block reason, or null when there
    // is no conflict.
    private static string? DescribeTriggeredConflict(ManifestDependency conflict, IReadOnlyDictionary<string, List<string>> installedVersions)
    {
        if (!installedVersions.TryGetValue(conflict.Name, out var presentVersions))
            return null;

        if (conflict.Operator is null || conflict.Constraint is null)
            return $"conflicts with {conflict.Name}, which is installed";

        if (!SemVer.TryParse(conflict.Constraint, out var range))
            return null;

        foreach (var presentVersion in presentVersions)
        {
            if (SemVer.TryParse(presentVersion, out var present) && SemVer.Satisfies(present, conflict.Operator, range))
                return $"conflicts with {conflict.Raw} (found {conflict.Name} {presentVersion})";
        }
        return null;
    }

    private static bool TryDiscoverMod(string modsDir, string manifestPath, out DiscoveredMod? mod, out BlockedMod? blockedMod)
    {
        mod = null;
        blockedMod = null;

        var modDir = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(modDir))
            return false;

        var relativeDirectoryPath = GetRelativeDirectoryPath(modsDir, manifestPath);

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
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

        try
        {
            var manifest = JsonSerializer.Deserialize<LoaderManifest>(json, JsonOptions);

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
                    "Manifest is missing a non-empty 'name'.",
                    manifest.Version);
                return false;
            }

            var name = manifest.Name.Trim();

            if (string.Equals(name, LoaderDependencyName, StringComparison.OrdinalIgnoreCase))
            {
                blockedMod = new BlockedMod(
                    string.Empty,
                    modDir,
                    relativeDirectoryPath,
                    $"Manifest name '{name}' is reserved for the loader.",
                    manifest.Version);
                return false;
            }

            if (!TryParseConstraints(manifest.Depends, "Dependency", out var dependencies, out var dependencyError))
            {
                blockedMod = new BlockedMod(name, modDir, relativeDirectoryPath, dependencyError!, manifest.Version);
                return false;
            }

            if (!TryParseConstraints(manifest.OptionalDepends, "Optional dependency", out var optionalDependencies, out var optionalError))
            {
                blockedMod = new BlockedMod(name, modDir, relativeDirectoryPath, optionalError!, manifest.Version);
                return false;
            }

            if (!TryParseConstraints(manifest.Conflicts, "Conflict", out var conflicts, out var conflictError))
            {
                blockedMod = new BlockedMod(name, modDir, relativeDirectoryPath, conflictError!, manifest.Version);
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
                name,
                manifest.Version ?? string.Empty,
                manifest.CompiledForUnity,
                manifest.CompiledForJiangyu,
                modDir,
                relativeDirectoryPath,
                manifestPath,
                bundlePaths,
                dependencies,
                optionalDependencies,
                conflicts);
            return true;
        }
        catch (Exception ex)
        {
            // A manifest whose shape the model rejects (a list given as a string, say) may
            // still carry a readable name and version, which keep it in the duplicate and
            // conflict checks as the installed mod it is.
            var (name, version) = ReadIdentity(json);
            blockedMod = new BlockedMod(
                name,
                modDir,
                relativeDirectoryPath,
                $"Failed to read manifest: {ex.Message}",
                version);
            return false;
        }
    }

    // The name and version string properties of a JSON object, when it parses as one and
    // the name is not the reserved loader name. Empty otherwise.
    private static (string Name, string? Version) ReadIdentity(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (string.Empty, null);
            // Keys match in any letter case and the last one wins, whatever its value, as
            // the manifest model reads them.
            string? Property(string key)
            {
                string? found = null;
                foreach (var property in document.RootElement.EnumerateObject())
                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                        found = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                return found;
            }
            var name = Property("name")?.Trim() ?? string.Empty;
            if (string.Equals(name, LoaderDependencyName, StringComparison.OrdinalIgnoreCase))
                name = string.Empty;
            return (name, Property("version"));
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    // Parses a `<name>` or `<name> <op> <constraint>` list, shared by `depends`,
    // `optionalDepends` and `conflicts`. <paramref name="kind"/> labels the entry in error
    // text ("Dependency" / "Optional dependency" / "Conflict").
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
