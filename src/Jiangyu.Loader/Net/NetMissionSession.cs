using System.Collections;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using Jiangyu.Loader.Sdk.Hooks;
using Jiangyu.Shared.Net;
using Jiangyu.Shared.Net.Commands;
using Jiangyu.Shared.Net.Sync;
using MelonLoader;

namespace Jiangyu.Loader.Net;

/// <summary>
/// The runtime that drives one tactical mission from the replicated command stream. It
/// owns a <see cref="CommandReplicator"/> (ordering) and a <see cref="CommandApplier"/>
/// (replay), and applies commands one at a time gated on the game's action barriers: it
/// applies the next ordered command, waits for that command's completion event
/// (<c>OnMovementFinished</c> for a move, <c>OnAfterSkillUse</c> for a skill, and so on)
/// plus a quiet window for the fallout to settle, then applies the next. No peer ever acts
/// mid-action, which is what makes the shared stream reproduce. Local intent enters through
/// <see cref="SubmitLocal"/>; the loop self-pumps on the main thread as a coroutine.
/// </summary>
public sealed class NetMissionSession
{
    private const int SettleFrames = 60;
    private const int BarrierTimeoutFrames = 900;

    private readonly CommandReplicator _replicator;
    private readonly CommandApplier _applier;
    private readonly TacticalManager _manager;
    private readonly ulong _localPeer;
    private readonly Il2CppEventSubscriptions _hooks = new();
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);
    private readonly List<AppliedCommand> _applied = new();
    private readonly List<BarrierChecksum> _checksums = new();

    private bool _stopped;
    private int _frame;
    private int _lastActivityFrame;

    public NetMissionSession(INetTransport transport, PeerId host, IEnumerable<PeerId> remotes, TacticalManager manager)
    {
        _manager = manager;
        _localPeer = transport.LocalPeer.Value;
        _applier = new CommandApplier(manager);
        _replicator = new CommandReplicator(transport, host, remotes);
    }

    /// <summary>A command as applied on this peer, for forensics and the checksum service.</summary>
    public sealed class AppliedCommand
    {
        public long Seq;
        public string Kind;
        public string Result;
    }

    public bool IsHost => _replicator.IsHost;
    public CommandJournal Journal => _replicator.Journal;
    public IReadOnlyList<AppliedCommand> Applied => _applied;
    public int ReadyCount => _replicator.ReadyCount;

    /// <summary>The local barrier checksums taken so far, one per applied command.</summary>
    public IReadOnlyList<BarrierChecksum> Checksums => _checksums;

    /// <summary>Continuous desync detection over local vs remote barrier checksums.</summary>
    public DesyncMonitor Desync { get; } = new();

    /// <summary>Feed a remote peer's barrier checksum (from the control channel) into the
    /// monitor; a mismatch at a shared barrier raises <see cref="DesyncMonitor.Desynced"/>.</summary>
    public void RecordRemoteChecksum(BarrierChecksum checksum) => Desync.RecordRemote(checksum);

    /// <summary>Begin driving: subscribe to the barrier events and start the apply loop.</summary>
    public void Start()
    {
        AttachBarrierEvents();
        MelonCoroutines.Start(Loop());
    }

    public void Stop()
    {
        _stopped = true;
        _hooks.DetachAll();
    }

    /// <summary>Submit a command issued locally (by input capture, or a test driver). It is
    /// ordered by the host and applied when the stream delivers it here in order.</summary>
    public void SubmitLocal(string kind, string payload, string outcome = null)
        => _replicator.Submit(kind, payload, outcome);

    // The apply loop: pump the stream, and whenever the mission is idle (no action in
    // flight and the fallout has settled) apply the next ordered command.
    private IEnumerator Loop()
    {
        var awaiting = false;
        string barrierEvent = null;
        var barrierMark = 0;
        var awaitingSeq = 0L;

        while (!_stopped)
        {
            _frame++;
            _replicator.Pump();

            if (awaiting)
            {
                // awaiting is only ever set for a command with a barrier event, so
                // barrierEvent is non-null here.
                var fired = Count(barrierEvent) > barrierMark;
                var settled = _frame - _lastActivityFrame >= SettleFrames;
                var timedOut = _frame - _lastActivityFrame >= BarrierTimeoutFrames;
                if ((fired && settled) || timedOut)
                {
                    awaiting = false;
                    TakeChecksum(awaitingSeq);
                }
            }

            if (!awaiting && _replicator.TryDequeue(out var command))
            {
                barrierEvent = BarrierFor(command.Kind);
                var result = _applier.Apply(command);
                _lastActivityFrame = _frame;
                _applied.Add(new AppliedCommand { Seq = command.Seq, Kind = command.Kind, Result = result });
                if (barrierEvent != null)
                {
                    barrierMark = Count(barrierEvent);
                    awaiting = true;
                    awaitingSeq = command.Seq;
                }
                else
                {
                    // A barrier-less command settles immediately; checksum it now.
                    TakeChecksum(command.Seq);
                }
            }

            yield return null;
        }
    }

    // Project the sim state at a barrier, hash it, keep it, and offer it to the desync
    // monitor as this peer's opinion for that barrier. The transport layer sends it to the
    // other peer and feeds theirs back through RecordRemoteChecksum.
    private void TakeChecksum(long barrier)
    {
        var checksum = Checksum.Build(barrier, _localPeer, MissionProjection.Build(_manager));
        _checksums.Add(checksum);
        Desync.RecordLocal(checksum);
    }

    // The completion event a command waits on before the next may apply. Null for commands
    // with no barrier (they apply back to back).
    private static string BarrierFor(string kind) => kind switch
    {
        CommandKinds.Move => "MovementFinished",
        CommandKinds.Skill => "AfterSkillUse",
        CommandKinds.EndTurn => "PlayerTurn",
        CommandKinds.Skip => "TurnEnd",
        _ => null,
    };

    private int Count(string name) => _eventCounts.TryGetValue(name, out var c) ? c : 0;

    private void Note(string name)
    {
        _eventCounts[name] = Count(name) + 1;
        _lastActivityFrame = _frame;
    }

    private void AttachBarrierEvents()
    {
        _hooks.Hook<TacticalManager.OnMovementFinishedEvent>(_manager.add_OnMovementFinished, _manager.remove_OnMovementFinished,
            (Action<Actor, Tile>)((_, _) => Note("MovementFinished")), "MovementFinished");
        _hooks.Hook<TacticalManager.OnAfterSkillUseEvent>(_manager.add_OnAfterSkillUse, _manager.remove_OnAfterSkillUse,
            (Action<Skill>)(_ => Note("AfterSkillUse")), "AfterSkillUse");
        _hooks.Hook<TacticalManager.OnTurnEndEvent>(_manager.add_OnTurnEnd, _manager.remove_OnTurnEnd,
            (Action<Actor, bool>)((_, _) => Note("TurnEnd")), "TurnEnd");
        _hooks.Hook<TacticalManager.OnPlayerTurnEvent>(_manager.add_OnPlayerTurn, _manager.remove_OnPlayerTurn,
            () => Note("PlayerTurn"), "PlayerTurn");
    }

}
