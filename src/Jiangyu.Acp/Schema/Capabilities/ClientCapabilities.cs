using System.Text.Json.Serialization;

namespace Jiangyu.Acp.Schema;

public sealed class ClientCapabilities
{
    [JsonPropertyName("fs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileSystemCapability? Fs { get; set; }

    [JsonPropertyName("terminal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Terminal { get; set; }
}

public sealed class FileSystemCapability
{
    [JsonPropertyName("readTextFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadTextFile { get; set; }

    [JsonPropertyName("writeTextFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WriteTextFile { get; set; }
}
