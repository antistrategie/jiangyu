using System.Text;

namespace Jiangyu.Shared.Net.Sync;

/// <summary>One peer's hash of the agreed state projection at one action barrier,
/// partitioned by domain so a mismatch names which part diverged rather than only that
/// something did. Both peers produce one per barrier and exchange them out of band; the
/// <see cref="DesyncMonitor"/> compares same-barrier pairs.</summary>
public sealed class BarrierChecksum
{
    /// <summary>The barrier this checksum is taken at: the command sequence just applied,
    /// so both peers' checksums for the same point share a number.</summary>
    public long Barrier { get; set; }

    /// <summary>The peer that produced it.</summary>
    public ulong Source { get; set; }

    /// <summary>Hash over the whole projection: the fast equality check.</summary>
    public string? Hash { get; set; }

    /// <summary>Per-domain hashes (e.g. <c>faction:Player</c>), compared only when the top
    /// hash differs, to localise the divergence.</summary>
    public Dictionary<string, string> Partitions { get; set; } = [];
}

/// <summary>One domain of the projection: a name and its ordered lines.</summary>
public sealed class ChecksumPartition
{
    public ChecksumPartition(string name, IReadOnlyList<string> lines)
    {
        Name = name;
        Lines = lines;
    }

    public string Name { get; }
    public IReadOnlyList<string> Lines { get; }
}

/// <summary>Builds a <see cref="BarrierChecksum"/> from projected partitions and provides
/// the stable hash. FNV-1a 64 over UTF-8, the same platform-stable function the
/// determinism probe uses, so a checksum and a determinism journal agree.</summary>
public static class Checksum
{
    public static BarrierChecksum Build(long barrier, ulong source, IReadOnlyList<ChecksumPartition> partitions)
    {
        var result = new BarrierChecksum { Barrier = barrier, Source = source };
        var top = new StringBuilder();
        foreach (var partition in partitions.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var body = new StringBuilder();
            foreach (var line in partition.Lines)
                body.Append(line).Append('\n');
            var hash = Fnv1a(body.ToString());
            result.Partitions[partition.Name] = hash;
            top.Append(partition.Name).Append('=').Append(hash).Append('\n');
        }

        result.Hash = Fnv1a(top.ToString());
        return result;
    }

    public static string Fnv1a(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash.ToString("x16");
    }
}
