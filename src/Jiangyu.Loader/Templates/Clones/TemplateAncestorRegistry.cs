namespace Jiangyu.Loader.Templates;

// Exact type and folder matching keeps a subtree query from gaining clones from
// elsewhere. Entries remain available when MENACE clears and rebuilds a cache.
internal sealed class TemplateAncestorRegistry<T>
{
    private sealed class Slot
    {
        public string Folder;
        public readonly Dictionary<string, T> Clones = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<IntPtr, Slot> _slots = new();

    public void Remember(IntPtr type, string folder, string id, T clone)
    {
        if (!_slots.TryGetValue(type, out var slot))
            _slots[type] = slot = new Slot { Folder = folder.TrimEnd('/') };
        slot.Clones[id] = clone;
    }

    public bool TryGet(IntPtr type, string folder, out IReadOnlyDictionary<string, T> clones)
    {
        clones = null;
        if (!_slots.TryGetValue(type, out var slot)
            || !string.Equals(slot.Folder, folder?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;
        clones = slot.Clones;
        return true;
    }

    public static List<T> Missing(IReadOnlyDictionary<string, T> clones, IEnumerable<string> loadedIds)
    {
        // A template already returned by the game or another mod retains precedence.
        var present = new HashSet<string>(loadedIds, StringComparer.Ordinal);
        var missing = new List<T>();
        foreach (var pair in clones)
            if (!present.Contains(pair.Key))
                missing.Add(pair.Value);
        return missing;
    }
}
