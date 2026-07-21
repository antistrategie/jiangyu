using System.Collections;
using System.Text.Json;
using Jiangyu.Loader.Net.Steam;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Net;
using MelonLoader;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics.Net;

// Dev command (net): the phase-2 multiplayer proof harness. Drives the loader's
// transport and lobby layers end to end over the bridge:
//   loopback                  in-process transport pair: handshake to Ready, chat echo,
//                             and a deliberate mod-set mismatch, synchronously, no Steam
//   selftest                  the same protocol against the local SteamID over the real
//                             Steam messages transport (poll status for the verdict)
//   host | join {lobby}       friends-only Steam lobby; the handshake starts when the
//                             second member arrives (lobby ids travel as decimal strings)
//   send {text} | echo {enabled} | leave | status
// Long legs run as a coroutine on the main thread; poll status for progress.
internal static class NetProbe
{
    private const int SelfTestTimeoutFrames = 600;
    private const int NoteCap = 32;

    private static SteamLobby _lobby;
    private static SteamMessagesTransport _transport;
    private static NetSession _session;
    private static ModSetSummary _summary;
    private static bool _echo;
    private static bool _pumpRunning;
    private static string _lastError;
    private static object _selfTest;
    private static readonly List<string> Notes = new();

    public static object Run(JsonElement args, MelonLogger.Instance log)
    {
        SteamCallbacks.Log = line => Note(line);
        var op = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("op", out var o)
            ? o.GetString()
            : null;
        try
        {
            switch (op)
            {
                case "status":
                    return Status();
                case "loopback":
                    return RunLoopback();
                case "selftest":
                    return StartSelfTest(log);
                case "host":
                    return Start(host: true, 0, log);
                case "join":
                    return Start(host: false, ReadLobbyId(args), log);
                case "leave":
                    return Leave();
                case "send":
                    return Send(args);
                case "echo":
                    _echo = args.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                    return new { ok = true, echo = _echo };
                default:
                    return new { error = "net op must be status | loopback | selftest | host | join | leave | send | echo" };
            }
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static object Status()
    {
        var steamRunning = SafeBool(Il2CppSteamworks.SteamAPI.IsSteamRunning);
        var steamInitialised = SafeBool(() => Il2Cpp.SteamManager.Initialized);
        return new
        {
            ok = true,
            steam = new
            {
                running = steamRunning,
                initialised = steamInitialised,
                steamId = _transport?.LocalPeer.ToString(),
            },
            lobby = _lobby == null ? null : new
            {
                phase = _lobby.Phase.ToString(),
                id = _lobby.LobbyId == 0 ? null : _lobby.LobbyId.ToString(),
                owner = _lobby.Owner == 0 ? null : _lobby.Owner.ToString(),
                members = _lobby.Members.Select(m => m.ToString()).ToArray(),
                failure = _lobby.FailureReason,
            },
            session = DescribeSession(_session),
            echo = _echo,
            selfTest = _selfTest,
            notes = Notes.ToArray(),
            error = _lastError,
        };
    }

    private static object DescribeSession(NetSession session) => session == null ? null : new
    {
        phase = session.Phase.ToString(),
        isHost = session.IsHost,
        rejectDifferences = session.RejectDifferences.Count == 0 ? null : session.RejectDifferences.ToArray(),
        transcript = session.Transcript.ToArray(),
    };

    // The whole loopback exercise is synchronous: the in-process pair delivers on the
    // spot, so a handful of pump rounds settles both the happy path and the mismatch.
    private static object RunLoopback()
    {
        var summary = BuildLocalSummary();

        var (ta, tb) = LoopbackTransport.CreatePair();
        var host = new NetSession(ta, summary, isHost: true);
        var client = new NetSession(tb, summary, isHost: false);
        string clientHeard = null;
        client.ChatReceived += (_, text) => clientHeard = text;
        host.Connect(tb.LocalPeer);
        client.Connect(ta.LocalPeer);
        PumpRounds(host, client);
        var chatSent = host.SendChat("ping from host");
        PumpRounds(host, client);

        var mismatched = new ModSetSummary
        {
            NetProtocol = summary.NetProtocol,
            GameBuild = summary.GameBuild,
            LoaderVersion = summary.LoaderVersion,
            Mods = new List<ModSetEntry>(summary.Mods)
            {
                new() { Name = "LoopbackPhantomMod", Version = "9.9.9", ManifestHash = "ff" },
            },
        };
        var (tc, td) = LoopbackTransport.CreatePair();
        var strict = new NetSession(tc, summary, isHost: true);
        var phantom = new NetSession(td, mismatched, isHost: false);
        strict.Connect(td.LocalPeer);
        phantom.Connect(tc.LocalPeer);
        PumpRounds(strict, phantom);

        var pass = host.Phase == NetSessionPhase.Ready
            && client.Phase == NetSessionPhase.Ready
            && chatSent
            && clientHeard == "ping from host"
            && strict.Phase == NetSessionPhase.Rejected
            && phantom.Phase == NetSessionPhase.Rejected;
        return new
        {
            ok = pass,
            matched = new { host = DescribeSession(host), client = DescribeSession(client), chatDelivered = clientHeard },
            mismatched = new { host = DescribeSession(strict), client = DescribeSession(phantom) },
            summary = new { mods = summary.Mods.Count, gameBuild = summary.GameBuild },
        };
    }

    private static object StartSelfTest(MelonLogger.Instance log)
    {
        if (!SteamLive(out var reason))
            return new { error = reason };
        if (_pumpRunning)
            return new { error = "a net session is active (use leave first)" };

        _selfTest = new { phase = "running" };
        MelonCoroutines.Start(SelfTestLoop(log));
        return new { ok = true, started = true };
    }

    private static IEnumerator SelfTestLoop(MelonLogger.Instance log)
    {
        var summary = BuildLocalSummary();
        var transport = new SteamMessagesTransport(_ => true, line => Note(line));
        var session = new NetSession(transport, summary, isHost: true);
        string heard = null;
        session.ChatReceived += (_, text) => heard = text;
        session.Connect(transport.LocalPeer);

        var sent = false;
        for (var frame = 0; frame < SelfTestTimeoutFrames; frame++)
        {
            try
            {
                session.Pump();
                if (session.Phase == NetSessionPhase.Ready && !sent)
                {
                    session.SendChat("selftest echo");
                    sent = true;
                }
            }
            catch (Exception ex)
            {
                _selfTest = new { phase = "failed", error = $"{ex.GetType().Name}: {ex.Message}" };
                transport.CloseSessionWith(transport.LocalPeer);
                yield break;
            }

            if (heard == "selftest echo")
                break;
            yield return null;
        }

        transport.CloseSessionWith(transport.LocalPeer);
        _selfTest = new
        {
            phase = heard == "selftest echo" ? "passed" : "failed",
            sessionPhase = session.Phase.ToString(),
            chatDelivered = heard,
            steamId = transport.LocalPeer.ToString(),
        };
        log?.Msg($"[net] selftest {(heard == "selftest echo" ? "passed" : "failed")} (session {session.Phase})");
    }

    private static object Start(bool host, ulong lobbyId, MelonLogger.Instance log)
    {
        if (!SteamLive(out var reason))
            return new { error = reason };
        if (_lobby != null)
            return new { error = "net session already active (use leave first)" };
        if (!host && lobbyId == 0)
            return new { error = "join needs {lobby: \"<decimal SteamID64>\"}" };

        _lastError = null;
        _summary = BuildLocalSummary();
        _lobby = new SteamLobby(line => Note(line));
        _transport = new SteamMessagesTransport(
            id => _lobby != null && _lobby.IsMember(id),
            line => Note(line));
        _session = new NetSession(_transport, _summary, isHost: host);
        _session.ChatReceived += OnChat;
        _lobby.MemberJoined = peer =>
        {
            if (peer != _transport.LocalPeer)
                _session.Connect(peer);
        };
        _lobby.MemberLeft = peer =>
        {
            _session.PeerLost(peer);
            _transport.CloseSessionWith(peer);
        };

        if (host)
            _lobby.Host(maxMembers: 2);
        else
            _lobby.Join(lobbyId);

        if (!_pumpRunning)
        {
            _pumpRunning = true;
            MelonCoroutines.Start(PumpLoop(log));
        }

        return new { ok = true, started = host ? "hosting" : "joining", mods = _summary.Mods.Count };
    }

    private static object Leave()
    {
        _lobby?.Leave();
        _lobby = null;
        _session = null;
        _transport?.Dispose();
        _transport = null;
        SteamCallbacks.Clear();
        _echo = false;
        return new { ok = true };
    }

    private static object Send(JsonElement args)
    {
        if (_session == null)
            return new { error = "no session (host or join first)" };
        var text = args.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(text))
            return new { error = "send needs {text}" };
        var sent = _session.SendChat(text);
        return sent
            ? new { ok = true }
            : (new { error = $"chat refused (session {_session.Phase})" });
    }

