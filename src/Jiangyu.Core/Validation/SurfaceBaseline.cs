using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Jiangyu.Core.Validation;

/// <summary>One game type and the public method signatures it declares.</summary>
public sealed record SurfaceType(string TypeName, IReadOnlyList<string> Members);

/// <summary>A snapshot of the method surface of the game types a manifest binds to.</summary>
public sealed record Surface(IReadOnlyList<SurfaceType> Types);

/// <summary>Per-type member-set drift between two surfaces.</summary>
public sealed record SurfaceTypeDiff(string TypeName, IReadOnlyList<string> AddedMembers, IReadOnlyList<string> RemovedMembers);

/// <summary>The drift between a committed surface and the current game.</summary>
public sealed record SurfaceDiff(
    IReadOnlyList<string> AddedTypes,
    IReadOnlyList<string> RemovedTypes,
    IReadOnlyList<SurfaceTypeDiff> ChangedTypes)
{
    /// <summary>No type added or removed, and no member added or removed on any shared type.</summary>
    public bool IsEmpty => AddedTypes.Count == 0 && RemovedTypes.Count == 0 && ChangedTypes.Count == 0;
}

/// <summary>
/// The method-surface arm of the structural baseline: a committed snapshot of the
/// public methods declared by the game types a manifest binds to, plus the diff against
/// the current game. The codegen contract check fails the build when a bound target is
/// removed or renamed (drift the manifest depends on); this is the complementary half —
/// it reports what a game update *added* to those types, i.e. candidate new verbs and
/// hooks. Capture is the thin reflection shell; the diff, the report, and the signature
/// formatting are pure, so they are unit-testable without a game.
/// </summary>
public static class SurfaceBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Short(Type t) => (t.FullName ?? t.Name).Replace('+', '.');

    /// <summary>The stable signature of a method, as recorded in the baseline.</summary>
    public static string MemberSignature(MethodInfo m)
        => $"{Short(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType)))})";

    /// <summary>
    /// Capture the declared public surface of each named type: real methods (a candidate
    /// verb) plus the <c>add_</c> event accessors (a candidate hook). The other special-
    /// name members — property accessors, <c>remove_</c>, operators — are noise and are
    /// dropped. Il2CppInterop projects a game event as <c>add_</c>/<c>remove_</c> methods
    /// rather than CLR event metadata, which is also how the hook publishers consume it,
    /// so a game update that adds an event surfaces here as a new <c>add_</c> accessor. A
    /// type that does not resolve is skipped — its absence is the contract check's job.
    /// </summary>
    public static Surface Capture(Assembly game, IEnumerable<string> typeNames)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = new List<SurfaceType>();
        foreach (var name in typeNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            var type = game.GetType(name);
            if (type is null) continue;

            var members = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName || m.Name.StartsWith("add_", StringComparison.Ordinal))
                .Select(MemberSignature)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            types.Add(new SurfaceType(name, members));
        }
        return new Surface(types);
    }

    /// <summary>Diff a committed surface against the current one.</summary>
    public static SurfaceDiff Diff(Surface previous, Surface current)
    {
        var prev = previous.Types.ToDictionary(t => t.TypeName, t => t.Members, StringComparer.Ordinal);
        var curr = current.Types.ToDictionary(t => t.TypeName, t => t.Members, StringComparer.Ordinal);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<SurfaceTypeDiff>();

        foreach (var name in prev.Keys.Union(curr.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var inPrev = prev.TryGetValue(name, out var prevMembers);
            var inCurr = curr.TryGetValue(name, out var currMembers);

            if (!inPrev) { added.Add(name); continue; }
            if (!inCurr) { removed.Add(name); continue; }

            var addedMembers = currMembers!.Except(prevMembers!, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var removedMembers = prevMembers!.Except(currMembers!, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (addedMembers.Count > 0 || removedMembers.Count > 0)
                changed.Add(new SurfaceTypeDiff(name, addedMembers, removedMembers));
        }

        return new SurfaceDiff(added, removed, changed);
    }

    /// <summary>Render the diff as an audit report. An empty diff reads as "no drift".</summary>
    public static string FormatReport(SurfaceDiff diff)
    {
        if (diff.IsEmpty)
            return "Surface baseline: no drift on the bound game types.";

        var lines = new List<string> { "Surface baseline: the bound game types drifted." };

        if (diff.AddedTypes.Count > 0)
        {
            lines.Add($"NEW TYPES ({diff.AddedTypes.Count}):");
            foreach (var t in diff.AddedTypes) lines.Add($"  {t}");
        }
        if (diff.RemovedTypes.Count > 0)
        {
            lines.Add($"REMOVED TYPES ({diff.RemovedTypes.Count}) -- also a contract-check failure:");
            foreach (var t in diff.RemovedTypes) lines.Add($"  {t}");
        }
        foreach (var t in diff.ChangedTypes)
        {
            lines.Add($"{t.TypeName}:");
            foreach (var m in t.AddedMembers) lines.Add($"  + {m}   (candidate verb/hook)");
            foreach (var m in t.RemovedMembers) lines.Add($"  - {m}   (drift)");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Capture the surface of <paramref name="typeNames"/> and either rewrite the
    /// committed baseline (when <paramref name="update"/>) or diff against it and report
    /// through <paramref name="log"/>. With no committed baseline yet, write it. Returns
    /// the diff, empty unless an existing baseline drifted.
    /// </summary>
    private static readonly SurfaceDiff NoDiff = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SurfaceTypeDiff>());

    public static SurfaceDiff CheckOrUpdate(Assembly game, IEnumerable<string> typeNames, string baselinePath, bool update, Action<string> log)
    {
        var current = Capture(game, typeNames);

        if (update)
        {
            Save(baselinePath, current);
            log($"surface: wrote baseline {baselinePath} ({current.Types.Count} type(s))");
            return NoDiff;
        }

        var previous = Load(baselinePath);
        if (previous is null)
        {
            Save(baselinePath, current);
            log($"surface: created baseline {baselinePath} ({current.Types.Count} type(s))");
            return NoDiff;
        }

        var diff = Diff(previous, current);
        log(FormatReport(diff));
        return diff;
    }

    /// <summary>Load a committed surface, or null when none is committed yet.</summary>
    public static Surface? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<Surface>(File.ReadAllText(path), JsonOptions) : null;

    /// <summary>Write the surface baseline.</summary>
    public static void Save(string path, Surface surface)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(surface, JsonOptions) + "\n");
    }
}
