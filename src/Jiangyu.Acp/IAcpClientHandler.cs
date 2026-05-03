using Jiangyu.Acp.Schema;

namespace Jiangyu.Acp;

/// <summary>
/// Callback interface for ACP agent-to-client requests. The host implements
/// this to handle file system, terminal, and permission operations that the
/// agent delegates back to the client.
/// </summary>
public interface IAcpClientHandler
{
    ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct);
    ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct);
    ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct);
    ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct);
    ValueTask<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken ct);
    ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken ct);
    ValueTask<KillTerminalResponse> KillTerminalAsync(KillTerminalRequest request, CancellationToken ct);
    ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken ct);
    ValueTask OnSessionUpdateAsync(SessionNotification notification, CancellationToken ct);
}
