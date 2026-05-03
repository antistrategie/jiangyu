using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Jiangyu.Acp.JsonRpc;

/// <summary>
/// JSON-RPC 2.0 endpoint using Content-Length framed messages over byte
/// streams (stdio convention). Bodies are read as raw UTF-8 bytes so that
/// the wire-level byte count matches the framing header regardless of the
/// JSON content (in particular, surrogate-pair codepoints).
/// </summary>
internal sealed class JsonRpcEndpoint : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private int _nextId;

    private Func<string, JsonElement?, CancellationToken, ValueTask<JsonElement?>>? _requestHandler;
    private Func<string, JsonElement?, CancellationToken, ValueTask>? _notificationHandler;

    public JsonRpcEndpoint(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>
    /// Registers a handler invoked when the remote side sends a JSON-RPC
    /// request (a message with an <c>id</c> and <c>method</c>). The handler
    /// returns a result element (or null) that is sent back as the response.
    /// </summary>
    public void OnRequest(Func<string, JsonElement?, CancellationToken, ValueTask<JsonElement?>> handler)
    {
        _requestHandler = handler;
    }

    /// <summary>
    /// Registers a handler invoked when the remote side sends a JSON-RPC
    /// notification (a message with <c>method</c> but no <c>id</c>).
    /// </summary>
    public void OnNotification(Func<string, JsonElement?, CancellationToken, ValueTask> handler)
    {
        _notificationHandler = handler;
    }

    /// <summary>
    /// Sends a JSON-RPC request and waits for the correlated response.
    /// </summary>
    public async Task<JsonRpcResponse> SendRequestAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        // Dispose the registration once the request settles, otherwise long-lived
        // cancellation tokens accumulate one entry per request indefinitely.
        await using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
                removed.TrySetCanceled(ct);
        });

        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        await WriteMessageAsync(JsonSerializer.SerializeToUtf8Bytes(request), ct).ConfigureAwait(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a JSON-RPC notification (no response expected).
    /// </summary>
    public async Task SendNotificationAsync(string method, JsonElement? @params, CancellationToken ct)
    {
        var notification = new JsonRpcNotification { Method = method, Params = @params };
        await WriteMessageAsync(JsonSerializer.SerializeToUtf8Bytes(notification), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads messages from the transport until the reader is exhausted or
    /// cancellation is requested. Dispatches to the appropriate handler.
    /// </summary>
    public async Task ListenAsync(CancellationToken ct)
    {
        var reader = PipeReader.Create(_input);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var messageBytes = await ReadMessageAsync(reader, ct).ConfigureAwait(false);
                if (messageBytes is null) break;

                using var doc = JsonDocument.Parse(messageBytes);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    var id = idElement.GetInt32();

                    if (root.TryGetProperty("method", out _))
                    {
                        // Incoming request from the remote side.
                        await HandleIncomingRequestAsync(root, id, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Response to one of our requests.
                        var response = JsonSerializer.Deserialize<JsonRpcResponse>(root.GetRawText())!;
                        if (_pending.TryRemove(id, out var tcs))
                            tcs.TrySetResult(response);
                    }
                }
                else if (root.TryGetProperty("method", out _))
                {
                    // Notification (no id).
                    var method = root.GetProperty("method").GetString()!;
                    root.TryGetProperty("params", out var @params);
                    var paramsCopy = @params.ValueKind != JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement(@params)
                        : (JsonElement?)null;

                    if (_notificationHandler is not null)
                        await _notificationHandler(method, paramsCopy, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            // Fail any in-flight requests so callers don't block forever.
            FailAllPending();
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement root, int id, CancellationToken ct)
    {
        var method = root.GetProperty("method").GetString()!;
        root.TryGetProperty("params", out var @params);
        var paramsCopy = @params.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(@params)
            : (JsonElement?)null;

        JsonRpcResponse response;
        if (_requestHandler is not null)
        {
            try
            {
                var result = await _requestHandler(method, paramsCopy, ct).ConfigureAwait(false);
                response = new JsonRpcResponse { Id = id, Result = result };
            }
            catch (Exception ex)
            {
                response = new JsonRpcResponse
                {
                    Id = id,
                    Error = new JsonRpcError
                    {
                        Code = JsonRpcErrorCodes.InternalError,
                        Message = ex.Message,
                    },
                };
            }
        }
        else
        {
            response = new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError
                {
                    Code = JsonRpcErrorCodes.MethodNotFound,
                    Message = $"No handler for method '{method}'",
                },
            };
        }

        await WriteMessageAsync(JsonSerializer.SerializeToUtf8Bytes(response), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a Content-Length framed message. Returns null at end-of-stream.
    /// </summary>
    private static async Task<byte[]?> ReadMessageAsync(PipeReader reader, CancellationToken ct)
    {
        int? contentLength = null;
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadHeader(ref buffer, out var headerLine, out var headersDone))
            {
                if (headersDone)
                {
                    if (contentLength is null)
                        throw new InvalidOperationException("Missing Content-Length header in JSON-RPC message.");
                    reader.AdvanceTo(buffer.Start);
                    return await ReadBodyAsync(reader, contentLength.Value, ct).ConfigureAwait(false);
                }

                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(headerLine.AsSpan()["Content-Length:".Length..].Trim());

                reader.AdvanceTo(buffer.Start);
                continue;
            }

            if (result.IsCompleted) return null;
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryReadHeader(ref ReadOnlySequence<byte> buffer, out string line, out bool headersDone)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineBytes, (byte)'\n', advancePastDelimiter: true))
        {
            line = "";
            headersDone = false;
            return false;
        }

        var raw = Encoding.ASCII.GetString(lineBytes.ToArray());
        line = raw.TrimEnd('\r');
        headersDone = line.Length == 0;
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private static async Task<byte[]?> ReadBodyAsync(PipeReader reader, int contentLength, CancellationToken ct)
    {
        var body = new byte[contentLength];
        var written = 0;
        while (written < contentLength)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length == 0 && result.IsCompleted) return null;

            var toCopy = (int)Math.Min(buffer.Length, contentLength - written);
            buffer.Slice(0, toCopy).CopyTo(body.AsSpan(written));
            written += toCopy;
            reader.AdvanceTo(buffer.GetPosition(toCopy));
        }
        return body;
    }

    /// <summary>
    /// Writes a Content-Length framed message.
    /// </summary>
    private async Task WriteMessageAsync(byte[] body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailAllPending()
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new AcpException("Connection closed.", JsonRpcErrorCodes.InternalError));
        }
    }

    public void Dispose()
    {
        FailAllPending();
        _writeLock.Dispose();
    }
}
