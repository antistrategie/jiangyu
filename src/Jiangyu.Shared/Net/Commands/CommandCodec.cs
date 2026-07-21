using System.Text.Json;

namespace Jiangyu.Shared.Net.Commands;

/// <summary>
/// Serialises a typed command body to and from the opaque JSON string a
/// <see cref="NetCommand.Payload"/> carries. The replication core never sees these types:
/// the loader encodes intent before <see cref="CommandReplicator.Submit"/> and decodes it
/// when applying a dequeued command. The same camelCase JSON convention as the control
/// wire keeps payloads readable in desync forensics.
/// </summary>
public static class CommandCodec
{
    public static string Encode<T>(T body) => JsonSerializer.Serialize(body, JsonOptions.CompactCamel);

    /// <summary>Decode a payload to <typeparamref name="T"/>. Null when the payload is
    /// absent or malformed, which the loader treats as a corrupt command (a desync).</summary>
    public static T? Decode<T>(string? payload)
        where T : class
    {
        if (string.IsNullOrEmpty(payload))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions.CompactCamel);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
