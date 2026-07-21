using Il2CppSteamworks;
using Jiangyu.Shared.Net;

namespace Jiangyu.Loader.Net.Steam;

internal enum SteamLobbyPhase
{
    Idle,
    Creating,
    Joining,
    InLobby,
    Failed,
}

/// <summary>
/// Steam lobby lifecycle over the game's matchmaking instance: create or join, then a
/// per-frame <see cref="Pump"/> that completes the pending call and diffs the member
/// list into joined/left notifications. Membership is the transport's session gate and
/// the session layer's connect signal. A join request accepted through the Steam
/// friends overlay lands here too and joins the requested lobby.
/// </summary>
internal sealed class SteamLobby
{
    private const int PendingCallTimeoutFrames = 1800;

    private readonly Action<string> _log;
    private readonly List<ulong> _members = new();
    private SteamApiCall<LobbyCreated_t> _pendingCreate;
    private SteamApiCall<LobbyEnter_t> _pendingJoin;
    private int _pendingFrames;

    public SteamLobby(Action<string> log)
    {
        _log = log;
        SteamCallbacks.Listen<GameLobbyJoinRequested_t>(OnJoinRequested);
    }

    public SteamLobbyPhase Phase { get; private set; } = SteamLobbyPhase.Idle;
    public ulong LobbyId { get; private set; }
    public string FailureReason { get; private set; }
    public IReadOnlyList<ulong> Members => _members;

    public Action<PeerId> MemberJoined;
    public Action<PeerId> MemberLeft;

    public ulong Owner => Phase == SteamLobbyPhase.InLobby
        ? SteamMatchmaking.GetLobbyOwner(Id(LobbyId)).m_SteamID
        : 0;

    public bool IsMember(ulong steamId) => _members.Contains(steamId);

    public void Host(int maxMembers)
    {
        if (Phase is SteamLobbyPhase.Creating or SteamLobbyPhase.Joining or SteamLobbyPhase.InLobby)
            return;
        _pendingCreate = new SteamApiCall<LobbyCreated_t>(
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxMembers));
        _pendingFrames = 0;
        Phase = SteamLobbyPhase.Creating;
    }

    public void Join(ulong lobbyId)
    {
        if (Phase is SteamLobbyPhase.Creating or SteamLobbyPhase.Joining or SteamLobbyPhase.InLobby)
            return;
        _pendingJoin = new SteamApiCall<LobbyEnter_t>(SteamMatchmaking.JoinLobby(Id(lobbyId)));
        _pendingFrames = 0;
        Phase = SteamLobbyPhase.Joining;
    }

    public void Leave()
    {
        if (Phase == SteamLobbyPhase.InLobby)
            SteamMatchmaking.LeaveLobby(Id(LobbyId));
        _pendingCreate = null;
        _pendingJoin = null;
        _members.Clear();
        LobbyId = 0;
        Phase = SteamLobbyPhase.Idle;
    }

    public void Pump()
    {
        switch (Phase)
        {
            case SteamLobbyPhase.Creating:
                PumpCreate();
                break;
            case SteamLobbyPhase.Joining:
                PumpJoin();
                break;
            case SteamLobbyPhase.InLobby:
                DiffMembers();
                break;
        }
    }

    private void PumpCreate()
    {
        if (!_pendingCreate.TryComplete(out var created, out var failure))
        {
            TickPendingTimeout("lobby creation");
            return;
        }

        _pendingCreate = null;
        if (failure != null || created.m_eResult != EResult.k_EResultOK)
        {
            Fail($"lobby creation failed ({failure ?? created.m_eResult.ToString()})");
            return;
        }

        Enter(created.m_ulSteamIDLobby);
    }

    private void PumpJoin()
    {
        if (!_pendingJoin.TryComplete(out var entered, out var failure))
        {
            TickPendingTimeout("lobby join");
            return;
        }

        _pendingJoin = null;

        // 1 is k_EChatRoomEnterResponseSuccess; the remaining response codes are all
        // failures with distinct causes (full, banned, gone).
        if (failure != null || entered.m_EChatRoomEnterResponse != 1)
        {
            Fail($"lobby join failed ({failure ?? $"enter response {entered.m_EChatRoomEnterResponse}"})");
            return;
        }

        Enter(entered.m_ulSteamIDLobby);
    }

    private void Enter(ulong lobbyId)
    {
        LobbyId = lobbyId;
        Phase = SteamLobbyPhase.InLobby;
        _log($"in lobby {lobbyId}");
        DiffMembers();
    }

    private void TickPendingTimeout(string what)
    {
        if (++_pendingFrames < PendingCallTimeoutFrames)
            return;
        _pendingCreate = null;
        _pendingJoin = null;
        Fail($"{what} timed out (Steam not responding)");
    }

    private void Fail(string reason)
    {
        FailureReason = reason;
        Phase = SteamLobbyPhase.Failed;
        _log(reason);
    }

    private void DiffMembers()
    {
        var lobby = Id(LobbyId);
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        var current = new List<ulong>(count);
        for (var i = 0; i < count; i++)
            current.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID);

        foreach (var member in current)
        {
            if (!_members.Contains(member))
            {
                _log($"lobby member joined: {member}");
                MemberJoined?.Invoke(new PeerId(member));
            }
        }

        foreach (var member in _members)
        {
            if (!current.Contains(member))
            {
                _log($"lobby member left: {member}");
                MemberLeft?.Invoke(new PeerId(member));
            }
        }

        _members.Clear();
        _members.AddRange(current);
    }

    private void OnJoinRequested(IntPtr pvParam)
    {
        var request = SteamInterop.Read<GameLobbyJoinRequested_t>(pvParam);
        _log($"overlay join request for lobby {request.m_steamIDLobby.m_SteamID}");
        if (Phase == SteamLobbyPhase.Idle)
            Join(request.m_steamIDLobby.m_SteamID);
    }

    private static CSteamID Id(ulong value)
    {
        var id = default(CSteamID);
        id.m_SteamID = value;
        return id;
    }
}
