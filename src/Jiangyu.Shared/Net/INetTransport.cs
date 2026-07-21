namespace Jiangyu.Shared.Net;

/// <summary>One message as drained from a transport channel.</summary>
public readonly struct NetInbound
{
    public NetInbound(PeerId from, int channel, byte[] payload)
    {
        From = from;
        Channel = channel;
        Payload = payload;
    }

    public PeerId From { get; }
    public int Channel { get; }
    public byte[] Payload { get; }
}

/// <summary>
/// A connectionless reliable message transport between session peers. Implementations
/// are poll-driven: <see cref="Receive"/> drains what has arrived on one channel since
/// the last call. Messages sent on a channel arrive in send order; distinct channels
/// are independent lanes. Sending to the local peer is valid and loops the message back.
/// </summary>
public interface INetTransport : IDisposable
{
    /// <summary>The local peer's identity on this transport.</summary>
    PeerId LocalPeer { get; }

    /// <summary>Send one reliable message to a peer on a channel. False when the
    /// transport cannot accept the send (disposed, or the peer is unreachable).</summary>
    bool Send(PeerId to, int channel, byte[] payload);

    /// <summary>Drain up to <paramref name="max"/> pending messages on one channel into
    /// <paramref name="into"/>, returning the number appended.</summary>
    int Receive(int channel, List<NetInbound> into, int max);
}
