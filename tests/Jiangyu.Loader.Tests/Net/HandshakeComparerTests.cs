using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class HandshakeComparerTests
{
    private static ModSetSummary Summary(params (string Name, string Version, string Hash)[] mods) => new()
    {
        NetProtocol = NetProtocol.Version,
        GameBuild = "0.7.10+19931",
        LoaderVersion = "0.6.0",
        Mods = [.. mods.Select(m => new ModSetEntry { Name = m.Name, Version = m.Version, ManifestHash = m.Hash })],
    };

    [Fact]
    public void IdenticalSummariesMatch()
    {
        var verdict = HandshakeComparer.Compare(
            Summary(("Alpha", "1.0.0", "aa"), ("Beta", "2.1.0", "bb")),
            Summary(("Alpha", "1.0.0", "aa"), ("Beta", "2.1.0", "bb")));
        Assert.True(verdict.Match);
        Assert.Empty(verdict.Differences);
    }

    [Fact]
    public void ProtocolMismatchShortCircuits()
    {
        var local = Summary(("Alpha", "1.0.0", "aa"));
        var remote = Summary(("Beta", "1.0.0", "bb"));
        remote.NetProtocol = NetProtocol.Version + 1;
        remote.GameBuild = "different";

        var verdict = HandshakeComparer.Compare(local, remote);
        var difference = Assert.Single(verdict.Differences);
        Assert.Contains("net protocol", difference);
    }

    [Fact]
    public void GameBuildAndLoaderMismatchAreReported()
    {
        var remote = Summary(("Alpha", "1.0.0", "aa"));
        remote.GameBuild = "0.7.11+20000";
        remote.LoaderVersion = "0.5.0";

        var verdict = HandshakeComparer.Compare(Summary(("Alpha", "1.0.0", "aa")), remote);
        Assert.False(verdict.Match);
        Assert.Contains(verdict.Differences, d => d.Contains("game build: local 0.7.10+19931, remote 0.7.11+20000"));
        Assert.Contains(verdict.Differences, d => d.Contains("loader: local 0.6.0, remote 0.5.0"));
    }

    [Fact]
    public void MissingAndExtraModsAreReportedFromBothSides()
    {
        var verdict = HandshakeComparer.Compare(
            Summary(("Alpha", "1.0.0", "aa")),
            Summary(("Beta", "2.0.0", "bb")));
        Assert.Contains(verdict.Differences, d => d.Contains("mod 'Alpha' 1.0.0: only on local"));
        Assert.Contains(verdict.Differences, d => d.Contains("mod 'Beta' 2.0.0: only on remote"));
    }

    [Fact]
    public void VersionMismatchWinsOverHashMismatch()
    {
        var verdict = HandshakeComparer.Compare(
            Summary(("Alpha", "1.0.0", "aa")),
            Summary(("Alpha", "1.1.0", "cc")));
        var difference = Assert.Single(verdict.Differences);
        Assert.Contains("version local 1.0.0, remote 1.1.0", difference);
    }

    [Fact]
    public void HashMismatchAtSameVersionIsReported()
    {
        var verdict = HandshakeComparer.Compare(
            Summary(("Alpha", "1.0.0", "aa")),
            Summary(("Alpha", "1.0.0", "cc")));
        var difference = Assert.Single(verdict.Differences);
        Assert.Contains("same version, different content", difference);
    }

    [Fact]
    public void SameSetInDifferentOrderIsRejected()
    {
        var verdict = HandshakeComparer.Compare(
            Summary(("Alpha", "1.0.0", "aa"), ("Beta", "2.0.0", "bb")),
            Summary(("Beta", "2.0.0", "bb"), ("Alpha", "1.0.0", "aa")));
        var difference = Assert.Single(verdict.Differences);
        Assert.Contains("mod load order", difference);
        Assert.Contains("local [Alpha, Beta]", difference);
        Assert.Contains("remote [Beta, Alpha]", difference);
    }

    [Fact]
    public void EmptyModSetsMatch()
    {
        var verdict = HandshakeComparer.Compare(Summary(), Summary());
        Assert.True(verdict.Match);
    }
}
