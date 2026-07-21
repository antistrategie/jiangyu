namespace Jiangyu.Shared.Net;

/// <summary>
/// Wire-level constants for the multiplayer net layer. The channel plan follows the
/// multiplayer framework design: one reliable ordered lane for session control, one for
/// the replicated command stream, and one for large transfers, kept apart so a bulk
/// transfer can never stall a command.
/// </summary>
public static class NetProtocol
{
    /// <summary>Version of the control-message wire format. Compared first in the
    /// handshake; peers on different versions reject before comparing anything else.</summary>
    public const int Version = 1;

    /// <summary>Reliable ordered lane for session control: handshake and chat.</summary>
    public const int ControlChannel = 0;

    /// <summary>Reliable ordered lane for the replicated command stream.</summary>
    public const int CommandChannel = 1;

    /// <summary>Reliable lane for snapshot chunks and other large transfers.</summary>
    public const int BulkChannel = 2;
}
