namespace Jiangyu.Studio.Host.Tests;

public class EnvelopeUnwrapTests
{
    [Fact]
    public void ValidEnvelope_ExtractsData()
    {
        var envelope = """{"id":"rpc","command":"Post","data":"{\"method\":\"ping\",\"id\":1}","version":2}""";

        var result = Rpc.RpcDispatcher.UnwrapEnvelope(envelope);

        Assert.Equal("""{"method":"ping","id":1}""", result);
    }

    [Fact]
    public void EnvelopeWithNullData_ReturnsFallback()
    {
        var envelope = """{"id":"rpc","command":"Post","data":null,"version":2}""";

        var result = Rpc.RpcDispatcher.UnwrapEnvelope(envelope);

        Assert.Equal(envelope, result);
    }

    [Fact]
    public void PlainJson_ReturnsSameString()
    {
        var raw = """{"method":"ping","id":1}""";

        var result = Rpc.RpcDispatcher.UnwrapEnvelope(raw);

        Assert.Equal(raw, result);
    }

    [Fact]
    public void InvalidJson_ReturnsSameString()
    {
        var garbage = "not json at all";

        var result = Rpc.RpcDispatcher.UnwrapEnvelope(garbage);

        Assert.Equal(garbage, result);
    }

    [Fact]
    public void EmptyDataString_ReturnsEmptyString()
    {
        var envelope = """{"id":"rpc","command":"Post","data":"","version":2}""";

        var result = Rpc.RpcDispatcher.UnwrapEnvelope(envelope);

        Assert.Equal("", result);
    }
}
