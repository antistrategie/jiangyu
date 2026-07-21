namespace Jiangyu.Shared.Net;

public enum NetSessionPhase
{
    Idle,
    Handshaking,
    Ready,
    Rejected,
    Lost,
}

/// <summary>
/// A two-peer session over one transport: the mod-set handshake followed by
/// control-channel traffic (chat, for now; the replicated command stream is the next
/// phase). Transport-agnostic and pump-driven: the owner calls <see cref="Pump"/> each
/// frame on the thread that owns the transport. The handshake is symmetric: both peers
/// send their summary on connect, each compares the other's against its own, and the
/// session is <see cref="NetSessionPhase.Ready"/> once both sides have accepted. The
/// peer may be the local peer itself; the session then handshakes with its own looped-
/// back traffic, which exercises a real transport end to end in one process.
/// </summary>
public sealed class NetSession
{
    private const int TranscriptCap = 64;

    private readonly INetTransport _transport;
    private readonly ModSetSummary _local;
    private readonly List<NetInbound> _drain = [];
    private readonly List<string> _transcript = [];
    private PeerId _peer;
    private bool _localAccepted;
    private bool _remoteAccepted;

    public NetSession(INetTransport transport, ModSetSummary local, bool isHost)
    {
        _transport = transport;
        _local = local;
        IsHost = isHost;
    }

    public bool IsHost { get; }
    public NetSessionPhase Phase { get; private set; } = NetSessionPhase.Idle;

    /// <summary>The handshake differences behind a <see cref="NetSessionPhase.Rejected"/>
    /// phase, from whichever side detected them first. Empty otherwise.</summary>
    public IReadOnlyList<string> RejectDifferences { get; private set; } = [];

    /// <summary>The most recent chat lines, oldest first, capped at 64.</summary>
    public IReadOnlyList<string> Transcript => _transcript;

    public event Action<PeerId, string>? ChatReceived;

    /// <summary>Begin the handshake with a known peer (from the lobby layer, or the
    /// local peer for a self test): send our summary and await theirs.</summary>
    public void Connect(PeerId peer)
    {
        if (Phase != NetSessionPhase.Idle)
            return;
        _peer = peer;
        Phase = NetSessionPhase.Handshaking;
        _transport.Send(peer, NetProtocol.ControlChannel, NetWire.Encode(NetMessageType.Summary, _local));
    }

    public void PeerLost(PeerId peer)
    {
        if (Phase is NetSessionPhase.Idle or NetSessionPhase.Lost || peer != _peer)
            return;
        Phase = NetSessionPhase.Lost;
    }

    /// <summary>Send a chat line to the peer. Chat only flows in a ready session.</summary>
    public bool SendChat(string text)
    {
        if (Phase != NetSessionPhase.Ready)
            return false;
        return _transport.Send(_peer, NetProtocol.ControlChannel, NetWire.Encode(NetMessageType.Chat, new ChatBody { Text = text }));
    }

    /// <summary>Drain and process everything pending on the control channel.</summary>
    public void Pump()
    {
        if (Phase is NetSessionPhase.Idle or NetSessionPhase.Lost)
            return;

        _drain.Clear();
        _transport.Receive(NetProtocol.ControlChannel, _drain, 64);
        foreach (var inbound in _drain)
        {
            if (inbound.From != _peer)
                continue;
            if (!NetWire.TryReadType(inbound.Payload, out var type))
                continue;
            switch (type)
            {
                case NetMessageType.Summary:
                    OnSummary(inbound.Payload);
                    break;
                case NetMessageType.Accept:
                    _remoteAccepted = true;
                    PromoteWhenBothAccepted();
                    break;
                case NetMessageType.Reject:
                    OnReject(inbound.Payload);
                    break;
                case NetMessageType.Chat:
                    OnChat(inbound.From, inbound.Payload);
                    break;
            }
        }
    }

    private void OnSummary(byte[] payload)
    {
        if (Phase != NetSessionPhase.Handshaking)
            return;
        var remote = NetWire.DecodeBody<ModSetSummary>(payload);
        if (remote is null)
            return;

        var verdict = HandshakeComparer.Compare(_local, remote);
        if (verdict.Match)
        {
            _localAccepted = true;
            _transport.Send(_peer, NetProtocol.ControlChannel, NetWire.Encode(NetMessageType.Accept, new AcceptBody()));
            PromoteWhenBothAccepted();
        }
        else
        {
            _transport.Send(_peer, NetProtocol.ControlChannel, NetWire.Encode(
                NetMessageType.Reject, new RejectBody { Differences = [.. verdict.Differences] }));
            Phase = NetSessionPhase.Rejected;
            RejectDifferences = verdict.Differences;
        }
    }

    private void OnReject(byte[] payload)
    {
        if (Phase == NetSessionPhase.Rejected)
            return;
        var body = NetWire.DecodeBody<RejectBody>(payload);
        Phase = NetSessionPhase.Rejected;
        RejectDifferences = body?.Differences ?? ["remote rejected the session"];
    }

    private void OnChat(PeerId from, byte[] payload)
    {
        if (Phase != NetSessionPhase.Ready)
            return;
        var body = NetWire.DecodeBody<ChatBody>(payload);
        if (body?.Text is not { } text)
            return;
        AppendTranscript($"{from}: {text}");
        ChatReceived?.Invoke(from, text);
    }

    private void PromoteWhenBothAccepted()
    {
        if (Phase == NetSessionPhase.Handshaking && _localAccepted && _remoteAccepted)
            Phase = NetSessionPhase.Ready;
    }

    private void AppendTranscript(string line)
    {
        if (_transcript.Count == TranscriptCap)
            _transcript.RemoveAt(0);
        _transcript.Add(line);
    }

    private sealed class AcceptBody
    {
    }

    private sealed class RejectBody
    {
        public List<string>? Differences { get; set; }
    }

    private sealed class ChatBody
    {
        public string? Text { get; set; }
    }
}
