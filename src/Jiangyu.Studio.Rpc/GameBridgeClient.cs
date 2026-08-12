using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
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

    // Connect() honours neither ReceiveTimeout nor SendTimeout: those cover an
    // established socket, not the handshake. Without a bound of its own, a port
    // that neither accepts nor refuses (a firewall filtering loopback, or a port
    // file left behind by a game that crashed) blocks for the OS-level timeout,
    // and it does so holding the lock every other RPC dispatches under. A live
    // local bridge answers in well under a millisecond, so this only ever cuts
    // short an attempt that was not going to succeed.
    private const int ConnectTimeoutMs = 1000;

    // A valid JSON `null` element, returned when a response carries no result, so
    // callers never hand a default(JsonElement) (ValueKind Undefined) to the
    // serialiser, which would throw.
    private static readonly JsonElement NullResult = JsonSerializer.SerializeToElement<object?>(null);

    private readonly object _lock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _nextId;
    private int _remoteProtocol;

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

        if (_remoteProtocol != 0 && _remoteProtocol != BridgeProtocol.Version)
            throw new InvalidOperationException(
                $"game bridge protocol mismatch: the loader speaks v{_remoteProtocol}, Studio expects v{BridgeProtocol.Version}. Redeploy the dev loader.");

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

        var (port, protocol) = ReadPortFile();
        if (port <= 0)
            return false;
        _remoteProtocol = protocol;
        // Held outside the try so a refused connect disposes the socket it opened.
        // A refusal throws out of the connect, and the field-based Cleanup cannot
        // reach a client that was never assigned: with the bridge toggle on and the
        // game down, this path runs on every poll, so the handles add up.
        TcpClient? client = null;
        try
        {
            client = new TcpClient { ReceiveTimeout = ReadTimeoutMs, SendTimeout = ReadTimeoutMs };
            var connected = AwaitConnect(client, client.ConnectAsync(IPAddress.Loopback, port), ConnectTimeoutMs);
            if (connected is null)
            {
                Cleanup();
                return false;
            }
            _client = connected;
            _stream = connected.GetStream();
            return true;
        }
        catch
        {
            try { client?.Dispose(); } catch { }
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Waits <paramref name="timeoutMs"/> for <paramref name="connecting"/>, returning
    /// the client when it lands and null when it does not.
    /// </summary>
    /// <remarks>
    /// Split out from the connect itself so the bound can be tested without a socket
    /// that stalls on cue. Abandoning the attempt disposes the client, which faults
    /// the pending task, so the fault is observed here rather than left to surface
    /// through <c>TaskScheduler.UnobservedTaskException</c>.
    /// </remarks>
    internal static TcpClient? AwaitConnect(TcpClient client, Task connecting, int timeoutMs)
    {
        if (connecting.Wait(timeoutMs))
            return client;

        _ = connecting.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        try { client.Dispose(); } catch { }
        return null;
    }

    private void Cleanup()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    // Reads the loader's published port file: its listening port and the bridge protocol
    // version it speaks (0 when absent), so Send can flag a stale loader clearly.
    private static (int port, int protocol) ReadPortFile()
    {
        var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        if (gameDir is null)
            return (0, 0);
        var path = Path.Combine(gameDir, "UserData", BridgeProtocol.PortFileName);
        if (!File.Exists(path))
            return (0, 0);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var port = doc.RootElement.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
            var protocol = doc.RootElement.TryGetProperty("protocol", out var protoEl) ? protoEl.GetInt32() : 0;
            return (port, protocol);
        }
        catch { return (0, 0); }
    }
}
