using System.Collections;
using System.Text.Json;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Jiangyu.Loader.Sdk.State;
using MelonLoader;
using GameOps = Jiangyu.Game.Strategy.Operations;

namespace Jiangyu.Loader.Diagnostics;

// Dev command (nav): unattended session navigation for spike and multiplayer-testing
// rigs. Drives the legs a player would click through so a fresh headless process can be
// walked from the title screen into a tactical mission entirely over the bridge:
//   load {save}              load a named save (fixture flow: the game autosaves over
//                            latest on operation start, so a rig that reloads latest
//                            keeps eating its own fixture; determinism runs load the
//                            dedicated fixture save by name instead)
//   load-latest              SaveSystem.TryGetLatestSaveState + Load (title-screen Continue)
//   enter-mission [operation=N] [mission=N]   wait for strategy, pick operation/mission by
//                            index, start the operation, open mission prep, launch
//   ops                      the current and available operations with their missions
//   endturn                  end the active faction's turn, then report round
//   round                    round number, active faction, mission liveness
//   status                   where the driver is (phase, strategy/tactical liveness, error)
// The long legs run as a coroutine on the main thread; poll status for progress.
// Mutating by definition, dev-loader only.
internal static class NavDriver
{
    private const int StrategyTimeoutFrames = 1800;
    private const int ScreenTimeoutFrames = 600;
    private const int PreviewSettleFrames = 600;
    private const int TacticalTimeoutFrames = 3600;

    private static string _phase = "idle";
    private static string _lastError;
    private static bool _running;

    public static object Run(JsonElement args, MelonLogger.Instance log)
    {
        var op = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("op", out var o)
            ? o.GetString()
            : null;
        try
        {
            switch (op)
            {
                case "status":
                    return Status();
                case "load":
                    return LoadByName(args);
                case "load-latest":
                    return LoadLatest();
                case "enter-mission":
                    return EnterMission(args, log);
                case "endturn":
                {
                    var tactical = TacticalState.Get();
                    if (tactical == null)
                        return new { error = "no tactical state" };
                    tactical.EndTurn();
                    return Round();
                }
                case "round":
                    return Round();
                case "ops":
                    return ListOps();
                default:
                    return new { error = "nav op must be status | load | load-latest | enter-mission | ops | endturn | round" };
            }
        }
        catch (Exception ex)
        {
            return new { error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    // Survey the strategy layer: the current operation and every available one, with
    // mission counts. Exists because enter-mission indexes into these lists blind, and a
    // save can sit in any layout (operation already started, missions consumed).
    private static object ListOps()
    {
        var strategy = StrategyState.Get();
        if (strategy == null)
            return new { error = "no strategy state" };
        object DescribeOp(Operation op)
        {
            if (op == null)
                return null;
            var missions = new List<object>();
            var all = GameOps.Missions(op);
            for (var i = 0; i < all.Count; i++)
            {
                var m = all[i];
                missions.Add(new { index = i, seed = Safe(() => m.GetSeed().ToString()) });
            }
            return new
            {
                name = Safe(() => op.GetTranslatedName()),
                missions,
            };
        }
        var available = new List<object>();
        var operations = GameOps.Available();
        for (var i = 0; i < operations.Count; i++)
            available.Add(new { index = i, op = DescribeOp(operations[i]) });
        Operation current = null;
        try { current = strategy.Operations.GetCurrentOperation(); } catch { }
        return new { ok = true, current = DescribeOp(current), available };
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "<null>"; }
        catch (Exception ex) { return $"<threw: {ex.GetType().Name}>"; }
    }

    private static object Round()
    {
        var manager = Il2CppMenace.Tactical.TacticalManager.Get();
        if (manager == null)
            return new { error = "no tactical manager" };
        return new
        {
            ok = true,
            round = manager.GetRound(),
            activeFaction = manager.GetActiveFactionID(),
            mission = Il2CppMenace.Tactical.TacticalManager.IsMissionRunning(),
        };
    }

    private static object Status() => new
    {
        ok = true,
        running = _running,
        phase = _phase,
        error = _lastError,
        strategy = SafeBool(() => StrategyState.Get() != null),
        mission = Il2CppMenace.Tactical.TacticalManager.IsMissionRunning(),
    };

    private static object LoadLatest()
    {
        if (!SaveSystem.TryGetLatestSaveState(out var state) || state == null)
            return new { error = "no save states found" };
        SaveSystem.Load(state);
        _phase = "loading save";
        return new { ok = true, loading = state.GetFilePath() };
    }

    private static object LoadByName(JsonElement args)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("save", out var s)
            ? s.GetString()
            : null;
        if (string.IsNullOrEmpty(name))
            return new { error = "load needs {save: \"<name>\"} (the name as typed in-game, or a save file path)" };

        foreach (var candidate in CandidateSavePaths(name))
        {
            if (!SaveSystem.TryGetSaveState(candidate, out var state) || state == null)
                continue;
            SaveSystem.Load(state);
            _phase = "loading save";
            return new { ok = true, loading = state.GetFilePath() };
        }

        return new { error = $"no save state found for '{name}'" };
    }

