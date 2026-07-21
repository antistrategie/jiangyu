using System.Text.Json;
using MelonLoader;
using MelonLoader.Utils;

namespace Jiangyu.Loader.Diagnostics.Determinism;

// Dev command (determinism): the record/replay determinism probe from
// docs/research/investigations/2026-07-19-multiplayer-framework-design.md section 8.
// Drives a scripted command sequence through a live tactical mission, journaling each
// command and hashing a state projection at every action barrier (record), then replays
// the same script from the same save in a fresh session and localises the first
// divergent barrier (replay). Scripts and journals live under
// <UserData>/determinism/ so a record/replay pair survives the process restart the
// comparison needs. Mutating (it plays the mission), dev-loader only.
//
// Ops (args.op): "record" {script:"name"} starts a recording run of
// determinism/<name>.script.json; "replay" {script:"name", journal:"<file>"} replays
// against a recorded journal; "compare" {a, b} diffs two journals offline; "status"
// reports the active run; "abort" stops it.
internal static class DeterminismProbe
{
    private static DeterminismSession _active;

    public static object Run(JsonElement args, MelonLogger.Instance log)
    {
        var op = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("op", out var o)
            ? o.GetString()
            : null;
        try
        {
            switch (op)
            {
                case "record":
                    return Start(DeterminismMode.Record, args, log);
                case "replay":
                    return Start(DeterminismMode.Replay, args, log);
                case "status":
                    return _active?.Status() ?? new { ok = true, running = false };
                case "abort":
                    if (_active == null)
                        return new { ok = true, running = false };
                    _active.Abort();
                    _active = null;
                    return new { ok = true, aborted = true };
                case "compare":
                    return Compare(args);
                default:
                    return new { error = "determinism op must be record | replay | compare | status | abort" };
            }
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    private static object Start(DeterminismMode mode, JsonElement args, MelonLogger.Instance log)
    {
        if (_active != null)
            return new { error = "a determinism run is already active (status/abort to inspect or stop it)" };
        if (!Il2CppMenace.Tactical.TacticalManager.IsMissionRunning())
            return new { error = "no mission running: load the fixed save first" };

        var scriptName = args.TryGetProperty("script", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(scriptName))
            return new { error = "missing script name (args.script)" };

        var script = DeterminismScript.Load(Path.Combine(Dir(), scriptName + ".script.json"));
        DeterminismJournal reference = null;
        if (mode == DeterminismMode.Replay)
        {
            var journalName = args.TryGetProperty("journal", out var j) ? j.GetString() : null;
            if (string.IsNullOrEmpty(journalName))
                return new { error = "replay needs args.journal (a journal file under determinism/)" };
            reference = DeterminismJournal.Load(Path.Combine(Dir(), journalName));
        }

        // The completion callback clears the slot only if it still holds this session:
        // an aborted session finishes a frame later than the abort, and must not clobber
        // a run that started in between.
        DeterminismSession session = null;
        session = new DeterminismSession(mode, scriptName, script, reference, log,
            onCompleted: () =>
            {
                if (ReferenceEquals(_active, session))
                    _active = null;
            });
        _active = session;
        _active.Begin();
        return new { ok = true, mode = mode.ToString().ToLowerInvariant(), script = scriptName, steps = script.Steps.Count };
    }

    // Offline journal-vs-journal comparison (record vs record, e.g. two machines, or two
    // runs of the same session to catch state carryover). File-only; needs no mission.
    private static object Compare(JsonElement args)
    {
        var a = args.TryGetProperty("a", out var ja) ? ja.GetString() : null;
        var b = args.TryGetProperty("b", out var jb) ? jb.GetString() : null;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return new { error = "compare needs args.a and args.b (journal files under determinism/)" };
        var first = DeterminismJournal.Load(Path.Combine(Dir(), a));
        var second = DeterminismJournal.Load(Path.Combine(Dir(), b));

        var steps = Math.Min(first.Steps.Count, second.Steps.Count);
        var mismatches = 0;
        var simMismatches = 0;
        object firstDiff = null;
        var firstMismatchSeq = -1;
        for (var i = 0; i < steps; i++)
        {
            if (first.Steps[i].Hash == second.Steps[i].Hash)
                continue;
            mismatches++;
            if (!DeterminismJournal.SimDiverges(first.Steps[i], second.Steps[i]))
                continue;
            simMismatches++;
            if (firstMismatchSeq < 0)
            {
                firstMismatchSeq = i;
                firstDiff = DeterminismSnapshot.Diff(
                    first.Steps[i].Mission, first.Steps[i].Snapshot ?? new List<string>(),
                    second.Steps[i].Mission, second.Steps[i].Snapshot ?? new List<string>());
            }
        }
        return new
        {
            ok = true,
            steps,
            lengthMismatch = first.Steps.Count != second.Steps.Count,
            // Sim semantics, like the per-step verdict: the raw baseline hash folds in
            // the frame-dependent UnityEngine.Random block and never matches across
            // processes.
            baselineMatch = first.Baseline != null && second.Baseline != null
                && !DeterminismJournal.SimDiverges(first.Baseline, second.Baseline),
            mismatches,
            simMismatches,
            rngOnlyMismatches = mismatches - simMismatches,
            firstMismatchSeq,
            firstMismatchDiff = firstDiff,
        };
    }

    // <UserData>/determinism: one folder for scripts, journals and replay reports so the
    // record/replay pair sits side by side across the process restart between them.
    internal static string Dir()
    {
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "determinism");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
