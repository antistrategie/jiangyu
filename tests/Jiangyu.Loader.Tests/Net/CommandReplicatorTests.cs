using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class CommandReplicatorTests
{
    // A 2-peer session over one loopback pair: peer 1 hosts, peer 2 is the client.
    private static (CommandReplicator Host, CommandReplicator Client, LoopbackTransport HostT, LoopbackTransport ClientT) Pair()
    {
        var (a, b) = LoopbackTransport.CreatePair(1, 2);
        var host = new CommandReplicator(a, a.LocalPeer, [b.LocalPeer]);
        var client = new CommandReplicator(b, a.LocalPeer, []);
        return (host, client, a, b);
    }

    private static List<NetCommand> DrainReady(CommandReplicator r)
    {
        var list = new List<NetCommand>();
        while (r.TryDequeue(out var c))
            list.Add(c);
        return list;
    }

    [Fact]
    public void HostSubmit_ReadiesLocallyAndAssignsDenseSequence()
    {
        var (host, _, _, _) = Pair();
        host.Submit("move", "{\"tile\":[1,2]}");
        host.Submit("endturn", "{}");

        var applied = DrainReady(host);
        Assert.Equal(2, applied.Count);
        Assert.Equal(0, applied[0].Seq);
        Assert.Equal(1, applied[1].Seq);
        Assert.Equal(2, host.Journal.Count);
    }

    [Fact]
    public void ClientSubmit_AppliesOnlyAfterHostOrdersItBack()
    {
        var (host, client, _, _) = Pair();

        client.Submit("move", "{\"tile\":[3,4]}");
        // Nothing applies on the client until the host has ordered it.
        Assert.Empty(DrainReady(client));
        Assert.Equal(0, client.Journal.Count);

        host.Pump();    // host receives the intent, orders and broadcasts it
        client.Pump();  // client receives the ordered copy

        var onClient = DrainReady(client);
        var onHost = DrainReady(host);
        Assert.Single(onClient);
        Assert.Single(onHost);
        Assert.Equal(0, onClient[0].Seq);
        Assert.Equal(2ul, onClient[0].Source);   // issued by the client (peer 2)
    }

    [Fact]
    public void InterleavedSubmits_ProduceOneIdenticalTotalOrderOnBothPeers()
    {
        var (host, client, _, _) = Pair();

        client.Submit("move", "c1");
        host.Submit("move", "h1");
        client.Submit("skill", "c2");
        host.Submit("endturn", "h2");

        // Pump until quiescent: client intents reach the host, ordered copies reach the client.
        for (var i = 0; i < 4; i++)
        {
            host.Pump();
            client.Pump();
        }

        var onHost = DrainReady(host);
        var onClient = DrainReady(client);

        Assert.Equal(4, onHost.Count);
        Assert.Equal(onHost.Count, onClient.Count);
        for (var i = 0; i < onHost.Count; i++)
        {
            Assert.Equal(i, onHost[i].Seq);
            Assert.Equal(onHost[i].Seq, onClient[i].Seq);
            Assert.Equal(onHost[i].Kind, onClient[i].Kind);
            Assert.Equal(onHost[i].Payload, onClient[i].Payload);
            Assert.Equal(onHost[i].Source, onClient[i].Source);
        }
    }

    [Fact]
    public void ClientReleasesInSequenceOrderDespiteBufferingAGap()
    {
        var (a, b) = LoopbackTransport.CreatePair(1, 2);
        var client = new CommandReplicator(b, a.LocalPeer, []);

        // Deliver seq 1 before seq 0 straight onto the client's command channel.
        var one = new NetCommand { Seq = 1, Source = 1, Kind = "skill", Payload = "one" };
        var zero = new NetCommand { Seq = 0, Source = 1, Kind = "move", Payload = "zero" };
        a.Send(b.LocalPeer, NetProtocol.CommandChannel, NetWire.Encode(NetMessageType.Command, one));
        client.Pump();
        Assert.Equal(0, client.ReadyCount);   // seq 1 is buffered behind the missing seq 0

        a.Send(b.LocalPeer, NetProtocol.CommandChannel, NetWire.Encode(NetMessageType.Command, zero));
        client.Pump();

        var applied = DrainReady(client);
        Assert.Equal(2, applied.Count);
        Assert.Equal("move", applied[0].Kind);
        Assert.Equal("skill", applied[1].Kind);
    }

    [Fact]
    public void ConflictingSequenceRaisesDesyncSignal()
    {
        var (a, b) = LoopbackTransport.CreatePair(1, 2);
        var client = new CommandReplicator(b, a.LocalPeer, []);
        NetCommand? conflict = null;
        client.Conflict += c => conflict = c;

        var first = new NetCommand { Seq = 0, Source = 1, Kind = "move", Payload = "a" };
        var clash = new NetCommand { Seq = 0, Source = 1, Kind = "move", Payload = "b" };
        a.Send(b.LocalPeer, NetProtocol.CommandChannel, NetWire.Encode(NetMessageType.Command, first));
        a.Send(b.LocalPeer, NetProtocol.CommandChannel, NetWire.Encode(NetMessageType.Command, clash));
        client.Pump();

        Assert.NotNull(conflict);
        Assert.Equal("b", conflict!.Payload);
    }
}
