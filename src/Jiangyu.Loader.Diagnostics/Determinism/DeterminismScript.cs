using System.Text.Json;

namespace Jiangyu.Loader.Diagnostics.Determinism;

// A scripted command sequence: the fixed input both the record and the replay run drive
// into the mission. JSON on disk (<UserData>/determinism/<name>.script.json) so the same
// bytes drive both processes. Each step is one game action; the session waits for the
// action's barrier event plus a quiet window before snapshotting, which is what makes
// the two runs' snapshots comparable.
//
// Step ops:
//   move    {actor, tile:[x,z]}              -> Actor.MoveTo
//   attack  {actor, skill:"id", tile:[x,z]}  -> Skill.Use(tile, null)
//   skip    {actor}                          -> Actor.SkipTurn
//   endturn {}                               -> TacticalState.EndTurn
//   wait    {frames:N}                       -> pure settle, then snapshot
//   seedrng {value:N}                        -> UnityEngine.Random.InitState(value)
//           (RNG-site probe: if a combat roll matches across processes only after an
//           identical InitState, the roll consumes UnityEngine.Random)
// An actor ref is {faction, template} or {faction, index}. Template (the actor's
// EntityTemplate name, e.g. "player_squad.helen") is the stable form: a faction's
// GetActors() list is not positionally stable once actors act, so index addressing is
// only safe for a first action.
// Optional per step: "waitEvent" overrides the default barrier event, "settle" the
// quiet-window frames, "timeout" the give-up frames, "note" a free-text journal label.
internal sealed class DeterminismScript
{
    public int SettleFrames = 90;
    public int TimeoutFrames = 3600;
    public List<Step> Steps = new();

    internal sealed class Step
    {
        public string Op;
        public string Faction;   // FactionType name, e.g. "Player" (attack/move/skip actor; spawn faction)
        public string ActorTemplate;
        public int ActorIndex = -1;
        public string Skill;     // skill GetID(), for attack
        public int? Usage;       // UsageParameter flags for attack (1=Free, 2=IgnoreUsabilityCheck)
        public string Template;  // EntityTemplate id, for spawn
        public int X, Z;         // target tile, for move/attack/spawn
        public int Frames;       // for wait
        public int Value;        // for seedrng
        public string WaitEvent; // barrier event override
        public int? Settle;
        public int? Timeout;
        public string Note;

        // The default barrier event for the op: the game's own "this action finished"
        // signal. Snapshot equivalence across runs hangs on these.
        public string DefaultWaitEvent => Op switch
        {
            "move" => DeterminismSession.EventMovementFinished,
            "attack" => DeterminismSession.EventAfterSkillUse,
            "skip" => DeterminismSession.EventTurnEnd,
            "endturn" => DeterminismSession.EventPlayerTurn,
            "spawn" => DeterminismSession.EventEntitySpawned,
            _ => null,
        };
    }

    public static DeterminismScript Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"script not found: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var script = new DeterminismScript();
        if (root.TryGetProperty("settleFrames", out var sf)) script.SettleFrames = sf.GetInt32();
        if (root.TryGetProperty("timeoutFrames", out var tf)) script.TimeoutFrames = tf.GetInt32();
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new FormatException("script needs a steps array");
        foreach (var el in steps.EnumerateArray())
        {
            var step = new Step { Op = el.GetProperty("op").GetString() };
            if (el.TryGetProperty("actor", out var actor))
            {
                step.Faction = actor.GetProperty("faction").GetString();
                if (actor.TryGetProperty("template", out var actorTemplate))
                    step.ActorTemplate = actorTemplate.GetString();
                if (actor.TryGetProperty("index", out var actorIndex))
                    step.ActorIndex = actorIndex.GetInt32();
            }
            // Spawn-style steps carry faction at the top level, not inside an actor ref.
            if (el.TryGetProperty("faction", out var faction)) step.Faction = faction.GetString();
            if (el.TryGetProperty("skill", out var skill)) step.Skill = skill.GetString();
            if (el.TryGetProperty("usage", out var usage)) step.Usage = usage.GetInt32();
            if (el.TryGetProperty("template", out var template)) step.Template = template.GetString();
            if (el.TryGetProperty("tile", out var tile) && tile.ValueKind == JsonValueKind.Array)
            {
                step.X = tile[0].GetInt32();
                step.Z = tile[1].GetInt32();
            }
            if (el.TryGetProperty("frames", out var frames)) step.Frames = frames.GetInt32();
            if (el.TryGetProperty("value", out var value)) step.Value = value.GetInt32();
            if (el.TryGetProperty("waitEvent", out var we)) step.WaitEvent = we.GetString();
            if (el.TryGetProperty("settle", out var se)) step.Settle = se.GetInt32();
            if (el.TryGetProperty("timeout", out var to)) step.Timeout = to.GetInt32();
            if (el.TryGetProperty("note", out var note)) step.Note = note.GetString();
            script.Steps.Add(step);
        }
        return script;
    }

    // The canonical per-step command echo written into the journal, so a journal alone
    // shows what each barrier hash followed.
    internal static object Describe(Step step) => step.Op switch
    {
        "move" => new { op = step.Op, faction = step.Faction, actor = step.ActorTemplate, index = step.ActorIndex, tile = new[] { step.X, step.Z }, note = step.Note },
        "attack" => new { op = step.Op, faction = step.Faction, actor = step.ActorTemplate, index = step.ActorIndex, skill = step.Skill, usage = step.Usage, tile = new[] { step.X, step.Z }, note = step.Note },
        "spawn" => new { op = step.Op, faction = step.Faction, template = step.Template, tile = new[] { step.X, step.Z }, note = step.Note },
        "skip" => new { op = step.Op, faction = step.Faction, actor = step.ActorTemplate, index = step.ActorIndex, note = step.Note },
        "wait" => new { op = step.Op, frames = step.Frames, note = step.Note },
        "seedrng" => new { op = step.Op, value = step.Value, note = step.Note },
        _ => new { op = step.Op, note = step.Note },
    };
}
