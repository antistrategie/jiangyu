using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Rpc;

namespace Jiangyu.Studio.Host.Rpc;

/// <summary>
/// Shape of the <c>agentsRegistryFetched</c> notification. Carries an
/// <c>ok</c> discriminator so the frontend treats it the same way as every
/// other host-pushed notification (CompileFinished's <c>success</c>,
/// AgentSessionCreated's <c>ok</c>): one binary field decides the path, no
/// "is this property defined" introspection.
/// </summary>
[RpcType]
public sealed class AgentRegistryFetchedNotification
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    // Raw registry document from the CDN. Shape is the published v1
    // schema; the frontend has its own RegistryDocument types.
    [JsonPropertyName("registry")]
    public JsonElement? Registry { get; init; }
}

/// <summary>
/// Shape of the <c>agentSessionCreated</c> notification. Three outcomes
/// share one envelope: session opened (<c>ok=true</c>), generic failure
/// (<c>ok=false</c> + <c>error</c>), or ACP auth_required
/// (<c>ok=false</c> + <c>authRequired=true</c> + <c>authMethods</c>).
/// The frontend handler reads <c>ok</c> first, then <c>authRequired</c>
/// to decide between sign-in prompt and plain error.
/// </summary>
[RpcType]
public sealed class AgentSessionCreatedNotification
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    // SessionModes / ConfigOption / AuthMethod come from Jiangyu.Acp.Schema
    // and aren't [RpcType]-annotated (ACP stays a standalone package with no
    // RPC coupling). The wire shape is whatever JsonSerializer writes for
    // those types; the frontend has its own hand-authored interfaces matching
    // the same fields.
    [JsonPropertyName("modes")]
    public JsonElement? Modes { get; init; }

    [JsonPropertyName("configOptions")]
    public JsonElement? ConfigOptions { get; init; }

    [JsonPropertyName("authRequired")]
    public bool AuthRequired { get; init; }

    [JsonPropertyName("authMethods")]
    public JsonElement? AuthMethods { get; init; }
}
