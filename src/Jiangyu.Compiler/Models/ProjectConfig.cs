using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Compiler.Models;

public sealed class ProjectConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonPropertyName("game")]
    public string? Game { get; set; }

    [JsonPropertyName("unityEditor")]
    public string? UnityEditor { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ProjectConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ProjectConfig>(json, JsonOptions)
        ?? throw new InvalidOperationException("Failed to deserialise .jiangyu/config.json");

    public static string ConfigDir => ".jiangyu";
    public static string FilePath => Path.Combine(ConfigDir, "config.json");
}
