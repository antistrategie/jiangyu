using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jiangyu.Core.Config;
using Jiangyu.Shared.Bridge;

namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Localhost TCP client to the running game's bridge (Jiangyu.Loader's
/// <c>BridgeServer</c>). Discovers the port from
/// <c>&lt;gameDir&gt;/UserData/jiangyu-bridge.json</c> and speaks the shared
/// <see cref="BridgeProtocol"/> wire format. Requests are synchronous and serialised
/// under a lock (one in flight at a time), with a read timeout so a stalled game
/// cannot hang the UI.
/// </summary>
internal sealed class GameBridgeClient
{
    private const int ReadTimeoutMs = 5000;

    // A valid JSON `null` element, returned when a response carries no result, so
    // callers never hand a default(JsonElement) (ValueKind Undefined) to the
    // serialiser, which would throw.
    private static readonly JsonElement NullResult = JsonSerializer.SerializeToElement<object?>(null);

    private readonly object _lock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _nextId;

    public bool IsConnected
    {
        get { lock (_lock) return _client is { Connected: true }; }
    }

    /// <summary>Connect if not already connected. Returns false when the game is down.</summary>
    public bool TryConnect()
    {
        lock (_lock) return EnsureConnected();
    }

    public void Disconnect()
    {
        lock (_lock) Cleanup();
    }

    /// <summary>Send a request and return its <c>result</c>. Throws when not connected or on a bridge error.</summary>
    public JsonElement Request(string method, object? parameters = null)
    {
        lock (_lock)
        {
            try
            {
                return Send(method, parameters);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // A connection the game closed on its previous exit only faults on use
                // (TcpClient.Connected lags a half-closed socket), so drop the stale socket
                // and try once more against the port the relaunched game just published.
                Cleanup();
                return Send(method, parameters);
            }
        }
    }

    // Assumes the lock is held.
    private JsonElement Send(string method, object? parameters)
    {
        if (!EnsureConnected())
            throw new InvalidOperationException("game bridge not connected (is the game running with the bridge flag?)");

        try
        {
            var id = (++_nextId).ToString();
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
                new BridgeRequest { Id = id, Method = method, Params = parameters });
            BridgeFraming.WriteMessage(_stream!, requestBytes);

            var raw = BridgeFraming.ReadMessage(_stream!) ?? throw new IOException("bridge connection closed");
            var response = JsonSerializer.Deserialize<BridgeResponse>(raw)
                ?? throw new InvalidOperationException("game bridge returned an empty response");
            if (!response.Ok)
                throw new InvalidOperationException($"game bridge error: {response.Error ?? "unknown error"}");
            return response.Result is JsonElement element ? element : NullResult;
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    // Assumes the lock is held.
    private bool EnsureConnected()
    {
        if (_client is { Connected: true })
            return true;
        Cleanup();

        var port = ReadPort();
        if (port <= 0)
            return false;
        try
        {
            var client = new TcpClient { ReceiveTimeout = ReadTimeoutMs, SendTimeout = ReadTimeoutMs };
            client.Connect(IPAddress.Loopback, port);
            _client = client;
            _stream = client.GetStream();
            return true;
        }
        catch
        {
            Cleanup();
            return false;
        }
    }

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    private static int ReadPort()
    {
        var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        if (gameDir is null)
            return 0;
        var path = Path.Combine(gameDir, "UserData", BridgeProtocol.PortFileName);
        if (!File.Exists(path))
            return 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
        }
        catch { return 0; }
    }
}
