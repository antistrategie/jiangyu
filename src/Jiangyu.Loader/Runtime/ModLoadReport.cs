using Jiangyu.Shared.Bundles;
using MelonLoader;

namespace Jiangyu.Loader.Runtime;

/// <summary>
/// What each loadable mod brought to the session, gathered across the startup passes and
/// written as one line per mod once code mods are up. Each count stands in for lines the
/// loader no longer writes per item: those are debug.
/// </summary>
internal sealed class ModLoadReport
{
    internal sealed class Counts
    {
        public int Bundles;
        public int Clones;
        public int PatchOps;
        public int Locales;
        public int Types;
        public int Systems;
        public int Patches;
    }

    private readonly Dictionary<string, Counts> _byMod = new(StringComparer.Ordinal);

    public Counts For(string modId)
    {
        if (!_byMod.TryGetValue(modId, out var counts))
            _byMod[modId] = counts = new Counts();
        return counts;
    }

    /// <summary>One line per mod, in load order. Zero counts are omitted, and the folder is
    /// named only when it differs from the mod id.</summary>
    public void Write(IReadOnlyList<DiscoveredMod> mods, MelonLogger.Instance log)
    {
        foreach (var mod in mods)
        {
            var counts = For(mod.Name);
            var parts = new List<string>(7);
            Add(parts, counts.Bundles, "bundle");
            Add(parts, counts.Clones, "clone");
            Add(parts, counts.PatchOps, "patch op");
            Add(parts, counts.Locales, "locale");
            Add(parts, counts.Types, "type");
            Add(parts, counts.Systems, "system");
            Add(parts, counts.Patches, "patch", "patches");

            var version = string.IsNullOrEmpty(mod.Version) ? string.Empty : $" {mod.Version}";
            var folder = string.Equals(mod.RelativeDirectoryPath, mod.Name, StringComparison.Ordinal)
                ? string.Empty
                : $" (in {mod.RelativeDirectoryPath})";
            var content = parts.Count > 0 ? string.Join(", ", parts) : "no content";
            log.Msg($"[{mod.Name}]{version}{folder}: {content}");
        }
    }

    private static void Add(List<string> parts, int count, string singular, string plural = null)
    {
        if (count <= 0)
            return;
        parts.Add($"{count} {(count == 1 ? singular : plural ?? singular + "s")}");
    }
}
