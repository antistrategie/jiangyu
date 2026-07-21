using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;
using Jiangyu.Shared.Net;

namespace Jiangyu.Loader.Net.Steam;

/// <summary>
/// <see cref="INetTransport"/> over the game's own ISteamNetworkingMessages instance:
/// connectionless per-channel reliable messages with SDR NAT traversal, no second
/// <c>SteamAPI_Init</c>. Sessions open implicitly: this side's first send to a peer
/// accepts that peer's pending request, and a session-request callback accepts
/// incoming ones that pass the gate (lobby membership). Main-thread only, like every
/// il2cpp surface the loader touches.
/// </summary>
internal sealed class SteamMessagesTransport : INetTransport
{
    private const int DrainBatch = 64;

    private readonly Il2CppStructArray<IntPtr> _messagePtrs = new(DrainBatch);
    private readonly Func<ulong, bool> _sessionGate;
    private readonly Action<string> _log;
    private readonly int _sendFlags;

    /// <param name="sessionGate">Decides whether an unsolicited session request from a
    /// SteamID64 is accepted. Wire this to lobby membership.</param>
    public SteamMessagesTransport(Func<ulong, bool> sessionGate, Action<string> log)
    {
        _sessionGate = sessionGate;
        _log = log;
        _sendFlags = Constants.k_nSteamNetworkingSend_Reliable
            | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
        LocalPeer = new PeerId(SteamUser.GetSteamID().m_SteamID);
        SteamCallbacks.Listen<SteamNetworkingMessagesSessionRequest_t>(OnSessionRequest);
    }

    public PeerId LocalPeer { get; }

    public unsafe bool Send(PeerId to, int channel, byte[] payload)
    {
        if (payload is not { Length: > 0 })
            return false;

        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(to.Value);
        fixed (byte* data = payload)
        {
            var result = SteamNetworkingMessages.SendMessageToUser(
                ref identity, (IntPtr)data, (uint)payload.Length, _sendFlags, channel);
            if (result != EResult.k_EResultOK)
                _log($"send to {to} on channel {channel} failed: {result}");
            return result == EResult.k_EResultOK;
        }
    }

    public int Receive(int channel, List<NetInbound> into, int max)
    {
        var appended = 0;
        while (appended < max)
        {
            var batch = Math.Min(DrainBatch, max - appended);
            var received = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _messagePtrs, batch);
            if (received <= 0)
                break;

            for (var i = 0; i < received; i++)
            {
                var pointer = _messagePtrs[i];
                try
                {
                    var message = SteamInterop.Read<SteamNetworkingMessage_t>(pointer);
                    var payload = new byte[message.m_cbSize];
                    if (message.m_cbSize > 0)
                        Marshal.Copy(message.m_pData, payload, 0, message.m_cbSize);
                    var identity = message.m_identityPeer;
                    into.Add(new NetInbound(new PeerId(identity.GetSteamID64()), channel, payload));
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                }
            }

            appended += received;
            if (received < batch)
                break;
        }

        return appended;
    }

    /// <summary>Close the messages session with a peer, dropping any queued traffic.</summary>
    public void CloseSessionWith(PeerId peer)
    {
        var identity = default(SteamNetworkingIdentity);
        identity.SetSteamID64(peer.Value);
        SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        _log($"closed messages session with {peer}");
    }

    public void Dispose()
    {
    }

    private void OnSessionRequest(IntPtr pvParam)
    {
        var request = SteamInterop.Read<SteamNetworkingMessagesSessionRequest_t>(pvParam);
        var identity = request.m_identityRemote;
        var steamId = identity.GetSteamID64();
        if (_sessionGate(steamId))
        {
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            _log($"accepted messages session from {steamId}");
        }
        else
        {
            _log($"refused messages session from {steamId} (not in the lobby)");
        }
    }
}
