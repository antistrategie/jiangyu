namespace Jiangyu.Shared.Net;

/// <summary>
/// The host-ordered command replication core. Every peer runs one, over the command
/// channel of a shared transport. A peer submits a command as intent: the host stamps it
/// with the next sequence, records it, broadcasts it, and readies it locally; a client
/// sends its intent to the host and applies nothing until the host's ordered copy comes
/// back. Both peers therefore apply an identical sequence. Application is gated outside
/// this class (the loader dequeues at an action barrier), so the replicator only owns the
/// ordering: assigning sequence on the host, reordering host broadcasts on a client, and
/// exposing the next ready command. Transport-agnostic and pump-driven; the owner calls
/// <see cref="Pump"/> on the thread that owns the transport.
/// </summary>
public sealed class CommandReplicator
{
    private readonly INetTransport _transport;
    private readonly PeerId _host;
    private readonly IReadOnlyList<PeerId> _remotes;
    private readonly List<NetInbound> _drain = [];
    private readonly Dictionary<long, NetCommand> _buffered = [];
    private readonly Queue<NetCommand> _ready = new();

    /// <param name="transport">The shared session transport.</param>
    /// <param name="host">The peer that assigns the command order.</param>
    /// <param name="remotes">On the host, the peers to broadcast ordered commands to (the
    /// other session members). On a client this is unused. The local peer is never
    /// included: the host readies its own commands directly rather than looping them back.</param>
    public CommandReplicator(INetTransport transport, PeerId host, IEnumerable<PeerId> remotes)
    {
        _transport = transport;
        _host = host;
        _remotes = [.. remotes];
    }

    public bool IsHost => _transport.LocalPeer == _host;

    /// <summary>The shared ordered log, for checksums, forensics, and resync.</summary>
    public CommandJournal Journal { get; } = new();

    /// <summary>Commands ordered and awaiting application at the next barrier.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>Raised when a host broadcast collides with a different command already
    /// recorded at that sequence: the streams have diverged. Carries the conflicting
    /// arrival; the session treats it as a desync.</summary>
    public event Action<NetCommand>? Conflict;

    /// <summary>Issue a command from the local peer. On the host it is ordered and
    /// readied at once; on a client it is sent to the host and readied only when the
    /// host's ordered copy returns through <see cref="Pump"/>.</summary>
    public void Submit(string kind, string payload, string? outcome = null)
    {
        var command = new NetCommand
        {
            Source = _transport.LocalPeer.Value,
            Kind = kind,
            Payload = payload,
            Outcome = outcome,
        };

        if (IsHost)
            Sequence(command);
        else
            _transport.Send(_host, NetProtocol.CommandChannel, NetWire.Encode(NetMessageType.Command, command));
    }

    /// <summary>Drain and process the command channel: on the host, order client intents;
    /// on a client, ingest host-ordered commands into the ready queue.</summary>
    public void Pump()
    {
        _drain.Clear();
        _transport.Receive(NetProtocol.CommandChannel, _drain, 128);
        foreach (var inbound in _drain)
        {
            if (!NetWire.TryReadType(inbound.Payload, out var type) || type != NetMessageType.Command)
                continue;
            var command = NetWire.DecodeBody<NetCommand>(inbound.Payload);
            if (command is null)
                continue;

            if (IsHost)
            {
                // Only unsequenced client intent is ordered here; the host never receives
                // its own sequenced broadcasts (it does not send them to itself).
                if (!command.IsSequenced)
                    Sequence(command);
            }
            else if (command.IsSequenced)
            {
                Ingest(command);
            }
        }
    }

    /// <summary>Take the next ordered command ready to apply, or false if none. The caller
    /// gates this on the game's action barrier.</summary>
    public bool TryDequeue(out NetCommand command)
    {
        if (_ready.Count > 0)
        {
            command = _ready.Dequeue();
            return true;
        }

        command = null!;
        return false;
    }

    private void Sequence(NetCommand command)
    {
        command.Seq = Journal.NextSeq;
        Journal.Record(command);
        var encoded = NetWire.Encode(NetMessageType.Command, command);
        foreach (var peer in _remotes)
            _transport.Send(peer, NetProtocol.CommandChannel, encoded);
        _ready.Enqueue(command);
    }

    private void Ingest(NetCommand command)
    {
        switch (Journal.Record(command))
        {
            case JournalResult.Recorded:
                _ready.Enqueue(command);
                DrainBuffered();
                break;
            case JournalResult.Gap:
                _buffered[command.Seq] = command;
                break;
            case JournalResult.Conflict:
                Conflict?.Invoke(command);
                break;
            case JournalResult.Duplicate:
                break;
        }
    }

    private void DrainBuffered()
    {
        while (_buffered.Remove(Journal.NextSeq, out var next))
        {
            Journal.Record(next);
            _ready.Enqueue(next);
        }
    }
}
