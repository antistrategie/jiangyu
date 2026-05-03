using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class CreateTerminalRequest
{
    [JsonPropertyName("command")]
    public required string Command { get; set; }

    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("maxOutputBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxOutputBytes { get; set; }
}

public sealed class CreateTerminalResponse
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class TerminalOutputRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class TerminalOutputResponse
{
    [JsonPropertyName("output")]
    public required string Output { get; set; }

    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; set; }

    [JsonPropertyName("exitStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalExitStatus? ExitStatus { get; set; }
}

public sealed class WaitForTerminalExitRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class WaitForTerminalExitResponse
{
    [JsonPropertyName("exitStatus")]
    public required TerminalExitStatus ExitStatus { get; set; }
}

public sealed class KillTerminalRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class KillTerminalResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public sealed class ReleaseTerminalRequest
{
    [JsonPropertyName("terminalId")]
    public required string TerminalId { get; set; }
}

public sealed class ReleaseTerminalResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public sealed class TerminalExitStatus
{
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; set; }

    [JsonPropertyName("signal")]
    public string? Signal { get; set; }
}
