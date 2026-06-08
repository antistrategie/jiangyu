using System.IO;
using System.Text.Json;

namespace Jiangyu.Shared;

/// <summary>
/// Read/write helpers for the JSON artefacts Jiangyu ships in a compiled mod (the manifest
/// and the compiled template program). One home for the serialiser options and the
/// load-from-directory boilerplate so every artefact round-trips identically.
/// </summary>
public static class JsonStore
{
    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions.PrettyRelaxedEscape);

    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions.PrettyRelaxedEscape);

    /// <summary>
    /// Deserialise <paramref name="fileName"/> from <paramref name="directory"/>, or null when
    /// the file is absent or unreadable.
    /// </summary>
    public static T? TryLoad<T>(string directory, string fileName) where T : class
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return null;
        try { return FromJson<T>(File.ReadAllText(path)); }
        catch { return null; }
    }
}
