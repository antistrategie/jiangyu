using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Shared;

namespace Jiangyu.Studio.Host.Acp;

/// <summary>
/// Per-project agent session metadata. The agent owns the conversation
/// content (we resume via ACP <c>session/load</c>); we only record enough
/// here to render the history popover without booting the agent: id,
/// which installed agent ran it, the agent's self-reported title (set via
/// <c>session_info_update</c>), timestamps, and a one-line preview of the
/// first user message.
///
/// Stored at <c>{projectRoot}/.jiangyu/agent-sessions.json</c>. Atomic
/// writes via a sibling .tmp file. JSON (not JSONL) because we update
/// fields in place — title, updatedAt — rather than only appending.
/// </summary>
internal static class AgentSessionsStore
{
    private const string DirectoryName = ".jiangyu";
    private const string FileName = "agent-sessions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Per-project file lock; concurrent writes from racing RPC handlers
    // on different paths are still possible (we only serialise within a
    // single project root), but we expect one project open at a time.
    private static readonly Lock FileLock = new();

    public static string PathFor(string projectRoot)
        => Path.Combine(projectRoot, DirectoryName, FileName);

    public static AgentSessionsFile Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        lock (FileLock)
        {
            if (!File.Exists(path))
                return new AgentSessionsFile();
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AgentSessionsFile>(json, JsonOptions)
                    ?? new AgentSessionsFile();
            }
            catch (Exception ex)
            {
                // Corrupt file — start fresh rather than crashing the agent
                // panel. The previous file isn't deleted; if a user wants
                // to recover they can salvage it manually.
                Console.Error.WriteLine($"[Sessions] Failed to read {path}: {ex.Message}");
                return new AgentSessionsFile();
            }
        }
    }

    public static void Save(string projectRoot, AgentSessionsFile file)
    {
        var path = PathFor(projectRoot);
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".jiangyu.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Insert a new session record or update an existing one (matched by
    /// id). On update, fields supplied as non-null replace the stored
    /// values; null means "leave alone". Always bumps updatedAt.
    /// </summary>
    public static AgentSessionsFile Upsert(
        string projectRoot,
        string id,
        string? agentId = null,
        string? agentName = null,
        string? title = null,
        string? firstMessage = null)
    {
        lock (FileLock)
        {
            var file = Load(projectRoot);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var idx = file.Sessions.FindIndex(s => s.Id == id);
            if (idx >= 0)
            {
                var existing = file.Sessions[idx];
                file.Sessions[idx] = new AgentSessionMeta
                {
                    Id = existing.Id,
                    AgentId = agentId ?? existing.AgentId,
                    AgentName = agentName ?? existing.AgentName,
                    Title = title ?? existing.Title,
                    FirstMessage = firstMessage ?? existing.FirstMessage,
                    CreatedAt = existing.CreatedAt,
                    UpdatedAt = now,
                };
            }
            else
            {
                file.Sessions.Add(new AgentSessionMeta
                {
                    Id = id,
                    AgentId = agentId,
                    AgentName = agentName,
                    Title = title,
                    FirstMessage = firstMessage,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            Save(projectRoot, file);
            return file;
        }
    }

    /// <summary>Persist the user's chosen mode for a session so resume
    /// can replay it. No-op when the session record doesn't exist yet —
    /// the modes UI only opens after Upsert has run for the session.</summary>
    public static void SetMode(string projectRoot, string id, string modeId)
    {
        lock (FileLock)
        {
            var file = Load(projectRoot);
            var idx = file.Sessions.FindIndex(s => s.Id == id);
            if (idx < 0) return;
            file.Sessions[idx].CurrentModeId = modeId;
            file.Sessions[idx].UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Save(projectRoot, file);
        }
    }

    /// <summary>Persist a configOption value the user picked (keyed by
    /// configId) so resume can replay it. Same no-op semantics as
    /// <see cref="SetMode"/>.</summary>
    public static void SetConfigValue(string projectRoot, string id, string configId, JsonElement value)
    {
        lock (FileLock)
        {
            var file = Load(projectRoot);
            var idx = file.Sessions.FindIndex(s => s.Id == id);
            if (idx < 0) return;
            var meta = file.Sessions[idx];
            meta.ConfigValues ??= [];
            meta.ConfigValues[configId] = value;
            meta.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Save(projectRoot, file);
        }
    }

    public static AgentSessionsFile Remove(string projectRoot, string id)
    {
        lock (FileLock)
        {
            var file = Load(projectRoot);
            file.Sessions.RemoveAll(s => s.Id == id);
            Save(projectRoot, file);
            return file;
        }
    }
}

[RpcType]
public sealed class AgentSessionsFile
{
    [JsonPropertyName("sessions")]
    public List<AgentSessionMeta> Sessions { get; set; } = [];
}

[RpcType]
public sealed class AgentSessionMeta
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [JsonPropertyName("agentName")]
    public string? AgentName { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>One-line preview of the first user message, captured on
    /// the first prompt of the session. Used as a fallback display
    /// label when no title has been set.</summary>
    [JsonPropertyName("firstMessage")]
    public string? FirstMessage { get; set; }

    /// <summary>Unix milliseconds.</summary>
    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; set; }

    /// <summary>Unix milliseconds.</summary>
    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    /// <summary>Last mode the user picked for this session (ACP
    /// <c>session/set_mode</c>). ACP doesn't require agents to persist
    /// these — Claude doesn't — so Studio re-applies on resume to make
    /// the choice survive across reloads and host restarts.</summary>
    [JsonPropertyName("currentModeId")]
    public string? CurrentModeId { get; set; }

    /// <summary>configId → last value the user chose (ACP
    /// <c>session/set_config_option</c>). Replayed on resume; same
    /// rationale as <see cref="CurrentModeId"/>.</summary>
    [JsonPropertyName("configValues")]
    public Dictionary<string, JsonElement>? ConfigValues { get; set; }
}
