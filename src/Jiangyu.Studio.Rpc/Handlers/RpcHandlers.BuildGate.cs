using System;
using System.Threading;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    // Compile, package, and deploy all read or write the project's compiled/ tree, so only
    // one may run at a time. Compile wipes and rebuilds it (CompiledOutput.Reset); package
    // zips it; deploy copies it. They share this in-process gate, so a concurrent attempt is
    // rejected up front rather than racing (e.g. a compile's wipe against a package's read,
    // which would otherwise yield a truncated archive or a FileNotFoundException mid-zip).
    public static readonly Lock BuildLock = new();
    public static bool BuildBusy;

    /// <summary>Acquire the exclusive build-output gate, or throw if another build operation
    /// already holds it. Pair with <see cref="EndBuildOp"/> in a finally.</summary>
    public static void BeginBuildOp()
    {
        lock (BuildLock)
        {
            if (BuildBusy)
                throw new InvalidOperationException("Another build operation (compile, package, or deploy) is already in progress.");
            BuildBusy = true;
        }
    }

    public static void EndBuildOp()
    {
        lock (BuildLock) BuildBusy = false;
    }
}
