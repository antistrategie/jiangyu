using Jiangyu.Shared.Net;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class NetWireTests
{
    private sealed class Body
    {
        public string? Text { get; set; }
        public int Count { get; set; }
    }

    [Fact]
    public void Encode_RoundTripsTypeAndBody()
    {
        var payload = NetWire.Encode(NetMessageType.Chat, new Body { Text = "hello", Count = 3 });

        Assert.True(NetWire.TryReadType(payload, out var type));
        Assert.Equal(NetMessageType.Chat, type);

        var body = NetWire.DecodeBody<Body>(payload);
        Assert.NotNull(body);
        Assert.Equal("hello", body!.Text);
        Assert.Equal(3, body.Count);
    }

    [Fact]
    public void TryReadType_RejectsEmptyAndUnknown()
    {
        Assert.False(NetWire.TryReadType([], out _));
        Assert.False(NetWire.TryReadType([0], out _));
        Assert.False(NetWire.TryReadType([200], out _));
    }

    [Fact]
    public void DecodeBody_ReturnsNullOnMalformedJson()
    {
        Assert.Null(NetWire.DecodeBody<Body>([(byte)NetMessageType.Chat, (byte)'{']));
        Assert.Null(NetWire.DecodeBody<Body>([(byte)NetMessageType.Chat]));
    }
}
