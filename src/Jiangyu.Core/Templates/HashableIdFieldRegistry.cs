using System.Text;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Registry of int-typed fields that accept string-keyed values at authoring
/// time. The compiler resolves the string to an int deterministically (FNV-1a
/// hash for content-addressed ids, lookup table for bank names). Lets modders
/// write <c>set "id" "test_rifle_fire"</c> instead of picking and memorising
/// raw integers, and keeps the same string usable everywhere a referencing
/// field also accepts it (Sound.id and ID.itemId share the hash space).
///
/// Stored as a hardcoded table for the SoundBank slice. Future expansion to
/// other type families belongs in Il2CppMetadataSupplement; the registry
/// abstraction here keeps that migration mechanical.
/// </summary>
public static class HashableIdFieldRegistry
{
    public enum HashableKind
    {
        /// <summary>String is FNV-1a 32-bit hashed to produce the int. Same
        /// string yields the same int every build, so cross-field references
        /// (Sound.id <-> ID.itemId) stay linked.</summary>
        Fnv1a32,
        /// <summary>String is looked up in <see cref="BankIds"/> to resolve a
        /// bank's <c>bankId</c> by its asset name.</summary>
        BankName,
    }

    // (declaring-type-FQN, field-name) -> resolution kind.
    // Il2Cpp-prefixed and unwrapped names are both registered because the
    // catalogue may report either form depending on the lookup path.
    private static readonly Dictionary<(string TypeFqn, string Field), HashableKind> Entries = new()
    {
        { ("Il2CppStem.Sound", "id"), HashableKind.Fnv1a32 },
        { ("Stem.Sound", "id"), HashableKind.Fnv1a32 },
        { ("Il2CppStem.ID", "itemId"), HashableKind.Fnv1a32 },
        { ("Stem.ID", "itemId"), HashableKind.Fnv1a32 },
        { ("Il2CppStem.ID", "bankId"), HashableKind.BankName },
        { ("Stem.ID", "bankId"), HashableKind.BankName },
    };

    // Vanilla MENACE soundbank names -> their serialised bankId. Read from
    // each asset's m_Structure.bankId in build 34.0.1. The IDs are a mix of
    // small counters (1..7) and FNV-style hashes; ship the table rather
    // than recomputing because some entries are baked, not derivable.
    private static readonly Dictionary<string, int> BankIds = new(StringComparer.Ordinal)
    {
        { "constructs_soundbank", 2 },
        { "environment_soundbank", 3 },
        { "explosions_soundbank", 4 },
        { "human_soundbank", 5 },
        { "vehicles_soundbank", 6 },
        { "weapons_soundbank", 7 },
        { "ui_soundbank", 631059796 },
        { "events_soundbank", 2106065257 },
        { "ambience_soundbank", -1906935000 },
        { "impacts_soundbank", -389963078 },
        { "aliens_soundbank", -1210270996 },
        { "accessories_soundbank", -1583282670 },
        { "construct_soldier_soundbank", -242829509 },
        { "music_soundbank", -1906925000 },
        { "tactical_ui_soundbank", -1602990657 },
    };

    public static bool TryResolve(string typeFqn, string fieldName, string? value, out int result, out string? error)
    {
        result = 0;
        error = null;
        if (string.IsNullOrEmpty(typeFqn) || string.IsNullOrEmpty(fieldName))
        {
            error = "type or field name is empty.";
            return false;
        }
        if (!Entries.TryGetValue((typeFqn, fieldName), out var kind))
        {
            error = $"field {typeFqn}.{fieldName} is not registered as hashable.";
            return false;
        }
        if (value == null)
        {
            error = "string value is null.";
            return false;
        }

        switch (kind)
        {
            case HashableKind.Fnv1a32:
                result = Fnv1a32(value);
                return true;
            case HashableKind.BankName:
                if (BankIds.TryGetValue(value, out var bankId))
                {
                    result = bankId;
                    return true;
                }
                error = $"no SoundBank named '{value}'. Known: {string.Join(", ", BankIds.Keys.OrderBy(k => k))}.";
                return false;
            default:
                error = $"unknown hashable kind {kind}.";
                return false;
        }
    }

    public static bool IsHashable(string typeFqn, string fieldName)
        => !string.IsNullOrEmpty(typeFqn)
           && !string.IsNullOrEmpty(fieldName)
           && Entries.ContainsKey((typeFqn, fieldName));

    // FNV-1a 32-bit, deterministic across runs / platforms. Inputs are
    // hashed as UTF-8 bytes so cross-platform builds produce the same int.
    public static int Fnv1a32(string s)
    {
        const uint FnvOffsetBasis = 2166136261u;
        const uint FnvPrime = 16777619u;
        var hash = FnvOffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        // Reinterpret as Int32; modders' hashes will land anywhere in the
        // signed 32-bit range, matching Stem's existing baked-id distribution.
        return unchecked((int)hash);
    }
}
