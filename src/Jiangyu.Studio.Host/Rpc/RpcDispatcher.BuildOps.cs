using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Rpc;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Rpc;

public static partial class RpcDispatcher
{
    // Shared scaffolding for the non-blocking build-output operations (package, deploy).
    // Zipping or copying compiled/ can take a beat for asset mods, and the dispatcher runs
    // handlers synchronously under a lock on the WebView thread, so doing the work inline
    // would freeze the UI. This acquires the shared build gate, runs the work on a worker
    // thread, broadcasts the finished event to every subscribed window (guarded, fresh
    // window list), and releases the gate. Returns {started:true} immediately. If the gate
    // is held, BeginBuildOp throws and the caller's RPC rejects (surfaced as a toast).
    private static JsonElement StartBuildOp(string finishedMethod, Func<object> work, Func<Exception, object> onError)
    {
        RpcHandlers.BeginBuildOp();
        try
        {
            _ = Task.Run(() =>
            {
                try { BroadcastSink(finishedMethod, work()); }
                catch (Exception ex) { BroadcastSink(finishedMethod, onError(ex)); }
                finally { RpcHandlers.EndBuildOp(); }
            });
        }
        catch
        {
            // Task failed to schedule (vanishingly rare); don't leak the gate.
            RpcHandlers.EndBuildOp();
            throw;
        }

        return JsonSerializer.SerializeToElement(new BuildStartedAck { Started = true });
    }

    [RpcType]
    internal sealed class BuildStartedAck
    {
        [JsonPropertyName("started")]
        public required bool Started { get; set; }
    }
}
