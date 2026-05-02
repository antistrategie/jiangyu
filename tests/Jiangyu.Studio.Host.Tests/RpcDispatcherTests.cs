using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class RpcDispatcherTests
{
    /// <summary>
    /// Captures the response string from HandleMessage for assertions.
    /// </summary>
    private static string Dispatch(string message)
    {
        string? captured = null;
        RpcDispatcher.HandleMessage(null!, message, response => captured = response);
        return captured ?? throw new InvalidOperationException("No response sent");
    }

    [Fact]
    public void UnknownMethod_ReturnsError()
    {
        var response = Dispatch("""{"id":1,"method":"totallyBogus"}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Contains("Unknown method", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void MissingMethod_ReturnsError()
    {
        var response = Dispatch("""{"id":2}""");

        var doc = JsonDocument.Parse(response);
        Assert.Contains("method", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJson_ReturnsError()
    {
        var response = Dispatch("not valid json {{{");

        var doc = JsonDocument.Parse(response);
        Assert.Contains("Malformed", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ResponseId_MatchesRequestId()
    {
        var response = Dispatch("""{"id":99,"method":"bogus"}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(99, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void MalformedJson_StillReturnsId_WhenParseable()
    {
        // A request with a valid id but missing method should still
        // return the id so the frontend promise doesn't hang.
        var response = Dispatch("""{"id":7,"params":{}}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }
}
