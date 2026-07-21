using Jiangyu.Shared.Net.Sync;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class DesyncMonitorTests
{
    private static BarrierChecksum Build(long barrier, ulong source, params (string Name, string[] Lines)[] partitions) =>
        Checksum.Build(barrier, source, [.. partitions.Select(p => new ChecksumPartition(p.Name, p.Lines))]);

    [Fact]
    public void MatchingChecksums_StayInSync()
    {
        var monitor = new DesyncMonitor();
        var local = Build(0, 1, ("faction:Player", ["hp=100"]), ("faction:Pirates", ["hp=70"]));
        var remote = Build(0, 2, ("faction:Player", ["hp=100"]), ("faction:Pirates", ["hp=70"]));

        monitor.RecordLocal(local);
        monitor.RecordRemote(remote);

        Assert.False(monitor.IsDesynced);
        Assert.Equal(1, monitor.Checked);
    }

    [Fact]
    public void DivergingPartition_IsLocalisedInTheReport()
    {
        var monitor = new DesyncMonitor();
        DesyncReport? report = null;
        monitor.Desynced += r => report = r;

        var local = Build(2, 1, ("faction:Player", ["morale=95"]), ("faction:Pirates", ["supp=0.23"]));
        var remote = Build(2, 2, ("faction:Player", ["morale=95"]), ("faction:Pirates", ["supp=0.42"]));

        monitor.RecordLocal(local);
        monitor.RecordRemote(remote);

        Assert.True(monitor.IsDesynced);
        Assert.NotNull(report);
        Assert.Equal(2, report.Barrier);
        Assert.Equal(["faction:Pirates"], report.DivergingPartitions);
    }

    [Fact]
    public void ChecksumsForDifferentBarriers_AreNotCompared()
    {
        var monitor = new DesyncMonitor();
        monitor.RecordLocal(Build(0, 1, ("f", ["a"])));
        monitor.RecordRemote(Build(1, 2, ("f", ["b"])));

        Assert.False(monitor.IsDesynced);
        Assert.Equal(0, monitor.Checked);
    }

    [Fact]
    public void OnlyTheFirstDesyncIsReported()
    {
        var monitor = new DesyncMonitor();
        var reports = new List<DesyncReport>();
        monitor.Desynced += reports.Add;

        monitor.RecordLocal(Build(0, 1, ("f", ["a"])));
        monitor.RecordRemote(Build(0, 2, ("f", ["b"])));
        monitor.RecordLocal(Build(1, 1, ("f", ["c"])));
        monitor.RecordRemote(Build(1, 2, ("f", ["d"])));

        Assert.Single(reports);
        Assert.Equal(0, reports[0].Barrier);
    }

    [Fact]
    public void PartitionPresentOnOnlyOnePeer_CountsAsDiverging()
    {
        var monitor = new DesyncMonitor();
        DesyncReport? report = null;
        monitor.Desynced += r => report = r;

        monitor.RecordLocal(Build(0, 1, ("faction:Player", ["x"]), ("faction:Reinforcements", ["y"])));
        monitor.RecordRemote(Build(0, 2, ("faction:Player", ["x"])));

        Assert.True(monitor.IsDesynced);
        Assert.Contains("faction:Reinforcements", report!.DivergingPartitions);
    }

    [Fact]
    public void IdenticalPartitions_ProduceIdenticalTopHash()
    {
        var a = Build(5, 1, ("faction:Player", ["hp=100", "ap=110"]), ("faction:Pirates", ["hp=70"]));
        var b = Build(5, 2, ("faction:Pirates", ["hp=70"]), ("faction:Player", ["hp=100", "ap=110"]));

        // Partition order at the call site must not change the hash (builder sorts).
        Assert.Equal(a.Hash, b.Hash);
    }
}
