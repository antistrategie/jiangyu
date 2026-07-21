using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class NetSessionTests
{
    private static ModSetSummary Summary(params string[] modNames) => new()
    {
        NetProtocol = NetProtocol.Version,
        GameBuild = "0.7.10+19931",
        LoaderVersion = "0.6.0",
        Mods = [.. modNames.Select(n => new ModSetEntry { Name = n, Version = "1.0.0", ManifestHash = "aa" })],
    };

    private static void PumpBoth(NetSession a, NetSession b, int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            a.Pump();
            b.Pump();
        }
    }

    [Fact]
    public void MatchingPeersReachReady()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);
        var client = new NetSession(tb, Summary("Alpha"), isHost: false);

        host.Connect(tb.LocalPeer);
        client.Connect(ta.LocalPeer);
        PumpBoth(host, client);

        Assert.Equal(NetSessionPhase.Ready, host.Phase);
        Assert.Equal(NetSessionPhase.Ready, client.Phase);
    }

    [Fact]
    public void MismatchedPeersBothReject()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);
        var client = new NetSession(tb, Summary("Alpha", "Beta"), isHost: false);

        host.Connect(tb.LocalPeer);
        client.Connect(ta.LocalPeer);
        PumpBoth(host, client);

        Assert.Equal(NetSessionPhase.Rejected, host.Phase);
        Assert.Equal(NetSessionPhase.Rejected, client.Phase);
        Assert.Contains(host.RejectDifferences, d => d.Contains("Beta"));
        Assert.Contains(client.RejectDifferences, d => d.Contains("Beta"));
    }

    [Fact]
    public void ChatFlowsBothWaysOnceReady()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);
        var client = new NetSession(tb, Summary("Alpha"), isHost: false);
        var hostHeard = new List<string>();
        var clientHeard = new List<string>();
        host.ChatReceived += (_, text) => hostHeard.Add(text);
        client.ChatReceived += (_, text) => clientHeard.Add(text);

        host.Connect(tb.LocalPeer);
        client.Connect(ta.LocalPeer);
        PumpBoth(host, client);

        Assert.True(host.SendChat("ping"));
        PumpBoth(host, client);
        Assert.True(client.SendChat("pong"));
        PumpBoth(host, client);

        Assert.Equal("pong", Assert.Single(hostHeard));
        Assert.Equal("ping", Assert.Single(clientHeard));
        Assert.Contains(host.Transcript, line => line.EndsWith(": pong"));
    }

    [Fact]
    public void ChatIsRefusedBeforeReady()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);
        host.Connect(tb.LocalPeer);
        Assert.False(host.SendChat("too early"));
    }

    [Fact]
    public void SelfSessionHandshakesAndEchoesOverOneTransport()
    {
        var (ta, _) = LoopbackTransport.CreatePair();
        var session = new NetSession(ta, Summary("Alpha"), isHost: true);
        var heard = new List<string>();
        session.ChatReceived += (_, text) => heard.Add(text);

        session.Connect(ta.LocalPeer);
        for (var i = 0; i < 4; i++)
            session.Pump();

        Assert.Equal(NetSessionPhase.Ready, session.Phase);
        Assert.True(session.SendChat("echo"));
        session.Pump();
        Assert.Equal("echo", Assert.Single(heard));
    }

    [Fact]
    public void PeerLossMarksTheSessionLost()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);
        host.Connect(tb.LocalPeer);
        host.PeerLost(tb.LocalPeer);
        Assert.Equal(NetSessionPhase.Lost, host.Phase);
    }

    [Fact]
    public void MessagesFromStrangersAreIgnored()
    {
        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, Summary("Alpha"), isHost: true);

        // The session expects a different peer than the one the transport pairs with.
        host.Connect(new PeerId(777));
        tb.Send(ta.LocalPeer, NetProtocol.ControlChannel, NetWire.Encode(NetMessageType.Summary, Summary("Alpha")));
        host.Pump();

        Assert.Equal(NetSessionPhase.Handshaking, host.Phase);
    }
}
