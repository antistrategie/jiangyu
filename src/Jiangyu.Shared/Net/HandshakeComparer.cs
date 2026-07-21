namespace Jiangyu.Shared.Net;

/// <summary>The outcome of comparing two mod-set summaries.</summary>
public sealed class HandshakeVerdict
{
    public HandshakeVerdict(IReadOnlyList<string> differences)
    {
        Differences = differences;
    }

    public bool Match => Differences.Count == 0;
    public IReadOnlyList<string> Differences { get; }
}

/// <summary>
/// Compares two peers' <see cref="ModSetSummary"/> and renders each difference as a line
/// a player can act on. Any difference rejects the session: non-identical sims desync by
/// construction, so this check is mandatory, not advisory.
/// </summary>
public static class HandshakeComparer
{
    public static HandshakeVerdict Compare(ModSetSummary local, ModSetSummary remote)
    {
        var differences = new List<string>();

        if (local.NetProtocol != remote.NetProtocol)
        {
            differences.Add($"net protocol: local {local.NetProtocol}, remote {remote.NetProtocol}");
            return new HandshakeVerdict(differences);
        }

        if (!string.Equals(local.GameBuild, remote.GameBuild, StringComparison.Ordinal))
            differences.Add($"game build: local {local.GameBuild}, remote {remote.GameBuild}");

        if (!string.Equals(local.LoaderVersion, remote.LoaderVersion, StringComparison.Ordinal))
            differences.Add($"loader: local {local.LoaderVersion}, remote {remote.LoaderVersion}");

        CompareModSets(local.Mods, remote.Mods, differences);
        return new HandshakeVerdict(differences);
    }

    private static void CompareModSets(List<ModSetEntry> local, List<ModSetEntry> remote, List<string> differences)
    {
        var localByName = local.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var remoteByName = remote.ToDictionary(m => m.Name, StringComparer.Ordinal);

        foreach (var mod in local)
        {
            if (!remoteByName.TryGetValue(mod.Name, out var theirs))
            {
                differences.Add($"mod '{mod.Name}' {mod.Version}: only on local");
                continue;
            }

            if (!string.Equals(mod.Version, theirs.Version, StringComparison.Ordinal))
                differences.Add($"mod '{mod.Name}': version local {mod.Version}, remote {theirs.Version}");
            else if (!string.Equals(mod.ManifestHash, theirs.ManifestHash, StringComparison.Ordinal))
                differences.Add($"mod '{mod.Name}': same version, different content (manifest hash mismatch)");
        }

        foreach (var mod in remote)
        {
            if (!localByName.ContainsKey(mod.Name))
                differences.Add($"mod '{mod.Name}' {mod.Version}: only on remote");
        }

        // Load order decides conflict winners, so an identical set in a different order
        // is still a different sim.
        if (differences.Count == 0
            && !local.Select(m => m.Name).SequenceEqual(remote.Select(m => m.Name), StringComparer.Ordinal))
        {
            differences.Add(
                $"mod load order: local [{string.Join(", ", local.Select(m => m.Name))}], "
                + $"remote [{string.Join(", ", remote.Select(m => m.Name))}]");
        }
    }
}
