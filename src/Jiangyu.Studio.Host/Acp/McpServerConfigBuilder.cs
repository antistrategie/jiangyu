using Jiangyu.Acp.Schema;

namespace Jiangyu.Studio.Host.Acp;

/// <summary>
/// Builds the <c>mcpServers</c> array sent to ACP agents on
/// <c>session/new</c>. We always emit both transports (stdio
/// <c>jiangyu-mcp</c> + HTTP <c>/mcp</c>) under the same name because
/// no agent honours both: claude-agent-sdk drops HTTP entries, Copilot's
/// ACP integration rejects stdio entries. Each agent silently picks the
/// one it supports.
///
/// <para>Also handles the bundled-bun launcher rewrite and sibling
/// binary resolution for single-file publishes (<c>jiangyu-mcp</c> and
/// <c>bun</c> ship under <c>&lt;install&gt;/bin/</c>).</para>
/// </summary>
internal static class McpServerConfigBuilder
{
    /// <summary>
    /// Set by <c>Program.Main</c> at startup once the WebApp has bound. Both
    /// fields are required for the HTTP entry in <see cref="Build"/>;
    /// when either is null we only send the stdio entry.
    /// </summary>
    public static string? HttpMcpUrl { get; set; }
    public static string? HttpMcpToken { get; set; }

    /// <summary>
    /// Candidate absolute paths where a host-sibling binary
    /// <paramref name="fileName"/> may live, in priority order:
    /// <list type="number">
    ///   <item><description><see cref="Environment.ProcessPath"/>'s
    ///     directory: the host exe location for production single-file
    ///     publishes, where CI lands <c>bin/bun</c> and
    ///     <c>bin/jiangyu-mcp</c>. <see cref="AppContext.BaseDirectory"/>
    ///     can't be used here because single-file extraction points it at
    ///     a temp dir that doesn't carry the sibling
    ///     binaries.</description></item>
    ///   <item><description><see cref="AppContext.BaseDirectory"/>: dev,
    ///     test, and framework-dependent runs, where
    ///     <see cref="Environment.ProcessPath"/> is <c>dotnet</c>/testhost
    ///     and the project's build targets drop sibling binaries next to
    ///     the test/dev output assemblies.</description></item>
    /// </list>
    /// </summary>
    internal static IEnumerable<string> SiblingBinaryCandidates(string fileName)
    {
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (processDir is not null) yield return Path.Combine(processDir, "bin", fileName);
        yield return Path.Combine(AppContext.BaseDirectory, "bin", fileName);
    }

