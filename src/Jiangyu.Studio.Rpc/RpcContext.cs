namespace Jiangyu.Studio.Rpc;

/// <summary>
/// Static state that the RPC handlers read at dispatch time. Owned by
/// whoever's hosting the handlers — Studio.Host sets <see cref="ProjectRoot"/>
/// when the user opens a project; the Jiangyu.Mcp binary sets it from
/// <see cref="Environment.CurrentDirectory"/> at startup.
///
/// This sits in the shared library because handlers (which live here) need
/// to read it. Studio.Host's full <c>ProjectWatcher</c> remains in Host —
/// only the bare project-root pointer is shared.
/// </summary>
public static class RpcContext
{
    public static string? ProjectRoot { get; set; }
}
