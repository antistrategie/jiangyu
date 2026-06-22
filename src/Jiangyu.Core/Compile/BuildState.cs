using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Per-phase build records from the last successful compile, persisted in
/// <c>.jiangyu/build-state.json</c> (local build cache, uncommitted). Each phase stores an
/// input fingerprint and an optional cached payload (e.g. mesh metadata the Unity pass would
/// otherwise have to re-derive). Incremental compilation skips a phase whose recorded
/// fingerprint matches its current inputs and whose outputs are still present. A fresh clone,
/// or a missing/corrupt/old-format file, degrades to a safe full rebuild.
/// </summary>
public sealed class BuildState
{
    public sealed class PhaseEntry
    {
        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public JsonElement? Data { get; set; }
    }

    [JsonPropertyName("phases")]
    public Dictionary<string, PhaseEntry> Phases { get; set; } = new(StringComparer.Ordinal);

    private const string FileName = "build-state.json";

    private static string DirFor(string projectDir) => Path.Combine(projectDir, ".jiangyu");

    public static BuildState Load(string projectDir)
        => JsonStore.TryLoad<BuildState>(DirFor(projectDir), FileName) ?? new BuildState();

    /// <summary>True when <paramref name="phase"/> was recorded with exactly
    /// <paramref name="fingerprint"/>. An empty fingerprint never matches, so a phase whose
    /// inputs couldn't be hashed always rebuilds.</summary>
    public bool Matches(string phase, string fingerprint)
        => fingerprint.Length > 0 && Phases.TryGetValue(phase, out var entry) && entry.Fingerprint == fingerprint;

    /// <summary>The cached payload recorded with a phase, deserialised to
    /// <typeparamref name="T"/>, or default when absent or undeserialisable.</summary>
    public T? GetData<T>(string phase)
    {
        if (!Phases.TryGetValue(phase, out var entry) || entry.Data is not { } data)
            return default;
        try { return data.Deserialize<T>(); }
        catch { return default; }
    }

    public void Record(string phase, string fingerprint, object? data = null)
        => Phases[phase] = new PhaseEntry
        {
            Fingerprint = fingerprint,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data),
        };

    public void Remove(string phase) => Phases.Remove(phase);

    public void Save(string projectDir)
    {
        var dir = DirFor(projectDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileName), JsonStore.ToJson(this));
    }
}
