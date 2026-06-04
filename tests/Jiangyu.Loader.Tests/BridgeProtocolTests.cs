using System.IO;
using System.Text;
using System.Text.Json;
using Jiangyu.Shared.Bridge;
using Xunit;

namespace Jiangyu.Loader.Tests;

public sealed class BridgeProtocolTests
{
    [Fact]
    public void Framing_RoundTripsAPayload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        using var stream = new MemoryStream();
        BridgeFraming.WriteMessage(stream, payload);
        stream.Position = 0;
        Assert.Equal(payload, BridgeFraming.ReadMessage(stream));
    }

    [Fact]
    public void Framing_ReadsBackToBackMessagesThenNullAtEof()
    {
        var a = Encoding.UTF8.GetBytes("one");
        var b = Encoding.UTF8.GetBytes("two");
        using var stream = new MemoryStream();
        BridgeFraming.WriteMessage(stream, a);
        BridgeFraming.WriteMessage(stream, b);
        stream.Position = 0;

        Assert.Equal(a, BridgeFraming.ReadMessage(stream));
        Assert.Equal(b, BridgeFraming.ReadMessage(stream));
        Assert.Null(BridgeFraming.ReadMessage(stream));
    }

    [Fact]
    public void ReadMessage_TruncatedHeaderOrBody_ReturnsNull()
    {
        using var shortHeader = new MemoryStream(new byte[] { 0, 0 });
        Assert.Null(BridgeFraming.ReadMessage(shortHeader));

        using var shortBody = new MemoryStream();
        shortBody.Write(new byte[] { 0, 0, 0, 10 }, 0, 4); // claims 10 bytes
        shortBody.Write(new byte[] { 1, 2, 3 }, 0, 3); // only 3 present
        shortBody.Position = 0;
        Assert.Null(BridgeFraming.ReadMessage(shortBody));
    }

    [Fact]
    public void ReadMessage_OversizeLength_ReturnsNullWithoutReadingBody()
    {
        var tooBig = BridgeProtocol.MaxMessageBytes + 1;
        using var stream = new MemoryStream();
        stream.Write(new[]
        {
            (byte)(tooBig >> 24), (byte)(tooBig >> 16), (byte)(tooBig >> 8), (byte)tooBig,
        }, 0, 4);
        stream.Position = 0;
        Assert.Null(BridgeFraming.ReadMessage(stream));
    }

    [Fact]
    public void Request_SerialisesLowercaseWireNames()
    {
        var json = JsonSerializer.Serialize(new BridgeRequest { Id = "7", Method = "ui.capture" });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("7", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("ui.capture", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public void Response_RoundTripsThroughBytesWithObjectResult()
    {
        var response = new BridgeResponse { Id = "1", Ok = true, Result = new { count = 3 } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);

        var back = JsonSerializer.Deserialize<BridgeResponse>(bytes);
        Assert.NotNull(back);
        Assert.Equal("1", back!.Id);
        Assert.True(back.Ok);
        Assert.True(back.Result is JsonElement);
        Assert.Equal(3, ((JsonElement)back.Result!).GetProperty("count").GetInt32());
    }

    [Fact]
    public void Request_ParamsObjectDeserialisesAsJsonElement()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new BridgeRequest { Id = "1", Method = "m", Params = new { enabled = true } });

        var back = JsonSerializer.Deserialize<BridgeRequest>(bytes);
        Assert.NotNull(back);
        Assert.True(back!.Params is JsonElement);
        Assert.True(((JsonElement)back.Params!).GetProperty("enabled").GetBoolean());
    }
}
