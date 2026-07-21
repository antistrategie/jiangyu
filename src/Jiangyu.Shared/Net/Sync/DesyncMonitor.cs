namespace Jiangyu.Shared.Net.Sync;

/// <summary>What the monitor found when two peers' checksums for one barrier disagreed.</summary>
public sealed class DesyncReport
{
    public long Barrier { get; set; }
    public string? LocalHash { get; set; }
    public string? RemoteHash { get; set; }

    /// <summary>The partition names whose hashes differed (or that only one peer had),
    /// ordinal-sorted. The actionable localisation: which domain of the sim diverged.</summary>
    public List<string> DivergingPartitions { get; set; } = [];
}

/// <summary>
/// Continuous desync detection: each peer records its own barrier checksums and the
/// remote's, and whenever both are present for a barrier their top hashes are compared.
/// A mismatch is a desync, localised to the diverging partitions. Only the first desync is
/// reported, because it is the actionable one: everything after a divergence is downstream
/// noise. Transport-agnostic; the session feeds it local and remote checksums.
/// </summary>
public sealed class DesyncMonitor
{
    private readonly Dictionary<long, BarrierChecksum> _local = [];
    private readonly Dictionary<long, BarrierChecksum> _remote = [];

    /// <summary>Raised once, when the first barrier mismatch is detected.</summary>
    public event Action<DesyncReport>? Desynced;

    /// <summary>True once a mismatch has been found.</summary>
    public bool IsDesynced { get; private set; }

    /// <summary>The first (and only reported) divergence, or null while in sync.</summary>
    public DesyncReport? FirstDesync { get; private set; }

    /// <summary>Barriers checked so far (both peers' checksums present).</summary>
    public int Checked { get; private set; }

    public void RecordLocal(BarrierChecksum checksum)
    {
        _local[checksum.Barrier] = checksum;
        Compare(checksum.Barrier);
    }

    public void RecordRemote(BarrierChecksum checksum)
    {
        _remote[checksum.Barrier] = checksum;
        Compare(checksum.Barrier);
    }

    private void Compare(long barrier)
    {
        if (IsDesynced)
            return;
        if (!_local.TryGetValue(barrier, out var local) || !_remote.TryGetValue(barrier, out var remote))
            return;

        Checked++;
        if (local.Hash == remote.Hash)
            return;

        var report = new DesyncReport { Barrier = barrier, LocalHash = local.Hash, RemoteHash = remote.Hash };
        foreach (var name in local.Partitions.Keys.Union(remote.Partitions.Keys))
        {
            local.Partitions.TryGetValue(name, out var lh);
            remote.Partitions.TryGetValue(name, out var rh);
            if (lh != rh)
                report.DivergingPartitions.Add(name);
        }

        report.DivergingPartitions.Sort(StringComparer.Ordinal);
        IsDesynced = true;
        FirstDesync = report;
        Desynced?.Invoke(report);
    }
}
