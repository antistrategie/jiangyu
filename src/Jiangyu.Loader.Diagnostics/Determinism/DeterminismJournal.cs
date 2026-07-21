using System.Text.Json;

namespace Jiangyu.Loader.Diagnostics.Determinism;

// The on-disk record of a run: one entry per scripted step carrying the command echo,
// the execution result, the barrier hash, and the full snapshot lines the hash was
// computed over. A journal is both the replay reference and the desync-forensics
// artefact: because the snapshot lines are journaled, a mismatch can be shown as a
// field-level diff rather than a bare hash inequality.
internal sealed class DeterminismJournal
{
    public string Kind { get; set; } = "determinism-journal";
    public string Mode { get; set; }
    public string Script { get; set; }
    public string GameVersion { get; set; }
    public string StartedUtc { get; set; }
    public string Seed { get; set; }
    public Entry Baseline { get; set; }
    public List<Entry> Steps { get; set; } = new();

    internal sealed class Entry
    {
        public int Seq { get; set; }
        public object Command { get; set; }
        public string Result { get; set; }
        public string Hash { get; set; }
        public string Mission { get; set; }
        public List<string> Snapshot { get; set; }

        /// <summary>Frames between the step's command execution and its snapshot (barrier
        /// wait plus quiet window). A timeout hiding behind a divergence shows up here.</summary>
        public int WaitedFrames { get; set; }
    }

    // The sim verdict between two entries at the same barrier. The hash folds in the
    // UnityEngine.Random seed block, which visuals consume per frame: it differs across
    // processes even when the sim is identical. So the verdict rests on the actor lines
    // and the mission string minus its rng fragment; a hash mismatch that passes here is
    // rng-only, counted but never reported as divergence. Both comparison surfaces
    // (replay-vs-reference and journal-vs-journal) use this one definition.
    public static bool SimDiverges(Entry reference, Entry actual)
        => MissionWithoutRng(reference.Mission) != MissionWithoutRng(actual.Mission)
           || !SameLines(reference.Snapshot, actual.Snapshot);

    private static string MissionWithoutRng(string mission)
        => mission?.Split(" rng=")[0];

    private static bool SameLines(List<string> a, List<string> b)
    {
        if (a == null || b == null || a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    public static DeterminismJournal Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"journal not found: {path}");
        var journal = JsonSerializer.Deserialize<DeterminismJournal>(File.ReadAllText(path), Options);
        if (journal?.Steps == null || journal.Kind != "determinism-journal")
            throw new InvalidDataException($"not a determinism journal: {path}");
        return journal;
    }
}
