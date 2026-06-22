using Jiangyu.Core.Compile;

namespace Jiangyu.Core.Tests.Compile;

public class BuildStateTests : IDisposable
{
    private readonly string _projectDir;

    public BuildStateTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"jiangyu-buildstate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState()
    {
        var state = BuildState.Load(_projectDir);
        Assert.False(state.Matches("prefabs", "abc"));
    }

    [Fact]
    public void RecordThenMatches()
    {
        var state = BuildState.Load(_projectDir);
        state.Record("prefabs", "abc");

        Assert.True(state.Matches("prefabs", "abc"));
        Assert.False(state.Matches("prefabs", "different"));
        Assert.False(state.Matches("other", "abc"));
    }

    [Fact]
    public void Matches_EmptyFingerprintNeverMatches()
    {
        var state = BuildState.Load(_projectDir);
        state.Record("prefabs", "");
        Assert.False(state.Matches("prefabs", ""));
    }

    [Fact]
    public void Remove_DropsEntry()
    {
        var state = BuildState.Load(_projectDir);
        state.Record("prefabs", "abc");
        state.Remove("prefabs");
        Assert.False(state.Matches("prefabs", "abc"));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var state = BuildState.Load(_projectDir);
        state.Record("prefabs", "abc");
        state.Save(_projectDir);

        var reloaded = BuildState.Load(_projectDir);
        Assert.True(reloaded.Matches("prefabs", "abc"));
        // Persisted under the local build cache, not the project root.
        Assert.True(File.Exists(Path.Combine(_projectDir, ".jiangyu", "build-state.json")));
    }

    [Fact]
    public void Record_WithData_RoundTripsThroughSave()
    {
        var state = BuildState.Load(_projectDir);
        var payload = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
        state.Record("assets", "fp", payload);
        state.Save(_projectDir);

        var reloaded = BuildState.Load(_projectDir);
        Assert.True(reloaded.Matches("assets", "fp"));
        var restored = reloaded.GetData<Dictionary<string, string>>("assets");
        Assert.NotNull(restored);
        Assert.Equal("1", restored!["a"]);
        Assert.Equal("2", restored["b"]);
    }

    [Fact]
    public void GetData_AbsentOrNoPayload_ReturnsDefault()
    {
        var state = BuildState.Load(_projectDir);
        state.Record("prefabs", "fp"); // no data payload
        Assert.Null(state.GetData<Dictionary<string, string>>("prefabs"));
        Assert.Null(state.GetData<Dictionary<string, string>>("missing"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyState()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, ".jiangyu"));
        File.WriteAllText(Path.Combine(_projectDir, ".jiangyu", "build-state.json"), "{ not valid json");

        var state = BuildState.Load(_projectDir);
        Assert.False(state.Matches("prefabs", "abc"));
    }
}