    /// <summary>
    /// Returns the first existing path from <see cref="SiblingBinaryCandidates"/>,
    /// or null when the binary isn't shipped at any candidate location
    /// (caller falls back to PATH).
    /// </summary>
    private static string? FindSiblingBinary(string fileName)
    {
        foreach (var path in SiblingBinaryCandidates(fileName))
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>
    /// Resolves the bundled bun binary at <c>&lt;install&gt;/bin/bun</c>
    /// (<c>bun.exe</c> on Windows). Returns the absolute path when it
    /// exists, null otherwise. Bundled in CI by the
    /// <c>build-studio-host</c> job; absent in dev builds and direct
    /// <c>dotnet run</c> invocations, in which case the caller falls
    /// back to PATH-resolved <c>bunx</c>/<c>npx</c>.
    /// </summary>
    public static string? ResolveBundledBun()
    {
        var name = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        return FindSiblingBinary(name);
    }

    /// <summary>
    /// Translates a registry-provided launcher (typically <c>bunx</c> /
    /// <c>npx</c> / <c>bun</c> for the npx-distributed agents that
    /// dominate the ACP registry) into the bundled bun binary, so
    /// modders without Node.js installed get the agent running. Falls
    /// through unchanged for binary agents (absolute paths) or when the
    /// bundled bun isn't present (dev builds rely on PATH).
    /// </summary>
    public static (string Command, string[] Args) ResolveLauncher(string command, string[] args)
    {
        var bundled = ResolveBundledBun();
        if (bundled is null) return (command, args);

        return command switch
        {
            // `bunx` and `npx` both delegate to bun's package runner; bun
            // is npm-spec-compatible so this is a drop-in replacement.
            "bunx" or "npx" => (bundled, ["x", .. args]),
            // Some registry entries declare `command: "bun"` with `args:
            // ["x", "package"]` already in place; leave args alone.
            "bun" => (bundled, args),
            _ => (command, args),
        };
    }

    /// <summary>
    /// Builds the mcpServers array. Sends both transports because:
    /// <list type="bullet">
    ///   <item><description><b>stdio</b> (<c>jiangyu-mcp</c> sibling exe):
    ///     Anthropic's claude-agent-sdk silently DROPS HTTP MCP entries
    ///     (despite advertising mcpCapabilities.http=true) and only
    ///     honours stdio.</description></item>
    ///   <item><description><b>http</b> (<c>POST /mcp</c> mounted on
    ///     Studio.Host): GitHub Copilot's ACP integration silently
    ///     REJECTS stdio entries (logs "Rejecting non-http/sse MCP
    ///     server") and only honours http/sse.</description></item>
    /// </list>
    /// Both share the name <c>jiangyu</c>; the agent only ends up with one
    /// after dropping the unsupported one, so the model sees a single
    /// unified server either way.
    /// </summary>
    internal static McpServerConfig[] Build()
    {
        var configs = new List<McpServerConfig>();

        // Support binaries (jiangyu-mcp, bundled bun) live in <install>/bin
        // so the studio executable stays alone at the install root and
        // users see one obvious launcher. FindSiblingBinary handles
        // single-file publishes (where AppContext.BaseDirectory is the
        // self-extract dir, not the install dir) by checking the host
        // exe's directory first.
        var exeName = OperatingSystem.IsWindows() ? "jiangyu-mcp.exe" : "jiangyu-mcp";
        var mcpPath = FindSiblingBinary(exeName);
        if (mcpPath is not null)
        {
            configs.Add(new McpServerConfig
            {
                Name = "jiangyu",
                Command = mcpPath,
                Args = [],
                Env = [],
            });
        }
        else
        {
            Console.Error.WriteLine($"[Agent] jiangyu-mcp binary not found in bin/ next to host exe or AppContext.BaseDirectory; stdio MCP disabled.");
        }

        if (HttpMcpUrl is { } url && HttpMcpToken is { } token)
        {
            configs.Add(new McpServerConfig
            {
                Name = "jiangyu",
                Type = "http",
                Url = url,
                Headers =
                [
                    new HttpHeader { Name = "Authorization", Value = $"Bearer {token}" },
                ],
            });
        }
        else
        {
            Console.Error.WriteLine("[Agent] HTTP MCP endpoint not bound; http MCP disabled.");
        }

        return [.. configs];
    }

    /// <summary>
    /// Renders the MCP server array for stderr logging. Skips the bearer
    /// token in HTTP headers — the value is a per-launch secret used to
    /// gate <c>/mcp</c> against unrelated processes on the same loopback,
    /// and dumping it to logs (which may be captured by the user's
    /// terminal scrollback or CI artefacts) defeats that.
    /// </summary>
    internal static string Describe(McpServerConfig[] configs)
    {
        var parts = new List<string>(configs.Length);
        foreach (var c in configs)
        {
            if (c.Type == "http" || c.Type == "sse")
                parts.Add($"{{name={c.Name},type={c.Type},url={c.Url},headers={c.Headers?.Length ?? 0}}}");
            else
                parts.Add($"{{name={c.Name},command={c.Command},args=[{string.Join(',', c.Args ?? [])}]}}");
        }
        return "[" + string.Join(", ", parts) + "]";
    }
}
