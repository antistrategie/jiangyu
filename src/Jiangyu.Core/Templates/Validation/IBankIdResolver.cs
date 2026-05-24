namespace Jiangyu.Core.Templates;

/// <summary>
/// Resolves a <c>Stem.SoundBank</c> by its asset name to the serialised
/// <c>bankId</c> integer that <c>Stem.ID.bankId</c> references throughout
/// the game's data. Decouples <see cref="HashableIdFieldRegistry"/> from the
/// source of truth so the host can plug in an asset-index-backed
/// implementation in production and an in-memory dictionary in tests.
/// </summary>
public interface IBankIdResolver
{
    /// <summary>Look up <paramref name="bankName"/> and return its
    /// <c>bankId</c>. Returns <c>false</c> when the name is not known to the
    /// resolver. Implementations must be case-sensitive (asset names are
    /// matched verbatim throughout Jiangyu).</summary>
    bool TryResolve(string bankName, out int bankId);

    /// <summary>The set of names this resolver knows. Used to render
    /// helpful error messages listing valid options when a lookup misses.</summary>
    IEnumerable<string> KnownBankNames { get; }
}

/// <summary>
/// In-memory <see cref="IBankIdResolver"/> backed by an explicit dictionary.
/// Used by tests and by the production path (which builds the dictionary
/// from the asset index at compile start).
/// </summary>
public sealed class InMemoryBankIdResolver : IBankIdResolver
{
    private readonly Dictionary<string, int> _byName;

    public InMemoryBankIdResolver(IEnumerable<KeyValuePair<string, int>> entries)
    {
        _byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            _byName[e.Key] = e.Value;
        }
    }

    public bool TryResolve(string bankName, out int bankId)
        => _byName.TryGetValue(bankName, out bankId);

    public IEnumerable<string> KnownBankNames => _byName.Keys;
}
