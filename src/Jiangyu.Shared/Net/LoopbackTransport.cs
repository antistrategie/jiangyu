namespace Jiangyu.Shared.Net;

/// <summary>
/// An in-process transport pair: what one endpoint sends, its partner receives. Backs
/// the handshake and session tests and the dev loopback self-test, so none of that
/// needs Steam or a second machine. Payload arrays are handed over, not copied.
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Queue<NetInbound>> _inbox = [];
    private LoopbackTransport? _partner;
    private bool _disposed;

    public PeerId LocalPeer { get; }

    private LoopbackTransport(PeerId local) => LocalPeer = local;

    /// <summary>Create two connected endpoints with the given identities.</summary>
    public static (LoopbackTransport A, LoopbackTransport B) CreatePair(ulong a = 1, ulong b = 2)
    {
        var ta = new LoopbackTransport(new PeerId(a));
        var tb = new LoopbackTransport(new PeerId(b));
        ta._partner = tb;
        tb._partner = ta;
        return (ta, tb);
    }

    public bool Send(PeerId to, int channel, byte[] payload)
    {
        var partner = _partner;
        if (_disposed || partner == null)
            return false;

        LoopbackTransport target;
        if (to == LocalPeer)
            target = this;
        else if (to == partner.LocalPeer)
            target = partner;
        else
            return false;

        return target.Deliver(new NetInbound(LocalPeer, channel, payload));
    }

    public int Receive(int channel, List<NetInbound> into, int max)
    {
        lock (_sync)
        {
            if (!_inbox.TryGetValue(channel, out var queue))
                return 0;
            var appended = 0;
            while (appended < max && queue.Count > 0)
            {
                into.Add(queue.Dequeue());
                appended++;
            }

            return appended;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _inbox.Clear();
        }
    }

    private bool Deliver(NetInbound message)
    {
        lock (_sync)
        {
            if (_disposed)
                return false;
            if (!_inbox.TryGetValue(message.Channel, out var queue))
            {
                queue = new Queue<NetInbound>();
                _inbox[message.Channel] = queue;
            }

            queue.Enqueue(message);
            return true;
        }
    }
}
