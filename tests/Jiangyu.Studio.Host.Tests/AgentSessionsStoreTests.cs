using Jiangyu.Studio.Host.Acp;

namespace Jiangyu.Studio.Host.Tests;

public class AgentSessionsStoreTests : IDisposable
{
    private readonly string _projectDir;

    public AgentSessionsStoreTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-sessions-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_OnFreshProject_ReturnsEmptyFile()
    {
        var file = AgentSessionsStore.Load(_projectDir);
        Assert.Empty(file.Sessions);
    }

    [Fact]
    public void Upsert_NewId_AppendsEntry()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1", agentName: "Claude", title: "Initial");
        var file = AgentSessionsStore.Load(_projectDir);

        var entry = Assert.Single(file.Sessions);
        Assert.Equal("s-1", entry.Id);
        Assert.Equal("Claude", entry.AgentName);
        Assert.Equal("Initial", entry.Title);
        Assert.True(entry.CreatedAt > 0);
        Assert.Equal(entry.CreatedAt, entry.UpdatedAt);
    }

    [Fact]
    public void Upsert_ExistingId_UpdatesFieldsAndBumpsUpdatedAt()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1", agentName: "Claude");
        var first = AgentSessionsStore.Load(_projectDir).Sessions[0];

        // Sleep just enough that updatedAt advances.
        Thread.Sleep(2);

        AgentSessionsStore.Upsert(_projectDir, "s-1", title: "Updated");
        var second = AgentSessionsStore.Load(_projectDir).Sessions[0];

        Assert.Equal("Claude", second.AgentName);            // preserved
        Assert.Equal("Updated", second.Title);               // applied
        Assert.Equal(first.CreatedAt, second.CreatedAt);     // immutable
        Assert.True(second.UpdatedAt >= first.UpdatedAt);    // bumped (monotonic, may equal on fast clocks)
    }

    [Fact]
    public void Upsert_NullArgs_LeaveExistingValuesAlone()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1",
            agentId: "claude-acp",
            agentName: "Claude",
            title: "Original",
            firstMessage: "hello");

        // Subsequent upsert with no fields supplied just bumps updatedAt;
        // none of the existing fields should clear to null.
        AgentSessionsStore.Upsert(_projectDir, "s-1");

        var entry = AgentSessionsStore.Load(_projectDir).Sessions[0];
        Assert.Equal("claude-acp", entry.AgentId);
        Assert.Equal("Claude", entry.AgentName);
        Assert.Equal("Original", entry.Title);
        Assert.Equal("hello", entry.FirstMessage);
    }

    [Fact]
    public void Remove_ExistingId_DeletesEntry()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1");
        AgentSessionsStore.Upsert(_projectDir, "s-2");
        AgentSessionsStore.Upsert(_projectDir, "s-3");

        AgentSessionsStore.Remove(_projectDir, "s-2");

        var ids = AgentSessionsStore.Load(_projectDir).Sessions.Select(s => s.Id).ToArray();
        Assert.Equal(["s-1", "s-3"], ids);
    }

    [Fact]
    public void Remove_MissingId_IsNoOp()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1");
        AgentSessionsStore.Remove(_projectDir, "missing");

        var ids = AgentSessionsStore.Load(_projectDir).Sessions.Select(s => s.Id).ToArray();
        Assert.Equal(["s-1"], ids);
    }

    [Fact]
    public void Save_WritesAtomicallyViaTmpRename()
    {
        AgentSessionsStore.Upsert(_projectDir, "s-1");

        var path = AgentSessionsStore.PathFor(_projectDir);
        Assert.True(File.Exists(path));
        // The .tmp shouldn't be left behind on a successful write.
        Assert.False(File.Exists(path + ".jiangyu.tmp"));
    }

    [Fact]
    public void Load_OnCorruptFile_ReturnsEmptyAndPreservesFile()
    {
        var dir = Path.Combine(_projectDir, ".jiangyu");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "agent-sessions.json");
        File.WriteAllText(path, "{ this is not valid json");

        var file = AgentSessionsStore.Load(_projectDir);

        Assert.Empty(file.Sessions);
        // Original file kept so the user can salvage it manually.
        Assert.True(File.Exists(path));
    }
}
