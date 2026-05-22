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
        // A SoundBank's own bankId is the FNV-1a hash of the bank's chosen
        // name. Modders cloning a vanilla bank pick a new name and set this
        // field to it; both the bank's bankId and any Stem.ID.bankId
        // reference to it resolve to the same int through the same hash.
        // Top-level patches use the short template name; composite-inner
        // ops use the FQN. Register all three forms.
        { ("SoundBank", "bankId"), HashableKind.Fnv1a32 },
        { ("Il2CppStem.SoundBank", "bankId"), HashableKind.Fnv1a32 },
        { ("Stem.SoundBank", "bankId"), HashableKind.Fnv1a32 },
    };

    // Bank-name → bankId resolution is supplied externally so the lookup
    // table doesn't have to ship as hardcoded data that goes stale on every
    // MENACE update. The compile path injects an asset-index-backed
    // resolver here at startup; tests inject an in-memory dictionary.
    // Until an explicit resolver is installed every BankName lookup errors
    // cleanly with a "no bank resolver installed" message.
    private static IBankIdResolver? _bankIdResolver;

    /// <summary>
    /// Compile-time wiring: the host (CompilationService, StudioServer, or
    /// a test harness) builds an <see cref="IBankIdResolver"/> from the
    /// loaded asset index and installs it before any TryResolve call hits
    /// a BankName field. Pass null to clear (e.g. between tests).
    ///
    /// <para><b>Contract:</b> the installed resolver is process-static
    /// and persists across calls until the next install or an explicit
    /// null-install. Every entry point into the validator
    /// (<see cref="TemplateCatalogValidator.Validate"/> and
    /// <see cref="TemplateCatalogValidator.ValidateEditorDocument"/>)
    /// MUST install a fresh resolver for the document or compile unit
    /// currently being validated. Otherwise BankName lookups will see
    /// the previous caller's data. Current callers (CompilationService,
    /// RpcHandlers.templatesParse) follow this rule; new entry points
    /// must do the same.</para>
    /// </summary>
    public static void InstallBankIdResolver(IBankIdResolver? resolver)
        => _bankIdResolver = resolver;

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
                if (_bankIdResolver == null)
                {
                    error = "no SoundBank resolver installed. The compile host must call "
                            + "HashableIdFieldRegistry.InstallBankIdResolver(...) once the asset index is loaded.";
                    return false;
                }
                if (_bankIdResolver.TryResolve(value, out var bankId))
                {
                    result = bankId;
                    return true;
                }
                error = $"no SoundBank named '{value}'. Known: {string.Join(", ", _bankIdResolver.KnownBankNames.OrderBy(k => k))}.";
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
