namespace Jiangyu.Core.Templates;

/// <summary>
/// Process-wide allowlist of discriminator strings observed in vanilla
/// tagged-string fields. Populated from
/// <see cref="Models.AssetIndex.TaggedDiscriminators"/> at compile start
/// (and on Studio RPC startup); consulted by
/// <see cref="TemplateTypeCatalog.ResolveTaggedDiscriminator"/> and the
/// Studio editor's discriminator picker.
///
/// The validator's per-subtype heuristic accepts more discriminator
/// forms than MENACE's runtime does: <c>composite="Action"</c> resolves
/// to <c>ActionConversationNode</c> via the candidate set, but the
/// runtime <c>OnAfterDeserialize</c> matches the
/// <c>ConversationNodeType</c> enum's member name (<c>ACTION</c>). The
/// allowlist tightens validation to forms vanilla actually uses.
///
/// Static state because the same registry serves three call sites
/// (compile validator, Studio RPC, post-compile applier) with
/// identical data. Bypass-on-empty keeps fixture tests working without
/// an installed index.
/// </summary>
public static class TaggedDiscriminatorIndex
{
    private static readonly object SyncRoot = new();
    private static Dictionary<string, HashSet<string>>? _allowed;

    /// <summary>
    /// Install the per-base allowlist. <paramref name="indexed"/> is
    /// keyed by polymorphic-base FQN (matches MemberShape's
    /// TaggedPolymorphicBase). Pass null to clear.
    /// </summary>
    public static void Install(IReadOnlyDictionary<string, List<string>>? indexed)
    {
        lock (SyncRoot)
        {
            if (indexed is null || indexed.Count == 0)
            {
                _allowed = null;
                return;
            }
            var copy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (baseFqn, discriminators) in indexed)
            {
                copy[baseFqn] = new HashSet<string>(discriminators, StringComparer.Ordinal);
            }
            _allowed = copy;
        }
    }

    /// <summary>
    /// Return the sampled discriminator allowlist for
    /// <paramref name="baseFqn"/>, or null when nothing was sampled.
    /// Callers fall back to the heuristic when null.
    /// </summary>
    public static IReadOnlySet<string>? GetAllowed(string baseFqn)
    {
        if (string.IsNullOrEmpty(baseFqn)) return null;
        lock (SyncRoot)
        {
            if (_allowed is null) return null;
            return _allowed.TryGetValue(baseFqn, out var set) ? set : null;
        }
    }

    /// <summary>
    /// True when an allowlist is installed for any base. Lets the
    /// catalogue's resolver distinguish "no data sampled, fall back to
    /// heuristic" from "data sampled but this base wasn't observed".
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            lock (SyncRoot) return _allowed is not null;
        }
    }
}
