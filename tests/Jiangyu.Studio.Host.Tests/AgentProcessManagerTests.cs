using Jiangyu.Studio.Host.Acp;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests over <see cref="AgentProcessManager"/>'s session-time configuration
/// surface (no subprocess spawned). The duality matters because no agent
/// honours both transports: Claude drops HTTP, Copilot rejects stdio. A
/// regression that cuts either entry surfaces only when the affected agent
/// stops seeing Jiangyu tools, so cover both shapes here.
/// </summary>
public class AgentProcessManagerTests : IDisposable
{
    private readonly string? _savedUrl;
    private readonly string? _savedToken;

    public AgentProcessManagerTests()
    {
        _savedUrl = AgentProcessManager.HttpMcpUrl;
        _savedToken = AgentProcessManager.HttpMcpToken;
    }

    public void Dispose()
    {
        AgentProcessManager.HttpMcpUrl = _savedUrl;
        AgentProcessManager.HttpMcpToken = _savedToken;
    }

    [Fact]
    public void BuildMcpServerConfig_WithBothEndpoints_EmitsStdioAndHttpEntries()
    {
        AgentProcessManager.HttpMcpUrl = "http://127.0.0.1:41697/mcp";
        AgentProcessManager.HttpMcpToken = "abc123";

        var configs = AgentProcessManager.BuildMcpServerConfig();

        var stdio = configs.SingleOrDefault(c => c.Type is null);
        Assert.NotNull(stdio);
        Assert.Equal("jiangyu", stdio.Name);
        Assert.NotNull(stdio.Command);
        Assert.EndsWith(
            OperatingSystem.IsWindows() ? "jiangyu-mcp.exe" : "jiangyu-mcp",
            stdio.Command);

        var http = configs.SingleOrDefault(c => c.Type == "http");
        Assert.NotNull(http);
        Assert.Equal("jiangyu", http.Name);
        Assert.Equal("http://127.0.0.1:41697/mcp", http.Url);

        // Both entries share the same name on purpose: each agent drops the
        // entry it can't honour and the model only ever sees one "jiangyu".
        var bothJiangyu = configs.Where(c => c.Name == "jiangyu").Count();
        Assert.Equal(2, bothJiangyu);
    }

    [Fact]
    public void BuildMcpServerConfig_AuthorizationHeaderCarriesBearerToken()
    {
        AgentProcessManager.HttpMcpUrl = "http://127.0.0.1:41697/mcp";
        AgentProcessManager.HttpMcpToken = "secret-token-xyz";

        var http = AgentProcessManager.BuildMcpServerConfig()
            .Single(c => c.Type == "http");

        Assert.NotNull(http.Headers);
        var authHeader = http.Headers.Single(h => h.Name == "Authorization");
        Assert.Equal("Bearer secret-token-xyz", authHeader.Value);
    }

    [Fact]
    public void BuildMcpServerConfig_WithoutHttpEndpoint_OmitsHttpEntry()
    {
        AgentProcessManager.HttpMcpUrl = null;
        AgentProcessManager.HttpMcpToken = null;

        var configs = AgentProcessManager.BuildMcpServerConfig();

        Assert.DoesNotContain(configs, c => c.Type == "http");
        // stdio entry still emitted as long as the binary exists in the test
        // bin (it does — Studio.Host's ProjectReference copies it).
    }

    [Fact]
    public void DescribeMcpServers_RedactsBearerToken()
    {
        AgentProcessManager.HttpMcpUrl = "http://127.0.0.1:41697/mcp";
        AgentProcessManager.HttpMcpToken = "should-not-appear-in-logs";

        var configs = AgentProcessManager.BuildMcpServerConfig();
        var rendered = AgentProcessManager.DescribeMcpServers(configs);

        Assert.DoesNotContain("should-not-appear-in-logs", rendered);
        Assert.Contains("name=jiangyu", rendered);
        Assert.Contains("type=http", rendered);
    }
}
