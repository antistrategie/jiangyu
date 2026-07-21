using System.Text.Json;

namespace Jiangyu.Shared.Net;

/// <summary>Discriminator byte leading every net message. Control-channel traffic uses
/// the handshake and chat types; the replicated command stream on the command channel
/// uses <see cref="Command"/>.</summary>
public enum NetMessageType : byte
{
    Summary = 1,
    Accept = 2,
    Reject = 3,
    Chat = 4,
    Command = 5,
}

/// <summary>
/// Control-message wire format: one discriminator byte followed by a compact camelCase
/// UTF-8 JSON body. Bandwidth is a non-issue at session-control rates, and JSON keeps
/// the traffic readable in desync forensics dumps.
/// </summary>
public static class NetWire
{
    public static byte[] Encode<T>(NetMessageType type, T body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions.CompactCamel);
        var payload = new byte[json.Length + 1];
        payload[0] = (byte)type;
        json.CopyTo(payload, 1);
        return payload;
    }

    public static bool TryReadType(byte[] payload, out NetMessageType type)
    {
        if (payload is { Length: >= 1 } && Enum.IsDefined(typeof(NetMessageType), payload[0]))
        {
            type = (NetMessageType)payload[0];
            return true;
        }

        type = default;
        return false;
    }

    /// <summary>Deserialise the JSON body after the discriminator byte. Null when the
    /// body is missing or malformed; the session drops such messages.</summary>
    public static T? DecodeBody<T>(byte[] payload)
        where T : class
    {
        if (payload is not { Length: >= 2 })
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload.AsSpan(1), JsonOptions.CompactCamel);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
