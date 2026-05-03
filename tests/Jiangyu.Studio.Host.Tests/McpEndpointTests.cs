using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jiangyu.Studio.Host.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests for the HTTP MCP transport mounted at <c>/mcp</c>. Exercises the
/// bearer-token gate, the body-size cap, and a happy-path tools/list call
/// to prove the same <see cref="McpServer"/> reflection target backs both
/// stdio and http transports.
/// </summary>
public class McpEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-not-a-real-secret";
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var mcp = new McpServer();
        mcp.DiscoverTools();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        _app = builder.Build();
        _app.UseRouting();
        McpEndpoint.Map(_app, mcp, Token);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private HttpRequestMessage MakeRequest(string body, string? token = Token)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return msg;
    }

    [Fact]
    public async Task MissingAuth_Returns401()
    {
        var response = await _client.SendAsync(MakeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        var response = await _client.SendAsync(MakeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", token: "totally-bogus"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CorrectToken_ToolsList_ReturnsTools()
    {
        var response = await _client.SendAsync(MakeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() > 0,
            "Expected tools/list to return at least one tool when authenticated");
    }

    [Fact]
    public async Task CorrectToken_Initialize_EchoesProtocolVersion()
    {
        var response = await _client.SendAsync(MakeRequest(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2025-06-18",
            doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task OversizedBody_DeclaredViaContentLength_Returns413()
    {
        // Build a request with a Content-Length header that exceeds the cap
        // without actually streaming that many bytes — the endpoint must
        // reject up front based on the declared length.
        var msg = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("x", Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        // ContentLength is normally derived from the body; assigning explicitly
        // forces the over-cap declaration on the wire.
        msg.Content.Headers.ContentLength = McpEndpoint.MaxBodyBytes + 1L;

        var response = await _client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task NotificationsInitialized_ReturnsNoContent()
    {
        // notifications/initialized is fire-and-forget; the JSON-RPC server
        // returns an empty string and the endpoint should respond 204.
        var response = await _client.SendAsync(MakeRequest(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
