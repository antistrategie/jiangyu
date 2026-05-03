using System.Text;
using System.Text.Json;
using Jiangyu.Acp;
using Jiangyu.Acp.JsonRpc;

namespace Jiangyu.Acp.Tests;

public class JsonRpcEndpointTests
{
    /// <summary>
    /// Helper: wraps a JSON body in Content-Length framing as raw UTF-8 bytes.
    /// </summary>
    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var combined = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        Buffer.BlockCopy(body, 0, combined, header.Length, body.Length);
        return combined;
    }

    [Fact]
    public async Task SendRequestAsync_WritesContentLengthFramedMessage()
    {
        var output = new MemoryStream();
        var input = new MemoryStream(); // no response; will cancel
        using var endpoint = new JsonRpcEndpoint(input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Fire and forget; it will be cancelled because there's no response.
        var task = endpoint.SendRequestAsync("test/method", null, cts.Token);
        try { await task; } catch (OperationCanceledException) { }

        var written = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Content-Length:", written);
        Assert.Contains("\"method\":\"test/method\"", written);
        Assert.Contains("\"jsonrpc\":\"2.0\"", written);
    }

    [Fact]
    public async Task SendNotificationAsync_WritesNotificationWithoutId()
    {
        var output = new MemoryStream();
        var input = new MemoryStream();
        using var endpoint = new JsonRpcEndpoint(input, output);

        await endpoint.SendNotificationAsync("test/notify", null, CancellationToken.None);

        var written = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"method\":\"test/notify\"", written);
        Assert.DoesNotContain("\"id\"", written);
    }

    [Fact]
    public async Task ListenAsync_DispatchesNotification()
    {
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "test/event",
            @params = new { value = 42 },
        });

        var input = new MemoryStream(Frame(notification));
        var output = new MemoryStream();
        using var endpoint = new JsonRpcEndpoint(input, output);

        string? receivedMethod = null;
        JsonElement? receivedParams = null;

        endpoint.OnNotification((method, @params, ct) =>
        {
            receivedMethod = method;
            receivedParams = @params;
            return ValueTask.CompletedTask;
        });

        await endpoint.ListenAsync(CancellationToken.None);

        Assert.Equal("test/event", receivedMethod);
        Assert.NotNull(receivedParams);
        Assert.Equal(42, receivedParams!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ListenAsync_CorrelatesResponseToRequest()
    {
        // Tested properly in RoundTrip_RequestResponseViaConnectedEndpoints.
        // This test verifies the written request contains an id.
        var clientOutput = new MemoryStream();

        using var endpoint = new JsonRpcEndpoint(new MemoryStream(), clientOutput);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var requestTask = endpoint.SendRequestAsync("echo", null, cts.Token);

        await Task.Delay(50);

        var written = Encoding.UTF8.GetString(clientOutput.ToArray());
        var headerEnd = written.IndexOf("\r\n\r\n") + 4;
        var body = written[headerEnd..];
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("id", out var idEl));
        Assert.True(idEl.GetInt32() > 0);

        try { await requestTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ListenAsync_HandlesIncomingRequest()
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "fs/read_text_file",
            @params = new { path = "/test.txt" },
        });

        var input = new MemoryStream(Frame(requestJson));
        var output = new MemoryStream();
        using var endpoint = new JsonRpcEndpoint(input, output);

        endpoint.OnRequest(async (method, @params, ct) =>
        {
            Assert.Equal("fs/read_text_file", method);
            return JsonSerializer.SerializeToElement(new { text = "hello" });
        });

        await endpoint.ListenAsync(CancellationToken.None);

        var written = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"text\":\"hello\"", written);
        Assert.Contains("\"id\":1", written);
    }

    [Fact]
    public async Task RoundTrip_RequestResponseViaConnectedEndpoints()
    {
        // Two endpoints connected via pipes: client sends, agent responds.
        using var clientToAgent = new BlockingStream();
        using var agentToClient = new BlockingStream();

        using var client = new JsonRpcEndpoint(agentToClient, clientToAgent);
        using var agent = new JsonRpcEndpoint(clientToAgent, agentToClient);

        agent.OnRequest(async (method, @params, ct) =>
        {
            if (method == "echo")
                return JsonSerializer.SerializeToElement(new { echoed = true });
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agentListenTask = Task.Run(() => agent.ListenAsync(cts.Token));
        var clientListenTask = Task.Run(() => client.ListenAsync(cts.Token));

        var response = await client.SendRequestAsync("echo", null, cts.Token);

        Assert.NotNull(response.Result);
        Assert.True(response.Result!.Value.GetProperty("echoed").GetBoolean());

        cts.Cancel();
        // Allow listen tasks to finish.
        try { await agentListenTask; } catch (OperationCanceledException) { }
        try { await clientListenTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ListenAsync_NonAsciiContentLengthFraming()
    {
        // Content-Length is a UTF-8 byte count. With non-ASCII chars (including
        // surrogate pairs that encode to 4 UTF-8 bytes) the byte count exceeds
        // the char count, so framing must read bytes, not chars.
        // Use a relaxed encoder so non-ASCII stays raw in the JSON, exercising
        // multi-byte and 4-byte UTF-8 paths in the reader.
        var options = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "test/unicode",
            @params = new { text = "héllo wörld 🎮" },
        }, options);

        // Two back-to-back framed messages: if byte/char mismatch exists,
        // the second message's framing will be corrupted.
        var framed = new MemoryStream();
        framed.Write(Frame(notification));
        framed.Write(Frame(notification));
        framed.Position = 0;

        var output = new MemoryStream();
        using var endpoint = new JsonRpcEndpoint(framed, output);

        var count = 0;
        endpoint.OnNotification((method, @params, ct) =>
        {
            Assert.Equal("test/unicode", method);
            Assert.Equal("héllo wörld 🎮", @params!.Value.GetProperty("text").GetString());
            count++;
            return ValueTask.CompletedTask;
        });

        await endpoint.ListenAsync(CancellationToken.None);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ListenAsync_EofFailsPendingRequests()
    {
        // When the remote side disconnects, in-flight requests must fail
        // rather than block forever.
        using var agentToClient = new BlockingStream();
        using var clientToAgent = new BlockingStream();

        using var endpoint = new JsonRpcEndpoint(agentToClient, clientToAgent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = Task.Run(() => endpoint.ListenAsync(cts.Token));

        // Send a request; it writes to clientToAgent (not read by listen loop).
        var requestTask = endpoint.SendRequestAsync("test/method", null, cts.Token);

        // Simulate agent disconnect (EOF on the read side).
        await Task.Delay(100);
        agentToClient.Close();

        // The listen loop should exit and fail the pending request.
        await listenTask;
        var ex = await Assert.ThrowsAsync<AcpException>(() => requestTask);
        Assert.Contains("Connection closed", ex.Message);
    }
}

/// <summary>
/// A simple in-memory stream that blocks reads until data is written,
/// simulating a pipe between two endpoints.
/// </summary>
internal sealed class BlockingStream : Stream
{
    private readonly SemaphoreSlim _dataAvailable = new(0);
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _currentOffset;
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        lock (_chunks) _chunks.Enqueue(copy);
        _dataAvailable.Release();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is not null && _currentOffset < _current.Length)
            {
                var toCopy = Math.Min(count, _current.Length - _currentOffset);
                Buffer.BlockCopy(_current, _currentOffset, buffer, offset, toCopy);
                _currentOffset += toCopy;
                if (_currentOffset >= _current.Length) _current = null;
                return toCopy;
            }

            if (_closed) return 0;

            _dataAvailable.Wait();

            lock (_chunks)
            {
                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _currentOffset = 0;
                }
            }
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        while (true)
        {
            if (_current is not null && _currentOffset < _current.Length)
            {
                var toCopy = Math.Min(count, _current.Length - _currentOffset);
                Buffer.BlockCopy(_current, _currentOffset, buffer, offset, toCopy);
                _currentOffset += toCopy;
                if (_currentOffset >= _current.Length) _current = null;
                return toCopy;
            }

            if (_closed) return 0;

            await _dataAvailable.WaitAsync(ct);

            lock (_chunks)
            {
                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _currentOffset = 0;
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        _dataAvailable.Release(); // Unblock any waiters.
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
