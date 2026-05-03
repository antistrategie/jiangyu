using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace Jiangyu.Acp.JsonRpc;

/// <summary>
/// JSON-RPC 2.0 endpoint supporting Content-Length framing (LSP convention)
/// or newline-delimited JSON (ACP JS SDK convention) over byte streams.
/// </summary>
internal sealed class JsonRpcEndpoint : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly FramingMode _framing;
    private int _nextId;

    private Func<string, JsonElement?, CancellationToken, ValueTask<JsonElement?>>? _requestHandler;
    private Func<string, JsonElement?, CancellationToken, ValueTask>? _notificationHandler;

    public JsonRpcEndpoint(Stream input, Stream output, FramingMode framing = FramingMode.ContentLength)
    {
        _input = input;
        _output = output;
        _framing = framing;
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
    /// cancellation is requested. Notifications are forwarded synchronously
    /// (they're cheap; ordering matters for streamed updates). Incoming
    /// requests are dispatched on the thread pool so a slow handler — e.g.
    /// a permission modal waiting on user input — doesn't pause the listen
    /// loop and starve the OS pipe of further agent traffic.
    /// </summary>
    public async Task ListenAsync(CancellationToken ct)
    {
        var reader = PipeReader.Create(_input);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var messageBytes = _framing == FramingMode.NdJson
                    ? await ReadNdJsonMessageAsync(reader, ct).ConfigureAwait(false)
                    : await ReadContentLengthMessageAsync(reader, ct).ConfigureAwait(false);
                if (messageBytes is null) break;

                using var doc = JsonDocument.Parse(messageBytes);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
                {
                    var id = idElement.GetInt32();

                    if (root.TryGetProperty("method", out _))
                    {
                        // Incoming request. Extract method/params now (the
                        // JsonDocument is disposed at the loop iteration's
                        // end), then fire-and-forget the async handler.
                        var method = root.GetProperty("method").GetString()!;
                        var paramsCopy = ClonePropertyOrNull(root, "params");
                        _ = DispatchIncomingRequestAsync(method, paramsCopy, id, ct);
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
                    // Notification (no id). Stay on the listen thread so
                    // ordered streams (agent_message_chunk, …) arrive in
                    // wire order at the handler.
                    var method = root.GetProperty("method").GetString()!;
                    var paramsCopy = ClonePropertyOrNull(root, "params");

                    if (_notificationHandler is not null)
                    {
                        try
                        {
                            await _notificationHandler(method, paramsCopy, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Don't let a single bad notification kill the listen loop.
                            Console.Error.WriteLine($"[JsonRpc] Notification handler error ({method}): {ex.Message}");
                        }
                    }
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

    private static JsonElement? ClonePropertyOrNull(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Undefined
            ? prop.Clone()
            : null;
    }

    private async Task DispatchIncomingRequestAsync(string method, JsonElement? @params, int id, CancellationToken ct)
    {
        JsonRpcResponse response;
        if (_requestHandler is not null)
        {
            try
            {
                var result = await _requestHandler(method, @params, ct).ConfigureAwait(false);
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

        try
        {
            await WriteMessageAsync(JsonSerializer.SerializeToUtf8Bytes(response), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Listen loop torn down; nothing to do.
        }
    }

    /// <summary>
    /// Hard ceiling on Content-Length so a malformed or hostile peer can't
    /// make us allocate gigabytes. 16 MiB is generous for ACP — typical
    /// messages are under 100 KiB, large diffs maybe 1 MiB.
    /// </summary>
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads a Content-Length framed message. Returns null at end-of-stream.
    /// </summary>
    private static async Task<byte[]?> ReadContentLengthMessageAsync(PipeReader reader, CancellationToken ct)
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
                {
                    var raw = headerLine.AsSpan()["Content-Length:".Length..].Trim();
                    if (!int.TryParse(raw, out var parsed) || parsed < 0)
                        throw new InvalidOperationException(
                            $"Malformed Content-Length header: '{headerLine}'");
                    if (parsed > MaxMessageBytes)
                        throw new InvalidOperationException(
                            $"JSON-RPC message size {parsed} exceeds {MaxMessageBytes}-byte limit.");
                    contentLength = parsed;
                }

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
    /// Writes a Content-Length framed or newline-delimited JSON message.
    /// </summary>
    private async Task WriteMessageAsync(byte[] body, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_framing == FramingMode.ContentLength)
            {
                var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                await _output.WriteAsync(header, ct).ConfigureAwait(false);
            }
            await _output.WriteAsync(body, ct).ConfigureAwait(false);
            if (_framing == FramingMode.NdJson)
            {
                await _output.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            }
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads a newline-delimited JSON message. Blank lines are skipped.
    /// Returns null at end-of-stream.
    /// </summary>
    private static async Task<byte[]?> ReadNdJsonMessageAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            // Scan for a newline delimiter.
            var position = buffer.PositionOf((byte)'\n');
            if (position is not null)
            {
                // Everything before the newline is one message.
                var lineSeq = buffer.Slice(0, position.Value);
                var line = lineSeq.ToArray();

                // Advance past the newline.
                reader.AdvanceTo(buffer.GetPosition(1, position.Value));

                // Skip blank lines.
                if (line.Length == 0 || line.All(b => b is (byte)'\r' or (byte)' ' or (byte)'\t'))
                    continue;

                // Trim trailing \r if present.
                if (line.Length > 0 && line[^1] == (byte)'\r')
                    return line.AsSpan(0, line.Length - 1).ToArray();
                return line;
            }

            if (result.IsCompleted) return null;
            reader.AdvanceTo(buffer.Start, buffer.End);
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
