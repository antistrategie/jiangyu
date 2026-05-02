using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class GrepTests : IDisposable
{
    private readonly string _projectDir;

    public GrepTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        File.WriteAllText(Path.Combine(_projectDir, "jiangyu.json"), "{}");

        Directory.CreateDirectory(Path.Combine(_projectDir, "templates"));
        File.WriteAllText(Path.Combine(_projectDir, "templates", "patch.kdl"), "health 100\ndamage 50\n");
        File.WriteAllText(Path.Combine(_projectDir, "readme.txt"), "This project has health mods.\n");
        File.WriteAllText(Path.Combine(_projectDir, "other.txt"), "Nothing relevant here.\n");

        ProjectWatcher.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        ProjectWatcher.ProjectRoot = null;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private static JsonElement Call(string method, JsonElement? parameters)
    {
        string? response = null;
        var request = new { id = 1, method, @params = parameters };
        var json = JsonSerializer.Serialize(request);
        RpcDispatcher.HandleMessage(null!, json, r => response = r);
        var doc = JsonDocument.Parse(response!);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            throw new Exception(err.GetString());
        return doc.RootElement.GetProperty("result");
    }

    private JsonElement MakeParams(object obj) =>
        JsonSerializer.SerializeToElement(obj);

    [Fact]
    public void Grep_FindsMatches()
    {
        var result = Call("grepFiles", MakeParams(new { pattern = "health", path = _projectDir }));
        var arr = result.EnumerateArray().ToList();

        Assert.Equal(2, arr.Count);
        Assert.All(arr, match =>
        {
            Assert.True(match.TryGetProperty("file", out _));
            Assert.True(match.TryGetProperty("line", out _));
            Assert.True(match.TryGetProperty("text", out _));
        });
    }

    [Fact]
    public void Grep_GlobFilter()
    {
        var result = Call("grepFiles", MakeParams(new { pattern = "health", path = _projectDir, glob = "*.kdl" }));
        var arr = result.EnumerateArray().ToList();

        Assert.Single(arr);
        Assert.Contains("patch.kdl", arr[0].GetProperty("file").GetString());
    }

    [Fact]
    public void Grep_RespectsLimit()
    {
        var result = Call("grepFiles", MakeParams(new { pattern = "health", path = _projectDir, limit = 1 }));
        var arr = result.EnumerateArray().ToList();

        Assert.Single(arr);
    }

    [Fact]
    public void Grep_NoMatches_ReturnsEmpty()
    {
        var result = Call("grepFiles", MakeParams(new { pattern = "zzzznothere", path = _projectDir }));
        var arr = result.EnumerateArray().ToList();

        Assert.Empty(arr);
    }

    [Fact]
    public void Grep_DefaultsToProjectRoot()
    {
        var result = Call("grepFiles", MakeParams(new { pattern = "health" }));
        var arr = result.EnumerateArray().ToList();

        Assert.True(arr.Count >= 2);
    }

    [Fact]
    public void Grep_SkipsGitDirectory()
    {
        var gitDir = Path.Combine(_projectDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "config"), "health in git config\n");

        var result = Call("grepFiles", MakeParams(new { pattern = "health", path = _projectDir }));
        var files = result.EnumerateArray().Select(m => m.GetProperty("file").GetString()).ToList();

        Assert.DoesNotContain(files, f => f!.Contains(".git"));
    }
}