    private static IEnumerator PumpLoop(MelonLogger.Instance log)
    {
        while (_lobby != null)
        {
            try
            {
                _lobby.Pump();
                _session?.Pump();
            }
            catch (Exception ex)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                log?.Warning($"[net] pump: {_lastError}");
            }

            yield return null;
        }

        _pumpRunning = false;
    }

    private static void OnChat(PeerId from, string text)
    {
        Note($"chat {from}: {text}");
        if (_echo && !text.StartsWith("echo: ", StringComparison.Ordinal))
            _session?.SendChat($"echo: {text}");
    }

    private static ModSetSummary BuildLocalSummary()
    {
        var plan = ModLoadPlanBuilder.Build(MelonEnvironment.ModsDirectory, BuildInfo.Version);
        string gameBuild;
        try
        {
            gameBuild = UnityEngine.Application.version;
        }
        catch
        {
            gameBuild = "unknown";
        }

        return ModSetSummaryBuilder.Build(plan, gameBuild, BuildInfo.Version);
    }

    private static bool SteamLive(out string reason)
    {
        reason = null;
        if (!SafeBool(() => Il2Cpp.SteamManager.Initialized))
            reason = "SteamManager is not initialised (game running without Steam?)";
        else if (!SafeBool(Il2CppSteamworks.SteamAPI.IsSteamRunning))
            reason = "Steam is not running";
        return reason == null;
    }

    private static void PumpRounds(NetSession a, NetSession b)
    {
        for (var i = 0; i < 4; i++)
        {
            a.Pump();
            b.Pump();
        }
    }

    private static ulong ReadLobbyId(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("lobby", out var l))
            return 0;
        if (l.ValueKind == JsonValueKind.String && ulong.TryParse(l.GetString(), out var fromString))
            return fromString;
        if (l.ValueKind == JsonValueKind.Number && l.TryGetUInt64(out var fromNumber))
            return fromNumber;
        return 0;
    }

    private static void Note(string line)
    {
        if (Notes.Count == NoteCap)
            Notes.RemoveAt(0);
        Notes.Add(line);
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }
}
