using System.Buffers.Binary;
using System.IO;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared.Bridge;

/// <summary>
/// The Studio-to-game bridge wire contract, shared so the loader's server and Studio's
/// client serialise the same envelope and frame it the same way instead of each
/// hand-rolling a copy that drifts. Messages are length-prefixed (4-byte big-endian
/// length + UTF-8 JSON) request/response envelopes.
/// </summary>
public static class BridgeProtocol
{
    /// <summary>Bumped on a breaking wire change. Written to the port file and answered by <c>ping</c>.</summary>
    public const int Version = 2;

    /// <summary>Reject a frame larger than this, so a desync reading a bogus length cannot allocate wildly.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>The file in the game's <c>UserData</c> where the loader publishes its listening port.</summary>
    public const string PortFileName = "jiangyu-bridge.json";
}

/// <summary>
/// The bridge method names, shared so the loader's handler registration and the client's
/// request calls reference one spelling. A mismatch would otherwise fail silently as an
/// "unknown method" response rather than at compile time. <c>command</c> carries a
/// <c>{ name, args }</c> payload dispatched to the loader's command registry (the verb
/// runner and the on-demand inspectors); <c>ping</c> is the version handshake.
/// </summary>
public static class BridgeMethods
{
    public const string Ping = "ping";
    public const string Command = "command";
}

/// <summary>A bridge request: <c>{ id, method, params }</c>.</summary>
public sealed class BridgeRequest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }

    /// <summary>Method arguments. Set to any serialisable object on the client; arrives as a
    /// <see cref="System.Text.Json.JsonElement"/> on the server.</summary>
    [JsonPropertyName("params")] public object? Params { get; set; }
}

/// <summary>A bridge response: <c>{ id, ok, result, error }</c>.</summary>
public sealed class BridgeResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>The handler's return value. Any serialisable object on the server; a
    /// <see cref="System.Text.Json.JsonElement"/> on the client.</summary>
    [JsonPropertyName("result")] public object? Result { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Length-prefixed framing for bridge messages over a stream.</summary>
public static class BridgeFraming
{
    public static void WriteMessage(Stream stream, byte[] payload)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        stream.Write(header, 0, 4);
        stream.Write(payload, 0, payload.Length);
    }

    /// <summary>Read one framed message, or null on end-of-stream or a length outside bounds.</summary>
    public static byte[]? ReadMessage(Stream stream)
    {
        var header = ReadExactly(stream, 4);
        if (header is null)
            return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > BridgeProtocol.MaxMessageBytes)
            return null;
        return ReadExactly(stream, length);
    }

    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            int read;
            try { read = stream.Read(buffer, offset, count - offset); }
            catch { return null; }
            if (read <= 0)
                return null;
            offset += read;
        }
        return buffer;
    }
}
