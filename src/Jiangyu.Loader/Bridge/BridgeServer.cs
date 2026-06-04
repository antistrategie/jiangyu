using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using Jiangyu.Shared.Bridge;
using MelonLoader;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Bridge;

/// <summary>
/// A localhost request/response socket so Studio (a separate process) can query and
/// drive the running game live. Gated by the <c>bridge</c> dev flag, so it never
/// opens for a normal player.
///
/// <para>Connections are accepted and read on background threads, but every request
/// handler runs on the Unity main thread (drained by <see cref="Pump"/> from
/// <c>OnUpdate</c>), because game and UI APIs are main-thread-only. Messages are
/// length-prefixed (4-byte big-endian length + UTF-8 JSON), request
/// <c>{id, method, params}</c> to response <c>{id, ok, result|error}</c>.</para>
///
/// <para>On start it writes its chosen port to <c>&lt;UserData&gt;/jiangyu-bridge.json</c>
/// so Studio (which knows the game path) can discover it; the file is removed on stop.
/// Localhost-only, no auth: a developer tool, not a player-facing service.</para>
/// </summary>
internal sealed class BridgeServer
{
    private readonly MelonLogger.Instance _log;
    private readonly string _portFilePath;
    private readonly Dictionary<string, Func<JsonElement, object>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingRequest> _pending = new();

    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;

    public BridgeServer(MelonLogger.Instance log)
    {
        _log = log;
        _portFilePath = Path.Combine(MelonEnvironment.UserDataDirectory, BridgeProtocol.PortFileName);
    }

    public bool IsRunning => _running;

    /// <summary>Register a handler for <paramref name="method"/>. It runs on the main thread.</summary>
    public void On(string method, Func<JsonElement, object> handler) => _handlers[method] = handler;

    public void Start()
    {
        if (_running)
            return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "JiangyuBridge" };
            _acceptThread.Start();
            WritePortFile(port);
            _log.Msg($"[bridge] listening on 127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            _running = false;
            _log.Error($"[bridge] start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        try { _listener?.Stop(); } catch { }
        try { if (File.Exists(_portFilePath)) File.Delete(_portFilePath); } catch { }
        while (_pending.TryDequeue(out _)) { }
        _log.Msg("[bridge] stopped");
    }

    /// <summary>Run queued request handlers on the main thread. Call from <c>OnUpdate</c>.</summary>
    public void Pump()
    {
        while (_pending.TryDequeue(out var request))
        {
            object result = null;
            string error = null;
            try
            {
                if (_handlers.TryGetValue(request.Method, out var handler))
                    result = handler(request.Params);
                else
                    error = $"unknown method '{request.Method}'";
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            request.Respond(result, error);
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { if (!_running) return; continue; }

            var thread = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "JiangyuBridgeClient" };
            thread.Start();
        }
    }

    private void ClientLoop(TcpClient client)
    {
        // Responses are written both from this read thread (malformed-request path)
        // and from the main thread via Pump (handler results). Serialise them on a
        // per-connection lock so two writers never interleave bytes on the stream.
        var writeLock = new object();
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                while (_running && client.Connected)
                {
                    var raw = BridgeFraming.ReadMessage(stream);
                    if (raw == null)
                        break;

                    BridgeRequest request;
                    try { request = JsonSerializer.Deserialize<BridgeRequest>(raw); }
                    catch { WriteResponse(stream, writeLock, null, null, "malformed request json"); continue; }
                    if (request == null)
                        continue;

                    var parameters = request.Params is JsonElement element ? element : default;
                    var capturedId = request.Id;
                    _pending.Enqueue(new PendingRequest(request.Method, parameters, (res, err) =>
                    {
                        try { WriteResponse(stream, writeLock, capturedId, res, err); } catch { }
                    }));
                }
            }
        }
        catch
        {
            // Client gone or read error; the connection thread simply ends.
        }
    }

    // camelCase to match Studio's generated [RpcType] types; the envelope keeps its [JsonPropertyName] names.
    private static readonly JsonSerializerOptions ResponseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void WriteResponse(NetworkStream stream, object writeLock, string id, object result, string error)
    {
        // The client may have disconnected between enqueue and this (main-thread) write,
        // disposing the stream; skip rather than throw into the swallowing catch.
        if (!stream.CanWrite)
            return;
        var response = new BridgeResponse { Id = id, Ok = error == null, Result = result, Error = error };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ResponseOptions);
        lock (writeLock)
            BridgeFraming.WriteMessage(stream, bytes);
    }

    private void WritePortFile(int port)
    {
        try
        {
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(_portFilePath, JsonSerializer.Serialize(new { port, pid, protocol = BridgeProtocol.Version }));
        }
        catch (Exception ex)
        {
            _log.Warning($"[bridge] could not write port file: {ex.Message}");
        }
    }

    private readonly struct PendingRequest
    {
        public string Method { get; }
        public JsonElement Params { get; }
        private readonly Action<object, string> _respond;

        public PendingRequest(string method, JsonElement parameters, Action<object, string> respond)
        {
            Method = method;
            Params = parameters;
            _respond = respond;
        }

        public void Respond(object result, string error) => _respond(result, error);
    }
}
