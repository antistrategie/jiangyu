namespace Jiangyu.Loader.Templates;

/// <summary>
/// Template ids a pass could not find on a type whose templates are live. Another loader
/// registers templates on its own schedule, so an id absent at one pass may be present at a
/// later one. Entries stay until they resolve, and each is reported once.
/// </summary>
internal sealed class LateTemplateSet
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        public Entry(string templateType, string templateId, string ownerLabel, int opCount, string detail)
        {
            TemplateType = templateType;
            TemplateId = templateId;
            OwnerLabel = ownerLabel;
            OpCount = opCount;
            Detail = detail;
        }

        public string TemplateType { get; }
        public string TemplateId { get; }
        public string OwnerLabel { get; }
        public int OpCount { get; }
        /// <summary>The caller's own wording for the report line, null when the type and id
        /// say enough.</summary>
        public string Detail { get; }
        public bool Reported { get; set; }
    }

    public bool IsEmpty => _entries.Count == 0;

    public int Count => _entries.Count;

    /// <summary>Operations across every entry still waiting.</summary>
    public int PendingOps
    {
        get
        {
            var total = 0;
            foreach (var entry in _entries.Values)
                total += entry.OpCount;
            return total;
        }
    }

    public IEnumerable<Entry> ForType(string templateType)
    {
        foreach (var entry in _entries.Values)
        {
            if (string.Equals(entry.TemplateType, templateType, StringComparison.Ordinal))
                yield return entry;
        }
    }

    public List<Entry> Snapshot() => new(_entries.Values);

    public bool Contains(string templateType, string templateId)
        => _entries.ContainsKey(Key(templateType, templateId));

    /// <summary>Whether an entry exists for the id under any type <paramref name="typeMatches"/>
    /// accepts.</summary>
    public bool Contains(Func<string, bool> typeMatches, string templateId)
    {
        foreach (var entry in _entries.Values)
        {
            if (string.Equals(entry.TemplateId, templateId, StringComparison.Ordinal) && typeMatches(entry.TemplateType))
                return true;
        }

        return false;
    }

    /// <summary>Adds the id. An id already present keeps its first entry.</summary>
    public bool Add(string templateType, string templateId, string ownerLabel, int opCount, string detail = null)
        => _entries.TryAdd(Key(templateType, templateId), new Entry(templateType, templateId, ownerLabel, opCount, detail));

    public bool Remove(string templateType, string templateId)
        => _entries.Remove(Key(templateType, templateId));

    /// <summary>Marks every entry unreported, so the next schedule end reports it again.</summary>
    public void ResetReported()
    {
        foreach (var entry in _entries.Values)
            entry.Reported = false;
    }

    /// <summary>Entries not yet reported, each marked reported as it is returned.</summary>
    public List<Entry> TakeUnreported()
    {
        var unreported = new List<Entry>();
        foreach (var entry in _entries.Values)
        {
            if (entry.Reported)
                continue;
            entry.Reported = true;
            unreported.Add(entry);
        }

        return unreported;
    }

    /// <summary>The type-qualified key for a template id, shared with the appliers' own
    /// per-id sets so a late registration can be matched against them.</summary>
    internal static string Key(string templateType, string templateId) => templateType + "\0" + templateId;

    /// <summary>Whether <paramref name="keys"/> (each a <see cref="Key"/>) names the template
    /// under any type that addresses the same templates as <paramref name="templateType"/>
    /// (<see cref="TemplateRuntimeAccess.SameTemplateSpace"/>, or <paramref name="sameSpace"/>
    /// when given): a clone is registered in every ancestor map, so one template has as many
    /// keys as it has names.</summary>
    public static bool KeyedUnderAnyName(IEnumerable<string> keys, string templateType, string templateId, Func<string, string, bool> sameSpace = null)
    {
        if (keys == null || string.IsNullOrEmpty(templateId))
            return false;
        sameSpace ??= TemplateRuntimeAccess.SameTemplateSpace;
        foreach (var key in keys)
        {
            var separator = key.IndexOf('\0');
            if (separator < 0 || key.Length - separator - 1 != templateId.Length)
                continue;
            if (string.CompareOrdinal(key, separator + 1, templateId, 0, templateId.Length) == 0
                && sameSpace(templateType, key.Substring(0, separator)))
                return true;
        }

        return false;
    }
}
