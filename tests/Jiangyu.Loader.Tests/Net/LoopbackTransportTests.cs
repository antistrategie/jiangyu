using System.Text;
using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class LoopbackTransportTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Send_DeliversToPartnerInOrder()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        Assert.True(a.Send(b.LocalPeer, NetProtocol.ControlChannel, Bytes("one")));
        Assert.True(a.Send(b.LocalPeer, NetProtocol.ControlChannel, Bytes("two")));

        var received = new List<NetInbound>();
        Assert.Equal(2, b.Receive(NetProtocol.ControlChannel, received, 8));
        Assert.Equal("one", Encoding.UTF8.GetString(received[0].Payload));
        Assert.Equal("two", Encoding.UTF8.GetString(received[1].Payload));
        Assert.Equal(a.LocalPeer, received[0].From);
    }

    [Fact]
    public void Receive_KeepsChannelsApart()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        a.Send(b.LocalPeer, NetProtocol.ControlChannel, Bytes("control"));
        a.Send(b.LocalPeer, NetProtocol.CommandChannel, Bytes("command"));

        var received = new List<NetInbound>();
        Assert.Equal(1, b.Receive(NetProtocol.CommandChannel, received, 8));
        Assert.Equal("command", Encoding.UTF8.GetString(received[0].Payload));
        Assert.Equal(1, b.Receive(NetProtocol.ControlChannel, received, 8));
    }

    [Fact]
    public void Receive_HonoursTheDrainCap()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        for (var i = 0; i < 5; i++)
            a.Send(b.LocalPeer, NetProtocol.ControlChannel, Bytes($"m{i}"));

        var received = new List<NetInbound>();
        Assert.Equal(3, b.Receive(NetProtocol.ControlChannel, received, 3));
        Assert.Equal(2, b.Receive(NetProtocol.ControlChannel, received, 8));
        Assert.Equal(0, b.Receive(NetProtocol.ControlChannel, received, 8));
    }

    [Fact]
    public void Send_ToSelfLoopsBack()
    {
        var (a, _) = LoopbackTransport.CreatePair();
        Assert.True(a.Send(a.LocalPeer, NetProtocol.ControlChannel, Bytes("self")));

        var received = new List<NetInbound>();
        Assert.Equal(1, a.Receive(NetProtocol.ControlChannel, received, 8));
        Assert.Equal(a.LocalPeer, received[0].From);
    }

    [Fact]
    public void Send_ToUnknownPeerFails()
    {
        var (a, _) = LoopbackTransport.CreatePair();
        Assert.False(a.Send(new PeerId(999), NetProtocol.ControlChannel, Bytes("lost")));
    }

    [Fact]
    public void Send_AfterDisposeFails()
    {
        var (a, b) = LoopbackTransport.CreatePair();
        b.Dispose();
        Assert.False(b.Send(a.LocalPeer, NetProtocol.ControlChannel, Bytes("gone")));
        Assert.False(a.Send(b.LocalPeer, NetProtocol.ControlChannel, Bytes("into the void")));

        var received = new List<NetInbound>();
        Assert.Equal(0, b.Receive(NetProtocol.ControlChannel, received, 8));
    }
}