    // TryGetSaveState addresses saves by file path. SaveNameFold.PathForName applies the
    // game's own fold to a save name and anchors it in the saves directory, so the name
    // as typed in-game resolves. The raw argument follows for a caller holding a full
    // path or an exact filename.
    private static IEnumerable<string> CandidateSavePaths(string name)
    {
        var bare = name.EndsWith(".save", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        var folded = SaveNameFold.PathForName(bare);
        if (folded != null)
            yield return folded;
        yield return name;
    }

    private static object EnterMission(JsonElement args, MelonLogger.Instance log)
    {
        if (_running)
            return new { error = "nav driver is already running (poll status)" };
        var opIdx = args.TryGetProperty("operation", out var oi) ? oi.GetInt32() : 0;
        var missionIdx = args.TryGetProperty("mission", out var mi) ? mi.GetInt32() : 0;
        _running = true;
        _lastError = null;
        try
        {
            MelonCoroutines.Start(EnterMissionLoop(opIdx, missionIdx, log));
        }
        catch
        {
            _running = false;
            throw;
        }
        return new { ok = true, started = true };
    }

    private static IEnumerator EnterMissionLoop(int opIdx, int missionIdx, MelonLogger.Instance log)
    {
        // Every exit, including a throw that MelonLoader swallows, releases the driver.
        try
        {
            // 1. Wait for the strategy layer (a load just completed, or the caller reached it).
            _phase = "waiting for strategy state";
            var waited = 0;
            while (StrategyState.Get() == null && waited++ < StrategyTimeoutFrames)
                yield return null;
            var strategy = StrategyState.Get();
            if (strategy == null)
            {
                Fail("strategy state never came up");
                yield break;
            }

            // 2. Pick the operation and mission.
            _phase = "selecting operation";
            Operation operation;
            Mission mission;
            try
            {
                var operations = GameOps.Available();
                operation = opIdx >= 0 && opIdx < operations.Count ? operations[opIdx] : null;
                if (operation == null)
                {
                    Fail($"no available operation at index {opIdx} ({operations.Count} available)");
                    yield break;
                }
                var missions = GameOps.Missions(operation);
                mission = missionIdx >= 0 && missionIdx < missions.Count ? missions[missionIdx] : null;
                if (mission == null)
                {
                    Fail($"no mission at index {missionIdx} ({missions.Count} in operation)");
                    yield break;
                }
                log?.Msg($"[nav] operation '{operation.GetTranslatedName()}', mission {missionIdx}, seed={mission.GetSeed()}");
            }
            catch (Exception ex)
            {
                Fail($"operation/mission selection: {ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            // Legs 3-6 run as one retryable chain. The prep screen's background scene
            // (loadout GameObjects) loads asynchronously; the first UpdateSupplies call
            // null-refs inside MissionPrepScene.SetLoadoutsStatus if the scene is not there
            // yet, and a same-screen retry keeps failing once that has happened. Re-running
            // the whole chain (start operation, reopen prep, wait, launch) is what recovers,
            // so the retry wraps the chain, not the single call.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                string chainError = null;
                var entered = false;
                // 3. Start the operation with that mission (mirrors the operation-select
                //    dialog's yes action, minus the dialog).
                _phase = "starting operation";
                try
                {
                    strategy.Operations.SetCurrentOperation(operation);
                    operation.StartOperation(mission);
                }
                catch (Exception ex)
                {
                    chainError = $"StartOperation: {ex.GetType().Name}: {ex.Message}";
                }

                // 4. Open the mission prep screen. TryOpen is the game's own static opener
                //    (the map click path calls it): it creates and binds the screen for the
                //    mission. StartOperation may already have opened it.
                MissionPrepUIScreen prep = null;
                if (chainError == null)
                {
                    _phase = "opening mission prep";
                    prep = OpenPrepScreen();
                    if (prep == null)
                    {
                        try
                        {
                            MissionPrepUIScreen.TryOpen(mission, null);
                        }
                        catch (Exception ex)
                        {
                            chainError = $"prep TryOpen: {ex.GetType().Name}: {ex.Message}";
                        }
                    }
                }

                // 5. Wait for the preview, then launch through the normal button path.
                if (chainError == null)
                {
                    _phase = "waiting for mission preview";
                    var frames = 0;
                    while (prep == null && frames++ < ScreenTimeoutFrames)
                    {
                        yield return null;
                        prep = OpenPrepScreen();
                    }
                    frames = 0;
                    while (prep != null && PreviewOf(prep) == null && frames++ < TacticalTimeoutFrames)
                        yield return null;
                    if (prep == null)
                        chainError = "mission prep screen never opened";
                    else if (PreviewOf(prep) == null)
                        chainError = "mission preview never became ready";
                    else
                    {
                        for (var i = 0; i < PreviewSettleFrames / 6; i++)
                            yield return null;
                        _phase = "launching";
                        try
                        {
                            var maxSupplies = strategy.GetMissionSupplies(mission);
                            var costs = prep.UpdateSupplies(maxSupplies);
                            prep.LaunchMission(costs, maxSupplies);
                        }
                        catch (Exception ex)
                        {
                            chainError = $"LaunchMission: {ex.GetType().Name}: {ex.Message}";
                        }
                    }
                }

                // 6. Wait for the tactical mission to be live.
                if (chainError == null)
                {
                    _phase = "waiting for tactical";
                    var tacticalFrames = 0;
                    while (!Il2CppMenace.Tactical.TacticalManager.IsMissionRunning() && tacticalFrames++ < TacticalTimeoutFrames)
                        yield return null;
                    if (!Il2CppMenace.Tactical.TacticalManager.IsMissionRunning())
                        chainError = "tactical mission never started";
                    else
                        entered = true;
                }

                if (entered)
                {
                    _phase = "in mission";
                    log?.Msg("[nav] tactical mission entered");
                    yield break;
                }
                log?.Msg($"[nav] enter-mission attempt {attempt} failed: {chainError}");
                if (attempt == 3)
                {
                    Fail(chainError);
                    yield break;
                }
                // Let the broken prep state settle before the chain re-runs.
                for (var i = 0; i < 300; i++)
                    yield return null;
            }
        }
        finally
        {
            _running = false;
        }
    }

    // Non-blocking probe: the currently open MissionPrepUIScreen, or null. The calling
    // coroutine's yield loop supplies the waiting.
    private static MissionPrepUIScreen OpenPrepScreen()
    {
        try { return UIManager.Get()?.GetOpenScreenOfType<MissionPrepUIScreen>(); }
        catch { return null; }
    }

    private static object PreviewOf(MissionPrepUIScreen prep)
    {
        try { return prep.GetMissionPreview(); }
        catch { return null; }
    }

    private static void Fail(string error)
    {
        _lastError = error;
        _phase = "failed";
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }
}
