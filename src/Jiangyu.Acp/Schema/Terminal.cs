using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class CreateTerminalRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("command")]
    public required string Command { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvVariable[]? Env { get; set; }

    [JsonPropertyName("outputByteLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputByteLimit { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class CreateTerminalResponse
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class TerminalOutputRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class TerminalOutputResponse
{
    [JsonPropertyName("output")]
    public required string Output { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("exitStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalExitStatus? ExitStatus { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class WaitForTerminalExitRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class WaitForTerminalExitResponse
{
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("signal")]
    public string? Signal { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class KillTerminalRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class KillTerminalResponse
{
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ReleaseTerminalRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class ReleaseTerminalResponse
{
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; set; }
}

public sealed class TerminalExitStatus
{
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("signal")]
    public string? Signal { get; set; }
}
