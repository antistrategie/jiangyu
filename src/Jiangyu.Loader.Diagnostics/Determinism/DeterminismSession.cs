using System.Collections;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;

namespace Jiangyu.Loader.Diagnostics.Determinism;

// One active record or replay run. Subscribes to the TacticalManager's completion
// events (the same IL2CPP delegate-marshalling pattern as TacticalHookPublisher),
// drives the script step by step as a coroutine, waits for each step's barrier event
// plus a quiet window, and snapshots at that barrier. Record journals the run; replay
// additionally diffs every barrier against the reference journal and reports the first
// divergence with actor-level detail. Runs entirely on the main thread.
internal sealed class DeterminismSession
{
    public const string EventMovementFinished = "MovementFinished";
    public const string EventAfterSkillUse = "AfterSkillUse";
    public const string EventTurnEnd = "TurnEnd";
    public const string EventPlayerTurn = "PlayerTurn";
    public const string EventRoundStart = "RoundStart";
    public const string EventMissionFinished = "MissionFinished";
    public const string EventEntitySpawned = "EntitySpawned";

    private readonly DeterminismMode _mode;
    private readonly string _scriptName;
    private readonly DeterminismScript _script;
    private readonly DeterminismJournal _reference;
    private readonly MelonLogger.Instance _log;
    private readonly Action _onCompleted;
    private readonly DeterminismJournal _journal = new();
    private readonly List<Action> _detach = new();
    private readonly List<object> _roots = new();
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);

    private TacticalManager _manager;
    private int _frame;
    private int _lastActivityFrame;
    private bool _aborted;
    private bool _finished;
    private int _stepIndex = -1;
    private string _lastHash = "-";
    private int _mismatches;
    private int _simMismatches;
    private int _firstMismatchSeq = -1;
    private object _firstMismatchDiff;

    public DeterminismSession(DeterminismMode mode, string scriptName, DeterminismScript script,
        DeterminismJournal reference, MelonLogger.Instance log, Action onCompleted)
    {
        _mode = mode;
        _scriptName = scriptName;
        _script = script;
        _reference = reference;
        _log = log;
        _onCompleted = onCompleted;
    }

    public void Begin()
    {
        _journal.Mode = _mode.ToString().ToLowerInvariant();
        _journal.Script = _scriptName;
        _journal.StartedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        try { _journal.GameVersion = UnityEngine.Application.version; } catch { }
        MelonCoroutines.Start(Run());
    }

    public void Abort() => _aborted = true;

    public object Status() => new
    {
        ok = true,
        running = !_finished,
        mode = _mode.ToString().ToLowerInvariant(),
        script = _scriptName,
        step = _stepIndex,
        steps = _script.Steps.Count,
        lastHash = _lastHash,
        mismatches = _mismatches,
        simMismatches = _simMismatches,
        firstMismatchSeq = _firstMismatchSeq, // first SIM divergence (rng-only mismatches excluded)
        firstMismatchDiff = _firstMismatchDiff,
        aborted = _aborted,
    };

    private IEnumerator Run()
    {
        _manager = TacticalManager.Get();
        if (_manager == null)
        {
            Fail("no tactical manager");
            yield break;
        }
        AttachEvents();
        _journal.Seed = TryReadSeed();
        _log.Msg($"[determinism] {_mode.ToString().ToLowerInvariant()} started: {_script.Steps.Count} step(s), seed={_journal.Seed ?? "<unreadable>"}");

        yield return QuietWindow(_script.SettleFrames, _script.TimeoutFrames);
        _journal.Baseline = Snapshot(-1, "baseline", null);
        _log.Msg($"[determinism] baseline hash {_journal.Baseline.Hash}");
        if (_mode == DeterminismMode.Replay && _reference.Baseline != null
            && _reference.Baseline.Hash != _journal.Baseline.Hash)
        {
            _log.Warning("[determinism] baseline hash differs from the journal: the loaded state is not "
                + "the recorded starting state (wrong save?). Per-step comparison continues regardless.");
        }

        for (_stepIndex = 0; _stepIndex < _script.Steps.Count && !_aborted; _stepIndex++)
        {
            var step = _script.Steps[_stepIndex];
            var waitEvent = step.WaitEvent ?? step.DefaultWaitEvent;
            var eventMark = waitEvent == null ? 0 : Count(waitEvent);
            var stepStartFrame = _frame;
            var result = Execute(step);

            // A "wait" step is itself the delay: burn its frames before the barrier wait.
            if (step.Op == "wait" && step.Frames > 0)
                for (var i = 0; i < step.Frames && !_aborted; i++)
                    yield return Frame();

            var timeout = step.Timeout ?? _script.TimeoutFrames;
            if (waitEvent != null && Count(waitEvent) == eventMark)
            {
                var waited = 0;
                while (Count(waitEvent) == eventMark && waited < timeout && !_aborted)
                {
                    waited++;
                    yield return Frame();
                }
                if (Count(waitEvent) == eventMark)
                    result += $"; barrier event {waitEvent} not seen within {timeout} frames";
            }
            yield return QuietWindow(step.Settle ?? _script.SettleFrames, timeout);

            var entry = Snapshot(_stepIndex, result, step);
            entry.WaitedFrames = _frame - stepStartFrame;
            _journal.Steps.Add(entry);
            _lastHash = entry.Hash;
            _log.Msg($"[determinism] step {_stepIndex} ({step.Op}) {entry.Hash} [{entry.Result}] ({entry.WaitedFrames}f)");

            if (_mode == DeterminismMode.Replay)
                Compare(_stepIndex, entry);
        }

        Finish();
    }

    private IEnumerator Frame()
    {
        _frame++;
        yield return null;
    }

    // Wait until no tactical event has fired for `settle` consecutive frames (or the
    // timeout passes). The barrier event says the action ended; the quiet window says
    // the fallout (deaths, morale changes, follow-up triggers) has settled, so the two
    // runs snapshot equivalent states.
    private IEnumerator QuietWindow(int settle, int timeout)
    {
        var waited = 0;
        while (_frame - _lastActivityFrame < settle && waited < timeout && !_aborted)
        {
            waited++;
            yield return Frame();
        }
    }

    private DeterminismJournal.Entry Snapshot(int seq, string result, DeterminismScript.Step step)
    {
        var snap = DeterminismSnapshot.Capture(_manager);
        return new DeterminismJournal.Entry
        {
            Seq = seq,
            Command = step == null ? null : DeterminismScript.Describe(step),
            Result = result,
            Hash = snap.Hash,
            Mission = $"round={snap.Round} faction={snap.ActiveFaction} active={snap.ActiveActor} rng={snap.RngState}",
            Snapshot = snap.ActorLines,
        };
    }

    private void Compare(int seq, DeterminismJournal.Entry entry)
    {
        if (seq >= _reference.Steps.Count)
        {
            NoteMismatch(seq, true, new { error = $"no reference step {seq} (journal has {_reference.Steps.Count})" });
            return;
        }
        var reference = _reference.Steps[seq];
        if (entry.Hash == reference.Hash)
            return;
        NoteMismatch(seq, DeterminismJournal.SimDiverges(reference, entry), DeterminismSnapshot.Diff(
            reference.Mission, reference.Snapshot ?? new List<string>(),
            entry.Mission, entry.Snapshot));
    }

    private void NoteMismatch(int seq, bool simDiverges, object diff)
    {
        _mismatches++;
        if (simDiverges)
            _simMismatches++;
        if (simDiverges && _firstMismatchSeq < 0)
        {
            _firstMismatchSeq = seq;
            _firstMismatchDiff = diff;
            _log.Warning($"[determinism] SIM DIVERGENCE at step {seq}");
        }
    }

    private string Execute(DeterminismScript.Step step)
    {
        try
        {
            switch (step.Op)
            {
                case "move":
                    {
                        var actor = ResolveActor(step);
                        var tile = ResolveTile(step);
                        var action = default(MovementAction);
                        return actor.MoveTo(tile, ref action, MovementFlags.None) ? "ok" : "rejected by game";
                    }
                case "attack":
                    {
                        var actor = ResolveActor(step);
                        var skill = ResolveSkill(actor, step.Skill);
                        var tile = ResolveTile(step);
                        var usage = step.Usage.HasValue ? (UsageParameter)step.Usage.Value : default;
                        skill.Use(tile, usage);
                        return "ok";
                    }
                case "spawn":
                    {
                        var faction = (FactionType)Enum.Parse(typeof(FactionType), step.Faction, true);
                        if (!Templates.TemplateRuntimeAccess.TryGetTemplateById(typeof(EntityTemplate), step.Template, out var template, out var error))
                            return $"error: {error ?? $"template '{step.Template}' not found"}";
                        var tile = ResolveTile(step);
                        return _manager.TrySpawnUnit(faction, (EntityTemplate)template, tile, out var unit) && unit != null
                            ? $"ok ({unit.GetTemplate()?.name})"
                            : "rejected by game";
                    }
                case "skip":
                    ResolveActor(step).SkipTurn();
                    return "ok";
                case "endturn":
                    Il2CppMenace.States.TacticalState.Get().EndTurn();
                    return "ok";
                case "wait":
                    return "ok";
                case "seedrng":
                    UnityEngine.Random.InitState(step.Value);
                    return "ok";
                default:
                    return $"unknown op '{step.Op}'";
            }
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // Template addressing is the stable form: a faction's GetActors() list is not
    // positionally stable once actors act, so an index only resolves a first action.
    // Every faction of the requested type is searched, not just the first match.
    private Actor ResolveActor(DeterminismScript.Step step)
    {
        var factionSeen = false;
        var factions = _manager.GetFactions();
        for (var i = 0; factions != null && i < factions.Length; i++)
        {
            var faction = factions[i];
            if (faction == null || !string.Equals(faction.GetFactionType().ToString(), step.Faction, StringComparison.OrdinalIgnoreCase))
                continue;
            factionSeen = true;
            var actors = faction.GetActors();
            for (var j = 0; actors != null && j < actors.Count; j++)
            {
                var actor = actors[j];
                if (actor == null)
                    continue;
                if (step.ActorTemplate != null)
                {
                    string templateName = null;
                    try { templateName = actor.GetTemplate()?.name; } catch { }
                    if (string.Equals(templateName, step.ActorTemplate, StringComparison.Ordinal))
                        return actor;
                }
                else if (j == step.ActorIndex)
                {
                    return actor;
                }
            }
        }

        if (!factionSeen)
            throw new InvalidOperationException($"faction '{step.Faction}' not found");
        throw new InvalidOperationException(step.ActorTemplate != null
            ? $"no actor with template '{step.ActorTemplate}' in faction {step.Faction}"
            : $"actor index {step.ActorIndex} out of range for faction {step.Faction}");
    }

    private Tile ResolveTile(DeterminismScript.Step step)
    {
        var tile = _manager.GetMap()?.GetTile(step.X, step.Z);
        return tile ?? throw new InvalidOperationException($"no tile at {step.X},{step.Z}");
    }

    private static Skill ResolveSkill(Actor actor, string skillId)
    {
        var all = actor.GetSkills()?.GetAllSkills();
        for (var i = 0; all != null && i < all.Count; i++)
        {
            var skill = all[i]?.TryCast<Skill>();
            if (skill != null && string.Equals(skill.GetID(), skillId, StringComparison.Ordinal))
                return skill;
        }
        throw new InvalidOperationException($"skill '{skillId}' not found on actor");
    }

    private int Count(string eventName)
        => _eventCounts.TryGetValue(eventName, out var count) ? count : 0;

    private void Note(string eventName)
    {
        _eventCounts[eventName] = Count(eventName) + 1;
        _lastActivityFrame = _frame;
    }

    // Every subscription both marks general activity (for the quiet window) and counts
    // the named event (for per-step barrier waits). Mirrors TacticalHookPublisher:
    // ConvertDelegate, subscribe, and root both halves so neither is collected while
    // live; the matching remove_ accessor runs on cleanup.
    private void AttachEvents()
    {
        Hook<TacticalManager.OnMovementFinishedEvent>(_manager.add_OnMovementFinished, _manager.remove_OnMovementFinished,
            (Action<Actor, Tile>)((_, _) => Note(EventMovementFinished)), EventMovementFinished);
        Hook<TacticalManager.OnMovementEvent>(_manager.add_OnMovement, _manager.remove_OnMovement,
            (Action<Actor, Tile, Tile, MovementAction, Entity>)((_, _, _, _, _) => Note("Movement")), "Movement");
        Hook<TacticalManager.OnAfterSkillUseEvent>(_manager.add_OnAfterSkillUse, _manager.remove_OnAfterSkillUse,
            (Action<Skill>)(_ => Note(EventAfterSkillUse)), EventAfterSkillUse);
        Hook<TacticalManager.OnSkillUseEvent>(_manager.add_OnSkillUse, _manager.remove_OnSkillUse,
            (Action<Actor, Skill, Tile>)((_, _, _) => Note("SkillUse")), "SkillUse");
        Hook<TacticalManager.OnTurnEndEvent>(_manager.add_OnTurnEnd, _manager.remove_OnTurnEnd,
            (Action<Actor, bool>)((_, _) => Note(EventTurnEnd)), EventTurnEnd);
        Hook<TacticalManager.OnPlayerTurnEvent>(_manager.add_OnPlayerTurn, _manager.remove_OnPlayerTurn,
            () => Note(EventPlayerTurn), EventPlayerTurn);
        Hook<TacticalManager.OnAITurnEvent>(_manager.add_OnAITurn, _manager.remove_OnAITurn,
            (Action<int>)(_ => Note("AITurn")), "AITurn");
        Hook<TacticalManager.OnRoundStartEvent>(_manager.add_OnRoundStart, _manager.remove_OnRoundStart,
            (Action<int>)(_ => Note(EventRoundStart)), EventRoundStart);
        Hook<TacticalManager.OnActorActedEvent>(_manager.add_OnActorActed, _manager.remove_OnActorActed,
            (Action<Actor>)(_ => Note("ActorActed")), "ActorActed");
        Hook<TacticalManager.OnDeathEvent>(_manager.add_OnDeath, _manager.remove_OnDeath,
            (Action<Entity, Entity>)((_, _) => Note("Death")), "Death");
        Hook<TacticalManager.OnEntitySpawnedEvent>(_manager.add_OnEntitySpawned, _manager.remove_OnEntitySpawned,
            (Action<Entity>)(_ => Note(EventEntitySpawned)), EventEntitySpawned);
        Hook<TacticalManager.OnFinishedEvent>(_manager.add_OnFinished, _manager.remove_OnFinished,
            () => Note(EventMissionFinished), EventMissionFinished);
    }

    private void Hook<TDelegate>(Action<TDelegate> add, Action<TDelegate> remove, Delegate managed, string name)
        where TDelegate : Il2CppSystem.Delegate
    {
        try
        {
            var proxy = DelegateSupport.ConvertDelegate<TDelegate>(managed);
            add(proxy);
            _roots.Add(managed);
            _roots.Add(proxy);
            _detach.Add(() =>
            {
                try { remove(proxy); }
                catch { /* manager already gone */ }
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"[determinism] failed to attach {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DetachEvents()
    {
        foreach (var detach in _detach)
            detach();
        _detach.Clear();
        _roots.Clear();
    }

    // Best-effort read of the mission/game seed through whatever accessor the build
    // exposes (GetMissionSeed / GetSeed / GetGameSeed on the manager or the mission).
    // The seed hierarchy is metadata-confirmed but the owning type is not; record what
    // is found, tolerate nothing.
    private string TryReadSeed()
    {
        object mission = null;
        try { mission = _manager.GetMission(); } catch { }
        foreach (var candidate in new[] { _manager, mission })
        {
            if (candidate == null)
                continue;
            foreach (var name in new[] { "GetMissionSeed", "GetSeed", "GetGameSeed" })
            {
                var method = candidate.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method == null || method.GetParameters().Length != 0)
                    continue;
                try
                {
                    var value = method.Invoke(method.IsStatic ? null : candidate, null);
                    return $"{candidate.GetType().Name}.{name}={value}";
                }
                catch { }
            }
        }
        return null;
    }

    private void Fail(string error)
    {
        _log.Error($"[determinism] {error}");
        _finished = true;
        _onCompleted();
    }

    private void Finish()
    {
        _finished = true;
        DetachEvents();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(DeterminismProbe.Dir(), $"{_scriptName}-{_mode.ToString().ToLowerInvariant()}-{stamp}.journal.json");
        try
        {
            _journal.Save(path);
            _log.Msg($"[determinism] journal written: {path}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[determinism] journal write failed: {ex.Message}");
        }

        if (_mode == DeterminismMode.Replay)
        {
            var verdict = _firstMismatchSeq < 0
                ? $"SIM MATCH: actor state agrees at all {_journal.Steps.Count} barrier(s) ({_mismatches - _simMismatches} rng-only fragment difference(s) ignored)"
                : $"SIM DIVERGENT: first at step {_firstMismatchSeq} ({_simMismatches} sim, {_mismatches - _simMismatches} rng-only, of {_journal.Steps.Count} steps)";
            _log.Msg($"[determinism] replay verdict: {verdict}");
        }
        _onCompleted();
    }
}
