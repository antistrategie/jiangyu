using System.Net;
using System.Net.Sockets;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// The bound on the bridge connect. TcpClient.Connect honours neither ReceiveTimeout
/// nor SendTimeout (those cover an established socket, not the handshake), so an
/// unbounded attempt blocks for the OS-level timeout while holding the lock every
/// other RPC dispatches under. The wait is driven here through a task the test
/// controls rather than a socket coaxed into stalling, so the timing is exact.
/// </summary>
public class GameBridgeClientTests
{
    [Fact]
    public void AwaitConnect_KeepsAClientThatConnectsInTime()
    {
        using var client = new TcpClient();

        var result = GameBridgeClient.AwaitConnect(client, Task.CompletedTask, timeoutMs: 1000);

        Assert.Same(client, result);
    }

    [Fact]
    public void AwaitConnect_GivesUpOnAConnectThatNeverLands()
    {
        var client = new TcpClient();
        var neverCompletes = new TaskCompletionSource();

        var result = GameBridgeClient.AwaitConnect(client, neverCompletes.Task, timeoutMs: 20);

        Assert.Null(result);
        neverCompletes.SetResult();
    }

    // Abandoning the attempt has to release the socket: with the bridge toggle on
    // and the game down this runs on every poll, so a client left undisposed here
    // is a handle leaked every couple of seconds.
    [Fact]
    public void AwaitConnect_DisposesTheClientItGivesUpOn()
    {
        var client = new TcpClient();
        var neverCompletes = new TaskCompletionSource();

        GameBridgeClient.AwaitConnect(client, neverCompletes.Task, timeoutMs: 20);

        // Disposal is observable through the members that fault once it has run.
        Assert.Throws<ObjectDisposedException>(() => client.GetStream());
        neverCompletes.SetResult();
    }

    // The wait bounds how long the caller is held, which is the whole point: an
    // attempt that outlives the timeout must not keep the dispatch lock with it.
    [Fact]
    public void AwaitConnect_ReturnsWithoutWaitingForTheAbandonedAttempt()
    {
        var client = new TcpClient();
        var neverCompletes = new TaskCompletionSource();

        var started = Environment.TickCount64;
        GameBridgeClient.AwaitConnect(client, neverCompletes.Task, timeoutMs: 20);
        var elapsed = Environment.TickCount64 - started;

        // Generous upper bound: the assertion is "bounded", not "precisely 20ms".
        Assert.True(elapsed < 5000, $"waited {elapsed.ToString()}ms for a 20ms timeout");
        neverCompletes.SetResult();
    }

    // A refused connect faults the task, and Wait surfaces that as an
    // AggregateException. EnsureConnected treats it as "not connected"; the point
    // here is that it propagates rather than being read as a successful connect.
    [Fact]
    public void AwaitConnect_PropagatesAFailedConnect()
    {
        using var client = new TcpClient();
        var refused = Task.FromException(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Throws<AggregateException>(
            () => GameBridgeClient.AwaitConnect(client, refused, timeoutMs: 1000));
    }

    // The abandoned attempt faults once its client is disposed. Nothing awaits it by
    // then, so its exception has to be observed where it is dropped, or it surfaces
    // later through TaskScheduler.UnobservedTaskException.
    [Fact]
    public async Task AwaitConnect_ObservesTheAbandonedAttemptsFailure()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e) => unobserved.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            RunAbandonedConnect();

            // The fault is only raised to the handler when the dropped task is collected.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();

            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // Kept out of the test body so the faulted task is unreachable for collection.
    private static void RunAbandonedConnect()
    {
        var client = new TcpClient();
        var failsAfterTheWait = Task.Run(async () =>
        {
            await Task.Delay(30);
            throw new SocketException((int)SocketError.TimedOut);
        });

        Assert.Null(GameBridgeClient.AwaitConnect(client, failsAfterTheWait, timeoutMs: 1));
        Thread.Sleep(200);
    }
}
